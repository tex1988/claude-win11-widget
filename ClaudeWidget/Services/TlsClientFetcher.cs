using System.Runtime.InteropServices;
using TlsClient.Core.Models.Entities;
using TlsClient.Core.Models.Requests;
using TlsClient.Native;

namespace ClaudeWidget.Services;

/// <summary>
/// Fetches JSON from claude.ai URLs using a TLS-fingerprint-impersonating
/// HTTP client (<c>bogdanfinn/tls-client</c>, via the <c>TlsClient.Native</c>
/// P/Invoke wrapper) instead of a hidden WebView2/Chromium instance.
///
/// Why this exists: claude.ai's Cloudflare protection doesn't just check the
/// <c>sessionKey</c> cookie — it also fingerprints the TLS ClientHello
/// (JA3/JA4-style) and HTTP/2 frame ordering of the connection. A bare .NET
/// <see cref="System.Net.Http.HttpClient"/> uses .NET's SChannel TLS stack,
/// which produces a different fingerprint than any real browser and gets
/// silently challenged/blocked by Cloudflare even with valid cookies (this
/// was tried in an earlier session — see SESSION.md). <c>tls-client</c>
/// solves this by replicating a real Chrome TLS ClientHello byte-for-byte
/// (cipher suites, extensions, extension order, ALPN, etc.) using a Go TLS
/// stack (<c>utls</c>) loaded as a native DLL, so Cloudflare's fingerprint
/// check sees "real Chrome" and passes the request through — without ever
/// spinning up an actual Chromium process. This is the same trick
/// <c>curl-impersonate</c>/<c>curl_cffi</c> use, but with an actively
/// maintained pre-built native DLL and a first-class .NET wrapper
/// (<c>ErenKrt/TlsClient.NET</c> on NuGet as <c>TlsClient.Native</c> +
/// <c>TlsClient.Native.win-x64</c>).
///
/// Memory-wise this is dramatically cheaper than the WebView2 approach: no
/// child process tree (GPU process, network service, crashpad, etc.), just
/// one native DLL loaded in-process — single-digit MB instead of the
/// 80-100+ MB WebView2's process tree used to cost.
///
/// This does NOT solve JS-challenge-based Cloudflare pages ("Just a
/// moment...", requires executing a JS puzzle) — only fingerprint-based
/// blocking of otherwise-valid API requests. If Cloudflare ever starts
/// serving a JS challenge for these specific claude.ai API endpoints (it
/// currently doesn't; only the fingerprint check applies here), this
/// approach would stop working and <see cref="HiddenWebViewFetcher"/> would
/// need to come back as the primary path. <see cref="ClaudeApiService"/>
/// already falls back to <see cref="HiddenWebViewFetcher"/> automatically
/// whenever a response looks Cloudflare-blocked, so that scenario is
/// handled without user-visible breakage — WebView2 just then pays its
/// memory cost only for as long as it's actually needed.
/// </summary>
internal static class TlsClientFetcher
{
    private static readonly object s_initLock = new();
    private static readonly SemaphoreSlim s_fetchLock = new(1, 1);
    private static NativeTlsClient? s_client;

    // Must match the TLS identifier's own advertised browser version so the
    // TLS fingerprint (ClientHello) and the User-Agent header are
    // consistent with each other — mismatched signals are themselves a
    // bot-detection heuristic.
    private const string ChromeUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/146.0.0.0 Safari/537.36";
    private const string ChromeSecChUa =
        "\"Chromium\";v=\"146\", \"Not-A.Brand\";v=\"24\", \"Google Chrome\";v=\"146\"";

    /// <summary>
    /// Sends a GET request to <paramref name="url"/> with the given
    /// claude.ai sessionKey cookie set, and returns the response body
    /// (expected to be raw JSON for claude.ai's internal API endpoints).
    /// </summary>
    public static Task<string> FetchJsonAsync(string url, string sessionKey, TimeSpan? timeout = null) =>
        SendAsync(url, sessionKey, "GET", null, timeout);

    /// <summary>
    /// Sends a POST request with a JSON body to <paramref name="url"/> —
    /// used for claude.ai mutation endpoints (e.g. enabling/disabling
    /// pay-as-you-go "usage credits" billing). Returns the response body.
    /// </summary>
    public static Task<string> PostJsonAsync(string url, string sessionKey, string jsonBody, TimeSpan? timeout = null) =>
        SendAsync(url, sessionKey, "POST", jsonBody, timeout);

    /// <summary>
    /// Sends a PUT request with a JSON body to <paramref name="url"/> — see
    /// <see cref="PostJsonAsync"/>.
    /// </summary>
    public static Task<string> PutJsonAsync(string url, string sessionKey, string jsonBody, TimeSpan? timeout = null) =>
        SendAsync(url, sessionKey, "PUT", jsonBody, timeout);

    private static async Task<string> SendAsync(string url, string sessionKey, string method, string? jsonBody, TimeSpan? timeout)
    {
        EnsureInitialized();
        var t = timeout ?? TimeSpan.FromSeconds(30);
        bool hasBody = jsonBody is not null;

        // Confirmed via a real Chrome request (DevTools "Copy as cURL")
        // that claude.ai's backend accepts a bare sessionKey cookie with no
        // anthropic-device-id/activitySessionId cookies, and that a
        // top-level document-navigation header set (accept: text/html...,
        // sec-fetch-dest: document, sec-fetch-mode: navigate,
        // sec-fetch-site: none, upgrade-insecure-requests, no
        // Origin/Referer at all) works even without the __cf_bm cookie
        // Cloudflare normally sets. Earlier attempts used a fetch()-style
        // header set (Origin/Referer, sec-fetch-site: same-origin,
        // anthropic-client-* headers, fabricated device/activity id
        // cookies borrowed from a third-party client's guesswork) which
        // got a genuine "account_session_invalid" rejection from claude.ai's
        // backend — so mirror the real, confirmed-working navigation
        // request shape instead of guessing at what a fetch() call needs.
        // POST/PUT mutation calls are the one exception: claude.ai's own web
        // app issues these as XHR/fetch() calls (not navigations), so they
        // use a fetch()-style header set instead (see below).
        Log($"TlsClientFetcher: {method} {url}, sessionKey len={sessionKey?.Length ?? 0}, prefix={(sessionKey != null && sessionKey.Length >= 14 ? sessionKey[..14] : sessionKey)}, suffix={(sessionKey != null && sessionKey.Length >= 6 ? sessionKey[^6..] : "")}");

        Dictionary<string, string> headers;
        List<string> headerOrder;
        if (hasBody)
        {
            headers = new Dictionary<string, string>
            {
                ["accept"] = "*/*",
                ["accept-language"] = "en-US,en;q=0.9",
                ["content-type"] = "application/json",
                ["origin"] = "https://claude.ai",
                ["referer"] = "https://claude.ai/new",
                ["sec-ch-ua"] = ChromeSecChUa,
                ["sec-ch-ua-mobile"] = "?0",
                ["sec-ch-ua-platform"] = "\"Windows\"",
                ["sec-fetch-dest"] = "empty",
                ["sec-fetch-mode"] = "cors",
                ["sec-fetch-site"] = "same-origin",
                ["user-agent"] = ChromeUserAgent,
                ["cookie"] = $"sessionKey={sessionKey}",
            };
            headerOrder = new List<string>
            {
                "accept", "accept-language", "content-type", "origin", "referer",
                "sec-ch-ua", "sec-ch-ua-mobile", "sec-ch-ua-platform",
                "sec-fetch-dest", "sec-fetch-mode", "sec-fetch-site",
                "user-agent", "cookie",
            };
        }
        else
        {
            headers = new Dictionary<string, string>
            {
                ["accept"] = "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7",
                ["accept-language"] = "en-US,en;q=0.9",
                ["cache-control"] = "max-age=0",
                ["priority"] = "u=0, i",
                ["sec-ch-ua"] = ChromeSecChUa,
                ["sec-ch-ua-mobile"] = "?0",
                ["sec-ch-ua-platform"] = "\"Windows\"",
                ["sec-fetch-dest"] = "document",
                ["sec-fetch-mode"] = "navigate",
                ["sec-fetch-site"] = "none",
                ["sec-fetch-user"] = "?1",
                ["upgrade-insecure-requests"] = "1",
                ["user-agent"] = ChromeUserAgent,
                ["cookie"] = $"sessionKey={sessionKey}",
            };
            headerOrder = new List<string>
            {
                "accept", "accept-language", "cache-control", "priority",
                "sec-ch-ua", "sec-ch-ua-mobile", "sec-ch-ua-platform",
                "sec-fetch-dest", "sec-fetch-mode", "sec-fetch-site", "sec-fetch-user",
                "upgrade-insecure-requests", "user-agent", "cookie",
            };
        }

        var request = new Request
        {
            RequestUrl = url,
            RequestMethod = new System.Net.Http.HttpMethod(method),
            RequestBody = jsonBody,
            TlsClientIdentifier = TlsClientIdentifier.Chrome146,
            TimeoutMilliseconds = (int)t.TotalMilliseconds,
            FollowRedirects = true,
            // Diagnosed via a standalone repro harness against the real
            // claude.ai endpoint with a known-valid sessionKey: the
            // RequestCookies list (TlsClientCookie) silently fails to reach
            // the server — every request sent that way came back
            // "account_session_invalid", identical to sending no cookie at
            // all, regardless of cookie Domain value or WithCustomCookieJar.
            // Setting a raw "cookie" header instead worked immediately and
            // returned real organization data. So the cookie is set via the
            // header set below ("cookie") rather than RequestCookies.
            Headers = headers,
            HeaderOrder = headerOrder,
        };

        await s_fetchLock.WaitAsync();
        try
        {
            var response = await s_client!.RequestAsync(request);

            Log($"TlsClientFetcher: {method} {url} -> Status={response.Status}, BodyLen={response.Body?.Length ?? 0}, BodySnippet={Snippet(response.Body)}");

            // NativeTlsClient reports transport-level failures (DNS, TLS
            // handshake, timeout, native library errors, ...) as
            // Status == 0 with the error message in Body instead of
            // throwing, so surface that as an exception the same way a
            // real HTTP failure would be.
            if (response.Status == 0)
            {
                Logger.Error("provider", $"TlsClientFetcher: {method} {url} transport failure: {response.Body}");
                throw new InvalidOperationException($"tls-client request to {url} failed: {response.Body}");
            }

            return response.Body;
        }
        finally
        {
            s_fetchLock.Release();
        }
    }

    private static string Snippet(string? body)
    {
        if (string.IsNullOrEmpty(body)) return "(empty)";
        return body.Length > 2000 ? body[..2000] : body;
    }

    private static void Log(string msg) => Logger.Info("provider", msg);

    private static void EnsureInitialized()
    {
        if (s_client is not null) return;

        lock (s_initLock)
        {
            if (s_client is not null) return;

            NativeTlsClient.Initialize(ResolveLibraryPath());
            s_client = new NativeTlsClient();
        }
    }

    /// <summary>
    /// Locates the native tls-client DLL that the TlsClient.Native.win-x64 /
    /// win-x32 NuGet packages copy next to the app's own binaries (via their
    /// buildTransitive .targets file) at
    /// "&lt;output&gt;\runtimes\tls-client\win\{x64|x32}\tls-client.dll".
    /// </summary>
    private static string ResolveLibraryPath()
    {
        string arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x32",
            var other => throw new PlatformNotSupportedException(
                $"No tls-client native library available for architecture '{other}'."),
        };

        string path = Path.Combine(AppContext.BaseDirectory, "runtimes", "tls-client", "win", arch, "tls-client.dll");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"tls-client native library not found at '{path}'. " +
                "Ensure the TlsClient.Native.win-{arch} NuGet package is referenced and its " +
                "buildTransitive target has copied the DLL next to the app binaries.",
                path);
        }

        return path;
    }
}
