using MusicLyricsWidget.Models;

namespace MusicLyricsWidget.Widget;

/// <summary>Cuts the lines around the current synced line into the blocks of the lyrics card.</summary>
internal static class LyricsWindow
{
    /// <summary>Previous lines on the large card (medium shows one).</summary>
    public const int LargePreviousLines = 3;

    /// <summary>Next lines sent; the large card shows 11, the medium card's maxLines cuts it to 6.</summary>
    public const int NextLines = 11;

    /// <summary>Shown as the current line during the intro, before the first sung line.</summary>
    public const string IntroMarker = "♪";

    private const string LineSeparator = "\n";

    public static LyricsWindowText For(IReadOnlyList<LyricsLine> lines, int index)
    {
        if (index < 0 || lines.Count == 0)
            return new LyricsWindowText(string.Empty, string.Empty, IntroMarker, Join(lines, 0, NextLines));

        var current = Math.Min(index, lines.Count - 1);
        return new LyricsWindowText(
            Join(lines, current - 1, 1),
            Join(lines, current - LargePreviousLines, LargePreviousLines),
            lines[current].Text,
            Join(lines, current + 1, NextLines));
    }

    private static string Join(IReadOnlyList<LyricsLine> lines, int start, int count)
    {
        var from = Math.Max(0, start);
        var to = Math.Min(lines.Count, start + count);
        return string.Join(LineSeparator, Enumerable.Range(from, Math.Max(0, to - from)).Select(i => lines[i].Text));
    }
}
