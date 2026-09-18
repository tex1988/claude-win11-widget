using System.Collections.Generic;
using System.Globalization;

namespace ClaudeWidget.Services;

/// <summary>Languages this app ships translations for. Anything else falls back to <see cref="En"/>.</summary>
public enum AppLanguage
{
    En,
    Ru,
    Uk,
}

/// <summary>
/// Every user-facing string key used by the app (companion window) and the
/// widget card, translated in <see cref="Localization"/>. Kept as an enum
/// (rather than raw string keys) so a missing translation is a compile error,
/// not a silent runtime fallback.
/// </summary>
public enum Str
{
    // ── Widget card / Adaptive Card labels ──────────────────────────────
    Loading,
    ErrorFetchingData,
    Refreshing,
    UpdateFailedShowingCached,
    ProviderError,
    SignInRequired,
    SignInBody,
    OpenAppToSignIn,
    CurrentSession,
    WeeklyLimits,
    AllModels,
    Refresh,
    UsedLabel,    // "Used:" (fallback template FactSet title)
    ResetLabel,   // "Reset:" (fallback template FactSet title)
    ResetsInAt,              // "Resets in {0}, at {1}"
    HaventUsedFable,
    NextPayment,             // "Next payment: {0}"
    NextPaymentWithInterval, // "Next payment: {0} ({1})"
    UsageCreditsHeader,      // "Usage credits: {0}" (On/Off)
    UsageCreditsLabel,       // "**Usage credits**:" (label-only half, value is separately clickable)
    CreditBalanceAndSpent,   // "Current balance: ${0}, spent: ${1}"
    PromotionalCredits,      // "Promotional credits: {0}" ({0} = joined tranche list)
    PromoTrancheWithExpiry,  // "${0} until {1}" (single tranche within PromotionalCredits)
    ExpiresLabel,            // "Expires: {0}"
    PlanFree,
    NoUsageLimits,
    PlanPro,
    PlanMax,
    PlanTeam,
    CreditUsageOn,
    CreditUsageOff,
    CreditMonthlyLimitLine,   // "Monthly limit: ${0}"
    CreditMonthlyLimitLabel,  // "Monthly limit:" (label-only half, value is separately clickable)
    CreditLimitInputPlaceholder,
    SetLimitButton,
    CreditActionFailedPrefix, // "Failed to update usage credits: {0}"

    SetLimitWindowTitle,
    SetLimitWindowHeader,
    SetLimitWindowBody,
    SetLimitInputLabel,
    SaveButton,
    CancelButton,
    SetLimitSavingStatus,
    SetLimitSuccessStatus,
    SetLimitFailedStatus,
    SetLimitInvalidValue,

    // ── Time units (countdown formatting: "4h 26m" / "1d 2h") ───────────
    HourUnit,
    MinuteUnit,
    DayUnit,

    // ── Billing interval ("Next payment: ... (monthly)") ────────────────
    BillingDaily,
    BillingWeekly,
    BillingMonthly,
    BillingQuarterly,
    BillingYearly,

    // ── Companion app window (MainPage) ─────────────────────────────────
    AppSubtitle,
    NotSignedInTitle,
    NotSignedInMessage,
    SignedInTitle,
    SignedInMessage,
    AuthorizationHeader,
    AuthorizationBody,
    SessionKeyPlaceholder,
    SignInButton,
    StatusHeader,
    SignedInBody,
    SignOutButton,
    EnterSessionKeyWarning,
    SignInFailedPrefix,     // "Failed to sign in: {0}"
    InvalidSessionKey,

    // ── ClaudeApiService errors (surfaced via LastError) ────────────────
    SessionKeyNotEntered,
    CouldNotValidateSessionKey,
    CouldNotParseResponse,               // "Failed to parse response ({0}): {1}"
    SessionKeyInvalidOrExpiredValidate,  // "Session key is invalid or expired: {0}"
    OrgNotFoundInResponse,               // "Could not find an organization in the response: {0}"
    ExceptionFetchingOrg,                // "Exception ({0}) while fetching organization: {1}"
    NoStoredSessionKey,
    FiveHourFieldMissing,                // "Response is missing the five_hour field: {0}"
    SessionKeyExpiredOrInvalidUsage,     // "Session key expired or invalid: {0}"
    ExceptionGeneric,                    // "Exception ({0}): {1}\n{2}"
    CloudflareJustAMoment,
    CloudflareBrowserCheck,
    ServerReturnedHtml,                  // "Server returned HTML instead of JSON (possibly blocked): {0}"
}

/// <summary>
/// Minimal in-process localization: detects the user's preferred Windows
/// display language once at startup, and resolves <see cref="Str"/> keys to
/// translated text for whichever of the app's supported languages
/// (English/Russian/Ukrainian) matches — or English if the system language
/// isn't one of those.
/// </summary>
/// <remarks>
/// Intentionally not resw/x:Uid-based: the widget card's text is generated
/// at runtime as Adaptive Card JSON (not XAML), so its labels can only ever
/// be resolved in code anyway — using the same lookup table for the
/// companion window's XAML-adjacent code-behind keeps both surfaces on one
/// language-detection code path instead of two different localization
/// systems that could disagree.
/// </remarks>
public static class Localization
{
    /// <summary>Detected once per process using the top of the user's Windows language list.</summary>
    public static AppLanguage Current { get; } = Detect();

    private static AppLanguage Detect()
    {
        try
        {
            var languages = Windows.System.UserProfile.GlobalizationPreferences.Languages;
            if (languages.Count > 0)
            {
                string primary = languages[0];
                int dash = primary.IndexOf('-');
                string prefix = (dash > 0 ? primary[..dash] : primary).ToLowerInvariant();
                return prefix switch
                {
                    "ru" => AppLanguage.Ru,
                    "uk" => AppLanguage.Uk,
                    _ => AppLanguage.En,
                };
            }
        }
        catch
        {
            // GlobalizationPreferences unavailable for some reason — fall back to English.
        }
        return AppLanguage.En;
    }

    /// <summary>Culture used for locale-appropriate date/time formatting (e.g. weekday/month names, date order).</summary>
    public static CultureInfo Culture { get; } = Current switch
    {
        AppLanguage.Ru => new CultureInfo("ru-RU"),
        AppLanguage.Uk => new CultureInfo("uk-UA"),
        _ => new CultureInfo("en-US"),
    };

    public static string Get(Str key) => Table[Current][key];

    public static string Get(Str key, params object[] args) => string.Format(Table[Current][key], args);

    /// <summary>
    /// "Aug 10, 2026" (English) / "10 авг. 2026" (Russian/Ukrainian) style
    /// short date. English keeps month-before-day (its natural order);
    /// Russian/Ukrainian use day-before-month, since a fixed custom .NET
    /// format string like "MMM d, yyyy" always puts the month first
    /// regardless of culture — CultureInfo alone does not reorder a custom
    /// pattern's literal tokens.
    /// </summary>
    public static string FormatDateShort(DateTime value) =>
        value.ToString(Current == AppLanguage.En ? "MMM d, yyyy" : "d MMM yyyy", Culture);

    /// <summary>
    /// "September 19, 2026" (English) / "19 сентября 2026"
    /// (Russian/Ukrainian) style long date (full month name). See
    /// <see cref="FormatDateShort"/> for why day/month order needs to be
    /// swapped explicitly per language.
    /// </summary>
    public static string FormatDateLong(DateTime value) =>
        value.ToString(Current == AppLanguage.En ? "MMMM d, yyyy" : "d MMMM yyyy", Culture);

    /// <summary>
    /// "Mon Jul 20, 06:00" (English) / "пн, 20 июл, 06:00"
    /// (Russian/Ukrainian) style weekday + short date + time, used for
    /// session/weekly/Fable reset timestamps. See
    /// <see cref="FormatDateShort"/> for why day/month order needs to be
    /// swapped explicitly per language.
    /// </summary>
    public static string FormatWeekdayDateTime(DateTime value) =>
        value.ToString(Current == AppLanguage.En ? "ddd MMM d, HH:mm" : "ddd, d MMM, HH:mm", Culture);

    /// <summary>
    /// Compact numeric date with a 2-digit year -- "9/19/26" (English,
    /// month-first) / "19.09.26" (Russian/Ukrainian, day-first with dot
    /// separators) -- used where several dates need to fit on one line
    /// (e.g. multiple promo-credit expirations). See <see cref="FormatDateShort"/>
    /// for why day/month order needs to be swapped explicitly per language.
    /// </summary>
    public static string FormatDateCompact(DateTime value) =>
        value.ToString(Current == AppLanguage.En ? "M/d/yy" : "dd.MM.yy", Culture);

    private static readonly Dictionary<AppLanguage, Dictionary<Str, string>> Table = new()
    {
        [AppLanguage.En] = new()
        {
            [Str.Loading] = "Loading…",
            [Str.ErrorFetchingData] = "Error fetching data",
            [Str.Refreshing] = "Refreshing…",
            [Str.UpdateFailedShowingCached] = "Update failed \u2014 showing cached data",
            [Str.ProviderError] = "Provider error",
            [Str.SignInRequired] = "Sign in required",
            [Str.SignInBody] = "Open the app and paste your claude.ai sessionKey cookie to start monitoring your 5-hour usage window.",
            [Str.OpenAppToSignIn] = "Open App to Sign In",
            [Str.CurrentSession] = "Current session",
            [Str.WeeklyLimits] = "Weekly limits",
            [Str.AllModels] = "All models",
            [Str.Refresh] = "Refresh",
            [Str.UsedLabel] = "Used:",
            [Str.ResetLabel] = "Reset:",
            [Str.ResetsInAt] = "Resets in {0}, at {1}",
            [Str.HaventUsedFable] = "You haven't used Fable yet",
            [Str.NextPayment] = "Next payment: {0}",
            [Str.NextPaymentWithInterval] = "Next payment: {0} ({1})",
            [Str.UsageCreditsHeader] = "**Usage credits**: {0}",
            [Str.UsageCreditsLabel] = "**Usage credits**: ",
            [Str.CreditBalanceAndSpent] = "Current balance: ${0}, spent: ${1}",
            [Str.PromotionalCredits] = "Promotional credits: {0}",
            [Str.PromoTrancheWithExpiry] = "${0} until {1}",
            [Str.ExpiresLabel] = "Expires: {0}",
            [Str.PlanFree] = "Free plan",
            [Str.NoUsageLimits] = "No usage limits to track",
            [Str.PlanPro] = "Pro plan",
            [Str.PlanMax] = "Max plan",
            [Str.PlanTeam] = "Team plan",
            [Str.CreditUsageOn] = "On",
            [Str.CreditUsageOff] = "Off",
            [Str.CreditMonthlyLimitLine] = "Monthly limit: ${0}",
            [Str.CreditMonthlyLimitLabel] = "Monthly limit: ",
            [Str.CreditLimitInputPlaceholder] = "Monthly limit ($)",
            [Str.SetLimitButton] = "Set limit",
            [Str.CreditActionFailedPrefix] = "Failed to update usage credits: {0}",

            [Str.SetLimitWindowTitle] = "Monthly Limit — Claude Widget",
            [Str.SetLimitWindowHeader] = "Set monthly spend limit",
            [Str.SetLimitWindowBody] = "Set the maximum amount claude.ai can charge for extra usage each month once your plan's included messages run out.",
            [Str.SetLimitInputLabel] = "Monthly limit ($)",
            [Str.SaveButton] = "Save",
            [Str.CancelButton] = "Cancel",
            [Str.SetLimitSavingStatus] = "Saving…",
            [Str.SetLimitSuccessStatus] = "Monthly limit updated.",
            [Str.SetLimitFailedStatus] = "Failed to update limit: {0}",
            [Str.SetLimitInvalidValue] = "Enter a valid amount.",

            [Str.HourUnit] = "h",
            [Str.MinuteUnit] = "m",
            [Str.DayUnit] = "d",

            [Str.BillingDaily] = "daily",
            [Str.BillingWeekly] = "weekly",
            [Str.BillingMonthly] = "monthly",
            [Str.BillingQuarterly] = "quarterly",
            [Str.BillingYearly] = "yearly",

            [Str.AppSubtitle] = "Widget for the Windows 11 board (Win + W)",
            [Str.NotSignedInTitle] = "Not signed in",
            [Str.NotSignedInMessage] = "Paste your sessionKey below to sign in.",
            [Str.SignedInTitle] = "Signed in",
            [Str.SignedInMessage] = "The widget is active. Open Win + W and add \u00abClaude Limits\u00bb.",
            [Str.AuthorizationHeader] = "Sign in",
            [Str.AuthorizationBody] = "Open claude.ai in your browser, sign in, then in DevTools (F12) \u2192 Application \u2192 Cookies \u2192 claude.ai copy the value of the \u00absessionKey\u00bb cookie and paste it below.",
            [Str.SessionKeyPlaceholder] = "Paste your sessionKey here",
            [Str.SignInButton] = "Sign in",
            [Str.StatusHeader] = "Status",
            [Str.SignedInBody] = "Usage data is only shown in the widget. Open Win + W and add \u00abClaude Limits\u00bb.",
            [Str.SignOutButton] = "Sign out",
            [Str.EnterSessionKeyWarning] = "Enter your sessionKey.",
            [Str.SignInFailedPrefix] = "Failed to sign in: {0}",
            [Str.InvalidSessionKey] = "Invalid sessionKey.",

            [Str.SessionKeyNotEntered] = "Session key was not entered.",
            [Str.CouldNotValidateSessionKey] = "Could not validate the session key.",
            [Str.CouldNotParseResponse] = "Failed to parse response ({0}): {1}",
            [Str.SessionKeyInvalidOrExpiredValidate] = "Session key is invalid or expired: {0}",
            [Str.OrgNotFoundInResponse] = "Could not find an organization in the response: {0}",
            [Str.ExceptionFetchingOrg] = "Exception ({0}) while fetching organization: {1}",
            [Str.NoStoredSessionKey] = "No stored session key. Please sign in again.",
            [Str.FiveHourFieldMissing] = "Response is missing the five_hour field: {0}",
            [Str.SessionKeyExpiredOrInvalidUsage] = "Session key expired or invalid: {0}",
            [Str.ExceptionGeneric] = "Exception ({0}): {1}\n{2}",
            [Str.CloudflareJustAMoment] = "Request blocked by Cloudflare (Just a moment). Try again later.",
            [Str.CloudflareBrowserCheck] = "Cloudflare requires a browser check. Try again later.",
            [Str.ServerReturnedHtml] = "Server returned HTML instead of JSON (possibly blocked): {0}",
        },

        [AppLanguage.Ru] = new()
        {
            [Str.Loading] = "Загрузка…",
            [Str.ErrorFetchingData] = "Ошибка получения данных",
            [Str.Refreshing] = "Обновление…",
            [Str.UpdateFailedShowingCached] = "Не удалось обновить \u2014 показаны сохранённые данные",
            [Str.ProviderError] = "Ошибка провайдера",
            [Str.SignInRequired] = "Требуется вход",
            [Str.SignInBody] = "Откройте приложение и вставьте cookie sessionKey с claude.ai, чтобы начать отслеживать использование 5-часового окна.",
            [Str.OpenAppToSignIn] = "Открыть приложение для входа",
            [Str.CurrentSession] = "Текущая сессия",
            [Str.WeeklyLimits] = "Недельные лимиты",
            [Str.AllModels] = "Все модели",
            [Str.Refresh] = "Обновить",
            [Str.UsedLabel] = "Использовано:",
            [Str.ResetLabel] = "Сброс:",
            [Str.ResetsInAt] = "Сброс через {0}, в {1}",
            [Str.HaventUsedFable] = "Вы ещё не использовали Fable",
            [Str.NextPayment] = "След. платёж: {0}",
            [Str.NextPaymentWithInterval] = "Следующий платёж: {0} ({1})",
            [Str.UsageCreditsHeader] = "**Использование кредитов**: {0}",
            [Str.UsageCreditsLabel] = "**Использование кредитов**: ",
            [Str.CreditBalanceAndSpent] = "Текущий баланс: ${0}, потрачено: ${1}",
            [Str.PromotionalCredits] = "Промо-кредиты: {0}",
            [Str.PromoTrancheWithExpiry] = "${0} до {1}",
            [Str.ExpiresLabel] = "Истекает: {0}",
            [Str.PlanFree] = "Тариф Free",
            [Str.NoUsageLimits] = "Нет лимитов для отслеживания",
            [Str.PlanPro] = "Тариф Pro",
            [Str.PlanMax] = "Тариф Max",
            [Str.PlanTeam] = "Тариф Team",
            [Str.CreditUsageOn] = "Вкл",
            [Str.CreditUsageOff] = "Выкл",
            [Str.CreditMonthlyLimitLine] = "Месячный лимит: ${0}",
            [Str.CreditMonthlyLimitLabel] = "Месячный лимит: ",
            [Str.CreditLimitInputPlaceholder] = "Месячный лимит ($)",
            [Str.SetLimitButton] = "Установить лимит",
            [Str.CreditActionFailedPrefix] = "Не удалось изменить использование кредитов: {0}",

            [Str.SetLimitWindowTitle] = "Месячный лимит — Claude Widget",
            [Str.SetLimitWindowHeader] = "Установить месячный лимит расходов",
            [Str.SetLimitWindowBody] = "Укажите максимальную сумму, которую claude.ai может списать за дополнительное использование в месяц после исчерпания лимита по тарифу.",
            [Str.SetLimitInputLabel] = "Месячный лимит ($)",
            [Str.SaveButton] = "Сохранить",
            [Str.CancelButton] = "Отмена",
            [Str.SetLimitSavingStatus] = "Сохранение…",
            [Str.SetLimitSuccessStatus] = "Месячный лимит обновлён.",
            [Str.SetLimitFailedStatus] = "Не удалось обновить лимит: {0}",
            [Str.SetLimitInvalidValue] = "Введите корректную сумму.",

            [Str.HourUnit] = "ч",
            [Str.MinuteUnit] = "мин",
            [Str.DayUnit] = "д",

            [Str.BillingDaily] = "ежедневно",
            [Str.BillingWeekly] = "еженедельно",
            [Str.BillingMonthly] = "ежемесячно",
            [Str.BillingQuarterly] = "ежеквартально",
            [Str.BillingYearly] = "ежегодно",

            [Str.AppSubtitle] = "Виджет для панели Windows 11 (Win + W)",
            [Str.NotSignedInTitle] = "Не авторизован",
            [Str.NotSignedInMessage] = "Вставьте sessionKey ниже чтобы войти.",
            [Str.SignedInTitle] = "Авторизован",
            [Str.SignedInMessage] = "Виджет активен. Откройте Win + W и добавьте «Claude Limits».",
            [Str.AuthorizationHeader] = "Авторизация",
            [Str.AuthorizationBody] = "Откройте claude.ai в браузере, войдите в аккаунт, затем в DevTools (F12) → Application → Cookies → claude.ai скопируйте значение cookie «sessionKey» и вставьте его ниже.",
            [Str.SessionKeyPlaceholder] = "Вставьте sessionKey сюда",
            [Str.SignInButton] = "Войти",
            [Str.StatusHeader] = "Статус",
            [Str.SignedInBody] = "Данные об использовании отображаются только в виджете. Откройте Win + W и добавьте «Claude Limits».",
            [Str.SignOutButton] = "Выйти",
            [Str.EnterSessionKeyWarning] = "Введите sessionKey.",
            [Str.SignInFailedPrefix] = "Не удалось авторизоваться: {0}",
            [Str.InvalidSessionKey] = "Неверный sessionKey.",

            [Str.SessionKeyNotEntered] = "Session key не введён.",
            [Str.CouldNotValidateSessionKey] = "Не удалось подтвердить session key.",
            [Str.CouldNotParseResponse] = "Не удалось разобрать ответ ({0}): {1}",
            [Str.SessionKeyInvalidOrExpiredValidate] = "Session key недействителен или истёк: {0}",
            [Str.OrgNotFoundInResponse] = "Не удалось найти организацию в ответе: {0}",
            [Str.ExceptionFetchingOrg] = "Исключение ({0}) при запросе организации: {1}",
            [Str.NoStoredSessionKey] = "Нет сохранённого session key. Войдите заново.",
            [Str.FiveHourFieldMissing] = "В ответе нет поля five_hour: {0}",
            [Str.SessionKeyExpiredOrInvalidUsage] = "Session key истёк или недействителен: {0}",
            [Str.ExceptionGeneric] = "Исключение ({0}): {1}\n{2}",
            [Str.CloudflareJustAMoment] = "Запрос заблокирован Cloudflare (Just a moment). Попробуйте позже.",
            [Str.CloudflareBrowserCheck] = "Cloudflare требует проверку в браузере. Попробуйте позже.",
            [Str.ServerReturnedHtml] = "Сервер вернул HTML вместо JSON (возможно, блокировка): {0}",
        },

        [AppLanguage.Uk] = new()
        {
            [Str.Loading] = "Завантаження…",
            [Str.ErrorFetchingData] = "Помилка отримання даних",
            [Str.Refreshing] = "Оновлення…",
            [Str.UpdateFailedShowingCached] = "Не вдалося оновити \u2014 показано збережені дані",
            [Str.ProviderError] = "Помилка провайдера",
            [Str.SignInRequired] = "Потрібен вхід",
            [Str.SignInBody] = "Відкрийте застосунок і вставте cookie sessionKey з claude.ai, щоб почати відстежувати використання 5-годинного вікна.",
            [Str.OpenAppToSignIn] = "Відкрити застосунок для входу",
            [Str.CurrentSession] = "Поточна сесія",
            [Str.WeeklyLimits] = "Тижневі ліміти",
            [Str.AllModels] = "Усі моделі",
            [Str.Refresh] = "Оновити",
            [Str.UsedLabel] = "Використано:",
            [Str.ResetLabel] = "Скидання:",
            [Str.ResetsInAt] = "Скидання через {0}, о {1}",
            [Str.HaventUsedFable] = "Ви ще не використовували Fable",
            [Str.NextPayment] = "Наступний платіж: {0}",
            [Str.NextPaymentWithInterval] = "Наступний платіж: {0} ({1})",
            [Str.UsageCreditsHeader] = "**Кредити використання**: {0}",
            [Str.UsageCreditsLabel] = "**Кредити використання**: ",
            [Str.CreditBalanceAndSpent] = "Поточний баланс: ${0}, витрачено: ${1}",
            [Str.PromotionalCredits] = "Промо-кредити: {0}",
            [Str.PromoTrancheWithExpiry] = "${0} до {1}",
            [Str.ExpiresLabel] = "Закінчується: {0}",
            [Str.PlanFree] = "Тариф Free",
            [Str.NoUsageLimits] = "Немає лімітів для відстеження",
            [Str.PlanPro] = "Тариф Pro",
            [Str.PlanMax] = "Тариф Max",
            [Str.PlanTeam] = "Тариф Team",
            [Str.CreditUsageOn] = "Увімк",
            [Str.CreditUsageOff] = "Вимк",
            [Str.CreditMonthlyLimitLine] = "Місячний ліміт: ${0}",
            [Str.CreditMonthlyLimitLabel] = "Місячний ліміт: ",
            [Str.CreditLimitInputPlaceholder] = "Місячний ліміт ($)",
            [Str.SetLimitButton] = "Встановити ліміт",
            [Str.CreditActionFailedPrefix] = "Не вдалося змінити використання кредитів: {0}",

            [Str.SetLimitWindowTitle] = "Місячний ліміт — Claude Widget",
            [Str.SetLimitWindowHeader] = "Встановити місячний ліміт витрат",
            [Str.SetLimitWindowBody] = "Вкажіть максимальну суму, яку claude.ai може списати за додаткове використання щомісяця після вичерпання ліміту тарифу.",
            [Str.SetLimitInputLabel] = "Місячний ліміт ($)",
            [Str.SaveButton] = "Зберегти",
            [Str.CancelButton] = "Скасувати",
            [Str.SetLimitSavingStatus] = "Збереження…",
            [Str.SetLimitSuccessStatus] = "Місячний ліміт оновлено.",
            [Str.SetLimitFailedStatus] = "Не вдалося оновити ліміт: {0}",
            [Str.SetLimitInvalidValue] = "Введіть коректну суму.",

            [Str.HourUnit] = "г",
            [Str.MinuteUnit] = "хв",
            [Str.DayUnit] = "д",

            [Str.BillingDaily] = "щоденно",
            [Str.BillingWeekly] = "щотижнево",
            [Str.BillingMonthly] = "щомісячно",
            [Str.BillingQuarterly] = "щоквартально",
            [Str.BillingYearly] = "щорічно",

            [Str.AppSubtitle] = "Віджет для панелі Windows 11 (Win + W)",
            [Str.NotSignedInTitle] = "Не авторизовано",
            [Str.NotSignedInMessage] = "Вставте sessionKey нижче, щоб увійти.",
            [Str.SignedInTitle] = "Авторизовано",
            [Str.SignedInMessage] = "Віджет активний. Відкрийте Win + W і додайте «Claude Limits».",
            [Str.AuthorizationHeader] = "Авторизація",
            [Str.AuthorizationBody] = "Відкрийте claude.ai в браузері, увійдіть в акаунт, потім у DevTools (F12) → Application → Cookies → claude.ai скопіюйте значення cookie «sessionKey» і вставте його нижче.",
            [Str.SessionKeyPlaceholder] = "Вставте sessionKey сюди",
            [Str.SignInButton] = "Увійти",
            [Str.StatusHeader] = "Статус",
            [Str.SignedInBody] = "Дані про використання відображаються тільки у віджеті. Відкрийте Win + W і додайте «Claude Limits».",
            [Str.SignOutButton] = "Вийти",
            [Str.EnterSessionKeyWarning] = "Введіть sessionKey.",
            [Str.SignInFailedPrefix] = "Не вдалося увійти: {0}",
            [Str.InvalidSessionKey] = "Невірний sessionKey.",

            [Str.SessionKeyNotEntered] = "Session key не введено.",
            [Str.CouldNotValidateSessionKey] = "Не вдалося підтвердити session key.",
            [Str.CouldNotParseResponse] = "Не вдалося розібрати відповідь ({0}): {1}",
            [Str.SessionKeyInvalidOrExpiredValidate] = "Session key недійсний або прострочений: {0}",
            [Str.OrgNotFoundInResponse] = "Не вдалося знайти організацію у відповіді: {0}",
            [Str.ExceptionFetchingOrg] = "Виняток ({0}) під час запиту організації: {1}",
            [Str.NoStoredSessionKey] = "Немає збереженого session key. Увійдіть знову.",
            [Str.FiveHourFieldMissing] = "У відповіді немає поля five_hour: {0}",
            [Str.SessionKeyExpiredOrInvalidUsage] = "Session key прострочений або недійсний: {0}",
            [Str.ExceptionGeneric] = "Виняток ({0}): {1}\n{2}",
            [Str.CloudflareJustAMoment] = "Запит заблоковано Cloudflare (Just a moment). Спробуйте пізніше.",
            [Str.CloudflareBrowserCheck] = "Cloudflare вимагає перевірку в браузері. Спробуйте пізніше.",
            [Str.ServerReturnedHtml] = "Сервер повернув HTML замість JSON (можливо, блокування): {0}",
        },
    };
}
