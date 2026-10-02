using System.Text.RegularExpressions;
using MusicLyricsWidget.Models;
using MusicLyricsWidget.Services;
using MusicLyricsWidget.Widget;
using Newtonsoft.Json.Linq;

namespace MusicLyricsWidget.Tests;

public class WidgetStateManagerTests
{
    private const string ListeningTemplateFile = "ListeningTemplate.json";
    private const string LyricsTemplateFile = "LyricsTemplate.json";
    private const string ErrorTemplateFile = "ErrorTemplate.json";
    private const string PlaceholderPattern = @"\$\{(\w+)\}";

    private static readonly SongInfo TestSong = new() { Title = "Test Title", Artist = "Test Artist" };
    private static readonly LyricsResult TestLyrics = new() { PlainLyrics = "Line one\nLine two", Source = "LRCLIB" };

    private sealed record Case(RecognitionState State, SongInfo? Song, LyricsResult? Lyrics, string? ErrorMessage);

    private static readonly Dictionary<string, Case> Cases = new()
    {
        ["Idle"] = new(RecognitionState.Idle, null, null, null),
        ["Listening"] = new(RecognitionState.Listening, null, null, null),
        ["Identifying"] = new(RecognitionState.Identifying, null, null, null),
        ["FetchingLyrics"] = new(RecognitionState.FetchingLyrics, TestSong, null, null),
        ["DisplayingLyrics with lyrics"] = new(RecognitionState.DisplayingLyrics, TestSong, TestLyrics, null),
        ["DisplayingLyrics without lyrics"] = new(RecognitionState.DisplayingLyrics, TestSong, null, null),
        ["Error with message"] = new(RecognitionState.Error, null, null, "Boom"),
        ["Error without message"] = new(RecognitionState.Error, null, null, null),
    };

    public static TheoryData<string> CaseNames
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var name in Cases.Keys)
                data.Add(name);
            return data;
        }
    }

    private readonly WidgetStateManager _manager = new();

    [Fact]
    public void Idle_ReturnsListeningTemplateWithReadyText()
    {
        var (template, data) = GetContent(Cases["Idle"]);

        Assert.Equal(ReadTemplate(ListeningTemplateFile), template);
        AssertData(data, ("statusText", "Ready — tap Listen Now to start"));
    }

    [Fact]
    public void Listening_ReturnsListeningTemplateWithListeningText()
    {
        var (template, data) = GetContent(Cases["Listening"]);

        Assert.Equal(ReadTemplate(ListeningTemplateFile), template);
        AssertData(data, ("statusText", "Listening for music..."));
    }

    [Fact]
    public void Identifying_ReturnsListeningTemplateWithIdentifyingText()
    {
        var (template, data) = GetContent(Cases["Identifying"]);

        Assert.Equal(ReadTemplate(ListeningTemplateFile), template);
        AssertData(data, ("statusText", "Identifying song..."));
    }

    [Fact]
    public void FetchingLyrics_ReturnsListeningTemplateMentioningArtistAndTitle()
    {
        var (template, data) = GetContent(Cases["FetchingLyrics"]);

        Assert.Equal(ReadTemplate(ListeningTemplateFile), template);
        var statusText = Assert.Single(ParseData(data)).Value;
        Assert.Contains("Test Artist - Test Title", statusText);
    }

    [Fact]
    public void DisplayingLyrics_WithSongAndLyrics_ReturnsLyricsTemplateWithPlainLyricsAndSource()
    {
        var (template, data) = GetContent(Cases["DisplayingLyrics with lyrics"]);

        Assert.Equal(ReadTemplate(LyricsTemplateFile), template);
        AssertData(
            data,
            ("songTitle", "Test Title"),
            ("artist", "Test Artist"),
            ("lyrics", "Line one\nLine two"),
            ("source", "LRCLIB"));
    }

    [Fact]
    public void DisplayingLyrics_WithSongButNoLyrics_ReturnsNoLyricsFoundAndEmptySource()
    {
        var (template, data) = GetContent(Cases["DisplayingLyrics without lyrics"]);

        Assert.Equal(ReadTemplate(LyricsTemplateFile), template);
        AssertData(
            data,
            ("songTitle", "Test Title"),
            ("artist", "Test Artist"),
            ("lyrics", "No lyrics found for this song."),
            ("source", ""));
    }

    [Fact]
    public void Error_WithMessage_ReturnsErrorTemplateWithThatMessage()
    {
        var (template, data) = GetContent(Cases["Error with message"]);

        Assert.Equal(ReadTemplate(ErrorTemplateFile), template);
        AssertData(data, ("errorMessage", "Boom"));
    }

    [Fact]
    public void Error_WithoutMessage_ReturnsUnknownError()
    {
        var (template, data) = GetContent(Cases["Error without message"]);

        Assert.Equal(ReadTemplate(ErrorTemplateFile), template);
        AssertData(data, ("errorMessage", "Unknown error"));
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void EveryTemplatePlaceholder_ExistsInTheReturnedData(string caseName)
    {
        var (template, data) = GetContent(Cases[caseName]);

        var placeholders = Regex.Matches(template, PlaceholderPattern)
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();
        var dataProperties = ParseData(data).Keys;

        Assert.NotEmpty(placeholders);
        Assert.All(placeholders, name => Assert.Contains(name, dataProperties));
    }

    private (string template, string data) GetContent(Case testCase) =>
        _manager.GetWidgetContent(testCase.State, testCase.Song, testCase.Lyrics, testCase.ErrorMessage);

    private static string ReadTemplate(string fileName) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Widget", "WidgetTemplates", fileName));

    private static Dictionary<string, string> ParseData(string data) =>
        JObject.Parse(data).Properties().ToDictionary(p => p.Name, p => p.Value.Value<string>() ?? string.Empty);

    private static void AssertData(string data, params (string Name, string Value)[] expected) =>
        Assert.Equal(expected.ToDictionary(e => e.Name, e => e.Value), ParseData(data));
}
