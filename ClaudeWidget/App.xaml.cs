using Microsoft.UI.Xaml;

namespace ClaudeWidget;

public partial class App : Application
{
    private readonly Uri? _protocolUri;
    private Window? _window;

    /// <summary>Exposed so child windows can parent themselves to the main window.</summary>
    public Window? MainAppWindow => _window;

    /// <param name="protocolUri">
    /// Set when the process was launched via the "claudewidget:" protocol
    /// (see Program.TryGetProtocolActivationUri) rather than a normal
    /// Start-menu/taskbar launch. A "setlimit" host opens
    /// <see cref="SetLimitWindow"/> directly instead of the usual
    /// sign-in/settings <see cref="MainWindow"/> — see
    /// Widgets/ClaudeUsageWidgetProvider.OpenSetLimitWindow, which is the
    /// only place that launches this URI today.
    /// </param>
    public App(Uri? protocolUri = null)
    {
        _protocolUri = protocolUri;
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (_protocolUri is not null && _protocolUri.Host == "setlimit")
        {
            var limitWindow = new SetLimitWindow(ParseValueQueryParam(_protocolUri));
            _window = limitWindow;
            limitWindow.Activate();
            return;
        }

        _window = new MainWindow();
        _window.Activate();
    }

    /// <summary>
    /// Minimal "?value=123.45" query parser \u2014 avoids pulling in
    /// System.Web/UriBuilder-style helpers for a single known parameter.
    /// </summary>
    private static double ParseValueQueryParam(Uri uri)
    {
        string query = uri.Query.TrimStart('?');
        foreach (string pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq < 0) continue;
            string key = Uri.UnescapeDataString(pair[..eq]);
            if (key != "value") continue;
            string rawValue = Uri.UnescapeDataString(pair[(eq + 1)..]);
            if (double.TryParse(rawValue, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }
        return 0;
    }
}
