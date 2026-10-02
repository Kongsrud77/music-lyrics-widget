using NAudio.Wave;

namespace MusicLyricsWidget.Services;

public class AudioCaptureService : IDisposable
{
    private WaveInEvent? _waveIn;
    private MemoryStream? _buffer;
    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly int _recordSeconds;
    private TaskCompletionSource<byte[]>? _captureCompletion;
    private int _bytesRecorded;
    private int _targetBytes;

    public AudioCaptureService(int sampleRate = 44100, int channels = 1, int recordSeconds = 10)
    {
        _sampleRate = sampleRate;
        _channels = channels;
        _recordSeconds = recordSeconds;
    }

    /// <summary>
    /// Records audio from the default microphone for the configured duration.
    /// Returns raw PCM audio data.
    /// </summary>
    public async Task<byte[]> CaptureAudioAsync(CancellationToken cancellationToken = default)
    {
        if (WaveInEvent.DeviceCount == 0)
            throw new InvalidOperationException("No microphone found. Please connect a microphone.");

        _captureCompletion = new TaskCompletionSource<byte[]>();
        _buffer = new MemoryStream();
        _bytesRecorded = 0;
        _targetBytes = _sampleRate * _channels * 2 * _recordSeconds; // 16-bit = 2 bytes per sample

        _waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(_sampleRate, 16, _channels),
            BufferMilliseconds = 500
        };

        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.RecordingStopped += OnRecordingStopped;

        using var registration = cancellationToken.Register(() =>
        {
            _waveIn?.StopRecording();
            _captureCompletion?.TrySetCanceled();
        });

        _waveIn.StartRecording();

        return await _captureCompletion.Task;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_buffer == null) return;

        var bytesToWrite = Math.Min(e.BytesRecorded, _targetBytes - _bytesRecorded);
        if (bytesToWrite > 0)
        {
            _buffer.Write(e.Buffer, 0, bytesToWrite);
            _bytesRecorded += bytesToWrite;
        }

        if (_bytesRecorded >= _targetBytes)
        {
            _waveIn?.StopRecording();
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            _captureCompletion?.TrySetException(e.Exception);
        }
        else if (_buffer != null)
        {
            _captureCompletion?.TrySetResult(_buffer.ToArray());
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

    public void Dispose()
    {
        _waveIn?.Dispose();
        _buffer?.Dispose();
    }
}
