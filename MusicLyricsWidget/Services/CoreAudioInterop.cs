using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace MusicLyricsWidget.Services;

/// <summary>
/// The few Core Audio calls NAudio does not offer correctly: opening the default microphone with RAW processing.
/// NAudio's own IAudioClient2 declares only its three new methods, so its SetClientProperties lands on the
/// IAudioClient GetBufferSize vtable slot (returns AUDCLNT_E_NOT_INITIALIZED); the declarations here keep every slot.
/// </summary>
internal static class CoreAudioInterop
{
    private const int DataFlowCapture = 1;     // EDataFlow.eCapture
    private const int RoleMultimedia = 1;      // ERole.eMultimedia
    private const int ClsctxAll = 0x17;        // CLSCTX_ALL
    private static readonly Guid MmDeviceEnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid AudioClient2Iid = new("726778CD-F60A-4EDA-82DE-E47610CD78AA");

    /// <summary>True when Windows has a default capture endpoint (a microphone) right now.</summary>
    public static bool HasDefaultMicrophone()
    {
        var enumerator = CreateEnumerator();
        try
        {
            if (enumerator.GetDefaultAudioEndpoint(DataFlowCapture, RoleMultimedia, out var device) < 0)
                return false;
            Marshal.ReleaseComObject(device);
            return true;
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    /// <summary>
    /// Opens the default microphone. With RAW the stream bypasses driver and system processing (noise suppression,
    /// voice gating) that removes music; a device that refuses RAW is opened with its normal processing instead.
    /// </summary>
    public static AudioClient OpenDefaultMicrophone(out bool isRaw)
    {
        var enumerator = CreateEnumerator();
        try
        {
            Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(DataFlowCapture, RoleMultimedia, out var device));
            try
            {
                var iid = AudioClient2Iid;
                Marshal.ThrowExceptionForHR(device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out var client));

                var properties = new AudioClientProperties
                {
                    cbSize = (uint)Marshal.SizeOf<AudioClientProperties>(),
                    eCategory = AudioStreamCategory.Media,
                    Options = AudioClientStreamOptions.Raw,
                };
                isRaw = ((IAudioClient2WithBase)client).SetClientProperties(ref properties) >= 0;
                return new AudioClient((IAudioClient)client);
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    private static IMMDeviceEnumeratorMinimal CreateEnumerator() =>
        (IMMDeviceEnumeratorMinimal)Activator.CreateInstance(Type.GetTypeFromCLSID(MmDeviceEnumeratorClsid)!)!;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumeratorMinimal
    {
        void EnumAudioEndpoints(); // vtable placeholder, never called
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDeviceMinimal device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceMinimal
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport, Guid("726778CD-F60A-4EDA-82DE-E47610CD78AA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient2WithBase
    {
        // The 12 IAudioClient slots and IsOffloadCapable are placeholders (never called); only their count matters.
        void Initialize(); void GetBufferSize(); void GetStreamLatency(); void GetCurrentPadding();
        void IsFormatSupported(); void GetMixFormat(); void GetDevicePeriod(); void Start(); void Stop();
        void Reset(); void SetEventHandle(); void GetService(); void IsOffloadCapable();
        [PreserveSig] int SetClientProperties(ref AudioClientProperties properties);
    }
}
