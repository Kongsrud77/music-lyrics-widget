using MusicLyricsWidget.Services;
using MusicLyricsWidget.Tests.Fakes;
using Newtonsoft.Json.Linq;

namespace MusicLyricsWidget.Tests;

public class LyricsSelectionTests
{
    private const string Synced = "[00:01.00] Hello\n[00:02.50] World";
    private const string Plain = "Hello\nWorld";

    private static JArray Results(params JObject[] tracks) => new(tracks);

    [Fact]
    public void SelectBestMatch_KnownDuration_PicksSmallestDifference()
    {
        var results = Results(
            LrclibJson.Track(351, Synced, Plain),
            LrclibJson.Track(263, Synced, Plain),
            LrclibJson.Track(317, Synced, Plain));

        var best = LyricsService.SelectBestMatch(results, TimeSpan.FromSeconds(263));

        Assert.Same(results[1], best);
    }

    [Fact]
    public void SelectBestMatch_DifferenceIsAbsolute_ShorterEntryCanWin()
    {
        var results = Results(
            LrclibJson.Track(300, Synced, Plain),
            LrclibJson.Track(258, Synced, Plain));

        var best = LyricsService.SelectBestMatch(results, TimeSpan.FromSeconds(263));

        Assert.Same(results[1], best);
    }

    [Fact]
    public void SelectBestMatch_Tie_PrefersSyncedLyrics()
    {
        var results = Results(
            LrclibJson.Track(263, null, Plain),
            LrclibJson.Track(263, Synced, Plain));

        var best = LyricsService.SelectBestMatch(results, TimeSpan.FromSeconds(263));

        Assert.Same(results[1], best);
    }

    [Fact]
    public void SelectBestMatch_TieWithSameKindOfLyrics_KeepsFirst()
    {
        var results = Results(
            LrclibJson.Track(260, Synced, Plain),
            LrclibJson.Track(266, Synced, Plain));

        var best = LyricsService.SelectBestMatch(results, TimeSpan.FromSeconds(263));

        Assert.Same(results[0], best);
    }

    [Fact]
    public void SelectBestMatch_CloserPlainEntry_BeatsFartherSyncedEntry()
    {
        var results = Results(
            LrclibJson.Track(300, Synced, Plain),
            LrclibJson.Track(264, null, Plain));

        var best = LyricsService.SelectBestMatch(results, TimeSpan.FromSeconds(263));

        Assert.Same(results[1], best);
    }

    [Fact]
    public void SelectBestMatch_EntryWithoutDuration_IsWorstWhenDurationIsKnown()
    {
        var results = Results(
            LrclibJson.Track(null, Synced, Plain),
            LrclibJson.Track(900, Synced, Plain));

        var best = LyricsService.SelectBestMatch(results, TimeSpan.FromSeconds(263));

        Assert.Same(results[1], best);
    }

    [Fact]
    public void SelectBestMatch_NoEntryHasDuration_FallsBackToSyncedFirst()
    {
        var results = Results(
            LrclibJson.Track(null, null, Plain),
            LrclibJson.Track(null, Synced, Plain));

        var best = LyricsService.SelectBestMatch(results, TimeSpan.FromSeconds(263));

        Assert.Same(results[1], best);
    }

    [Fact]
    public void SelectBestMatch_UnknownDuration_PicksFirstSyncedEntry()
    {
        var results = Results(
            LrclibJson.Track(351, null, Plain),
            LrclibJson.Track(263, Synced, Plain),
            LrclibJson.Track(317, Synced, Plain));

        var best = LyricsService.SelectBestMatch(results, null);

        Assert.Same(results[1], best);
    }

    [Fact]
    public void SelectBestMatch_UnknownDurationAndNoSynced_PicksFirstPlainEntry()
    {
        var results = Results(
            LrclibJson.Track(351, null, null),
            LrclibJson.Track(263, null, Plain),
            LrclibJson.Track(317, null, Plain));

        var best = LyricsService.SelectBestMatch(results, null);

        Assert.Same(results[1], best);
    }

    [Fact]
    public void SelectBestMatch_EntriesWithoutLyrics_AreIgnoredEvenWhenClosest()
    {
        var results = Results(
            LrclibJson.Track(263, null, null),
            LrclibJson.Track(263, "", "   "),
            LrclibJson.Track(300, null, Plain));

        var best = LyricsService.SelectBestMatch(results, TimeSpan.FromSeconds(263));

        Assert.Same(results[2], best);
    }

    [Fact]
    public void SelectBestMatch_EntryMissingTheLyricsFields_IsIgnored()
    {
        var results = new JArray(
            new JObject { ["duration"] = 263 },
            LrclibJson.Track(300, Synced, Plain));

        var best = LyricsService.SelectBestMatch(results, TimeSpan.FromSeconds(263));

        Assert.Same(results[1], best);
    }

    [Fact]
    public void SelectBestMatch_NoEntryHasLyrics_ReturnsNull()
    {
        var results = Results(
            LrclibJson.Track(263, null, null),
            LrclibJson.Track(264, " ", ""));

        Assert.Null(LyricsService.SelectBestMatch(results, TimeSpan.FromSeconds(263)));
        Assert.Null(LyricsService.SelectBestMatch(results, null));
    }

    [Fact]
    public void SelectBestMatch_EmptyArray_ReturnsNull()
    {
        Assert.Null(LyricsService.SelectBestMatch(new JArray(), TimeSpan.FromSeconds(263)));
        Assert.Null(LyricsService.SelectBestMatch(new JArray(), null));
    }

    [Fact]
    public void ToLyricsResult_SyncedLyrics_ReturnsParsedLinesFromLrclib()
    {
        var result = LyricsService.ToLyricsResult(LrclibJson.Track(263, Synced, "ignored plain text"));

        Assert.NotNull(result);
        Assert.Equal("LRCLIB", result.Source);
        Assert.True(result.HasSyncedLyrics);
        Assert.Equal(2, result.SyncedLyrics.Count);
        Assert.Equal(TimeSpan.FromSeconds(1), result.SyncedLyrics[0].Timestamp);
        Assert.Equal("Hello", result.SyncedLyrics[0].Text);
        Assert.Equal(TimeSpan.FromMilliseconds(2500), result.SyncedLyrics[1].Timestamp);
        Assert.Equal("Hello\nWorld", result.PlainLyrics);
    }

    [Fact]
    public void ToLyricsResult_OnlyPlainLyrics_ReturnsPlainText()
    {
        var result = LyricsService.ToLyricsResult(LrclibJson.Track(263, null, Plain));

        Assert.NotNull(result);
        Assert.Equal("LRCLIB", result.Source);
        Assert.False(result.HasSyncedLyrics);
        Assert.Equal(Plain, result.PlainLyrics);
    }

    [Fact]
    public void ToLyricsResult_BlankSyncedLyrics_UsesPlainText()
    {
        var result = LyricsService.ToLyricsResult(LrclibJson.Track(263, "  ", Plain));

        Assert.NotNull(result);
        Assert.False(result.HasSyncedLyrics);
        Assert.Equal(Plain, result.PlainLyrics);
    }

    [Fact]
    public void ToLyricsResult_SyncedLyricsWithoutTimestamps_UsesPlainText()
    {
        var result = LyricsService.ToLyricsResult(LrclibJson.Track(263, "no timestamps here", Plain));

        Assert.NotNull(result);
        Assert.False(result.HasSyncedLyrics);
        Assert.Equal(Plain, result.PlainLyrics);
    }

    [Fact]
    public void ToLyricsResult_NoLyrics_ReturnsNull()
    {
        Assert.Null(LyricsService.ToLyricsResult(LrclibJson.Track(263, null, null)));
        Assert.Null(LyricsService.ToLyricsResult(LrclibJson.Track(263, "", "  ")));
        Assert.Null(LyricsService.ToLyricsResult(LrclibJson.Track(263, "no timestamps here", null)));
        Assert.Null(LyricsService.ToLyricsResult(new JObject { ["instrumental"] = true }));
    }
}
