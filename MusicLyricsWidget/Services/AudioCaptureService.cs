using NAudio.Wave;

namespace MusicLyricsWidget.Services;

public class AudioCaptureService : IAudioCapture
{
    private const int BitsPerSample = 16;
    private const int BytesPerSample = BitsPerSample / 8;
    private const int CaptureIdle = 0;
    private const int CaptureActive = 1;
    private const string NoMicrophoneMessage = "No microphone found. Please connect a microphone.";
    private const string CaptureInProgressMessage = "An audio capture is already in progress.";

    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly int _recordSeconds;
    private readonly Func<WaveFormat, IWaveIn> _waveInFactory;
    private readonly Func<int> _deviceCount;
    private int _captureState = CaptureIdle;

    /// <summary>
    /// Records through <see cref="RawMicrophoneCapture"/> (WASAPI RAW): the processed WinMM stream of laptop
    /// microphone arrays suppresses music to silence, so ACRCloud never matched it.
    /// </summary>
    public AudioCaptureService(int sampleRate = 44100, int channels = 1, int recordSeconds = 10)
        : this(
            sampleRate,
            channels,
            recordSeconds,
            format => new RawMicrophoneCapture(format),
            () => CoreAudioInterop.HasDefaultMicrophone() ? 1 : 0)
    {
    }

    /// <summary>
    /// Test seam: lets tests supply a fake wave-in and device count instead of the real microphone.
    /// </summary>
    internal AudioCaptureService(
        int sampleRate,
        int channels,
        int recordSeconds,
        Func<WaveFormat, IWaveIn> waveInFactory,
        Func<int> deviceCount)
    {
        _sampleRate = sampleRate;
        _channels = channels;
        _recordSeconds = recordSeconds;
        _waveInFactory = waveInFactory;
        _deviceCount = deviceCount;
    }

    /// <summary>
    /// Records audio from the default microphone for the configured duration.
    /// Returns raw PCM audio data. Only one capture can run at a time; the microphone is released after every capture.
    /// </summary>
    public async Task<byte[]> CaptureAudioAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _captureState, CaptureActive, CaptureIdle) != CaptureIdle)
            throw new InvalidOperationException(CaptureInProgressMessage);

        try
        {
            return await RecordAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _captureState, CaptureIdle);
        }
    }

    private async Task<byte[]> RecordAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_deviceCount() == 0)
            throw new InvalidOperationException(NoMicrophoneMessage);

        var targetBytes = _sampleRate * _channels * BytesPerSample * _recordSeconds;
        var completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waveIn = _waveInFactory(new WaveFormat(_sampleRate, BitsPerSample, _channels));
        var buffer = new MemoryStream(targetBytes);

        // Handlers run on the device's thread (or synchronously inside StopRecording) and can still fire after the
        // capture is over. Everything they touch, StopRecording included, happens under the gate while !finished;
        // the finally block flips 'finished' under the same gate before it disposes anything.
        var gate = new object();
        var recordedBytes = 0;
        var stopRequested = false;
        var finished = false;

        void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            lock (gate)
            {
                if (finished) return;

                var bytesToWrite = Math.Min(e.BytesRecorded, targetBytes - recordedBytes);
                if (bytesToWrite > 0)
                {
                    buffer.Write(e.Buffer, 0, bytesToWrite);
                    recordedBytes += bytesToWrite;
                }

                if (recordedBytes >= targetBytes && !stopRequested)
                {
                    stopRequested = true;
                    waveIn.StopRecording();
                }
            }
        }

        void OnRecordingStopped(object? sender, StoppedEventArgs e)
        {
            lock (gate)
            {
                if (finished) return;

                if (e.Exception != null)
                    completion.TrySetException(e.Exception);
                else
                    completion.TrySetResult(buffer.ToArray());
            }
        }

        waveIn.DataAvailable += OnDataAvailable;
        waveIn.RecordingStopped += OnRecordingStopped;
        try
        {
            waveIn.StartRecording();

            using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            return await completion.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The task is already cancelled at this point, so a RecordingStopped raised synchronously by
            // StopRecording cannot complete it with the partial buffer.
            waveIn.StopRecording();
            throw;
        }
        finally
        {
            waveIn.DataAvailable -= OnDataAvailable;
            waveIn.RecordingStopped -= OnRecordingStopped;
            lock (gate)
            {
                finished = true;
            }

            try
            {
                waveIn.Dispose();
            }
            finally
            {
                buffer.Dispose();
            }
        }
    }

    /// <summary>
    /// Wraps raw PCM data in a WAV container for ACRCloud.
    /// </summary>
    public byte[] WrapInWav(byte[] pcmData)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        int dataSize = pcmData.Length;
        int fileSize = 36 + dataSize;

        // RIFF header
        writer.Write("RIFF"u8);
        writer.Write(fileSize);
        writer.Write("WAVE"u8);

        // fmt chunk
        writer.Write("fmt "u8);
        writer.Write(16);                     // chunk size
        writer.Write((short)1);               // PCM format
        writer.Write((short)_channels);
        writer.Write(_sampleRate);
        writer.Write(_sampleRate * _channels * 2); // byte rate
        writer.Write((short)(_channels * 2)); // block align
        writer.Write((short)16);              // bits per sample

        // data chunk
        writer.Write("data"u8);
        writer.Write(dataSize);
        writer.Write(pcmData);

        return ms.ToArray();
    }

    /// <summary>
    /// Nothing to release here: every capture disposes its own wave-in and buffer when it ends.
    /// </summary>
    public void Dispose()
    {
    }
}
