using MusicLyricsWidget.Models;
using MusicLyricsWidget.Services;

namespace MusicLyricsWidget.Tests;

public class SyncedLyricsCursorTests
{
    private static readonly List<LyricsLine> Lines = new[] { 10, 15, 20 }
        .Select(s => new LyricsLine { Timestamp = TimeSpan.FromSeconds(s), Text = $"at {s}" }).ToList();

    // Whole milliseconds: TimeSpan.FromSeconds(9.7) can land a tick short of the boundary under test.
    private static TimeSpan Ms(int milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    [Theory]
    [InlineData(0, -1)]
    [InlineData(9_699, -1)]
    [InlineData(9_700, 0)]      // exactly LineLead before the first line
    [InlineData(14_699, 0)]
    [InlineData(14_700, 1)]
    [InlineData(100_000, 2)]
    public void IndexAt_ShowsEachLineLineLeadEarly(int milliseconds, int expected)
    {
        Assert.Equal(Ms(300), SyncedLyricsCursor.LineLead);
        Assert.Equal(expected, SyncedLyricsCursor.IndexAt(Lines, Ms(milliseconds)));
    }

    [Fact]
    public void IndexAt_WithoutLines_IsBeforeTheFirst() =>
        Assert.Equal(-1, SyncedLyricsCursor.IndexAt(new List<LyricsLine>(), Ms(5_000)));

    [Theory]
    [InlineData(9_000, 700)]
    [InlineData(12_000, 2_700)]
    public void TimeUntilNextLine_CountsToTheNextLinesLeadPoint(int milliseconds, int expected) =>
        Assert.Equal(Ms(expected), SyncedLyricsCursor.TimeUntilNextLine(Lines, Ms(milliseconds)));

    [Fact]
    public void TimeUntilNextLine_AfterTheLastLine_IsNull() =>
        Assert.Null(SyncedLyricsCursor.TimeUntilNextLine(Lines, Ms(25_000)));
}
