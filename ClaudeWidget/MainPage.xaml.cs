using System;
using ClaudeWidget.Services;
using ClaudeWidget.Widgets;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClaudeWidget;

/// <summary>
/// The companion app only handles sign-in/sign-out (pasting the sessionKey
/// cookie). All usage rendering (bars, percentages, resets) happens only in
/// the Widgets-board card — see <see cref="ClaudeUsageWidgetProvider"/> and
/// Widgets/Templates/UsageCardTemplate.json.
/// </summary>
public sealed partial class MainPage : Page
{
    public MainPage()
    {
        InitializeComponent();
        ApplyLocalizedStrings();
        Loaded += OnLoaded;
    }

    // ── Static labels (language never changes mid-session, so these are set once) ──

    private void ApplyLocalizedStrings()
    {
        SubtitleTextBlock.Text = Localization.Get(Str.AppSubtitle);
        AuthorizationHeaderTextBlock.Text = Localization.Get(Str.AuthorizationHeader);
        AuthorizationBodyTextBlock.Text = Localization.Get(Str.AuthorizationBody);
        SessionKeyTextBox.PlaceholderText = Localization.Get(Str.SessionKeyPlaceholder);
        SubmitSessionKeyButton.Content = Localization.Get(Str.SignInButton);
        StatusHeaderTextBlock.Text = Localization.Get(Str.StatusHeader);
        SignedInBodyTextBlock.Text = Localization.Get(Str.SignedInBody);
        SignOutButton.Content = Localization.Get(Str.SignOutButton);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshAuthState();
    }

    // ── Auth state ────────────────────────────────────────────────────────────

    private void RefreshAuthState()
    {
        bool ok = ClaudeApiService.IsSignedIn();
        SignInPanel.Visibility    = ok ? Visibility.Collapsed : Visibility.Visible;
        SignedInPanel.Visibility  = ok ? Visibility.Visible   : Visibility.Collapsed;

        if (ok)
        {
            StatusBar.Severity = InfoBarSeverity.Success;
            StatusBar.Title    = Localization.Get(Str.SignedInTitle);
            StatusBar.Message  = Localization.Get(Str.SignedInMessage);
        }
        else
        {
            StatusBar.Severity = InfoBarSeverity.Informational;
            StatusBar.Title    = Localization.Get(Str.NotSignedInTitle);
            StatusBar.Message  = Localization.Get(Str.NotSignedInMessage);
        }
    }

    // ── Session key sign-in ──────────────────────────────────────────────────

    private async void SubmitSessionKeyButton_Click(object sender, RoutedEventArgs e)
    {
        string key = SessionKeyTextBox.Text;
        if (string.IsNullOrWhiteSpace(key))
        {
            ShowAction(Localization.Get(Str.EnterSessionKeyWarning), InfoBarSeverity.Warning);
            return;
        }

        SubmitSessionKeyButton.IsEnabled = false;
        bool ok;
        try
        {
            ok = await ClaudeApiService.ValidateAndStoreSessionKeyAsync(key);
        }
        catch (Exception ex)
        {
            SubmitSessionKeyButton.IsEnabled = true;
            ShowAction(Localization.Get(Str.SignInFailedPrefix, ex.Message), InfoBarSeverity.Error);
            return;
        }

        SubmitSessionKeyButton.IsEnabled = true;

        if (ok)
        {
            SessionKeyTextBox.Text = string.Empty;
            RefreshAuthState();
            _ = ClaudeUsageWidgetProvider.RefreshAllActiveWidgetsAsync();
        }
        else
        {
            ShowAction(ClaudeApiService.LastError ?? Localization.Get(Str.InvalidSessionKey), InfoBarSeverity.Warning);
        }
    }

    // ── Signed-in actions ─────────────────────────────────────────────────────

    private void SignOutButton_Click(object sender, RoutedEventArgs e)
    {
        ClaudeApiService.SignOut();
        RefreshAuthState();
        _ = ClaudeUsageWidgetProvider.RefreshAllActiveWidgetsAsync();
    }

    private void ShowAction(string message, InfoBarSeverity severity)
    {
        ActionBar.Message  = message;
        ActionBar.Severity = severity;
        ActionBar.IsOpen   = true;
    }
}
