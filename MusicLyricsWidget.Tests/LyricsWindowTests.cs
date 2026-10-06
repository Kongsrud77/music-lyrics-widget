using MusicLyricsWidget.Models;
using MusicLyricsWidget.Widget;

namespace MusicLyricsWidget.Tests;

public class LyricsWindowTests
{
    private static readonly List<LyricsLine> Lines = Enumerable.Range(1, 20)
        .Select(i => new LyricsLine { Timestamp = TimeSpan.FromSeconds(i * 5), Text = $"L{i}" }).ToList();

    private static string Range(int from, int to) => string.Join("\n", Enumerable.Range(from, to - from + 1).Select(i => $"L{i}"));

    [Fact]
    public void BeforeTheFirstLine_ShowsTheIntroMarkerAndTheFirstLines()
    {
        var w = LyricsWindow.For(Lines, -1);

        Assert.Equal(new LyricsWindowText("", "", "♪", Range(1, 11)), w);
    }

    [Fact]
    public void AtTheFirstLine_HasNoPreviousLines()
    {
        Assert.Equal(new LyricsWindowText("", "", "L1", Range(2, 12)), LyricsWindow.For(Lines, 0));
    }

    [Fact]
    public void InTheMiddle_ShowsOnePreviousForMediumAndThreeForLarge()
    {
        Assert.Equal(new LyricsWindowText("L5", Range(3, 5), "L6", Range(7, 17)), LyricsWindow.For(Lines, 5));
    }

    [Fact]
    public void NearTheStart_LargeShowsOnlyTheLinesThatExist()
    {
        Assert.Equal(new LyricsWindowText("L1", "L1", "L2", Range(3, 13)), LyricsWindow.For(Lines, 1));
    }

    [Fact]
    public void AtTheLastLine_HasNoNextLines()
    {
        Assert.Equal(new LyricsWindowText("L19", Range(17, 19), "L20", ""), LyricsWindow.For(Lines, 19));
    }

    [Fact]
    public void AnIndexPastTheEnd_IsTreatedAsTheLastLine()
    {
        Assert.Equal(LyricsWindow.For(Lines, 19), LyricsWindow.For(Lines, 25));
    }
}
