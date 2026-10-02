using System.Runtime.InteropServices;
using MusicLyricsWidget.Widget;

namespace MusicLyricsWidget;

public static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        if (args.Length > 0 && args.Contains("-RegisterProcessAsComServer"))
        {
            // Activated as widget provider via COM
            var providerGuid = Guid.Parse("3B37F72B-9C04-418C-88AE-BEB6058AE758");
            using var server = new ComServer(providerGuid, () => new LyricsWidgetProvider());

            var exitEvent = new ManualResetEvent(false);
            exitEvent.WaitOne();
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

    public ComServer(Guid clsid, Func<object> factory)
    {
        var classFactory = new GenericClassFactory(factory);
        var hr = CoRegisterClassObject(
            clsid,
            classFactory,
            CLSCTX_LOCAL_SERVER,
            REGCLS_MULTIPLEUSE | REGCLS_SUSPENDED,
            out _cookie);
        Marshal.ThrowExceptionForHR(hr);
        CoResumeClassObjects();
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

[ComVisible(true)]
internal class GenericClassFactory : IClassFactory
{
    private readonly Func<object> _factory;

    public GenericClassFactory(Func<object> factory)
    {
        _factory = factory;
    }

    public int CreateInstance(IntPtr pUnkOuter, ref Guid riid, out IntPtr ppvObject)
    {
        ppvObject = IntPtr.Zero;
        if (pUnkOuter != IntPtr.Zero)
            return unchecked((int)0x80040110); // CLASS_E_NOAGGREGATION

        var instance = _factory();
        ppvObject = Marshal.GetIUnknownForObject(instance);
        return 0; // S_OK
    }

    public int LockServer(bool fLock) => 0;
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
