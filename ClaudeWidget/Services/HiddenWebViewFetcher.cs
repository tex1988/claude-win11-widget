using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace ClaudeWidget.Services;

/// <summary>
/// Fetches JSON from claude.ai URLs using a hidden Chromium (WebView2)
/// instance instead of a plain HTTP client.
///
/// Why this exists: claude.ai's Cloudflare protection fingerprints/blocks
/// plain HttpClient requests (returns a "Just a moment..." challenge page)
/// even when a valid sessionKey cookie is supplied. A real Chromium engine
/// passes Cloudflare's checks automatically because it actually runs the
/// challenge JS, same reasoning the reference Electron widget uses a hidden
/// BrowserWindow instead of Node's fetch/http.
///
/// NOTE (tried and reverted): switching to a plain HttpClient that reuses
/// cookies harvested from a transient WebView2 session (to save memory) was
/// attempted and reverted — it left the widget stuck showing "Loading..."
/// indefinitely, most likely because Cloudflare's bot-detection also checks
/// TLS/HTTP fingerprints that a bare .NET HttpClient can't replicate, not
/// just the cf_clearance cookie. Do not reintroduce that approach without a
/// solid way to verify it actually round-trips real data first. If RAM
/// usage needs to be revisited, prefer an idle-timeout that closes and
/// re-creates this persistent WebView2 after N minutes of inactivity
/// instead of moving fetches off WebView2 entirely.
///
/// WebView2 requires a genuine STA COM apartment with a real Win32 message
/// loop on the thread that creates/owns it (see Microsoft's WebView2
/// threading model docs) — DispatcherQueueController's dedicated thread does
/// not reliably provide that combination. So this class spins up its own
/// classic STA System.Threading.Thread, runs a real GetMessage/DispatchMessage
/// loop on it, and installs a custom SynchronizationContext (the same pattern
/// WindowsFormsSynchronizationContext uses) so that `await` continuations
/// after WebView2 calls correctly resume back on that same thread instead of
/// an arbitrary thread-pool thread.
///
/// A single WebView2 instance is created lazily and reused for all
/// subsequent fetches, mirroring fetchMultipleViaWindow's "single reused
/// BrowserWindow" approach for lower overhead. This works both in the normal
/// GUI process and the headless widget COM-server process, since neither
/// needs a visible window — only a real HWND to host the browser control.
/// </summary>
internal static class HiddenWebViewFetcher
{
    private static readonly SemaphoreSlim s_initLock = new(1, 1);
    private static WebViewThreadSyncContext? s_syncContext;
    private static CoreWebView2? s_webView;
    // Controller must be kept alive — the GC collecting it closes CoreWebView2.
    private static CoreWebView2Controller? s_controller;
    private static IntPtr s_hwnd;

    private const string ClassName = "ClaudeWidgetHiddenWebViewHost";
    private static bool s_classRegistered;
    // Keep the delegate rooted for the lifetime of the process — native code
    // holds a raw function pointer to it and the GC must never collect it.
    private static readonly WndProcDelegate s_wndProc = WndProc;

    private const uint WM_APP = 0x8000;
    private const uint WM_EXECUTE_QUEUE = WM_APP + 1;

    // WebView2 is single-threaded by design: only one navigation can be
    // in flight at a time. Serialize all fetch calls so that rapid Refresh
    // clicks (or background timer fires) never race with each other.
    private static readonly SemaphoreSlim s_fetchLock = new(1, 1);

    /// <summary>
    /// Navigates the hidden WebView2 to <paramref name="url"/> with the given
    /// claude.ai sessionKey cookie set, and returns the page body text
    /// (expected to be raw JSON for claude.ai's internal API endpoints).
    /// </summary>
    public static async Task<string> FetchJsonAsync(string url, string sessionKey, TimeSpan? timeout = null)
    {
        await EnsureInitializedAsync();
        var t = timeout ?? TimeSpan.FromSeconds(30);

        await s_fetchLock.WaitAsync();
        try
        {
            // Retry once — first attempt can fail on a stale Cloudflare
            // challenge or transient navigation error.
            try
            {
                return await s_syncContext!.RunAsync(
                    () => FetchOnWebViewThreadAsync(url, sessionKey, t));
            }
            catch
            {
                // Brief pause so WebView2 settles before the retry.
                await Task.Delay(500);
                return await s_syncContext!.RunAsync(
                    () => FetchOnWebViewThreadAsync(url, sessionKey, t));
            }
        }
        finally
        {
            s_fetchLock.Release();
        }
    }

    private static async Task EnsureInitializedAsync()
    {
        if (s_webView is not null) return;

        await s_initLock.WaitAsync();
        try
        {
            if (s_webView is not null) return;

            var readyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var thread = new Thread(() => ThreadMain(readyTcs))
            {
                IsBackground = true,
                Name = "ClaudeWidgetWebViewThread",
            };
            // Real STA apartment: the CLR calls CoInitializeEx(COINIT_APARTMENTTHREADED)
            // for us before running the thread proc, matching what WebView2's
            // threading model requires.
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            await readyTcs.Task;
        }
        finally
        {
            s_initLock.Release();
        }
    }

    private static void ThreadMain(TaskCompletionSource readyTcs)
    {
        try
        {
            s_hwnd = CreateHiddenWindow();
            if (s_hwnd == IntPtr.Zero)
            {
                readyTcs.TrySetException(new InvalidOperationException(
                    $"CreateWindowExW failed with error {Marshal.GetLastWin32Error()}."));
                return;
            }

            s_syncContext = new WebViewThreadSyncContext(s_hwnd);
            SynchronizationContext.SetSynchronizationContext(s_syncContext);

            // Kick off WebView2 initialization on this thread. It runs
            // synchronously up to its first await, then returns control here
            // so we can enter the message loop; its continuations are
            // marshalled back onto this same thread via s_syncContext.
            _ = InitializeWebViewAsync(readyTcs);
        }
        catch (Exception ex)
        {
            readyTcs.TrySetException(ex);
            return;
        }

        RunMessageLoop();
    }

    private static async Task InitializeWebViewAsync(TaskCompletionSource readyTcs)
    {
        try
        {
            string userDataFolder = Path.Combine(
                Windows.Storage.ApplicationData.Current.LocalFolder.Path, "WebView2");

            // This WebView2 is never actually shown (controller.IsVisible = false
            // below) — it only exists to run Cloudflare's JS challenge and read
            // back document.body.innerText. So GPU compositing, crash reporting,
            // extensions, and background network services are all pure overhead
            // here; disabling them trims several child processes (GPU process,
            // crashpad handler, etc.) from Task Manager's "Claude Limits" group
            // without touching anything Cloudflare's bot-check actually needs
            // (a real JS engine), so it shouldn't affect fetch success at all.
            var options = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments =
                    "--disable-gpu --disable-gpu-compositing --disable-software-rasterizer " +
                    "--disable-extensions --disable-background-networking --disable-crash-reporter",
            };

            var env = await CoreWebView2Environment.CreateWithOptionsAsync(null, userDataFolder, options);
            var windowRef = CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)s_hwnd.ToInt64());
            var controller = await env.CreateCoreWebView2ControllerAsync(windowRef);
            controller.Bounds = new Windows.Foundation.Rect(0, 0, 1024, 768);
            controller.IsVisible = false;

            s_controller = controller;
            s_webView = controller.CoreWebView2;

            // WebView2 immediately starts an internal navigation to about:blank
            // after the controller is created. Navigate() throws ERROR_INVALID_STATE
            // if called while that initial navigation is still in progress.
            // Wait for it to complete before declaring the WebView2 ready.
            var initNavTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnInitNav(object? s, CoreWebView2NavigationCompletedEventArgs e)
            {
                s_webView.NavigationCompleted -= OnInitNav;
                initNavTcs.TrySetResult();
            }
            s_webView.NavigationCompleted += OnInitNav;
            // If it's already done (NavigationCompleted already fired), resolve immediately.
            await initNavTcs.Task.WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(_ => { }, TaskContinuationOptions.None);

            readyTcs.TrySetResult();
        }
        catch (Exception ex)
        {
            readyTcs.TrySetException(ex);
        }
    }

    private static void RunMessageLoop()
    {
        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
    }

    // Must be called already running on the WebView2 thread (via s_syncContext.RunAsync).
    private static async Task<string> FetchOnWebViewThreadAsync(string url, string sessionKey, TimeSpan timeout)
    {
        var webView = s_webView!;

        var cookie = webView.CookieManager.CreateCookie("sessionKey", sessionKey, ".claude.ai", "/");
        cookie.IsSecure = true;
        cookie.IsHttpOnly = true;
        webView.CookieManager.AddOrUpdateCookie(cookie);

        var navTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnNavigationCompleted(object? s, CoreWebView2NavigationCompletedEventArgs e) => navTcs.TrySetResult(e.IsSuccess);

        webView.NavigationCompleted += OnNavigationCompleted;
        using var cts = new CancellationTokenSource(timeout);
        using var reg = cts.Token.Register(() => navTcs.TrySetResult(false));
        try
        {
            webView.Navigate(url);
            bool ok = await navTcs.Task;
            if (!ok)
            {
                throw new TimeoutException($"Navigation to {url} failed or timed out after {timeout.TotalSeconds}s.");
            }
        }
        finally
        {
            webView.NavigationCompleted -= OnNavigationCompleted;
        }

        // ExecuteScriptAsync returns a JSON-encoded string literal; unwrap it.
        string raw = await webView.ExecuteScriptAsync("document.body.innerText || document.body.textContent");
        return JsonSerializer.Deserialize<string>(raw) ?? string.Empty;
    }

    internal static void PostExecuteMessage(IntPtr hwnd) => PostMessageW(hwnd, WM_EXECUTE_QUEUE, IntPtr.Zero, IntPtr.Zero);

    // ── Minimal hidden Win32 window + message loop plumbing ────────────────

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(
        int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_EXECUTE_QUEUE)
        {
            s_syncContext?.DrainQueue();
            return IntPtr.Zero;
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private static void EnsureClassRegistered()
    {
        if (s_classRegistered) return;

        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            style = 0,
            lpfnWndProc = s_wndProc,
            cbClsExtra = 0,
            cbWndExtra = 0,
            hInstance = GetModuleHandleW(null),
            hIcon = IntPtr.Zero,
            hCursor = IntPtr.Zero,
            hbrBackground = IntPtr.Zero,
            lpszMenuName = null,
            lpszClassName = ClassName,
            hIconSm = IntPtr.Zero,
        };

        RegisterClassExW(ref wc);
        s_classRegistered = true;
    }

    private static IntPtr CreateHiddenWindow()
    {
        EnsureClassRegistered();
        // No WS_VISIBLE bit set → window is created but never shown, exactly
        // like Electron's `show: false` BrowserWindow option.
        const int WS_OVERLAPPED = 0x00000000;
        return CreateWindowExW(
            0, ClassName, string.Empty, WS_OVERLAPPED,
            0, 0, 1024, 768,
            IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
    }
}

/// <summary>
/// A SynchronizationContext bound to <see cref="HiddenWebViewFetcher"/>'s
/// dedicated STA thread. Posted callbacks are queued and executed inline
/// from that thread's Win32 message loop (via a custom WM_APP message),
/// exactly the same pattern WindowsFormsSynchronizationContext uses — this
/// is what makes `await` after a WebView2 call correctly resume back on the
/// thread that owns the WebView2 COM objects.
/// </summary>
internal sealed class WebViewThreadSyncContext : SynchronizationContext
{
    private readonly IntPtr _hwnd;
    private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();

    public WebViewThreadSyncContext(IntPtr hwnd) => _hwnd = hwnd;

    public override void Post(SendOrPostCallback d, object? state)
    {
        _queue.Enqueue((d, state));
        HiddenWebViewFetcher.PostExecuteMessage(_hwnd);
    }

    public override void Send(SendOrPostCallback d, object? state) =>
        throw new NotSupportedException("Synchronous Send is not supported/used on the WebView2 thread.");

    internal void DrainQueue()
    {
        while (_queue.TryDequeue(out var item))
        {
            item.Callback(item.State);
        }
    }

    /// <summary>
    /// Schedules <paramref name="asyncFunc"/> to run on the WebView2 thread
    /// and returns a Task that completes with its result (or exception),
    /// resolved back on whatever context called this method.
    /// </summary>
    public Task<T> RunAsync<T>(Func<Task<T>> asyncFunc)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(async _ =>
        {
            try
            {
                T result = await asyncFunc();
                tcs.TrySetResult(result);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }, null);
        return tcs.Task;
    }
}
