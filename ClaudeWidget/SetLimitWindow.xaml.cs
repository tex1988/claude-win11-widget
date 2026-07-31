using System;
using ClaudeWidget.Services;
using ClaudeWidget.Widgets;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace ClaudeWidget;

/// <summary>
/// Standalone window for adjusting the monthly pay-as-you-go spend limit,
/// opened by clicking the "Monthly limit" line on the widget card (see
/// Widgets/ClaudeUsageWidgetProvider.OpenSetLimitWindow). Kept as its own
/// small window rather than an inline Input.Number + button row on the
/// widget card itself -- there isn't enough room on the card for a usable
/// number input and label together.
/// </summary>
public sealed partial class SetLimitWindow : Window
{
    public SetLimitWindow(double initialValue)
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Resize(new SizeInt32(420, 300));
        Title = Localization.Get(Str.SetLimitWindowTitle);

        HeaderTextBlock.Text = Localization.Get(Str.SetLimitWindowHeader);
        BodyTextBlock.Text = Localization.Get(Str.SetLimitWindowBody);
        InputLabelTextBlock.Text = Localization.Get(Str.SetLimitInputLabel);
        SaveButton.Content = Localization.Get(Str.SaveButton);
        CancelButton.Content = Localization.Get(Str.CancelButton);

        LimitNumberBox.Value = initialValue;
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        double value = LimitNumberBox.Value;
        if (double.IsNaN(value) || value < 0)
        {
            ShowStatus(Localization.Get(Str.SetLimitInvalidValue), InfoBarSeverity.Warning);
            return;
        }

        SaveButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        ShowStatus(Localization.Get(Str.SetLimitSavingStatus), InfoBarSeverity.Informational);

        int limitCents = (int)Math.Round(value * 100);
        bool ok = await ClaudeApiService.SetMonthlyCreditLimitAsync(limitCents);

        if (ok)
        {
            ShowStatus(Localization.Get(Str.SetLimitSuccessStatus), InfoBarSeverity.Success);
            _ = ClaudeUsageWidgetProvider.RefreshAllActiveWidgetsAsync();
            Close();
        }
        else
        {
            SaveButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
            ShowStatus(Localization.Get(Str.SetLimitFailedStatus, ClaudeApiService.LastError ?? string.Empty), InfoBarSeverity.Error);
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusBar.Message = message;
        StatusBar.Severity = severity;
        StatusBar.IsOpen = true;
    }
}
