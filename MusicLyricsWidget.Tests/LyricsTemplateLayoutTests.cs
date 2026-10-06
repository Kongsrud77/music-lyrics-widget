using MusicLyricsWidget.Models;
using MusicLyricsWidget.Services;
using MusicLyricsWidget.Widget;
using Newtonsoft.Json.Linq;

namespace MusicLyricsWidget.Tests;

/// <summary>
/// The Widgets Board drops an element taller than the space left in the card, together with everything after it
/// (seen: maxLines 20 made lyrics, source and button vanish). Budget per spec, calibrated on a real medium card:
/// medium ≈ 10 caption lines, large ≈ 17.
/// </summary>
public class LyricsTemplateLayoutTests
{
    private const string PlainBinding = "${plainLyrics}";

    private static JObject Template() => JObject.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Widget", "WidgetTemplates", "LyricsTemplate.json")));

    private static JObject SizeContainer(string size) => Assert.Single(
        Template()["body"]!.OfType<JObject>(),
        b => (string?)b["type"] == "Container" && ((string?)b["$when"])?.Contains($"\"{size}\"") == true);

    private static int MaxLines(JToken block) => (int?)block["maxLines"] ?? int.MaxValue;

    [Theory]
    [InlineData("medium", 9, 9)]
    [InlineData("large", 15, 16)]
    public void EachSize_FitsItsBudget(string size, int plainBudget, int syncedBudget)
    {
        var blocks = SizeContainer(size)["items"]!.OfType<JObject>().ToList();
        var plain = Assert.Single(blocks, b => (string?)b["text"] == PlainBinding);
        var synced = blocks.Where(b => (string?)b["text"] != PlainBinding).ToList();

        Assert.All(blocks, b => Assert.True((bool?)b["wrap"]));
        Assert.InRange(MaxLines(plain), 1, plainBudget);
        Assert.Equal(3, synced.Count);
        Assert.InRange(synced.Sum(MaxLines), 3, syncedBudget);
    }

    [Fact]
    public void EveryLyricsBlock_IsInsideASizeContainer_AndHiddenWhenEmpty()
    {
        var topLevelTexts = Template()["body"]!.OfType<JObject>()
            .Where(b => (string?)b["type"] == "TextBlock")
            .Select(b => (string?)b["text"]);
        Assert.Equal(new[] { "${songTitle}", "${artist}", "Source: ${source}" }, topLevelTexts);

        foreach (var size in new[] { "medium", "large" })
            Assert.All(SizeContainer(size)["items"]!.OfType<JObject>(), b => Assert.Contains("!= ''", (string?)b["$when"]));
    }

    [Fact]
    public void TheSourceLine_ShowsOnlyOnTheLargeCard_AndNotWithoutASource()
    {
        var source = Assert.Single(
            Template()["body"]!.OfType<JObject>(),
            b => (string?)b["text"] == "Source: ${source}");

        var when = (string?)source["$when"];
        Assert.Contains("\"large\"", when);
        Assert.Contains("source != ''", when);   // "No lyrics found" and the like carry no source: no bare "Source: "
    }

    [Fact]
    public void PlainData_IsLimitedToWhatTheLargeCardShows()
    {
        var plain = string.Join("\n", Enumerable.Range(1, 40).Select(i => $"Line {i}"));
        var lyrics = new LyricsResult { PlainLyrics = plain, Source = "LRCLIB" };
        var song = new SongInfo { Title = "Come Together", Artist = "The Beatles" };

        var (_, data) = new WidgetStateManager().GetWidgetContent(RecognitionState.DisplayingLyrics, song, lyrics);

        var sent = ((string?)JObject.Parse(data)["plainLyrics"])!.Split('\n');
        Assert.Equal(Enumerable.Range(1, 15).Select(i => $"Line {i}"), sent);
    }
}
