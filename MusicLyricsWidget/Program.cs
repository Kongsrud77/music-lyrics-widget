using System.Runtime.InteropServices;
using Microsoft.Windows.Widgets.Providers;
using MusicLyricsWidget.Widget;
using WinRT;

namespace MusicLyricsWidget;

public static class Program
{
    private const string RegisterProcessAsComServerArgument = "-RegisterProcessAsComServer";
    private const string ComServerThreadName = "WidgetProviderComServer";

    [STAThread]
    static void Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        if (args.Length > 0 && args.Contains(RegisterProcessAsComServerArgument))
        {
            // Activated as widget provider via COM. Main stays STA for the WinUI path, so the
            // server runs on its own MTA thread (as Microsoft's reference provider does).
            var serverThread = new Thread(RunComServer) { Name = ComServerThreadName };
            serverThread.SetApartmentState(ApartmentState.MTA);
            serverThread.Start();
            serverThread.Join();
        }
        else
        {
            // Normal app launch — show settings window
            Microsoft.UI.Xaml.Application.Start(p =>
            {
                var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                    Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
                System.Threading.SynchronizationContext.SetSynchronizationContext(context);
                new App();
            });
        }

    }

    /// <summary>
    /// Serves the widget provider class object until the last widget is deleted, then revokes it.
    /// </summary>
    private static void RunComServer()
    {
        // Deliberately not disposed: a provider instance may still signal it while the process shuts down.
        var noWidgetsLeft = new ManualResetEvent(false);
        var providerClsid = Guid.Parse(WidgetProviderIds.ProviderClsid);

        using var server = new ComServer(
            providerClsid,
            () => new LyricsWidgetProvider(onLastWidgetDeleted: () => noWidgetsLeft.Set()));

        noWidgetsLeft.WaitOne();
    }
}

/// <summary>
/// Registers a COM class factory so the Windows Widget Host can activate the provider.
/// </summary>
internal class ComServer : IDisposable
{
    private uint _cookie;

    private const uint CLSCTX_LOCAL_SERVER = 4;
    private const uint REGCLS_MULTIPLEUSE = 1;
    private const uint REGCLS_SUSPENDED = 4;

    [DllImport("ole32.dll")]
    private static extern int CoRegisterClassObject(
        in Guid rclsid,
        [MarshalAs(UnmanagedType.IUnknown)] object pUnk,
        uint dwClsContext,
        uint flags,
        out uint lpdwRegister);

    [DllImport("ole32.dll")]
    private static extern int CoRevokeClassObject(uint dwRegister);

    [DllImport("ole32.dll")]
    private static extern int CoResumeClassObjects();

    public ComServer(Guid clsid, Func<IWidgetProvider> factory)
    {
        var classFactory = new WidgetProviderClassFactory(factory);
        var hr = CoRegisterClassObject(
            clsid,
            classFactory,
            CLSCTX_LOCAL_SERVER,
            REGCLS_MULTIPLEUSE | REGCLS_SUSPENDED,
            out _cookie);
        Marshal.ThrowExceptionForHR(hr);
        Marshal.ThrowExceptionForHR(CoResumeClassObjects());
    }

    public void Dispose()
    {
        if (_cookie != 0)
        {
            CoRevokeClassObject(_cookie);
            _cookie = 0;
        }
    }
}

/// <summary>
/// Creates widget providers as WinRT objects. The Widget Host calls the provider through the WinRT
/// <see cref="IWidgetProvider"/> vtable, which a classic CCW from <c>Marshal.GetIUnknownForObject</c> lacks.
/// </summary>
[ComVisible(true)]
internal class WidgetProviderClassFactory : IClassFactory
{
    private const int S_OK = 0;
    private const int CLASS_E_NOAGGREGATION = unchecked((int)0x80040110);

    private readonly Func<IWidgetProvider> _factory;

    public WidgetProviderClassFactory(Func<IWidgetProvider> factory)
    {
        _factory = factory;
    }

    public int CreateInstance(IntPtr pUnkOuter, ref Guid riid, out IntPtr ppvObject)
    {
        ppvObject = IntPtr.Zero;
        if (pUnkOuter != IntPtr.Zero)
            return CLASS_E_NOAGGREGATION;

        IntPtr unknown = MarshalInspectable<IWidgetProvider>.FromManaged(_factory());
        try
        {
            return Marshal.QueryInterface(unknown, in riid, out ppvObject);
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    public int LockServer(bool fLock) => S_OK;
}

[ComImport]
[Guid("00000001-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IClassFactory
{
    [PreserveSig]
    int CreateInstance(IntPtr pUnkOuter, ref Guid riid, out IntPtr ppvObject);

    [PreserveSig]
    int LockServer(bool fLock);
}
