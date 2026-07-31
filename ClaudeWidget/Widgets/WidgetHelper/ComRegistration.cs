// Low-level COM class registration helpers used to register the widget
// provider's COM class factory when the app is launched by the Widgets host
// as a COM server (via "-RegisterProcessAsComServer").
//
// Adapted from the official Microsoft sample:
// https://github.com/microsoft/WindowsAppSDK-Samples/tree/main/Samples/Widgets/cs-console-packaged

using System;
using System.Runtime.InteropServices;
using Microsoft.Windows.Widgets.Providers;
using WinRT;

namespace ClaudeWidget.Widgets.WidgetHelper;

internal static class ComGuids
{
    public const string IClassFactory = "00000001-0000-0000-C000-000000000046";
    public const string IUnknown = "00000000-0000-0000-C000-000000000046";
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid(ComGuids.IClassFactory)]
internal interface IClassFactory
{
    [PreserveSig]
    int CreateInstance(IntPtr pUnkOuter, ref Guid riid, out IntPtr ppvObject);

    [PreserveSig]
    int LockServer(bool fLock);
}

internal static class ComClassObject
{
    [DllImport("ole32.dll")]
    private static extern int CoRegisterClassObject(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rclsid,
        [MarshalAs(UnmanagedType.IUnknown)] object pUnk,
        uint dwClsContext,
        uint flags,
        out uint lpdwRegister);

    [DllImport("ole32.dll")]
    private static extern int CoRevokeClassObject(uint dwRegister);

    private const uint CLSCTX_LOCAL_SERVER = 0x4;
    private const uint REGCLS_MULTIPLEUSE = 0x1;

    public static void Register(Guid clsid, object factory, out uint cookie)
    {
        int result = CoRegisterClassObject(clsid, factory, CLSCTX_LOCAL_SERVER, REGCLS_MULTIPLEUSE, out cookie);
        if (result != 0)
        {
            Marshal.ThrowExceptionForHR(result);
        }
    }

    public static int Revoke(uint cookie) => CoRevokeClassObject(cookie);
}

/// <summary>
/// A generic IClassFactory implementation that creates instances of TWidgetProvider
/// when requested by the COM activation machinery.
/// </summary>
internal sealed class WidgetProviderFactory<TWidgetProvider> : IClassFactory
    where TWidgetProvider : IWidgetProvider, new()
{
    private const int CLASS_E_NOAGGREGATION = -2147221232;
    private const int E_NOINTERFACE = -2147467262;

    public int CreateInstance(IntPtr pUnkOuter, ref Guid riid, out IntPtr ppvObject)
    {
        ppvObject = IntPtr.Zero;

        if (pUnkOuter != IntPtr.Zero)
        {
            Marshal.ThrowExceptionForHR(CLASS_E_NOAGGREGATION);
        }

        if (riid == typeof(TWidgetProvider).GUID || riid == Guid.Parse(ComGuids.IUnknown))
        {
            ppvObject = MarshalInspectable<TWidgetProvider>.FromManaged(new TWidgetProvider());
        }
        else
        {
            Marshal.ThrowExceptionForHR(E_NOINTERFACE);
        }

        return 0;
    }

    public int LockServer(bool fLock) => 0;
}

/// <summary>
/// Registers a widget provider's COM class factory for the lifetime of this object.
/// Dispose to revoke the registration.
/// </summary>
internal sealed class RegistrationManager<TWidgetProvider> : IDisposable
    where TWidgetProvider : IWidgetProvider, new()
{
    private readonly uint _cookie;
    private bool _disposed;
    private readonly System.Threading.ManualResetEvent _disposedEvent = new(false);

    private RegistrationManager(uint cookie)
    {
        _cookie = cookie;
    }

    public static RegistrationManager<TWidgetProvider> RegisterProvider()
    {
        ComClassObject.Register(typeof(TWidgetProvider).GUID, new WidgetProviderFactory<TWidgetProvider>(), out uint cookie);
        return new RegistrationManager<TWidgetProvider>(cookie);
    }

    public System.Threading.ManualResetEvent GetDisposedEvent() => _disposedEvent;

    public void Dispose()
    {
        if (!_disposed)
        {
            ComClassObject.Revoke(_cookie);
            _disposed = true;
            _disposedEvent.Set();
        }
        GC.SuppressFinalize(this);
    }
}
