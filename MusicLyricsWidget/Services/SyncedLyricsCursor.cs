using MusicLyricsWidget.Models;

namespace MusicLyricsWidget.Services;

/// <summary>Which synced lyrics line belongs to a playback position. Lines must be sorted by timestamp.</summary>
internal static class SyncedLyricsCursor
{
    /// <summary>A line is shown this much before its timestamp, to absorb the delay until the Widgets Board redraws.</summary>
    public static readonly TimeSpan LineLead = TimeSpan.FromMilliseconds(300);

    /// <summary>Index of the line being sung at <paramref name="position"/>, or -1 before the first line.</summary>
    public static int IndexAt(IReadOnlyList<LyricsLine> lines, TimeSpan position)
    {
        var index = -1;
        for (var i = 0; i < lines.Count && lines[i].Timestamp - LineLead <= position; i++)
            index = i;
        return index;
    }

    /// <summary>Time from <paramref name="position"/> until the next line is shown; null after the last line.</summary>
    public static TimeSpan? TimeUntilNextLine(IReadOnlyList<LyricsLine> lines, TimeSpan position)
    {
        var next = IndexAt(lines, position) + 1;
        return next < lines.Count ? lines[next].Timestamp - LineLead - position : null;
    }
}
