using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MusicLyricsWidget.Services;

/// <summary>
/// Records the default microphone through WASAPI in RAW mode, so laptop microphone arrays with built-in noise
/// suppression (e.g. Intel Smart Sound) do not gate music to silence. WASAPI converts to <see cref="WaveFormat"/>.
/// Measured on the target laptop: the processed stream (WinMM/WASAPI) was 50–98 % digital zero and never matched,
/// the RAW stream of the same Beatles song matched on every attempt.
/// </summary>
internal sealed class RawMicrophoneCapture : IWaveIn
{
    private const long BufferDuration100Ns = 10_000_000;   // 1 s WASAPI buffer
    private const int PollIntervalMilliseconds = 20;
    private const int StopJoinTimeoutMilliseconds = 2000;
    private const string ThreadName = "RawMicrophoneCapture";

    private Thread? _thread;
    private volatile bool _stopRequested;

    public RawMicrophoneCapture(WaveFormat format)
    {
        WaveFormat = format;
    }

    public WaveFormat WaveFormat { get; set; }

    /// <summary>Whether the last recording ran in RAW mode (false: the device refused RAW and processing applied).</summary>
    public bool IsRaw { get; private set; }

    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    public void StartRecording()
    {
        if (_thread != null)
            throw new InvalidOperationException("Recording has already been started.");

        _thread = new Thread(CaptureLoop) { IsBackground = true, Name = ThreadName };
        _thread.Start();
    }

    public void StopRecording() => _stopRequested = true;

    public void Dispose()
    {
        StopRecording();
        if (_thread != null && _thread != Thread.CurrentThread)
            _thread.Join(StopJoinTimeoutMilliseconds);
    }

    private void CaptureLoop()
    {
        Exception? error = null;
        AudioClient? client = null;
        try
        {
            client = CoreAudioInterop.OpenDefaultMicrophone(out var isRaw);
            IsRaw = isRaw;
            client.Initialize(
                AudioClientShareMode.Shared,
                AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality,
                BufferDuration100Ns,
                0,
                WaveFormat,
                Guid.Empty);

            var captureClient = client.AudioCaptureClient;
            client.Start();
            while (!_stopRequested)
            {
                Thread.Sleep(PollIntervalMilliseconds);
                Drain(captureClient);
            }
            client.Stop();
        }
        catch (Exception ex)
        {
            error = ex;
        }
        finally
        {
            client?.Dispose();
            RecordingStopped?.Invoke(this, new StoppedEventArgs(error));
        }
    }

    private void Drain(AudioCaptureClient captureClient)
    {
        for (var packet = captureClient.GetNextPacketSize(); packet > 0 && !_stopRequested; packet = captureClient.GetNextPacketSize())
        {
            var buffer = captureClient.GetBuffer(out var frames, out var flags);
            var bytes = new byte[frames * WaveFormat.BlockAlign];
            if ((flags & AudioClientBufferFlags.Silent) == 0)
                Marshal.Copy(buffer, bytes, 0, bytes.Length);
            captureClient.ReleaseBuffer(frames);
            DataAvailable?.Invoke(this, new WaveInEventArgs(bytes, bytes.Length));
        }
    }
}
