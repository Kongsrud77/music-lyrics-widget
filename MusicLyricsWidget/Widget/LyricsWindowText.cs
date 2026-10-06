namespace MusicLyricsWidget.Widget;

/// <summary>The lyrics text the card shows around the current synced line ("\n"-separated; empty when none).</summary>
internal sealed record LyricsWindowText(string PreviousLineMedium, string PreviousLinesLarge, string CurrentLine, string NextLines);
