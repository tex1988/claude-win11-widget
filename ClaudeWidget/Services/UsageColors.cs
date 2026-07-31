namespace ClaudeWidget.Services;

/// <summary>Which usage bucket a bar represents (each has its own base color).</summary>
public enum UsageBucket
{
    Session,
    Weekly,
    Fable,
}

/// <summary>
/// Threshold-based coloring shared between the companion app UI and the
/// Widgets-board AdaptiveCard, mirroring the reference Electron app's
/// getBackgroundColor() logic (warn at 75%, danger at 90%).
/// </summary>
public static class UsageColors
{
    public const double WarnThreshold = 75;
    public const double DangerThreshold = 90;

    private const string SessionHex = "#8B5CF6"; // purple
    private const string WeeklyHex  = "#3B82F6"; // blue
    private const string FableHex   = "#06B6D4"; // cyan
    private const string UnusedHex  = "#4B5563"; // gray (unused bucket, e.g. Fable at 0%)
    private const string WarnHex    = "#F59E0B"; // amber
    private const string DangerHex  = "#EF4444"; // red

    /// <summary>Hex color (for WinUI SolidColorBrush) for a given percent/bucket.</summary>
    public static string GetHex(double percent, UsageBucket bucket, bool hasStarted = true)
    {
        if (bucket == UsageBucket.Fable && !hasStarted) return UnusedHex;
        if (percent >= DangerThreshold) return DangerHex;
        if (percent >= WarnThreshold) return WarnHex;
        return bucket switch
        {
            UsageBucket.Session => SessionHex,
            UsageBucket.Fable => FableHex,
            _ => WeeklyHex,
        };
    }

    /// <summary>
    /// AdaptiveCard <c>ProgressBar</c>/<c>TextBlock</c> "color" token closest
    /// to the reference app's color at this percent (the Widgets host only
    /// supports the fixed enum: Default, Dark, Light, Accent, Good, Warning,
    /// Attention — no arbitrary hex fills).
    /// </summary>
    public static string GetCardStyle(double percent, UsageBucket bucket, bool hasStarted = true)
    {
        if (bucket == UsageBucket.Fable && !hasStarted) return "Default"; // gray (label only)
        if (percent >= DangerThreshold) return "Attention"; // red
        if (percent >= WarnThreshold) return "Warning";     // amber
        return bucket switch
        {
            UsageBucket.Session => "Good",   // green
            UsageBucket.Fable => "Accent",   // blue-ish
            _ => "Accent",                    // blue
        };
    }
}
