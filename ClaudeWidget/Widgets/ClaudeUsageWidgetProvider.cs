using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using ClaudeWidget.Services;
using Microsoft.Windows.Widgets;
using Microsoft.Windows.Widgets.Providers;

namespace ClaudeWidget.Widgets;

[ComVisible(true)]
[ComDefaultInterface(typeof(IWidgetProvider))]
[Guid("2494E3C1-531B-41C8-87CB-E68A4A4F2F7F")]
public sealed class ClaudeUsageWidgetProvider : IWidgetProvider
{
    public const string DefinitionId = "ClaudeUsageWidget";

    /// <summary>
    /// Last successfully-fetched usage per widget instance. Used so a
    /// refresh (whether triggered by the Refresh button, widget
    /// activation, or a periodic host refresh) can keep showing the
    /// previous numbers while the new fetch is in flight, instead of
    /// resetting everything to 0%/"—" or jumping to a different card
    /// layout.
    /// </summary>
    private static readonly ConcurrentDictionary<string, UsageResult> Cache = new();

    // Inline template — always available even if the file can't be read.
    // All labels are ${...} bindings supplied by BuildData/BuildErrorData, so
    // this fallback stays localized the same way as the real template file.
    private const string FallbackTemplate = """
        {
          "type": "AdaptiveCard",
          "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
          "version": "1.5",
          "body": [
            {
              "type": "TextBlock",
              "text": "${currentSessionLabel}",
              "size": "Small",
              "isSubtle": true,
              "spacing": "None"
            },
            {
              "type": "FactSet",
              "spacing": "Medium",
              "facts": [
                { "title": "${usedLabel}", "value": "${sessionPercent}%" },
                { "title": "${resetLabel}", "value": "${sessionResetLine}" }
              ]
            },
            {
              "type": "TextBlock",
              "text": "${lastUpdated}",
              "size": "Small",
              "isSubtle": true,
              "horizontalAlignment": "Right",
              "spacing": "Medium"
            }
          ],
          "actions": [
            {
              "type": "Action.Execute",
              "id": "RefreshProWidget",
              "title": "${refreshLabel}",
              "verb": "Refresh"
            }
          ]
        }
        """;

    public void CreateWidget(WidgetContext widgetContext)   { Log($"CreateWidget: id={widgetContext.Id}"); DoRefresh(widgetContext); }
    public void Activate(WidgetContext widgetContext)       { Log($"Activate: id={widgetContext.Id}"); DoRefresh(widgetContext); }
    public void Deactivate(string widgetId)                { Log($"Deactivate: id={widgetId}"); }
    public void DeleteWidget(string widgetId, string customState) { Log($"DeleteWidget: id={widgetId}"); Cache.TryRemove(widgetId, out _); }

    /// <summary>
    /// Pushes a fresh update to every currently-active instance of this widget.
    /// Call this from the main app process right after sign-in/sign-out so the
    /// widget doesn't wait for its own refresh timer to notice the auth state
    /// changed.
    /// </summary>
    public static async Task RefreshAllActiveWidgetsAsync()
    {
        try
        {
            var infos = WidgetManager.GetDefault().GetWidgetInfos();
            foreach (var info in infos)
            {
                if (info.WidgetContext.DefinitionId != DefinitionId) continue;
                Log($"RefreshAllActiveWidgetsAsync: refreshing widgetId={info.WidgetContext.Id}");

                if (!ClaudeApiService.IsSignedIn())
                {
                    // Signed out — drop any cached usage so a later sign-in
                    // doesn't briefly flash stale numbers.
                    Cache.TryRemove(info.WidgetContext.Id, out _);
                }

                await RefreshAsync(info.WidgetContext.Id, info.WidgetContext.Size);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("provider", $"RefreshAllActiveWidgetsAsync EXCEPTION: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public void OnActionInvoked(WidgetActionInvokedArgs args)
    {
        Log($"OnActionInvoked: verb={args.Verb}");
        if (args.Verb == "Refresh")
        {
            // RefreshAsync sends a "Refreshing…" update over cached data
            // right away, then the real data once the fetch resolves. The
            // host also keeps the Refresh button's own spinner active until
            // we respond to this Action.Execute with an UpdateWidget call.
            _ = RefreshAsync(args.WidgetContext.Id, args.WidgetContext.Size);
        }
        else if (args.Verb == "OpenApp")
        {
            LaunchApp();
        }
        else if (args.Verb == "ToggleCredit")
        {
            _ = ToggleCreditAsync(args.WidgetContext.Id, args.WidgetContext.Size, args.Data);
        }
        else if (args.Verb == "OpenSetLimit")
        {
            OpenSetLimitWindow(args.Data);
        }
    }

    private static void LaunchApp()
    {
        try
        {
            // Launch the main app window so the user can enter their session key.
            var uri = new Uri("claudewidget:");
            _ = Windows.System.Launcher.LaunchUriAsync(uri);
        }
        catch (Exception ex) { Logger.Error("provider", $"LaunchApp failed: {ex.Message}"); }
    }

    private static async Task ToggleCreditAsync(string widgetId, WidgetSize size, string data)
    {
        try
        {
            bool enable = true;
            try
            {
                using var doc = JsonDocument.Parse(data);
                if (doc.RootElement.TryGetProperty("enable", out var enableEl))
                {
                    enable = enableEl.ValueKind == JsonValueKind.True ||
                             (enableEl.ValueKind == JsonValueKind.String && bool.TryParse(enableEl.GetString(), out var parsed) && parsed);
                }
            }
            catch (JsonException ex)
            {
                Logger.Error("provider", "ToggleCreditAsync: could not parse action data: " + ex.Message);
            }

            Log("ToggleCreditAsync: widgetId=" + widgetId + ", enable=" + enable);
            bool ok = await ClaudeApiService.SetOverageEnabledAsync(enable);
            if (!ok)
            {
                Logger.Error("provider", "ToggleCreditAsync: SetOverageEnabledAsync failed, LastError: " + (ClaudeApiService.LastError ?? "(none)"));
            }

            await RefreshAsync(widgetId, size);
        }
        catch (Exception ex)
        {
            Logger.Error("provider", "ToggleCreditAsync EXCEPTION: " + ex.GetType().Name + ": " + ex.Message);
            await RefreshAsync(widgetId, size);
        }
    }

    /// <summary>
    /// Launches the companion app's dedicated <see cref="SetLimitWindow"/> so
    /// the user can adjust the monthly pay-as-you-go spend cap in a proper
    /// window instead of a cramped inline input+button row on the widget
    /// card itself (there isn't enough room there). Runs in the headless
    /// COM-server process, so it hands off to the main app process the same
    /// way <see cref="LaunchApp"/> does for sign-in, via the
    /// "claudewidget:" protocol — just with a "setlimit" host and the
    /// current limit passed as a query parameter so the window opens
    /// pre-filled instead of blank.
    /// </summary>
    private static void OpenSetLimitWindow(string data)
    {
        try
        {
            double value = 0;
            try
            {
                using var doc = JsonDocument.Parse(data);
                if (doc.RootElement.TryGetProperty("value", out var valueEl))
                {
                    if (valueEl.ValueKind == JsonValueKind.Number)
                    {
                        value = valueEl.GetDouble();
                    }
                    else if (valueEl.ValueKind == JsonValueKind.String &&
                             double.TryParse(valueEl.GetString(), out var parsed))
                    {
                        value = parsed;
                    }
                }
            }
            catch (JsonException ex)
            {
                Logger.Error("provider", "OpenSetLimitWindow: could not parse action data: " + ex.Message);
            }

            string valueText = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var uri = new Uri($"claudewidget://setlimit?value={Uri.EscapeDataString(valueText)}");
            Log("OpenSetLimitWindow: launching " + uri);
            _ = Windows.System.Launcher.LaunchUriAsync(uri);
        }
        catch (Exception ex) { Logger.Error("provider", $"OpenSetLimitWindow failed: {ex.Message}"); }
    }

    public void OnWidgetContextChanged(WidgetContextChangedArgs args) => DoRefresh(args.WidgetContext);

    // Fire-and-forget with synchronous fallback so the widget always gets
    // at least one UpdateWidget call even if async explodes.
    //
    // Routine tracing goes through Logger.Info (Debug builds only); call
    // sites that log an actual failure (caught exceptions, failed
    // fetches/mutations) call Logger.Error directly instead of this helper
    // so they're always captured, including in Release builds.
    private static void Log(string msg) => Logger.Info("provider", msg);

    /// <summary>
    /// Used for lifecycle events (widget created/activated/context changed)
    /// that aren't tied to a pending Action.Execute. Hands off to
    /// <see cref="RefreshAsync"/>, which keeps showing cached data (with a
    /// "Refreshing…" status line) while the fetch is in flight.
    /// </summary>
    /// <remarks>
    /// NOTE: <see cref="WidgetUpdateRequestOptions.IsPlaceholderContent"/>
    /// was tried here to let the Widgets host show its own native loading
    /// skeleton, but in practice the host got stuck showing the skeleton
    /// permanently even after a later isPlaceholderContent=false update
    /// succeeded — so it's deliberately NOT used. Do not reintroduce it
    /// without confirming the host's behavior first.
    /// </remarks>
    private static void DoRefresh(WidgetContext context)
    {
        Log($"DoRefresh called, widgetId={context.Id}, definitionId={context.DefinitionId}");

        if (!ClaudeApiService.IsSignedIn())
        {
            Log("Not signed in — sending auth card immediately");
            Cache.TryRemove(context.Id, out _);
            SendUpdate(context.Id, GetAuthTemplate(), BuildAuthData());
            return;
        }

        _ = RefreshAsync(context.Id, context.Size);
    }

    private static async Task RefreshAsync(string widgetId, WidgetSize size)
    {
        try
        {
            if (!ClaudeApiService.IsSignedIn())
            {
                Log("Not signed in — sending auth card");
                Cache.TryRemove(widgetId, out _);
                SendUpdate(widgetId, GetAuthTemplate(), BuildAuthData());
                return;
            }

            // Show cached data immediately (dimmed to gray, with a
            // "Refreshing…" status line) while the fetch below is in
            // flight. Deliberately NOT using IsPlaceholderContent=true here
            // — see remarks on DoRefresh.
            bool isLarge = size == WidgetSize.Large;

            UsageResult? cached = Cache.TryGetValue(widgetId, out var c) ? c : null;
            string placeholderData = cached.HasValue
                ? BuildData(cached.Value, isLarge, statusOverride: Localization.Get(Str.Refreshing), dim: true)
                : BuildErrorData(Localization.Get(Str.Loading), isLarge, timestampOverride: Localization.Get(Str.Refreshing));

            SendUpdate(widgetId, GetTemplate(), placeholderData);

            var usage = await ClaudeApiService.FetchProUsageAsync();
            string data;

            if (usage.HasValue)
            {
                Cache[widgetId] = usage.Value;
                data = BuildData(usage.Value, isLarge);
            }
            else if (Cache.TryGetValue(widgetId, out var cachedAfter))
            {
                Logger.Error("provider", $"Fetch returned null — LastError: {ClaudeApiService.LastError ?? "(none)"}");
                // Fetch failed — keep showing the last known values rather
                // than resetting everything to 0%/"—".
                data = BuildData(cachedAfter, isLarge, statusOverride: Localization.Get(Str.UpdateFailedShowingCached));
            }
            else
            {
                Logger.Error("provider", $"Fetch returned null, no cache — LastError: {ClaudeApiService.LastError ?? "(none)"}");
                data = BuildErrorData(Localization.Get(Str.ErrorFetchingData), isLarge);
            }

            SendUpdate(widgetId, GetTemplate(), data);
        }
        catch (Exception ex)
        {
            Logger.Error("provider", $"RefreshAsync EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            // Last-ditch: send error state (or cached state) so the widget
            // doesn't stay in the "Refreshing…" state forever.
            bool isLarge = size == WidgetSize.Large;
            string data = Cache.TryGetValue(widgetId, out var cached)
                ? BuildData(cached, isLarge, statusOverride: Localization.Get(Str.UpdateFailedShowingCached))
                : BuildErrorData(Localization.Get(Str.ProviderError), isLarge);
            SendUpdate(widgetId, FallbackTemplate, data);
        }
    }

    private static void SendUpdate(string widgetId, string template, string data)
    {
        try
        {
            Log($"SendUpdate: widgetId={widgetId}");
            WidgetManager.GetDefault().UpdateWidget(new WidgetUpdateRequestOptions(widgetId)
            {
                Template = template,
                Data = data,
            });
            Log("SendUpdate: UpdateWidget call succeeded");
        }
        catch (Exception ex)
        {
            Logger.Error("provider", $"SendUpdate EXCEPTION: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string GetTemplate()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Widgets", "Templates", "UsageCardTemplate.json");
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        catch { }
        return FallbackTemplate;
    }

    private static string GetAuthTemplate()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Widgets", "Templates", "AuthCardTemplate.json");
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        catch { }
        // Inline auth fallback
        return """
            {
              "type": "AdaptiveCard",
              "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
              "version": "1.5",
              "body": [
                { "type": "TextBlock", "text": "${signInRequiredLabel}", "weight": "Bolder", "size": "Small", "spacing": "None" },
                { "type": "TextBlock", "text": "${signInBodyLabel}", "size": "Small", "wrap": true, "spacing": "Medium" }
              ],
              "actions": [
                { "type": "Action.Execute", "verb": "OpenApp", "title": "${openAppToSignInLabel}" }
              ]
            }
            """;
    }

    /// <summary>Data payload for the auth ("sign in required") card, localized to the system language.</summary>
    private static string BuildAuthData()
    {
        return JsonSerializer.Serialize(new
        {
            signInRequiredLabel = Localization.Get(Str.SignInRequired),
            signInBodyLabel = Localization.Get(Str.SignInBody),
            openAppToSignInLabel = Localization.Get(Str.OpenAppToSignIn),
        });
    }

    /// <summary>Neutral gray used for bars/percent text while a refresh is in flight.</summary>
    private const string DimHex = "#6B7280"; // gray-500

    /// <summary>Section-divider rule color — darker, muted gray so it doesn't
    /// read as a stark white/host-default separator on the dark card background.</summary>
    private const string SeparatorHex = "#5A5A5A";
    private static readonly string SeparatorUri = ProgressBarImage.BuildSeparatorDataUri(SeparatorHex);

    /// <summary>
    /// Builds the data payload for the card: a PNG progress-bar image
    /// (rendered by <see cref="ProgressBarImage"/>, colored via threshold)
    /// plus percent and a combined "Resets in X, at Y" line for each of
    /// session/weekly/Fable.
    /// </summary>
    /// <param name="dim">
    /// When true, bars and percent text are rendered in neutral gray
    /// instead of their usual threshold colors, and the percent text is
    /// marked subtle — used for the "Refreshing…" frame shown while a
    /// fetch is in flight, so the (possibly stale) numbers read as
    /// de-emphasized rather than implying a fresh, colored reading.
    /// </param>
    private static string BuildData(UsageResult usage, bool isLarge, string? statusOverride = null, bool dim = false)
    {
        double sessionPct = Math.Clamp(Math.Round(usage.SessionPercent, 1), 0, 100);
        double weeklyPct = Math.Clamp(Math.Round(usage.WeeklyPercent, 1), 0, 100);

        string sessionColor = dim ? "Default" : UsageColors.GetCardStyle(sessionPct, UsageBucket.Session);
        string weeklyColor = dim ? "Default" : UsageColors.GetCardStyle(weeklyPct, UsageBucket.Weekly);

        string sessionBarUri = ProgressBarImage.BuildDataUri(sessionPct, dim ? DimHex : UsageColors.GetHex(sessionPct, UsageBucket.Session));
        string weeklyBarUri = ProgressBarImage.BuildDataUri(weeklyPct, dim ? DimHex : UsageColors.GetHex(weeklyPct, UsageBucket.Weekly));

        string lastUpdated = statusOverride ?? DateTime.Now.ToString("HH:mm:ss");

        return JsonSerializer.Serialize(new
        {
            currentSessionLabel = Localization.Get(Str.CurrentSession),
            weeklyLimitsLabel = Localization.Get(Str.WeeklyLimits),
            allModelsLabel = Localization.Get(Str.AllModels),
            refreshLabel = Localization.Get(Str.Refresh),
            usedLabel = Localization.Get(Str.UsedLabel),
            resetLabel = Localization.Get(Str.ResetLabel),

            sessionPercent = sessionPct,
            sessionColor,
            sessionTextSubtle = dim,
            sessionBarUri,
            sessionResetLine = usage.SessionResetLine,

            weeklyPercent = weeklyPct,
            weeklyColor,
            weeklyTextSubtle = dim,
            weeklyBarUri,
            weeklyResetLine = usage.WeeklyResetLine,

            // Accounts with no usage limits to track (free plan) get a short
            // explanatory note in place of the two bars. Shown on both card
            // sizes, unlike the isLarge-gated rows below, since the usage
            // section is the small card's only content.
            hasUsageLimits = usage.HasUsageLimits,
            hasNoUsageLimits = !usage.HasUsageLimits,
            noUsageLimitsLine = usage.NoUsageLimitsLine,
            // The note block owns the plan name on both card sizes when there
            // are no limits, so the note never renders above the plan it refers
            // to. hasPlanLine below is suppressed in that case to avoid showing
            // the plan name twice on the large card.
            hasNoLimitsPlanLine = !usage.HasUsageLimits && !string.IsNullOrEmpty(usage.PlanLine),

            separatorUri = SeparatorUri,
            isLarge,
            planLine = usage.PlanLine,
            // Suppressed when there are no usage limits — the note block above
            // already renders the plan name in that case (see hasNoLimitsPlanLine).
            hasPlanLine = isLarge && usage.HasUsageLimits && !string.IsNullOrEmpty(usage.PlanLine),
            paymentLine = usage.NextPaymentLine,
            hasPaymentLine = isLarge && !string.IsNullOrEmpty(usage.NextPaymentLine),
            hasPlanAndPaymentLine = isLarge && usage.HasPlanAndPaymentLine,
            nextPaymentColor = usage.IsPaymentSoon ? "Warning" : "Default",
            nextPaymentSubtle = !usage.IsPaymentSoon,
            nextPaymentWeight = usage.IsPaymentSoon ? "Bolder" : "Default",

            hasCredit = isLarge && usage.HasCredit,
            creditHeaderLine = usage.CreditHeaderLine,
            creditHeaderLabel = usage.CreditHeaderLabel,
            creditHeaderValue = usage.CreditHeaderValue,
            creditBalanceLine = usage.CreditBalanceLine,
            // Only the On/Off value text is clickable (to toggle usage
            // credits on/off), not the whole line — gated the same way as
            // before: only once claude.ai has reported a definite on/off
            // state, and not while a refresh is in flight (dim), since
            // clicking during a refresh could race with the fetch that's
            // about to overwrite the card anyway.
            hasCreditToggle = isLarge && !dim && usage.HasCredit && usage.CreditToggleKnown,
            // Plain, non-clickable fallback for the same header line when
            // the toggle isn't available yet (refresh in flight, or the
            // on/off state hasn't been determined) — without this the
            // header would simply disappear instead of degrading gracefully.
            hasCreditNonToggle = isLarge && usage.HasCredit && !(!dim && usage.CreditToggleKnown),
            creditToggleEnable = !(usage.ExtraUsageEnabled ?? false),
            hasCreditMonthlyLimitLine = isLarge && usage.HasCreditMonthlyLimitLine,
            creditMonthlyLimitLine = usage.CreditMonthlyLimitLine,
            creditMonthlyLimitLabel = usage.CreditMonthlyLimitLabel,
            creditMonthlyLimitValue = usage.CreditMonthlyLimitValue,
            // Only the dollar-amount value text is clickable (opens the
            // separate "Set monthly limit" window in the companion app),
            // not the whole line — only once usage credits are confirmed on
            // and not mid-refresh (same gating rationale as hasCreditToggle
            // above). Otherwise it's shown as plain, non-clickable text.
            hasCreditLimitClickable = isLarge && !dim && usage.HasCreditMonthlyLimitLine && usage.ExtraUsageEnabled == true,
            hasCreditLimitPlain = isLarge && usage.HasCreditMonthlyLimitLine && !(!dim && usage.ExtraUsageEnabled == true),
            currentMonthlyLimitValue = usage.Credit?.MonthlyLimitUsd ?? 0,
            hasPromoCredit = isLarge && usage.HasPromoCredit,
            promoCreditsLine = usage.PromoCreditsLine,

            lastUpdated,
        });
    }

    private static string BuildErrorData(string message, bool isLarge = true, string? timestampOverride = null)
    {
        string emptyBarUri = ProgressBarImage.BuildDataUri(0, "#EF4444");
        return JsonSerializer.Serialize(new
        {
            currentSessionLabel = Localization.Get(Str.CurrentSession),
            weeklyLimitsLabel = Localization.Get(Str.WeeklyLimits),
            allModelsLabel = Localization.Get(Str.AllModels),
            refreshLabel = Localization.Get(Str.Refresh),
            usedLabel = Localization.Get(Str.UsedLabel),
            resetLabel = Localization.Get(Str.ResetLabel),

            sessionPercent = 0, sessionColor = "Attention", sessionBarUri = emptyBarUri, sessionResetLine = message,
            weeklyPercent = 0, weeklyColor = "Attention", weeklyBarUri = emptyBarUri, weeklyResetLine = message,

            // Always true on the error path: the bars are what carry the error
            // message, and a fetch failure must not be mistaken for a free plan.
            hasUsageLimits = true,
            hasNoUsageLimits = false,
            noUsageLimitsLine = "",
            hasNoLimitsPlanLine = false,

            separatorUri = SeparatorUri,
            isLarge,
            planLine = "",
            hasPlanLine = false,
            paymentLine = "",
            hasPaymentLine = false,
            hasPlanAndPaymentLine = false,
            nextPaymentColor = "Default",
            nextPaymentSubtle = true,
            nextPaymentWeight = "Default",

            hasCredit = false,
            creditHeaderLine = "",
            creditHeaderLabel = "",
            creditHeaderValue = "",
            creditBalanceLine = "",
            hasCreditToggle = false,
            hasCreditNonToggle = false,
            creditToggleEnable = true,
            hasCreditMonthlyLimitLine = false,
            creditMonthlyLimitLine = "",
            creditMonthlyLimitLabel = "",
            creditMonthlyLimitValue = "",
            hasCreditLimitClickable = false,
            hasCreditLimitPlain = false,
            currentMonthlyLimitValue = 0,
            hasPromoCredit = false,
            promoCreditsLine = "",

            lastUpdated = timestampOverride ?? DateTime.Now.ToString("HH:mm:ss"),
        });
    }
}
