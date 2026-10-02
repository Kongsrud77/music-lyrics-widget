namespace MusicLyricsWidget.Models;

public class LyricsResult
{
    public string PlainLyrics { get; set; } = string.Empty;
    public List<LyricsLine> SyncedLyrics { get; set; } = new();
    public bool HasSyncedLyrics => SyncedLyrics.Count > 0;
    public string Source { get; set; } = string.Empty;
}

public class LyricsLine
{
    public TimeSpan Timestamp { get; set; }
    public string Text { get; set; } = string.Empty;

    public override string ToString() =>
        $"[{Timestamp:mm\\:ss\\.ff}] {Text}";
}
