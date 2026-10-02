using MusicLyricsWidget.Services;

namespace MusicLyricsWidget.Tests.Fakes;

/// <summary>Returns a fixed PCM buffer, optionally held at a <see cref="BlockingGate"/>.</summary>
internal sealed class FakeAudioCapture : IAudioCapture
{
    private static readonly byte[] Pcm = { 1, 2, 3, 4 };

    private int _captureCalls;

    public BlockingGate? Gate { get; set; }

    public int CaptureCalls => Volatile.Read(ref _captureCalls);

    public int DisposeCalls { get; private set; }

    public async Task<byte[]> CaptureAudioAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _captureCalls);
        if (Gate != null)
            await Gate.WaitAsync();
        return Pcm;
    }

    public byte[] WrapInWav(byte[] pcmData) => pcmData;

    public void Dispose() => DisposeCalls++;
}
