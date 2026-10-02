namespace MusicLyricsWidget.Services;

/// <summary>
/// Records microphone audio and wraps it for upload. Abstracted so the recognition loop can be tested without a microphone.
/// </summary>
public interface IAudioCapture : IDisposable
{
    /// <summary>
    /// Records audio for the configured duration and returns raw PCM data.
    /// </summary>
    Task<byte[]> CaptureAudioAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Wraps raw PCM data in a WAV container.
    /// </summary>
    byte[] WrapInWav(byte[] pcmData);
}
