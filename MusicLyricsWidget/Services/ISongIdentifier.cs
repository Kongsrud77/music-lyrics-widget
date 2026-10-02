using MusicLyricsWidget.Models;

namespace MusicLyricsWidget.Services;

/// <summary>
/// Identifies a song from recorded audio.
/// </summary>
public interface ISongIdentifier
{
    /// <summary>
    /// Returns the identified song, or null when no music was recognized.
    /// </summary>
    Task<SongInfo?> IdentifyAsync(byte[] audioData, CancellationToken cancellationToken = default);
}
