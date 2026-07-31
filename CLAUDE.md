# ClaudeWidget — Project Reference for AI Agents

## What this project is

A Windows 11 **Widgets board widget** that displays claude.ai usage stats
(session %, weekly %, Fable %) in real time. Built with WinUI 3 / Windows App SDK.
Shows animated progress bars, reset countdowns, and a Refresh button.

## Workflow: after every code change

Any time source files are edited, always run all three steps in order, every
time, without waiting to be asked:

```powershell
powershell -File build.ps1
powershell -File deploy.ps1
powershell -File killwidget.ps1
```

`build.ps1` alone does not update the running widget, and `deploy.ps1` alone
does not reliably kill stale COM-server processes (see "Known pitfalls"
below) — skipping either step is the most common cause of "my change isn't
showing up" confusion. Only skip this sequence if the user explicitly says
not to build/deploy yet.

## File map

```
widget2/
  CLAUDE.md                          <- this file
  .gitignore                         <- ignores build output, release/, local.settings.ps1, stray logs
  local.settings.ps1.example         <- template for local.settings.ps1 (checked in)
  local.settings.ps1                 <- gitignored; holds $CertThumbprint for this machine
  build.ps1                          <- dotnet build (Debug, x64, produces MSIX)
  deploy.ps1                         <- sign + install MSIX + restart Widgets sidebar
  package-release.ps1                <- build Release MSIX + wrap into ClaudeWidgetSetup.exe installer
  readlog.ps1                        <- read last N lines of a log category (default: provider)
  checklog.ps1                       <- read last N lines with proper UTF-8 encoding + optional filter
  killwidget.ps1                     <- kill ClaudeWidget.exe process
  ClaudeWidget/
    Program.cs                       <- dual-mode entry point (GUI vs COM server)
    App.xaml / App.xaml.cs           <- WinUI application
    MainWindow.xaml / .cs            <- sign-in / session key entry UI
    MainPage.xaml / .cs              <- main page inside window
    SetLimitWindow.xaml / .cs         <- standalone window for editing the monthly pay-as-you-go spend cap
    Package.appxmanifest             <- MSIX manifest, COM activation, widget definition, resource languages
    Services/
      ClaudeApiService.cs            <- fetches usage from claude.ai API
      TlsClientFetcher.cs            <- primary fetch path: TLS-fingerprint-impersonating HTTP client
      HiddenWebViewFetcher.cs        <- fallback fetch path: hidden WebView2 browser
      ProgressBarImage.cs            <- generates PNG progress bar data URIs
      UsageColors.cs                 <- percent -> color mapping
      Localization.cs                <- EN/RU/UK string table + system-language detection
    Widgets/
      ClaudeUsageWidgetProvider.cs   <- IWidgetProvider COM implementation
      Templates/
        UsageCardTemplate.json       <- Adaptive Card 1.5 template (normal state)
        AuthCardTemplate.json        <- Adaptive Card shown when not signed in
      WidgetHelper/
        ComRegistration.cs           <- low-level COM class factory registration
    Strings/
      en-US/Resources.resw           <- MSIX package description resources (per-language)
      ru-RU/Resources.resw
      uk-UA/Resources.resw
```

## Architecture overview

```
Program.cs
  GUI mode  -> WinUI App -> MainWindow (session key entry / sign-in UI)
  COM mode  -> RunAsWidgetComServer()
                 -> ComRegistration: registers ClaudeUsageWidgetProvider class factory
                 -> blocks on ManualResetEvent until Widgets host releases
                      ClaudeUsageWidgetProvider  (IWidgetProvider)
                        -> ClaudeApiService.FetchProUsageAsync()
                             -> FetchWithFallbackAsync()
                                  -> TlsClientFetcher.FetchJsonAsync()      (primary, cheap)
                                  -> HiddenWebViewFetcher.FetchJsonAsync()  (fallback, heavy, only if primary fails/blocked)
```

## Dual-mode process

`Program.Main` checks args:
- **No args** -> normal WinUI GUI (settings/sign-in window)
- **`-RegisterProcessAsComServer`** -> headless COM server; registers
  `ClaudeUsageWidgetProvider` as COM class factory, blocks until host releases it

Logging goes through `Services/Logger.cs`, a shared best-effort file logger
(`Logger.Info(category, msg)` / `Logger.Error(category, msg)`) used by
`Program.cs`, `ClaudeUsageWidgetProvider.cs`, and `TlsClientFetcher.cs`. Log
files live under `%LOCALAPPDATA%\ClaudeWidget\Logs\{category}.log`
(NOT the Desktop — that was only ever a quick hack for early development):
- `launch.log` - every process launch with args, COM-registration progress
- `provider.log` - widget lifecycle events, fetch results, errors

**Important: MSIX file-system redirection.** Since the widget COM-server
process is a packaged (MSIX) app, `Environment.SpecialFolder.LocalApplicationData`
does NOT resolve to the literal `%LOCALAPPDATA%` on disk — Windows
transparently redirects it to a package-private path:
`%LOCALAPPDATA%\Packages\{PackageFamilyName}\LocalCache\Local\ClaudeWidget\Logs\`.
`checklog.ps1`/`readlog.ps1` resolve the correct `PackageFamilyName` via
`Get-AppxPackage` automatically — always use those scripts (or replicate that
resolution) rather than assuming the literal `%LOCALAPPDATA%\ClaudeWidget\Logs\`
path exists.

**`Logger.Info` is Debug-build-only** (compiled out to a no-op in Release via
an internal `VerboseLoggingEnabled` const) so a widget that refreshes
indefinitely on a timer doesn't grow an unbounded log file on end-user
machines. **`Logger.Error` always writes, in both Debug and Release** — call
sites that log an actual failure (caught exceptions, failed fetches/mutations)
must call `Logger.Error` directly rather than the local `Log(...)` wrapper
(which is Info-only) so they're never silently dropped in a Release build.
When adding a new log call, decide up front which one it should be — routine
tracing vs. an actual error worth surfacing to end users' logs.

## Widget provider (`ClaudeUsageWidgetProvider.cs`)

- Implements `IWidgetProvider` COM interface
- Static `Cache` dict keyed by widget instance ID stores last `UsageResult`
- `DoRefresh()`: if signed in, hands off to `RefreshAsync()`; if not signed in, sends the auth card immediately
- `RefreshAsync()`: sends cached data immediately (dimmed, with a "Refreshing…" status line) while the fetch is in flight, then pushes the real data (or an "update failed" line over stale cached data) once it resolves — no custom shimmer/skeleton animation, the host's own native spinner covers the Refresh button while an `Action.Execute` is pending
- **First-ever refresh (no cache yet)** goes through `BuildErrorData(..., timestampOverride: ...)` instead of `BuildData()` — pass `Localization.Get(Str.Refreshing)` as the `timestampOverride` here so the "last updated" line reads "Refreshing…" instead of either a real (misleadingly-early) timestamp or going blank. `timestampOverride` defaults to the real current time for genuine fetch-failure error states, where showing "last attempted at HH:mm:ss" is still useful.
- `OnActionInvoked` verbs:
  - `"Refresh"`: calls `RefreshAsync()` directly (host shows native spinner until the resulting `UpdateWidget` call)
  - `"OpenApp"`: launches the companion app via the `"claudewidget:"` protocol (sign-in window)
  - `"ToggleCredit"`: calls `ToggleCreditAsync()`, which reads `{"enable": bool}` from `args.Data` and calls `ClaudeApiService.SetOverageEnabledAsync(enable)`, then refreshes
  - `"OpenSetLimit"`: calls `OpenSetLimitWindow()`, which reads `{"value": number}` from `args.Data` and launches `claudewidget://setlimit?value=<amount>` via `Windows.System.Launcher.LaunchUriAsync` — see "Usage credits toggle & monthly limit editor" below
- `BuildData()` / `BuildErrorData()` produce JSON for Adaptive Card data binding — **every new template `${...}` binding needs an entry in BOTH methods**; `BuildErrorData` needs a safe default or the error-card path fails/omits that field
- Refresh button always enabled (no `isEnabled` binding) — WebView2/TlsClient-level fetch serialization (see below) is what prevents overlapping requests, not UI debounce
- **NOTE:** `WidgetUpdateRequestOptions.IsPlaceholderContent` was tried to show the host's native loading skeleton and reverted — it got stuck showing the skeleton permanently even after a later `isPlaceholderContent=false` update succeeded. Do not reintroduce without confirming host behavior first (see remarks on `DoRefresh` in the source).

## Usage credits toggle & monthly limit editor

The widget card lets the user toggle claude.ai's pay-as-you-go "usage credits"
billing on/off and view/edit the monthly spend cap, directly from the card
where the account state supports it.

- **Toggle:** the credit-header value (on/off state) is clickable when `UsageResult.CreditToggleKnown` is true (i.e. claude.ai reported a definite on/off state). Clicking sends the `"ToggleCredit"` verb, which calls `ClaudeApiService.SetOverageEnabledAsync`.
- **Monthly limit editor:** the "Monthly limit" value is clickable **only when usage credits are currently on** (`ExtraUsageEnabled == true`) — if credits are off, the row renders as plain (non-clickable) text. Clicking sends the `"OpenSetLimit"` verb, which launches the standalone `SetLimitWindow` (420×300, in the companion app process) pre-filled with the current limit; Save calls `ClaudeApiService.SetMonthlyCreditLimitAsync` and triggers `RefreshAllActiveWidgetsAsync()` on success.
- **Protocol hand-off plumbing** (widget card runs in the headless COM-server process, which can't host a WinUI window itself):
  - `Program.cs`'s `TryGetProtocolActivationUri()` reads `AppInstance.GetCurrent().GetActivatedEventArgs()` to detect a `"claudewidget:"` protocol launch and extract the activation `Uri`.
  - `App.xaml.cs` takes an optional `Uri? protocolUri`; if `protocolUri.Host == "setlimit"`, it opens `SetLimitWindow` directly instead of the normal `MainWindow`, parsing `?value=` manually (`ParseValueQueryParam`) to avoid a `System.Web`/`UriBuilder` dependency for one parameter.
- **Adaptive Card `selectAction` gotcha (root cause of an early "nothing is clickable" bug):** `selectAction` is only valid on `Container`/`Column`/`ColumnSet`/`Image`/`ActionSet`/the card root — it is silently ignored (not an error) on a bare `TextBlock`. The credit-header and monthly-limit lines are therefore each split into a two-column `ColumnSet` (label column + value column), with `selectAction` only on the value `Column`, not wrapped around a raw `TextBlock`.
- **Label/value split + spacing:** only the *value* half of each line is clickable (not the whole row) — see `CreditHeaderLabel`/`CreditHeaderValue` and `CreditMonthlyLimitLabel`/`CreditMonthlyLimitValue` in `ClaudeApiService.cs`. Both the clickable and non-clickable (disabled/"refreshing") variants of these rows use an **identical `ColumnSet` structure** with the same `spacing: "Small"` on the value column, differing only in whether `selectAction`/accent styling is present — this guarantees pixel-identical alignment regardless of state. Do not try to create the gap with a trailing space in the `TextBlock` text instead — the Adaptive Card renderer trims trailing whitespace, so that approach silently produces no gap.
- **claude.ai's real request shapes** (confirmed via "Copy as cURL" from a real browser session — see `ClaudeApiService.SetOverageEnabledAsync`/`SetMonthlyCreditLimitAsync`):
  - Turn ON (first time): `POST /api/organizations/{orgId}/setup_overage_billing` body `{"org_monthly_spend_limit": <cents>}`
  - Turn OFF / change limit: `PUT /api/organizations/{orgId}/overage_spend_limit` body `{"is_enabled": bool, "monthly_credit_limit": <cents>}` — **note the field name is `monthly_credit_limit` on this endpoint, not `org_monthly_spend_limit`** (that name is only valid on the separate `setup_overage_billing` POST). Sending the wrong field name here was silently accepted (`Status=OK`) with no actual effect — always diff GET responses before/after a mutation to confirm it actually took effect, don't trust HTTP status alone.
- Credits section is **no longer strictly read-only** now that the toggle/limit-editor exist — but any *new* write-capable control here should still confirm the exact request shape from a real browser session first (see the `TlsClientFetcher` "Request shape matters" note below); don't guess payload field names.

## API service (`ClaudeApiService.cs`)

- Credentials in Windows **PasswordVault**, resource `"ClaudeWidget_Auth"`, usernames `"SessionKey"` and `"OrgId"`
- `SanitizeSessionKey()` strips wrapping quotes/backticks users sometimes paste along with the cookie value (defensive — avoids a confusing generic "Invalid authorization" error)
- Endpoints:
  - `GET https://claude.ai/api/organizations` -> pick org with "chat" capability, get UUID
  - `GET https://claude.ai/api/organizations/{orgId}/usage` -> usage buckets (session/weekly/Fable %)
  - `GET https://claude.ai/api/organizations/{orgId}/subscription_details` -> `next_charge_at`, `billing_interval` (best-effort; failures just omit the payment line on the card)
  - `GET https://claude.ai/api/organizations/{orgId}/paused_subscription_details` -> `plan_type` -> mapped to "Pro plan"/"Max plan"/"Team plan"/"Free plan" (best-effort)
  - `GET https://claude.ai/api/organizations/{orgId}/prepaid/credits` -> credit balance (`amount`, in cents, real USD balance — not `balance_credits`, a different coarser unit) + `promo_tranches[]` array (best-effort; `FetchCreditInfoAsync`), each tranche having its own `remaining_amount_minor_units` (cents) + `expires_at` (ISO 8601) — claude.ai can grant several promo tranches at once (e.g. separate signup/referral promos), each expiring independently, so all of them are collected into `List<PromoTranche>` and combined onto a single `UsageResult.PromoCreditsLine`, e.g. `"Promotional credits: $95.40 until 9/19/26, $10.00 until 1/27/27"` (compact 2-digit-year date via `Localization.FormatDateCompact`, not the long locale date, so multiple tranches still fit on one line)
  - `GET https://claude.ai/api/organizations/{orgId}/overage_spend_limit` -> `used_credits` (amount spent this period), `is_enabled` (whether the account's "Enable extra usage" pay-as-you-go setting is currently on), `monthly_credit_limit` (cents) — all read (best-effort); the same endpoint also accepts a mutating PUT, see "Usage credits toggle & monthly limit editor" above
- Usage response fields: `five_hour.utilization`, `seven_day.utilization`, `seven_day_fable.utilization`, each with `resets_at` (ISO 8601)
- `LastError` static string set on any failure (exception type + message + stack trace)
- `LooksLikeBlockedResponse()` detects Cloudflare challenge pages
- **All requests go through `FetchWithFallbackAsync(url, sessionKey)`** — a single private helper every endpoint method calls — which tries `TlsClientFetcher` first and only falls back to `HiddenWebViewFetcher` if that throws or the response looks Cloudflare-blocked. See the two fetcher sections below for why there are two implementations.
- **Credits section is no longer strictly read-only** — see "Usage credits toggle & monthly limit editor" above for the two mutating endpoints (`setup_overage_billing` POST, `overage_spend_limit` PUT) and their confirmed request shapes. Any *new* write-capable control added here should still confirm the exact request shape from a real browser session first (see the `TlsClientFetcher` "Request shape matters" note below for why guessing this is risky).

## Primary fetcher: TLS-fingerprint impersonation (`TlsClientFetcher.cs`)

**Why it exists:** claude.ai's Cloudflare protection doesn't just check the `sessionKey` cookie — it also fingerprints the TLS ClientHello (JA3/JA4-style) and HTTP/2 frame ordering of the connection. A bare .NET `HttpClient` uses .NET's SChannel TLS stack, which produces a different fingerprint than any real browser and gets silently blocked by Cloudflare even with valid cookies (confirmed the hard way — see "History" below). `TlsClientFetcher` solves this using `bogdanfinn/tls-client` (via the `ErenKrt/TlsClient.NET` NuGet wrapper, packages `TlsClient.Native` + `TlsClient.Native.win-x64`/`win-x32`) — a native DLL that replicates a real Chrome TLS ClientHello byte-for-byte using a Go `utls` stack, so Cloudflare's fingerprint check sees "real Chrome" without ever spinning up an actual Chromium process.

- **Memory cost:** single-digit MB (one native DLL loaded in-process) vs. WebView2's 80-100+ MB persistent Chromium process tree.
- **Limitation:** only solves *fingerprint-based* blocking of otherwise-valid API requests — does NOT solve a JS-challenge page ("Just a moment...", requires executing a JS puzzle). Cloudflare currently doesn't serve JS challenges for these specific claude.ai API endpoints (only the fingerprint check applies here). If that ever changes, `HiddenWebViewFetcher` is the automatic fallback (see `ClaudeApiService.FetchWithFallbackAsync`).
- **Request shape matters:** confirmed via a real Chrome request ("Copy as cURL" in DevTools) that claude.ai's backend accepts a bare `sessionKey` cookie with a *document-navigation*-style header set (`sec-fetch-dest: document`, `sec-fetch-mode: navigate`, `sec-fetch-site: none`, no Origin/Referer) — a fetch()-style header set (Origin/Referer, `sec-fetch-site: same-origin`, fabricated `anthropic-client-*`/device-id cookies) got a genuine `account_session_invalid` rejection from claude.ai's own backend, not a Cloudflare block. `TlsClientFetcher` mirrors the confirmed-working navigation request shape exactly (see `Headers`/`HeaderOrder` in the source) rather than guessing.
- **Cookie gotcha:** the `tls-client` library's `RequestCookies`/cookie-jar API (`TlsClientCookie`) silently fails to actually send the cookie to the server — every request sent that way came back `account_session_invalid`, identical to sending no cookie, regardless of `Domain` value. Setting a raw `cookie` header instead (`["cookie"] = $"sessionKey={sessionKey}"`) works immediately. Do not switch back to the cookie-jar API without re-verifying against a real account.
- `TlsClientIdentifier.Chrome146` — must be kept in sync with the `Chrome`-version numbers in `ChromeUserAgent`/`ChromeSecChUa` (mismatched TLS-identifier vs. User-Agent version is itself a bot-detection signal).
- `NativeTlsClient.RequestAsync` reports transport failures (DNS, TLS handshake, timeout, native lib errors) as `Status == 0` with the error in `Body` instead of throwing — `TlsClientFetcher` converts that into a thrown exception so `FetchWithFallbackAsync` triggers the WebView2 fallback correctly.
- `ResolveLibraryPath()` looks for `tls-client.dll` at `<output>\runtimes\tls-client\win\{x64|x32}\tls-client.dll` — see the `.csproj` note below for why this needs an explicit `<Content>` item instead of relying on the NuGet package's own `buildTransitive` copy behavior.

## Fallback fetcher: hidden WebView2 (`HiddenWebViewFetcher.cs`)

**Why it exists:** kept as a fallback in case `TlsClientFetcher`'s fingerprint impersonation ever stops working against Cloudflare (e.g. Cloudflare starts serving an actual JS challenge for these endpoints) — a real Chromium engine passes any Cloudflare JS challenge automatically because it actually executes the challenge JS, which no amount of TLS/header spoofing can replicate.

- Spins a dedicated **STA thread** with real Win32 `GetMessage`/`DispatchMessage` loop (required by WebView2)
- Creates hidden `CoreWebView2Controller` on that thread via a real HWND
- `WebViewThreadSyncContext` marshals `await` continuations back to the STA thread
- `FetchJsonAsync()` serializes all calls via `SemaphoreSlim(1,1)` — one navigation at a time
- Per fetch: set cookie -> `Navigate(url)` -> await `NavigationCompleted` -> `ExecuteScriptAsync` to read `document.body.innerText`
- Retries once on failure with 500ms delay
- **Single WebView2 instance is created lazily and kept alive for the process lifetime** (not torn down between fetches) — intentional, see "History" below before trying to change it. Since `TlsClientFetcher` is now the primary path, this fallback only pays its memory cost when actually invoked (i.e. rarely) rather than for the whole process lifetime like it used to when it was the only fetcher.
- `CoreWebView2EnvironmentOptions.AdditionalBrowserArguments` disables GPU compositing, extensions, crash reporter, and background networking (`--disable-gpu --disable-gpu-compositing --disable-software-rasterizer --disable-extensions --disable-background-networking --disable-crash-reporter`) — safe trims since this WebView2 is never shown (`controller.IsVisible = false`), applied without breaking Cloudflare's check

### Critical implementation details

**Store the controller, not just the webview:**
Both `s_controller` (CoreWebView2Controller) AND `s_webView` (CoreWebView2) must be stored as static fields.
If `s_controller` is GC'd, it closes/invalidates `s_webView` and all access throws `COMException ERROR_INVALID_STATE` (0x8007139F).

**Wait for internal about:blank nav before first use:**
After `CreateCoreWebView2ControllerAsync` returns, WebView2 immediately starts an internal navigation to `about:blank`.
We subscribe to `NavigationCompleted` and wait for it before setting `s_webView` (marking ready).
Calling `Navigate()` while that init nav is in progress also throws `ERROR_INVALID_STATE`.

### History: how this two-fetcher design was arrived at

1. **Original state:** `HiddenWebViewFetcher` was the *only* fetch path, kept alive for the whole process lifetime — reliable, but cost 80-100+ MB of persistent WebView2/Chromium child processes (GPU process, network service, storage service, crashpad handler) for a widget that just needs a handful of small JSON responses.
2. **Tried and reverted — cookie harvest + plain HttpClient:** rewrote `HiddenWebViewFetcher` to spin up transiently (create → navigate → harvest `cf_clearance`+other cookies → `controller.Close()` → dispose) and routed real API calls through a plain `HttpClient` presenting the harvested cookies. **Broke completely** — widget got stuck on "Loading…"/0% indefinitely with no exceptions logged, because Cloudflare's block was silent (`LooksLikeBlockedResponse` kept returning true). Root cause: Cloudflare's bot detection correlates the TLS/HTTP fingerprint of the *original* challenge-passing connection with subsequent requests — a bare HttpClient's SChannel fingerprint doesn't match the WebView2/Chromium fingerprint that earned the cookie, so the cookie alone doesn't survive replay on a different client. Fully reverted to the always-on WebView2 model.
3. **Current state — TLS-fingerprint impersonation via `TlsClientFetcher`:** instead of trying to replay a *harvested* cookie on a mismatched fingerprint, replicate the fingerprint itself using `bogdanfinn/tls-client`'s `utls`-based Chrome TLS impersonation. This directly solves the root cause identified in step 2 (matching fingerprint, not just matching cookie) without needing WebView2 at all for the common case — WebView2 is now purely a fallback via `ClaudeApiService.FetchWithFallbackAsync`, invoked only if `TlsClientFetcher` fails or gets blocked.

**Do not** attempt the cookie-harvest-then-plain-HttpClient approach from step 2 again — it's a fundamentally different (and already-disproven) strategy from the TLS-impersonation approach in step 3. If `TlsClientFetcher` itself ever needs revisiting (e.g. Cloudflare starts detecting `utls`-based impersonation too), the fallback-to-WebView2 design already handles graceful degradation; consider only tightening/updating the TLS identifier or header set, not reintroducing a harvest-and-replay design.

## Localization (`Localization.cs`)

- Ships **English (default), Russian, Ukrainian**. `Localization.Current` is detected once per process from `Windows.System.UserProfile.GlobalizationPreferences.Languages[0]` (the top of the user's Windows display-language list), matched by `ru`/`uk` prefix; anything else (including no match) falls back to `En`.
- `Localization.Culture` — a matching `CultureInfo` (`ru-RU`/`uk-UA`/`en-US`) used for locale-correct date/time formatting (weekday/month names, date ordering) wherever the app formats a `DateTime`, e.g. `UsageResult.WeeklyResetAtText`, `NextPaymentLine` in `ClaudeApiService.cs`.
- **Custom .NET date format strings do NOT get reordered by `CultureInfo` alone** — a fixed pattern like `"MMM d, yyyy"` always renders month-first regardless of culture. `Localization.cs` provides three helpers that pick a different literal pattern per language instead of relying on `CultureInfo` to reorder tokens: `FormatDateShort` (`"MMM d, yyyy"` En / `"d MMM yyyy"` Ru/Uk), `FormatDateLong` (full month name equivalents), `FormatWeekdayDateTime` (weekday + short date + time, used for session/weekly/Fable reset timestamps), and `FormatDateCompact` (2-digit-year numeric date — `"M/d/yy"` En / `"dd.MM.yy"` Ru/Uk — used where several dates need to fit on one line, e.g. `UsageResult.PromoCreditsLine`). Always use one of these instead of a hand-rolled `.ToString("...", Localization.Culture)` call.
- All user-facing text (widget card labels, companion app window text, API error messages) is a `Str` enum member resolved via `Localization.Get(Str.Key)` / `Get(Str.Key, args)` — deliberately **not** resw/x:Uid-based, because the widget card's text is generated at runtime as Adaptive Card JSON (not XAML) and can only ever be resolved in code anyway; using one lookup table for both the widget card and the companion window's code-behind keeps both surfaces on a single language-detection path.
- Countdown/reset text (`FormatCountdown` in `ClaudeApiService.cs`) uses localized, spaced time-unit suffixes via `Str.HourUnit`/`MinuteUnit`/`DayUnit` (`h`/`m`/`d` for EN, `ч`/`мин`/`д` for RU, `г`/`хв`/`д` for UK) — e.g. `"4 ч 26 мин"`, not `"4ч26мин"`.
- Billing interval text (`"Next payment: ... (monthly)"`) is localized via `LocalizeBillingInterval()` in `ClaudeApiService.cs`, which maps the API's raw `billing_interval` string (`month`/`monthly`/`year`/etc.) to a `Str.Billing*` key rather than displaying the raw English value.
- The `Strings/{en-US,ru-RU,uk-UA}/Resources.resw` files are separate from `Localization.cs` — they only supply the MSIX **package description** resources (`AppDescription`, `WidgetDescription` in `Package.appxmanifest`), which Windows resolves via its own resource-indexing (`resources.pri`) independent of the in-app `Str` table. `Package.appxmanifest`'s `<Resources>` list must explicitly enumerate `en-US`/`ru-RU`/`uk-UA` (not `x-generate`) for `makepri` to index all three.
- To change wording, edit the three `Table[AppLanguage.*]` dictionaries directly in `Localization.cs` — every `Str` key must have an entry in all three tables (compile error if one's missing, by design, so a missing translation can't silently fall through at runtime).

## Repo setup: `local.settings.ps1` (one-time, per machine)

`deploy.ps1` and `package-release.ps1` both sign the MSIX with a local dev
certificate (subject `CN=ClaudeUsageWidgetLocal`) and need that cert's
thumbprint. The thumbprint is machine-specific (regenerating the cert changes
it) and is **not** committed — it lives in `local.settings.ps1`, which is
gitignored, and both scripts dot-source it and `throw` immediately with a
clear message if it's missing.

**First-time setup on a new machine/clone:**
```powershell
Copy-Item local.settings.ps1.example local.settings.ps1
# then edit local.settings.ps1 and set $CertThumbprint to the thumbprint of
# your local CN=ClaudeUsageWidgetLocal cert (must be trusted machine-wide in
# Cert:\LocalMachine\TrustedPeople — see the "Certificate thumbprint matters"
# note under deploy.ps1's steps below for how to find/verify it).
```
`build.ps1`, `readlog.ps1`, `checklog.ps1`, and `killwidget.ps1` don't need
this file — only the two signing scripts do.

## Build & deploy

```powershell
# Build (from widget2/)
powershell -File build.ps1

# Deploy (sign, uninstall old, install new, restart Widgets sidebar)
powershell -File deploy.ps1

# Kill old process if it didn't pick up new binary
powershell -File killwidget.ps1
```

MSIX output: `D:\BuildCache\ClaudeWidget\bin\x64\Debug\net8.0-windows10.0.26100.0\win-x64\ClaudeWidget_1.0.0.0_x64_Debug.msix` (this is the loose per-architecture MSIX `deploy.ps1` actually signs/installs — see note below on why the `AppPackages\` path is not used)

### What the build step actually does

`dotnet build` on `ClaudeWidget.csproj` with those flags:
- `-c Debug` — Debug configuration (no `PublishReadyToRun`/`PublishTrimmed`, per the csproj's `PropertyGroup` guarding those on `Configuration != Debug`).
- `-p:Platform=x64 -p:RuntimeIdentifier=win-x64` — **must both be set together**; WinUI 3/Windows App SDK projects are platform-specific (x86/x64/ARM64), and a mismatched `RuntimeIdentifier` vs `Platform` causes obscure restore/build failures. x64 is the only platform this project has been built/tested with.
- `-p:GenerateAppxPackageOnBuild=true` — in addition to the normal `bin\` DLL/EXE output, also produces an MSIX package. Historically this landed under `AppPackages\`, but after a clean `bin`/`obj`/`AppPackages` wipe that folder can lose its `_x64` segment and only contain a `.msixbundle` (not a loose per-architecture `.msix`) — see the note below and in `deploy.ps1` itself.
- `-p:AppxPackageSigningEnabled=false` — skips signing during the build itself; signing happens explicitly afterward in `deploy.ps1` via `signtool.exe` with the project's local test cert, not as part of `dotnet build`.
- `-p:UapAppxPackageBuildMode=SideloadOnly` — packages for sideloading (no Store association needed), appropriate for a locally-signed dev cert install rather than a Store submission.

Output lands in (at least) one reliable place:
- Raw build + loose MSIX: `D:\BuildCache\ClaudeWidget\bin\x64\Debug\net8.0-windows10.0.26100.0\win-x64\` — contains `ClaudeWidget.dll` (+ exe, deps) **and** `ClaudeWidget_1.0.0.0_x64_Debug.msix`. This loose per-architecture `.msix` is the file `deploy.ps1` signs and installs.
- `AppPackages\` may also contain a build output (e.g. a `.msixbundle`), but its exact folder name and contents have changed across clean rebuilds (see `deploy.ps1`'s inline comment) — don't rely on a hardcoded `AppPackages\...` path; use the `bin\x64\Debug\...\win-x64\` loose `.msix` instead.

### What `deploy.ps1` actually does (step by step)

1. **Locate signtool** — searches a couple of known NuGet cache paths for `signtool.exe` (from the `Microsoft.Windows.SDK.BuildTools` package), falling back to a recursive `Get-ChildItem` search under `D:\BuildCache\NuGetPackages\microsoft.windows.sdk.buildtools` if the hardcoded paths don't match (e.g. after a SDK version bump).
2. **Sign the MSIX** — `signtool sign /fd SHA256 /sha1 $certThumbprint /s My $msix`, signing directly from the certificate already installed in `Cert:\CurrentUser\My` (subject `CN=ClaudeUsageWidgetLocal`, matching `Package.appxmanifest`'s `Publisher`) rather than a `.pfx` file on disk. This is why `AppxPackageSigningEnabled=false` was used at build time — signing is done here instead. `$certThumbprint` comes from dot-sourcing gitignored `local.settings.ps1` (see "Repo setup" above) rather than being hardcoded in the script. Exits early (`exit 1`) if `signtool` fails.
   - **Original approach (`.pfx` file) is gone/broken:** the script used to reference a `.pfx` at `C:\Users\zinke\Desktop\widget\package\ClaudeUsageWidgetLocal.pfx`, which no longer exists on this machine — `signtool sign /f ... /p ...` failed with "Store IsDiskFile() failed." Fixed by signing straight from the cert store instead.
   - **Certificate thumbprint matters — must be the one trusted machine-wide.** Two `CN=ClaudeUsageWidgetLocal` certs can exist in `Cert:\CurrentUser\My` with different thumbprints (e.g. from being regenerated at different times). `Add-AppxPackage` requires the signing cert to be trusted specifically in `Cert:\LocalMachine\TrustedPeople` — signing with a thumbprint that's only in `Cert:\CurrentUser\TrustedPeople` (not machine-wide) will sign successfully but then fail install with `HRESULT 0x800B0109` ("cert chain terminated in an untrusted root"/untrusted publisher). Verify which thumbprint is machine-trusted with `Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where Subject -match ClaudeUsageWidgetLocal` and make sure `deploy.ps1`'s `$certThumbprint` matches it exactly.
3. **Kill old process** — `Stop-Process -Force` on any running `ClaudeWidget` process, so the subsequent package removal/install isn't blocked by file locks on the old binary.
4. **Remove old package** — `Remove-AppxPackage` for the `ClaudeWidget` package family, uninstalling whatever version is currently registered (this is also why the MSIX version can stay fixed at `1.0.0.0` across dev iterations — see "Known pitfalls" below).
5. **Install new package** — `Add-AppxPackage -Path $msix -ForceApplicationShutdown`, sideloading the freshly-signed MSIX. `-ForceApplicationShutdown` closes any remaining instance of the app that might otherwise block install.
6. **Restart Widgets sidebar** — kills the `Widgets` and `WidgetService` processes (Windows 11's Widgets Board host processes), forcing Windows to relaunch them fresh so they pick up the newly-registered widget provider/COM registration instead of continuing to talk to a stale one.

### What `killwidget.ps1` does and why it's still needed after `deploy.ps1`

`deploy.ps1` step 3 already kills `ClaudeWidget` by exact process name match (`Get-Process ClaudeWidget`), and step 6 restarts the Widgets host processes — in theory that should be enough. In practice, the Widgets host sometimes launches a **new COM-server process instance that still ends up running old cached code** (observed empirically; exact cause not fully diagnosed — possibly a race between the host restart and Windows' AppX package cache, or the host reusing an activation from just before the uninstall/reinstall completed). `killwidget.ps1` does a broader `Get-Process | Where-Object { $_.Name -like '*ClaudeWidget*' }` sweep and force-kills anything matching, as a blunt guarantee that no old-binary process survives — always run it after `deploy.ps1`, not just when something looks stale.

After `killwidget.ps1`, the next widget `Activate`/`CreateWidget` call from the Widgets host will spin up a fresh COM-server process from the newly-installed MSIX, guaranteed to run the latest build.

## Debugging

```powershell
# Read log (UTF-8 safe)
powershell -File checklog.ps1

# Read last N lines
powershell -File readlog.ps1 -Last 40
```

**Common errors in log:**
- `COMException ERROR_INVALID_STATE` on `CookieManager` or `Navigate` -> controller GC'd or init nav still in progress
- `Fetch returned null` -> check `LastError` on same line for reason
- `Cloudflare` / `Just a moment` -> session key expired or Cloudflare blocked request

## Key GUIDs

- Widget provider COM GUID: `2494E3C1-531B-41C8-87CB-E68A4A4F2F7F` (in `ClaudeUsageWidgetProvider.cs` and `Package.appxmanifest`)
- Widget definition ID: `ClaudeUsageWidget` (in `Package.appxmanifest` and `ClaudeUsageWidgetProvider.cs`)

## Known pitfalls for AI agents

- **Never use `$_` or `$()` in `-Command` strings passed to `powershell.exe` via the shell** — the shell eats them. Always use `-File script.ps1` with named parameters instead.
- **Use the `powershell` command, not `pwsh`.** This repo's scripts are written for and tested against Windows PowerShell 5.1 (`powershell.exe`). Invoking them via `pwsh` (PowerShell 7) instead has caused subtle inconsistencies (different console output encoding/formatting, `ANSI` escape codes leaking into captured output, redirection/`nul`-file quirks). Always run `powershell -File script.ps1 ...`, never `pwsh -File script.ps1 ...`, for every script in this repo (`deploy.ps1`, `killwidget.ps1`, `checklog.ps1`, `readlog.ps1`).
- **`deploy.ps1`'s live/streamed output is unreliable through the agent's terminal tool** — with a live-running pty, output can appear truncated/cut off mid-line (e.g. only the first couple lines show even though the script ran to completion) or a command wrapping it in `2>&1 | Out-String` / redirecting to a file can get interrupted. This is a display/capture quirk of the tool, not a script failure. To reliably check the result of `deploy.ps1` (or any script whose full output matters), redirect to a log file and read it back in a **separate** tool call: `powershell -File deploy.ps1 > deploy_out.txt 2>&1` then `powershell -File checklog.ps1` won't work for this (that's for the widget provider log) — instead just `Read`/`type` `deploy_out.txt` directly afterward. Don't pipe `deploy.ps1`'s own output through `Out-String -Width` or similar in the same invocation; keep it a plain redirect.
- **After deploy, always kill old process** (`killwidget.ps1`) — the sidebar restart doesn't always reload the COM server binary.
- **Log encoding** — always read log files with `-Encoding UTF8` or via `checklog.ps1`/`readlog.ps1`, otherwise Russian/Ukrainian text appears garbled.
- **Log location changed from Desktop to `%LOCALAPPDATA%\ClaudeWidget\Logs\`** — if you're debugging an older build or a user references `widget_launch_log.txt`/`widget_provider_log.txt` on their Desktop, that's a pre-Logger.cs build; current builds write `launch.log`/`provider.log` under `%LOCALAPPDATA%\ClaudeWidget\Logs\` instead.
- **`Logger.Info` is compiled out in Release builds** — if a widget bug only reproduces on an end-user's machine and `provider.log`/`launch.log` looks sparse, remember only `Logger.Error` calls survive there; routine tracing only exists in local Debug builds. Don't mistake a short Release log for "nothing happened."
- **Build platform must be x64** — always pass `-p:Platform=x64 -p:RuntimeIdentifier=win-x64`.
- **MSIX version is fixed at 1.0.0.0** — deploy script uninstalls the old package first, so this is fine for dev.
- **`AppPackages\` output path/naming is unstable across clean rebuilds** — don't hardcode a path under it. `deploy.ps1` signs/installs the loose `.msix` from `bin\x64\Debug\...\win-x64\` instead, which is reliably produced regardless of how `AppPackages\`'s folder naming shifts.
- **Use `powershell -File build.ps1`, not `pwsh`** — same rule as the other scripts in this repo.
- **`local.settings.ps1` is required and gitignored** — a fresh clone won't have it; `deploy.ps1`/`package-release.ps1` `throw` with a copy-paste-able fix (`Copy-Item local.settings.ps1.example local.settings.ps1`) if it's missing rather than failing on an undefined `$CertThumbprint`. Never hardcode the thumbprint back into the scripts or commit `local.settings.ps1` itself.
- **`.gitignore` covers `bin/`, `obj/`, `AppPackages/`, `BundleArtifacts/`, and `release/`** (all regenerated by `build.ps1`/`package-release.ps1`) plus stray logs (`*.log`, `diag_log*.txt`) and the Windows redirection-typo artifact `nul`. If one of these reappears in `git status`, it's build output that shouldn't be staged, not a sign something's wrong.