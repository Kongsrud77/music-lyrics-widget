using MusicLyricsWidget.Models;
using MusicLyricsWidget.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MusicLyricsWidget.Widget;

public class WidgetStateManager
{
    // The large card shows 14 lyrics lines (LyricsTemplate.json caps each size with maxLines, because the Widgets
    // Board drops an element that does not fit, plus everything after it); sending more is wasted payload.
    private const int MaxLyricsLines = 14;

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
        string? errorMessage = null)
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
                CreateData(new
                {
                    songTitle = song.Title,
                    artist = song.Artist,
                    lyrics = FirstLines(lyrics.PlainLyrics, MaxLyricsLines),
                    source = lyrics.Source
                })),

            RecognitionState.DisplayingLyrics when song != null => (
                _lyricsTemplate,
                CreateData(new
                {
                    songTitle = song.Title,
                    artist = song.Artist,
                    lyrics = "No lyrics found for this song.",
                    source = ""
                })),

            RecognitionState.Error => (
                _errorTemplate,
                CreateData(new { errorMessage = errorMessage ?? "Unknown error" })),

            _ => (
                _listeningTemplate,
                CreateData(new { statusText = "Ready — tap Listen Now to start" }))
        };
    }

    private static string CreateData(object data) =>
        JsonConvert.SerializeObject(data);

    private static string FirstLines(string text, int count) =>
        string.Join("\n", text.Split('\n').Take(count));
}
