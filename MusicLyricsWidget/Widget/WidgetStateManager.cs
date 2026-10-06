using MusicLyricsWidget.Models;
using MusicLyricsWidget.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MusicLyricsWidget.Widget;

public class WidgetStateManager
{
    // Plain lyrics sent: the large card shows 15 lines, the medium card's maxLines cuts it to 9. LyricsTemplate.json
    // caps every block per size, because the Widgets Board drops an element that does not fit plus all after it.
    private const int MaxPlainLyricsLines = 15;
    private const string NoLyricsMessage = "No lyrics found for this song.";

    private readonly string _listeningTemplate;
    private readonly string _lyricsTemplate;
    private readonly string _errorTemplate;

    public WidgetStateManager()
    {
        var basePath = Path.Combine(AppContext.BaseDirectory, "Widget", "WidgetTemplates");
        _listeningTemplate = File.ReadAllText(Path.Combine(basePath, "ListeningTemplate.json"));
        _lyricsTemplate = File.ReadAllText(Path.Combine(basePath, "LyricsTemplate.json"));
        _errorTemplate = File.ReadAllText(Path.Combine(basePath, "ErrorTemplate.json"));
    }

    public (string template, string data) GetWidgetContent(
        RecognitionState state,
        SongInfo? song = null,
        LyricsResult? lyrics = null,
        string? errorMessage = null,
        int? currentLineIndex = null)
    {
        return state switch
        {
            RecognitionState.Listening => (
                _listeningTemplate,
                CreateData(new { statusText = "Listening for music..." })),

            RecognitionState.Identifying => (
                _listeningTemplate,
                CreateData(new { statusText = "Identifying song..." })),

            RecognitionState.NoMatch => (
                _listeningTemplate,
                CreateData(new { statusText = "No music recognised yet — listening again shortly..." })),

            RecognitionState.FetchingLyrics => (
                _listeningTemplate,
                CreateData(new { statusText = $"Found: {song} — fetching lyrics..." })),

            RecognitionState.DisplayingLyrics when song != null && lyrics != null => (
                _lyricsTemplate,
                LyricsData(song, lyrics, currentLineIndex)),

            RecognitionState.DisplayingLyrics when song != null => (
                _lyricsTemplate,
                LyricsData(song, null, null)),

            RecognitionState.Error => (
                _errorTemplate,
                CreateData(new { errorMessage = errorMessage ?? "Unknown error" })),

            _ => (
                _listeningTemplate,
                CreateData(new { statusText = "Ready — tap Listen Now to start" }))
        };
    }

    /// <summary>
    /// Lyrics-card data. With synced lines and a known current line: the window around it; otherwise plain text from
    /// the start (or the no-lyrics message). Every field is always present, so the template's $when tests are exact.
    /// </summary>
    private static string LyricsData(SongInfo song, LyricsResult? lyrics, int? currentLineIndex)
    {
        var window = lyrics is { HasSyncedLyrics: true } && currentLineIndex != null
            ? LyricsWindow.For(lyrics.SyncedLyrics, currentLineIndex.Value)
            : null;

        var plain = window != null ? string.Empty
            : lyrics == null ? NoLyricsMessage
            : FirstLines(lyrics.PlainLyrics, MaxPlainLyricsLines);

        return CreateData(new
        {
            songTitle = song.Title,
            artist = song.Artist,
            source = lyrics?.Source ?? string.Empty,
            plainLyrics = plain,
            previousLineMedium = window?.PreviousLineMedium ?? string.Empty,
            previousLinesLarge = window?.PreviousLinesLarge ?? string.Empty,
            currentLine = window?.CurrentLine ?? string.Empty,
            nextLines = window?.NextLines ?? string.Empty,
        });
    }

    private static string CreateData(object data) =>
        JsonConvert.SerializeObject(data);

    private static string FirstLines(string text, int count) =>
        string.Join("\n", text.Split('\n').Take(count));
}
