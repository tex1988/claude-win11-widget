using System;
using System.IO;

namespace ClaudeWidget.Services;

/// <summary>
/// Shared best-effort file logger used by the COM-server entry point
/// (<see cref="ClaudeWidget.Program"/>), the widget provider
/// (<see cref="ClaudeWidget.Widgets.ClaudeUsageWidgetProvider"/>), and the
/// TLS-fingerprint fetch client (<see cref="TlsClientFetcher"/>).
///
/// Log files live under
/// <c>%LOCALAPPDATA%\ClaudeWidget\Logs\{category}.log</c> instead of the
/// Desktop — the Desktop was only ever used for quick manual debugging
/// during early development, and isn't an appropriate place for an app to
/// permanently drop files (clutters the user's Desktop, and per-user
/// %LOCALAPPDATA% is the conventional location for this kind of app-private
/// diagnostic data).
///
/// In Release builds, <see cref="Info"/> calls are suppressed entirely —
/// only <see cref="Error"/> calls are written — so a widget that refreshes
/// on a timer indefinitely doesn't grow an unbounded log file on end-user
/// machines. All <see cref="Info"/> tracing remains active in Debug builds
/// for local development/troubleshooting.
/// </summary>
internal static class Logger
{
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClaudeWidget", "Logs");

#if DEBUG
    private const bool VerboseLoggingEnabled = true;
#else
    private const bool VerboseLoggingEnabled = false;
#endif

    /// <summary>
    /// Routine tracing (widget lifecycle events, request/response summaries,
    /// success paths). Written in Debug builds only.
    /// </summary>
    public static void Info(string category, string message) => Write(category, "INFO", message, VerboseLoggingEnabled);

    /// <summary>
    /// Genuine failures (exceptions, failed fetches, failed mutations).
    /// Always written, including in Release builds.
    /// </summary>
    public static void Error(string category, string message) => Write(category, "ERROR", message, true);

    private static void Write(string category, string level, string message, bool enabled)
    {
        if (!enabled) return;

        try
        {
            Directory.CreateDirectory(LogDirectory);
            var logPath = Path.Combine(LogDirectory, $"{category}.log");
            File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}\n");
        }
        catch
        {
            // Best-effort logging only.
        }
    }
}
