using MusicLyricsWidget.Services;

namespace MusicLyricsWidget.Tests;

public class LrcParserTests
{
    [Fact]
    public void ParseLrc_SingleLine_ReturnsTimestampAndText()
    {
        var lines = LyricsService.ParseLrc("[01:23.45] Hello");

        var line = Assert.Single(lines);
        Assert.Equal(new TimeSpan(0, 0, 1, 23, 450), line.Timestamp);
        Assert.Equal("Hello", line.Text);
    }

    [Fact]
    public void ParseLrc_ThreeDigitFraction_IsTreatedAsMilliseconds()
    {
        var lines = LyricsService.ParseLrc("[00:05.123] x");

        var line = Assert.Single(lines);
        Assert.Equal(TimeSpan.FromMilliseconds(5123), line.Timestamp);
        Assert.Equal("x", line.Text);
    }

    [Fact]
    public void ParseLrc_EmptyTextLines_AreSkipped()
    {
        var lines = LyricsService.ParseLrc("[00:01.00]\n[00:02.00]    \n[00:03.00] Kept");

        var line = Assert.Single(lines);
        Assert.Equal("Kept", line.Text);
    }

    [Fact]
    public void ParseLrc_CrlfInput_ParsesAndTextHasNoCarriageReturn()
    {
        var lines = LyricsService.ParseLrc("[00:01.00] First\r\n[00:02.00] Second\r\n");

        Assert.Equal(new[] { "First", "Second" }, lines.Select(l => l.Text));
        Assert.All(lines, l => Assert.DoesNotContain('\r', l.Text));
    }

    [Fact]
    public void ParseLrc_OutOfOrderInput_IsReturnedSortedAscending()
    {
        var lines = LyricsService.ParseLrc("[00:30.00] Third\n[00:10.00] First\n[00:20.00] Second");

        Assert.Equal(new[] { "First", "Second", "Third" }, lines.Select(l => l.Text));
        Assert.Equal(
            new[] { TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30) },
            lines.Select(l => l.Timestamp));
    }

    [Fact]
    public void ParseLrc_NonLrcLines_AreIgnored()
    {
        var lines = LyricsService.ParseLrc("[ar:Some Artist]\nplain text without timestamp\n[00:01.00] Real\n");

        var line = Assert.Single(lines);
        Assert.Equal("Real", line.Text);
    }
}
