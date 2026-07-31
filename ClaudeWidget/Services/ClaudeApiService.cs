using System.Linq;
using System.Text.Json;
using Windows.Security.Credentials;

namespace ClaudeWidget.Services;

/// <summary>
/// Auth model: the user grabs their own `sessionKey` cookie value from
/// claude.ai in their browser (DevTools → Application → Cookies) and pastes
/// it into the widget. No OAuth, no browser automation, no token exchange.
///
/// The widget only ever does two things with it:
///   1. Checks whether a key is stored and still valid (via
///      <see cref="ValidateAndStoreSessionKeyAsync"/> / <see cref="IsSignedIn"/>).
///   2. Fetches 5-hour usage using that key
///      (<see cref="FetchProUsageAsync"/>), asking the user for a new key if
///      the stored one has expired/is rejected.
///
/// Requests are made primarily through <see cref="TlsClientFetcher"/> (a
/// TLS-fingerprint-impersonating HTTP client), because claude.ai's
/// Cloudflare protection fingerprints the TLS handshake and blocks a plain
/// HttpClient even with a valid sessionKey cookie — see
/// <see cref="TlsClientFetcher"/>'s remarks for details and for why it
/// replaced the old always-on hidden WebView2 approach for memory reasons.
/// If a fetch through it ever comes back looking Cloudflare-blocked (or
/// throws), <see cref="FetchWithFallbackAsync"/> transparently retries once
/// through <see cref="HiddenWebViewFetcher"/> (a real, heavier, hidden
/// Chromium instance) before giving up, so a tightened Cloudflare
/// fingerprint check degrades gracefully instead of breaking the widget.
/// </summary>
public static class ClaudeApiService
{
    /// <summary>
    /// Human-readable detail for the last failure (HTTP status + body
    /// snippet, exception message, etc.), so the UI can show something more
    /// useful than a generic error.
    /// </summary>
    public static string? LastError { get; private set; }

    private const string OrganizationsEndpoint = "https://claude.ai/api/organizations";
    private const string UsageEndpointFormat = "https://claude.ai/api/organizations/{0}/usage";
    private const string SubscriptionEndpointFormat = "https://claude.ai/api/organizations/{0}/subscription_details";
    private const string PlanEndpointFormat = "https://claude.ai/api/organizations/{0}/paused_subscription_details";
    private const string CreditsEndpointFormat = "https://claude.ai/api/organizations/{0}/prepaid/credits";
    private const string OverageSpendLimitEndpointFormat = "https://claude.ai/api/organizations/{0}/overage_spend_limit";
    private const string SetupOverageBillingEndpointFormat = "https://claude.ai/api/organizations/{0}/setup_overage_billing";

    private const string VaultResource = "ClaudeWidget_Auth";

    /// <summary>
    /// Fetches a claude.ai URL, preferring the lightweight TLS-fingerprint
    /// client and transparently falling back to the heavier hidden WebView2
    /// browser if that attempt throws or comes back looking
    /// Cloudflare-blocked. See <see cref="TlsClientFetcher"/>'s remarks for
    /// why the fallback exists.
    /// </summary>
    private static async Task<string> FetchWithFallbackAsync(string url, string sessionKey)
    {
        try
        {
            string body = await TlsClientFetcher.FetchJsonAsync(url, sessionKey);
            if (!LooksLikeBlockedResponse(body, out _))
            {
                return body;
            }
        }
        catch
        {
            // Fall through to the WebView2 fallback below.
        }

        return await HiddenWebViewFetcher.FetchJsonAsync(url, sessionKey);
    }

    /// <summary>
    /// Validates a pasted sessionKey by calling claude.ai's organizations
    /// endpoint with it, then stores both the key and the resolved
    /// organization id if valid.
    /// </summary>
    public static async Task<bool> ValidateAndStoreSessionKeyAsync(string sessionKey)
    {
        LastError = null;

        sessionKey = SanitizeSessionKey(sessionKey);
        if (string.IsNullOrEmpty(sessionKey))
        {
            LastError = Localization.Get(Str.SessionKeyNotEntered);
            return false;
        }

        string? orgId = await FetchOrganizationIdAsync(sessionKey);
        if (string.IsNullOrEmpty(orgId))
        {
            LastError ??= Localization.Get(Str.CouldNotValidateSessionKey);
            return false;
        }

        var vault = new PasswordVault();
        RemoveExistingCredential(vault, "SessionKey");
        RemoveExistingCredential(vault, "OrgId");

        vault.Add(new PasswordCredential(VaultResource, "SessionKey", sessionKey));
        vault.Add(new PasswordCredential(VaultResource, "OrgId", orgId));

        return true;
    }

    private static async Task<string?> FetchOrganizationIdAsync(string sessionKey)
    {
        try
        {
            string body = await FetchWithFallbackAsync(OrganizationsEndpoint, sessionKey);

            if (LooksLikeBlockedResponse(body, out string blockedReason))
            {
                LastError = blockedReason;
                return null;
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
                string snippet = body.Length > 500 ? body[..500] : body;
                LastError = Localization.Get(Str.CouldNotParseResponse, OrganizationsEndpoint, snippet);
                return null;
            }

            using (doc)
            {
                var root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("error", out var errEl))
                {
                    string errMsg = errEl.ValueKind == JsonValueKind.Object &&
                                     errEl.TryGetProperty("message", out var m)
                        ? m.GetString() ?? errEl.ToString()
                        : errEl.ToString();
                    LastError = Localization.Get(Str.SessionKeyInvalidOrExpiredValidate, errMsg);
                    return null;
                }

                if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
                {
                    string snippet = body.Length > 500 ? body[..500] : body;
                    LastError = Localization.Get(Str.OrgNotFoundInResponse, snippet);
                    return null;
                }

                // Prefer an org with "chat" capability (matches the account
                // the user is actually signed into on claude.ai), falling
                // back to the first entry otherwise.
                JsonElement? chosen = null;
                foreach (var org in root.EnumerateArray())
                {
                    if (org.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var cap in caps.EnumerateArray())
                        {
                            if (cap.GetString() == "chat")
                            {
                                chosen = org;
                                break;
                            }
                        }
                    }
                    if (chosen is not null) break;
                }
                chosen ??= root[0];

                return chosen.Value.TryGetProperty("uuid", out var uuid) ? uuid.GetString() : null;
            }
        }
        catch (Exception ex)
        {
            LastError = Localization.Get(Str.ExceptionFetchingOrg, ex.GetType().Name, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Returns true if a sessionKey is currently stored (does not verify it's
    /// still valid with claude.ai — that only happens on demand via
    /// <see cref="FetchProUsageAsync"/> or <see cref="ValidateAndStoreSessionKeyAsync"/>).
    /// </summary>
    public static bool IsSignedIn()
    {
        try
        {
            var vault = new PasswordVault();
            vault.Retrieve(VaultResource, "SessionKey");
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void SignOut()
    {
        var vault = new PasswordVault();
        RemoveExistingCredential(vault, "SessionKey");
        RemoveExistingCredential(vault, "OrgId");
    }

    private static void RemoveExistingCredential(PasswordVault vault, string username)
    {
        try
        {
            var cred = vault.Retrieve(VaultResource, username);
            vault.Remove(cred);
        }
        catch
        {
            // Not present; nothing to remove.
        }
    }

    /// <summary>
    /// Cleans up a pasted sessionKey value. Users sometimes paste from a
    /// context that wraps the value in extra characters (backticks/quotes
    /// from a Markdown code span, surrounding quote marks if the cookie
    /// value was copied including DevTools' quoting, etc.) instead of the
    /// bare cookie value from DevTools -> Application -> Cookies. Any of
    /// that gets silently included in the cookie sent to claude.ai and
    /// results in a generic "Invalid authorization" response that looks
    /// identical to a genuinely expired/wrong key, so strip common
    /// wrapping characters defensively before storing/using it.
    /// </summary>
    private static string SanitizeSessionKey(string sessionKey)
    {
        sessionKey = sessionKey.Trim();
        // Strip a matching pair of wrapping quote/backtick characters, and
        // any leading/trailing backticks that aren't part of a pair (e.g.
        // a single stray backtick copied from a chat message).
        sessionKey = sessionKey.Trim('`');
        if (sessionKey.Length >= 2)
        {
            char first = sessionKey[0];
            char last = sessionKey[^1];
            if ((first == '"' && last == '"') || (first == '\'' && last == '\''))
            {
                sessionKey = sessionKey[1..^1];
            }
        }
        return sessionKey.Trim();
    }

    /// <summary>
    /// Retrieves the stored sessionKey + orgId from the PasswordVault, if
    /// any. Shared by <see cref="FetchProUsageAsync"/> and the usage-credits
    /// mutation methods below.
    /// </summary>
    private static bool TryGetStoredCredentials(out string sessionKey, out string orgId)
    {
        try
        {
            var vault = new PasswordVault();
            var keyCred = vault.Retrieve(VaultResource, "SessionKey");
            keyCred.RetrievePassword();
            sessionKey = keyCred.Password;

            var orgCred = vault.Retrieve(VaultResource, "OrgId");
            orgCred.RetrievePassword();
            orgId = orgCred.Password;
            return true;
        }
        catch
        {
            sessionKey = string.Empty;
            orgId = string.Empty;
            return false;
        }
    }

    public static async Task<UsageResult?> FetchProUsageAsync()
    {
        try
        {
            LastError = null;

            if (!TryGetStoredCredentials(out string sessionKey, out string orgId))
            {
                LastError = Localization.Get(Str.NoStoredSessionKey);
                return null;
            }

            string usageUrl = string.Format(UsageEndpointFormat, orgId);
            string body = await FetchWithFallbackAsync(usageUrl, sessionKey);

            if (LooksLikeBlockedResponse(body, out string blockedReason))
            {
                LastError = blockedReason;
                return null;
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
                string snippet = body.Length > 500 ? body[..500] : body;
                LastError = Localization.Get(Str.CouldNotParseResponse, usageUrl, snippet);
                return null;
            }

            using (doc)
            {
                var root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var errEl))
                {
                    string errMsg = errEl.ValueKind == JsonValueKind.Object &&
                                     errEl.TryGetProperty("message", out var m)
                        ? m.GetString() ?? errEl.ToString()
                        : errEl.ToString();

                    // Session key expired/revoked — drop it so the UI asks for a new one.
                    SignOut();
                    LastError = Localization.Get(Str.SessionKeyExpiredOrInvalidUsage, errMsg);
                    return null;
                }

                if (!root.TryGetProperty("five_hour", out var fiveHour))
                {
                    string snippet = body.Length > 500 ? body[..500] : body;
                    LastError = Localization.Get(Str.FiveHourFieldMissing, snippet);
                    return null;
                }

                double sessionPct = fiveHour.GetProperty("utilization").GetDouble();
                DateTime? sessionResetsAt = TryGetResetsAt(fiveHour);

                double weeklyPct = 0;
                DateTime? weeklyResetsAt = null;
                if (root.TryGetProperty("seven_day", out var sevenDay))
                {
                    weeklyPct = sevenDay.GetProperty("utilization").GetDouble();
                    weeklyResetsAt = TryGetResetsAt(sevenDay);
                }

                // Fable is a separate weekly-limited product bucket (shown on
                // claude.ai's own Usage settings page alongside "All models").
                // Its used/unused state matters more than the exact key name,
                // so fall back to a case-insensitive scan for any "*fable*"
                // property if "seven_day_fable" isn't present.
                bool hasFable = false;
                double fablePct = 0;
                DateTime? fableResetsAt = null;
                JsonElement? fableBucket = null;
                if (root.TryGetProperty("seven_day_fable", out var fableExact))
                {
                    fableBucket = fableExact;
                }
                else
                {
                    foreach (var prop in root.EnumerateObject())
                    {
                        if (prop.Name.Contains("fable", StringComparison.OrdinalIgnoreCase))
                        {
                            fableBucket = prop.Value;
                            break;
                        }
                    }
                }
                if (fableBucket.HasValue && fableBucket.Value.ValueKind == JsonValueKind.Object)
                {
                    hasFable = true;
                    if (fableBucket.Value.TryGetProperty("utilization", out var util) &&
                        util.ValueKind is JsonValueKind.Number)
                    {
                        fablePct = util.GetDouble();
                    }
                    fableResetsAt = TryGetResetsAt(fableBucket.Value);
                }

                (DateTime? nextChargeAt, string? billingInterval) = await FetchSubscriptionDetailsAsync(orgId, sessionKey);
                string? planName = await FetchPlanNameAsync(orgId, sessionKey);
                CreditInfo? creditInfo = await FetchCreditInfoAsync(orgId, sessionKey);

                return new UsageResult(
                    sessionPct, sessionResetsAt,
                    weeklyPct, weeklyResetsAt,
                    hasFable, fablePct, fableResetsAt,
                    nextChargeAt, billingInterval, planName,
                    creditInfo);
            }
        }
        catch (Exception ex)
        {
            LastError = Localization.Get(Str.ExceptionGeneric, ex.GetType().Name, ex.Message, ex.StackTrace ?? string.Empty);
            return null;
        }
    }

    /// <summary>
    /// Fetches next-payment-date/billing-cadence from the same org's
    /// subscription_details endpoint. Best-effort: failures here don't
    /// fail the overall usage fetch (billing info is a "nice to have" on
    /// the card, not the core session/weekly percentages), so any error
    /// just results in a null date and the card omits the payment line.
    /// </summary>
    private static async Task<(DateTime? nextChargeAt, string? billingInterval)> FetchSubscriptionDetailsAsync(string orgId, string sessionKey)
    {
        try
        {
            string url = string.Format(SubscriptionEndpointFormat, orgId);
            string body = await FetchWithFallbackAsync(url, sessionKey);

            if (LooksLikeBlockedResponse(body, out _)) return (null, null);

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object) return (null, null);

            DateTime? nextChargeAt = null;
            if (root.TryGetProperty("next_charge_at", out var nca) && nca.ValueKind != JsonValueKind.Null &&
                DateTime.TryParse(nca.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
            {
                nextChargeAt = parsed.ToLocalTime();
            }

            string? billingInterval = root.TryGetProperty("billing_interval", out var bi) && bi.ValueKind == JsonValueKind.String
                ? bi.GetString()
                : null;

            return (nextChargeAt, billingInterval);
        }
        catch
        {
            // Best-effort — see summary above.
            return (null, null);
        }
    }

    /// <summary>
    /// Fetches the account's plan name ("Free plan" / "Pro plan" / "Max
    /// plan" / "Team plan") from paused_subscription_details' plan_type
    /// field, applying the same mapping the claude.ai UI itself uses (a
    /// "free" tier flag still displays as "Pro plan" for accounts with an
    /// active Tier 3 flag — mirrors the reference JS console snippet).
    /// Best-effort, same as <see cref="FetchSubscriptionDetailsAsync"/>.
    /// </summary>
    private static async Task<string?> FetchPlanNameAsync(string orgId, string sessionKey)
    {
        try
        {
            string url = string.Format(PlanEndpointFormat, orgId);
            string body = await FetchWithFallbackAsync(url, sessionKey);

            if (LooksLikeBlockedResponse(body, out _)) return null;

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            string rawPlan = root.TryGetProperty("plan_type", out var pt) && pt.ValueKind == JsonValueKind.String
                ? (pt.GetString() ?? "free").ToLowerInvariant()
                : "free";

            return rawPlan switch
            {
                "pro" or "free" => Localization.Get(Str.PlanPro),
                "max" => Localization.Get(Str.PlanMax),
                "team" => Localization.Get(Str.PlanTeam),
                _ => Localization.Get(Str.PlanFree),
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Fetches usage-credits info: combines the prepaid balance/promo-credit
    /// endpoint with the overage-spend-limit endpoint (which is where the
    /// "amount spent" figure actually lives — the prepaid/credits endpoint
    /// only reports the remaining balance, not spend). Best-effort, same as
    /// <see cref="FetchSubscriptionDetailsAsync"/>: any failure just omits
    /// the credits section from the card.
    /// </summary>
    private static async Task<CreditInfo?> FetchCreditInfoAsync(string orgId, string sessionKey)
    {
        try
        {
            string creditsUrl = string.Format(CreditsEndpointFormat, orgId);
            string creditsBody = await FetchWithFallbackAsync(creditsUrl, sessionKey);
            if (LooksLikeBlockedResponse(creditsBody, out _)) return null;

            using var creditsDoc = JsonDocument.Parse(creditsBody);
            var creditsRoot = creditsDoc.RootElement;
            if (creditsRoot.ValueKind != JsonValueKind.Object) return null;

            // No credits enabled/purchased for this account — omit the section.
            // Note: "amount" (cents) is the actual USD balance — e.g. 9541 =>
            // $95.41. "balance_credits" is a separate, much coarser credits
            // count (e.g. 95) and must not be used as a dollar amount.
            if (!creditsRoot.TryGetProperty("amount", out var balEl) ||
                balEl.ValueKind != JsonValueKind.Number)
            {
                return null;
            }

            double balance = balEl.GetInt64() / 100.0;

            var promoTrancheList = new List<PromoTranche>();
            if (creditsRoot.TryGetProperty("promo_tranches", out var promoTranches) &&
                promoTranches.ValueKind == JsonValueKind.Array)
            {
                foreach (var tranche in promoTranches.EnumerateArray())
                {
                    double? amount = null;
                    DateTime? expiresAt = null;
                    if (tranche.TryGetProperty("remaining_amount_minor_units", out var remEl) &&
                        remEl.ValueKind == JsonValueKind.Number)
                    {
                        amount = remEl.GetInt64() / 100.0;
                    }
                    if (tranche.TryGetProperty("expires_at", out var expEl) &&
                        expEl.ValueKind == JsonValueKind.String &&
                        DateTime.TryParse(expEl.GetString(), null,
                            System.Globalization.DateTimeStyles.RoundtripKind, out var parsedExp))
                    {
                        expiresAt = parsedExp.ToLocalTime();
                    }
                    if (amount.HasValue)
                    {
                        promoTrancheList.Add(new PromoTranche(amount.Value, expiresAt));
                    }
                }
            }

            double spent = 0;
            bool? extraUsageEnabled = null;
            double? monthlyLimit = null;
            try
            {
                string overageUrl = string.Format(OverageSpendLimitEndpointFormat, orgId);
                string overageBody = await FetchWithFallbackAsync(overageUrl, sessionKey);
                if (!LooksLikeBlockedResponse(overageBody, out _))
                {
                    using var overageDoc = JsonDocument.Parse(overageBody);
                    var overageRoot = overageDoc.RootElement;
                    if (overageRoot.ValueKind == JsonValueKind.Object)
                    {
                        if (overageRoot.TryGetProperty("used_credits", out var usedEl) &&
                            usedEl.ValueKind == JsonValueKind.Number)
                        {
                            spent = usedEl.GetInt64() / 100.0;
                        }

                        // "is_enabled" is claude.ai's own flag for whether this
                        // account is allowed to consume credits for extra usage
                        // once the plan's included messages run out (the
                        // "Enable extra usage" toggle on claude.ai's billing
                        // settings page).
                        if (overageRoot.TryGetProperty("is_enabled", out var enabledEl) &&
                            (enabledEl.ValueKind == JsonValueKind.True || enabledEl.ValueKind == JsonValueKind.False))
                        {
                            extraUsageEnabled = enabledEl.GetBoolean();
                        }

                        // "monthly_credit_limit" is the account's configured
                        // pay-as-you-go spend cap (in cents), i.e. the
                        // "org_monthly_spend_limit" set via
                        // setup_overage_billing / overage_spend_limit.
                        if (overageRoot.TryGetProperty("monthly_credit_limit", out var limitEl) &&
                            limitEl.ValueKind == JsonValueKind.Number)
                        {
                            monthlyLimit = limitEl.GetInt64() / 100.0;
                        }
                    }
                }
            }
            catch
            {
                // Spend figure/enabled flag are best-effort within an already-
                // best-effort fetch — fall back to 0/unknown and still show
                // balance/promo info.
            }

            return new CreditInfo(spent, balance, promoTrancheList, extraUsageEnabled, monthlyLimit);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Enables or disables pay-as-you-go "usage credits" billing (claude.ai's
    /// "Enable extra usage" toggle). Turning it on calls
    /// <c>setup_overage_billing</c> (creates the Metronome contract/customer
    /// if needed) with a monthly spend cap; turning it off calls
    /// <c>overage_spend_limit</c> with <c>is_enabled: false</c> — mirrors the
    /// two distinct requests the claude.ai web app itself issues for each
    /// direction (confirmed via a real browser "Copy as cURL").
    /// </summary>
    /// <param name="enable">True to turn usage credits on, false to turn off.</param>
    /// <param name="monthlySpendLimitCents">
    /// Monthly spend cap in cents, only used (and required) when
    /// <paramref name="enable"/> is true.
    /// </param>
    public static async Task<bool> SetOverageEnabledAsync(bool enable, int? monthlySpendLimitCents = null)
    {
        try
        {
            LastError = null;

            if (!TryGetStoredCredentials(out string sessionKey, out string orgId))
            {
                LastError = Localization.Get(Str.NoStoredSessionKey);
                return false;
            }

            string body;
            if (enable)
            {
                string url = string.Format(SetupOverageBillingEndpointFormat, orgId);
                string payload = JsonSerializer.Serialize(new { org_monthly_spend_limit = monthlySpendLimitCents ?? 4000 });
                body = await TlsClientFetcher.PostJsonAsync(url, sessionKey, payload);
            }
            else
            {
                string url = string.Format(OverageSpendLimitEndpointFormat, orgId);
                string payload = JsonSerializer.Serialize(new { is_enabled = false });
                body = await TlsClientFetcher.PutJsonAsync(url, sessionKey, payload);
            }

            return HandleMutationResponse(body);
        }
        catch (Exception ex)
        {
            LastError = Localization.Get(Str.ExceptionGeneric, ex.GetType().Name, ex.Message, ex.StackTrace ?? string.Empty);
            return false;
        }
    }

    /// <summary>
    /// Updates the monthly pay-as-you-go spend cap for an account that
    /// already has usage credits enabled. Uses the same
    /// <c>overage_spend_limit</c> PUT endpoint as disabling, just with
    /// <c>is_enabled: true</c> and the new limit instead.
    /// </summary>
    /// <remarks>
    /// The PUT payload field is <c>monthly_credit_limit</c> — matching the
    /// field name this same <c>overage_spend_limit</c> endpoint returns on
    /// GET/PUT (see <see cref="FetchCreditInfoAsync"/>) — not
    /// <c>org_monthly_spend_limit</c>, which is only the request field name
    /// for the separate <c>setup_overage_billing</c> POST endpoint used by
    /// <see cref="SetOverageEnabledAsync"/> when first turning credits on.
    /// Sending <c>org_monthly_spend_limit</c> here was silently ignored by
    /// the server (still returned <c>Status=OK</c>, but the limit never
    /// actually changed) — confirmed by comparing GET responses before/after.
    /// </remarks>
    public static async Task<bool> SetMonthlyCreditLimitAsync(int monthlySpendLimitCents)
    {
        try
        {
            LastError = null;

            if (!TryGetStoredCredentials(out string sessionKey, out string orgId))
            {
                LastError = Localization.Get(Str.NoStoredSessionKey);
                return false;
            }

            string url = string.Format(OverageSpendLimitEndpointFormat, orgId);
            string payload = JsonSerializer.Serialize(new { is_enabled = true, monthly_credit_limit = monthlySpendLimitCents });
            string body = await TlsClientFetcher.PutJsonAsync(url, sessionKey, payload);

            return HandleMutationResponse(body);
        }
        catch (Exception ex)
        {
            LastError = Localization.Get(Str.ExceptionGeneric, ex.GetType().Name, ex.Message, ex.StackTrace ?? string.Empty);
            return false;
        }
    }

    /// <summary>
    /// Shared response handling for the mutation endpoints above: detects a
    /// Cloudflare block or an <c>{"error": ...}</c> payload and sets
    /// <see cref="LastError"/> accordingly, returning whether the mutation
    /// can be considered successful.
    /// </summary>
    private static bool HandleMutationResponse(string body)
    {
        if (LooksLikeBlockedResponse(body, out string blockedReason))
        {
            LastError = blockedReason;
            return false;
        }

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind == JsonValueKind.Object &&
            doc.RootElement.TryGetProperty("error", out var errEl))
        {
            LastError = errEl.ValueKind == JsonValueKind.Object &&
                         errEl.TryGetProperty("message", out var m)
                ? m.GetString() ?? errEl.ToString()
                : errEl.ToString();
            return false;
        }

        return true;
    }

    private static DateTime? TryGetResetsAt(JsonElement bucket)
    {
        if (bucket.TryGetProperty("resets_at", out var r) && r.ValueKind != JsonValueKind.Null)
        {
            if (DateTime.TryParse(r.GetString(), null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var resetTime))
            {
                return resetTime.ToLocalTime();
            }
        }
        return null;
    }

    /// <summary>
    /// Detects Cloudflare challenge/blocked pages returned in place of JSON.
    /// </summary>
    private static bool LooksLikeBlockedResponse(string body, out string reason)
    {
        if (body.Contains("Just a moment"))
        {
            reason = Localization.Get(Str.CloudflareJustAMoment);
            return true;
        }
        if (body.Contains("Enable JavaScript and cookies to continue"))
        {
            reason = Localization.Get(Str.CloudflareBrowserCheck);
            return true;
        }
        if (body.TrimStart().StartsWith("<"))
        {
            reason = Localization.Get(Str.ServerReturnedHtml, body.Length > 300 ? body[..300] : body);
            return true;
        }
        reason = string.Empty;
        return false;
    }
}

/// <summary>
/// Both usage buckets from a single call to the usage endpoint: the 5-hour
/// "session" limit and the 7-day "weekly" limit. Reset timestamps are kept
/// as raw local <see cref="DateTime"/> values (not pre-formatted strings) so
/// the UI can compute a live "resets in Xh Ym" countdown and re-render it
/// without re-fetching.
/// </summary>
public readonly record struct UsageResult(
    double SessionPercent,
    DateTime? SessionResetsAt,
    double WeeklyPercent,
    DateTime? WeeklyResetsAt,
    bool HasFable,
    double FablePercent,
    DateTime? FableResetsAt,
    DateTime? NextChargeAt = null,
    string? BillingInterval = null,
    string? PlanName = null,
    CreditInfo? Credit = null)
{
    /// <summary>"14:30" style time for the session reset.</summary>
    public string SessionResetAtText =>
        SessionResetsAt.HasValue ? SessionResetsAt.Value.ToString("HH:mm", Localization.Culture) : "—";

    /// <summary>"Mon Jul 20, 06:00" style date+time for the weekly reset.</summary>
    public string WeeklyResetAtText =>
        WeeklyResetsAt.HasValue ? Localization.FormatWeekdayDateTime(WeeklyResetsAt.Value) : "—";

    public string FableResetAtText =>
        FableResetsAt.HasValue ? Localization.FormatWeekdayDateTime(FableResetsAt.Value) : "—";

    /// <summary>Matches claude.ai's own wording: "You haven't used Fable yet" when unused.</summary>
    public string FableStatusText =>
        !FableResetsAt.HasValue && FablePercent <= 0
            ? Localization.Get(Str.HaventUsedFable)
            : Localization.Get(Str.ResetsInAt, FableCountdownText, FableResetAtText);

    /// <summary>Single-line "Resets in 4h 26m, at 18:00" style summary used by the widget card.</summary>
    public string SessionResetLine => Localization.Get(Str.ResetsInAt, SessionCountdownText, SessionResetAtText);
    public string WeeklyResetLine => Localization.Get(Str.ResetsInAt, WeeklyCountdownText, WeeklyResetAtText);
    public string FableResetLine =>
        HasFable
            ? (!FableResetsAt.HasValue && FablePercent <= 0
                ? Localization.Get(Str.HaventUsedFable)
                : Localization.Get(Str.ResetsInAt, FableCountdownText, FableResetAtText))
            : Localization.Get(Str.HaventUsedFable);

    /// <summary>"2h 29m" / "14h 29m" / "3d 2h" style countdown text (space between value and unit).</summary>
    public static string FormatCountdown(DateTime? resetsAt)
    {
        if (!resetsAt.HasValue) return "—";
        var diff = resetsAt.Value - DateTime.Now;
        if (diff.TotalSeconds <= 0) return "Resetting…";

        string h = Localization.Get(Str.HourUnit);
        string m = Localization.Get(Str.MinuteUnit);
        string d = Localization.Get(Str.DayUnit);

        int totalHours = (int)diff.TotalHours;
        int minutes = diff.Minutes;
        if (totalHours >= 24)
        {
            int days = totalHours / 24;
            int remHours = totalHours % 24;
            return $"{days} {d} {remHours} {h}";
        }
        if (totalHours > 0) return $"{totalHours} {h} {minutes} {m}";
        return $"{minutes} {m}";
    }

    public string SessionCountdownText => FormatCountdown(SessionResetsAt);
    public string WeeklyCountdownText => FormatCountdown(WeeklyResetsAt);
    public string FableCountdownText => FormatCountdown(FableResetsAt);

    /// <summary>
    /// "Next payment: Aug 10, 2026 (monthly)" style line, or empty string
    /// if billing info wasn't available (e.g. subscription_details fetch
    /// failed) — the card hides the whole row in that case.
    /// </summary>
    public string NextPaymentLine
    {
        get
        {
            if (!NextChargeAt.HasValue) return string.Empty;
            string date = Localization.FormatDateShort(NextChargeAt.Value);
            string? interval = LocalizeBillingInterval(BillingInterval);
            return string.IsNullOrEmpty(interval)
                ? Localization.Get(Str.NextPayment, date)
                : Localization.Get(Str.NextPaymentWithInterval, date, interval);
        }
    }

    private static string? LocalizeBillingInterval(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        return raw.Trim().ToLowerInvariant() switch
        {
            "day" or "daily" => Localization.Get(Str.BillingDaily),
            "week" or "weekly" => Localization.Get(Str.BillingWeekly),
            "month" or "monthly" => Localization.Get(Str.BillingMonthly),
            "quarter" or "quarterly" => Localization.Get(Str.BillingQuarterly),
            "year" or "yearly" or "annual" or "annually" => Localization.Get(Str.BillingYearly),
            _ => raw,
        };
    }

    /// <summary>True when the next charge is within 3 days (used to highlight the payment line in yellow).</summary>
    public bool IsPaymentSoon =>
        NextChargeAt.HasValue && (NextChargeAt.Value - DateTime.Now).TotalDays <= 3 && (NextChargeAt.Value - DateTime.Now).TotalDays >= 0;

    /// <summary>
    /// "**Pro plan** · Next payment: Aug 10, 2026 (monthly)" style single
    /// line combining plan name and next-payment date, or just whichever
    /// half is available if the other one failed to fetch. Empty if
    /// neither is available — the card hides the whole row in that case.
    /// </summary>
    public string PlanAndPaymentLine
    {
        get
        {
            bool hasPlan = !string.IsNullOrEmpty(PlanName);
            bool hasPayment = !string.IsNullOrEmpty(NextPaymentLine);
            string boldPlan = hasPlan ? $"**{PlanName}**" : string.Empty;
            if (hasPlan && hasPayment) return $"{boldPlan} \u00b7 {NextPaymentLine}";
            if (hasPlan) return boldPlan;
            return hasPayment ? NextPaymentLine : string.Empty;
        }
    }

    public bool HasPlanAndPaymentLine => !string.IsNullOrEmpty(PlanName) || !string.IsNullOrEmpty(NextPaymentLine);

    public bool HasCredit => Credit.HasValue;

    /// <summary>Whether claude.ai reported an is_enabled flag for extra usage (pay-as-you-go credit spend once the plan's included messages run out). Null if unknown (e.g. best-effort fetch failed).</summary>
    public bool? ExtraUsageEnabled => Credit?.ExtraUsageEnabled;

    /// <summary>"**Usage credits**: On" / "**Usage credits**: Off" header line, empty if the enabled/disabled state is unknown.</summary>
    public string CreditHeaderLine =>
        ExtraUsageEnabled switch
        {
            true => Localization.Get(Str.UsageCreditsHeader, Localization.Get(Str.CreditUsageOn)),
            false => Localization.Get(Str.UsageCreditsHeader, Localization.Get(Str.CreditUsageOff)),
            null => string.Empty,
        };

    /// <summary>
    /// Label half of CreditHeaderLine, split out so only the On/Off value
    /// text (see CreditHeaderValue) is clickable on the widget card rather
    /// than the whole line.
    /// </summary>
    public string CreditHeaderLabel => Localization.Get(Str.UsageCreditsLabel);

    /// <summary>"On" / "Off" value half of CreditHeaderLine, empty if unknown.</summary>
    public string CreditHeaderValue =>
        ExtraUsageEnabled switch
        {
            true => Localization.Get(Str.CreditUsageOn),
            false => Localization.Get(Str.CreditUsageOff),
            null => string.Empty,
        };

    /// <summary>"Current balance: $95.41, spent: $4.59" style line.</summary>
    public string CreditBalanceLine =>
        Credit.HasValue
            ? Localization.Get(Str.CreditBalanceAndSpent, Credit.Value.BalanceUsd.ToString("0.00"), Credit.Value.SpentUsd.ToString("0.00"))
            : string.Empty;

    /// <summary>
    /// "Promotional credits: $95.40 until 9/19/26, $10.00 until 1/27/27"
    /// style single line combining every active promo tranche (claude.ai can
    /// grant several at once, e.g. separate signup/referral promos, each
    /// with its own expiry) -- empty if none are active. Uses a compact,
    /// locale-correct 2-digit-year date (see Localization.FormatDateCompact)
    /// so multiple tranches still fit on one line; tranches without a known
    /// expiry show just the amount.
    /// </summary>
    public string PromoCreditsLine
    {
        get
        {
            if (!Credit.HasValue || Credit.Value.PromoTranches.Count == 0) return string.Empty;
            var parts = Credit.Value.PromoTranches.Select(t =>
                t.ExpiresAt.HasValue
                    ? Localization.Get(Str.PromoTrancheWithExpiry, t.AmountUsd.ToString("0.00"), Localization.FormatDateCompact(t.ExpiresAt.Value))
                    : "$" + t.AmountUsd.ToString("0.00"));
            return Localization.Get(Str.PromotionalCredits, string.Join(", ", parts));
        }
    }

    public bool HasPromoCredit => !string.IsNullOrEmpty(PromoCreditsLine);

    /// <summary>"Monthly limit: $40.00" style line, empty if unknown/no limit set.</summary>
    public string CreditMonthlyLimitLine =>
        Credit.HasValue && Credit.Value.MonthlyLimitUsd.HasValue
            ? Localization.Get(Str.CreditMonthlyLimitLine, Credit.Value.MonthlyLimitUsd.Value.ToString("0.00"))
            : string.Empty;

    public bool HasCreditMonthlyLimitLine => !string.IsNullOrEmpty(CreditMonthlyLimitLine);

    /// <summary>
    /// Label half of CreditMonthlyLimitLine, split out so only the dollar
    /// amount (see CreditMonthlyLimitValue) is clickable on the widget card
    /// rather than the whole line.
    /// </summary>
    public string CreditMonthlyLimitLabel => Localization.Get(Str.CreditMonthlyLimitLabel);

    /// <summary>"$40.00" value half of CreditMonthlyLimitLine, empty if unknown/no limit set.</summary>
    public string CreditMonthlyLimitValue =>
        Credit.HasValue && Credit.Value.MonthlyLimitUsd.HasValue
            ? "$" + Credit.Value.MonthlyLimitUsd.Value.ToString("0.00")
            : string.Empty;

    /// <summary>
    /// True once claude.ai has reported a definite on/off state for usage
    /// credits — used to gate the clickable toggle/limit editor on the
    /// widget card (an unknown state, e.g. a best-effort fetch failure,
    /// shouldn't offer a toggle that might send the wrong request).
    /// </summary>
    public bool CreditToggleKnown => Credit.HasValue && ExtraUsageEnabled.HasValue;
}

/// <summary>
/// Usage-credits snapshot combining claude.ai's prepaid/credits endpoint
/// (balance + any active promotional tranche) and its overage_spend_limit
/// endpoint (amount spent this billing period) — mirrors the "Usage
/// credits" panel on claude.ai's billing settings page.
/// </summary>
public readonly record struct CreditInfo(
    double SpentUsd,
    double BalanceUsd,
    List<PromoTranche> PromoTranches,
    bool? ExtraUsageEnabled = null,
    double? MonthlyLimitUsd = null);

/// <summary>
/// A single active promotional-credit grant: a dollar amount and its own
/// expiration date. claude.ai's prepaid/credits response can return several
/// of these at once (e.g. separate signup and referral promos), each
/// expiring independently — see <see cref="UsageResult.PromoCreditsLine"/>.
/// </summary>
public readonly record struct PromoTranche(double AmountUsd, DateTime? ExpiresAt);
