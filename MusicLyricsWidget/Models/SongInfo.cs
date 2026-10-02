namespace MusicLyricsWidget.Models;

public class SongInfo
{
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public string? AcrId { get; set; }
    public TimeSpan? Duration { get; set; }

    public override string ToString() => $"{Artist} - {Title}";

    public override bool Equals(object? obj)
    {
        if (obj is SongInfo other)
            return string.Equals(Title, other.Title, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Artist, other.Artist, StringComparison.OrdinalIgnoreCase);
        return false;
    }

    public override int GetHashCode() =>
        HashCode.Combine(Title.ToLowerInvariant(), Artist.ToLowerInvariant());
}
