using MusicLyricsWidget.Models;
using MusicLyricsWidget.Services;
using MusicLyricsWidget.Widget;
using Newtonsoft.Json.Linq;

namespace MusicLyricsWidget.Tests;

/// <summary>
/// The Widgets Board drops an element that is taller than the space left in the card, together with everything
/// after it: with "maxLines": 20 the lyrics, the source and the button all vanished and only title + artist showed.
/// Budget (Microsoft widget design fundamentals: 16 px margins, 48 px attribution area, caption text 12/16 px):
/// medium 300x304 leaves room for about 8 caption lines after title, artist and button, large 300x462 about 16.
/// </summary>
public class LyricsTemplateLayoutTests
{
    private const string LyricsBinding = "${lyrics}";
    private const int MaxMediumLyricsLines = 6;
    private const int MaxLargeLyricsLines = 14;

    private static JObject Template() => JObject.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Widget", "WidgetTemplates", "LyricsTemplate.json")));

    private static List<JObject> LyricsBlocks() =>
        Template().Descendants().OfType<JObject>().Where(o => (string?)o["text"] == LyricsBinding).ToList();

    [Theory]
    [InlineData("medium", MaxMediumLyricsLines)]
    [InlineData("large", MaxLargeLyricsLines)]
    public void EachWidgetSize_HasOneLyricsBlock_CappedToFitTheCard(string size, int maxLines)
    {
        var block = Assert.Single(LyricsBlocks(), b => ((string?)b["$when"])?.Contains($"\"{size}\"") == true);

        Assert.True((bool?)block["wrap"]);
        Assert.InRange((int?)block["maxLines"] ?? int.MaxValue, 1, maxLines);
    }

    [Fact]
    public void EveryLyricsBlock_IsTiedToAWidgetSize_SoNoUncappedBlockRendersEverywhere()
    {
        Assert.All(LyricsBlocks(), block => Assert.Contains("$host.widgetSize", (string?)block["$when"]));
    }

    [Fact]
    public void LyricsData_IsLimitedToWhatTheLargestCardShows()
    {
        var plain = string.Join("\n", Enumerable.Range(1, 40).Select(i => $"Line {i}"));
        var lyrics = new LyricsResult { PlainLyrics = plain, Source = "LRCLIB" };
        var song = new SongInfo { Title = "Come Together", Artist = "The Beatles" };

        var (_, data) = new WidgetStateManager().GetWidgetContent(RecognitionState.DisplayingLyrics, song, lyrics);

        var sent = ((string?)JObject.Parse(data)["lyrics"])!.Split('\n');
        Assert.Equal(Enumerable.Range(1, MaxLargeLyricsLines).Select(i => $"Line {i}"), sent);
    }
}
