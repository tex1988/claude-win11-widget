using System.Runtime.InteropServices;
using ClaudeWidget.Services;
using ClaudeWidget.Widgets;
using ClaudeWidget.Widgets.WidgetHelper;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using WinRT;

namespace ClaudeWidget;

/// <summary>
/// Custom entry point. The process can be launched two different ways:
///
///  1. Normal GUI launch (user opens the app / Start menu tile): runs the
///     regular WinUI App with the settings/sign-in window.
///  2. COM server launch (the Widgets host activates
///     "ClaudeWidgetProvider" per Package.appxmanifest): the OS launches
///     this exe with "-RegisterProcessAsComServer" and expects the process
///     to register the IWidgetProvider COM class and stay alive, without UI.
/// </summary>
public static class Program
{
    [DllImport("Microsoft.ui.xaml.dll")]
    private static extern void XamlCheckProcessRequirements();

    [STAThread]
    private static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Logger.Error("launch", "UNHANDLED EXCEPTION: " + e.ExceptionObject);
        };

        // Diagnostic: log every launch so we can see if/when the OS activates us
        Logger.Info("launch", "args='" + string.Join(" ", args) + "'");

        if (args.Length > 0 && args[0] == "-RegisterProcessAsComServer")
        {
            RunAsWidgetComServer();
            return;
        }

        // If we were launched via the "claudewidget:" protocol (e.g. the
        // widget's "Monthly limit" line opening the standalone limit-editor
        // window — see ClaudeUsageWidgetProvider.OpenSetLimitWindow), grab
        // the activation URI now so App can decide which window to show.
        Uri? protocolUri = TryGetProtocolActivationUri();

        XamlCheckProcessRequirements();
        ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App(protocolUri);
        });
    }

    private static Uri? TryGetProtocolActivationUri()
    {
        try
        {
            var activatedArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
            if (activatedArgs?.Kind == ExtendedActivationKind.Protocol &&
                activatedArgs.Data is Windows.ApplicationModel.Activation.IProtocolActivatedEventArgs protocolArgs)
            {
                return protocolArgs.Uri;
            }
        }
        catch { }
        return null;
    }

    private static void RunAsWidgetComServer()
    {
        Logger.Info("launch", "COM server path entered, registering provider");

        try
        {
            ComWrappersSupport.InitializeComWrappers();
            Logger.Info("launch", "ComWrappersSupport.InitializeComWrappers succeeded");

            using var manager = RegistrationManager<ClaudeUsageWidgetProvider>.RegisterProvider();
            Logger.Info("launch", "Provider registered, waiting for disposal");

            using var disposedEvent = manager.GetDisposedEvent();
            disposedEvent.WaitOne();
            Logger.Info("launch", "Disposed event signaled, exiting normally");
        }
        catch (Exception ex)
        {
            Logger.Error("launch", "FATAL exception in RunAsWidgetComServer: " + ex);
            throw;
        }
    }
}
