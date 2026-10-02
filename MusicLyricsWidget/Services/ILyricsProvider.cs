using MusicLyricsWidget.Models;

namespace MusicLyricsWidget.Services;

/// <summary>
/// Looks up lyrics for an identified song.
/// </summary>
public interface ILyricsProvider
{
    /// <summary>
    /// Returns the lyrics for the song, or null when none were found.
    /// </summary>
    Task<LyricsResult?> GetLyricsAsync(SongInfo song, CancellationToken cancellationToken = default);
}
