# Synkroniserede lyrics Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The widget card highlights the lyrics line being sung, with context lines, and moves on by itself while the Widgets panel is open.

**Architecture:** ACRCloud's offsets give the song position at the end of the recorded sample; the orchestrator anchors it to the capture-end timestamp. A new `LyricsClock` (owned by the orchestrator, driven by .NET 8 `TimeProvider`) uses the pure `SyncedLyricsCursor` to fire at each line change. `WidgetStateManager` turns the current line into a window (`LyricsWindow`) that a size-aware template renders.

**Tech Stack:** C# 12 / .NET 8 / WinUI 3 / Windows App SDK 1.5, Newtonsoft.Json, NAudio 2.2.1, xUnit 2.9, Microsoft.Extensions.TimeProvider.Testing (tests only).

**Spec:** `docs/superpowers/specs/2026-10-06-synced-lyrics-design.md`

## Global Constraints

- Build: `dotnet build MusicLyricsWidget.sln -c Debug -p:Platform=x64` — 0 errors, 0 warnings.
- Test: `dotnet test MusicLyricsWidget.Tests/MusicLyricsWidget.Tests.csproj -c Debug -p:Platform=x64` — all green, output pristine. Tests never touch the network, the microphone, or WinRT `WidgetManager`.
- The test project compiles app sources via linked `<Compile Include>` items; `Services/*.cs` and `Models/*.cs` are wildcard-linked, files under `Widget/` are linked one by one. Never link `Program.cs`, `App.xaml.cs`, `MainWindow.xaml.cs`, `Widget/LyricsWidgetProvider.cs`.
- No new NuGet packages in the app project. The test project may add `Microsoft.Extensions.TimeProvider.Testing` (latest stable that restores for `net8.0-windows`).
- C# style: file-scoped namespaces, `_camelCase` fields, XML `<summary>` on public/internal members, one type per file for new types, named constants (no magic numbers/strings).
- Error handling: no empty `catch`; a timer callback never lets an exception escape (it would crash the widget process).
- `LineLead` = 300 ms. Intro marker = "♪". Lines are sorted by timestamp (`LyricsService.ParseLrc` sorts them).
- Card budget (spec, calibrated): medium — synced 1 previous / ≤2 current / 6 next (≤ 9 lines), plain 9; large — synced 3 / ≤2 / 11 (≤ 16 lines) + "Source", plain 15 + "Source". Title and artist `maxLines` 1.
- Data fields of the lyrics card, always all present (empty strings when unused): `songTitle`, `artist`, `source`, `plainLyrics`, `previousLineMedium`, `previousLinesLarge`, `currentLine`, `nextLines`. "No lyrics found for this song." goes in `plainLyrics`.
- Never read or print `%LOCALAPPDATA%\MusicLyricsWidget\settings.json` (real secret). Do not register/deploy the package or start the app; the controller deploys.
- Commits: Danish, imperative, explain why, one logical change each, ending with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Branch `feature/synced-lyrics`; never commit to `main`.

---

### Task 0: Verify ACRCloud offset semantics (controller + user, before Task 1)

Not a subagent task. With music playing, run the diagnostic probe so it prints `play_offset_ms`, `db_begin/end_time_offset_ms`, `sample_begin/end_time_offset_ms` and `duration_ms` for 2–3 real matches of a 10 s sample. Accept the spec formula when `db_end + (10 s − sample_end)` and `play_offset_ms` agree within 0.5 s. If they disagree, rule which one is the position at sample end, amend Task 1 accordingly, and ledger the ruling.

### Task 1: Song position at the end of the sample

**Files:**
- Modify: `MusicLyricsWidget/Models/SongInfo.cs`
- Modify: `MusicLyricsWidget/Services/SongIdentificationService.cs`
- Create: `MusicLyricsWidget.Tests/AcrCloudPositionTests.cs`

**Interfaces:**
- Produces: `SongInfo.PositionAtSampleEnd` (`TimeSpan?`); `SongIdentificationService.ParseResponse(string json, TimeSpan? sampleDuration = null)`; `internal static TimeSpan? SongIdentificationService.PositionAtSampleEnd(JToken music, TimeSpan? sampleDuration, TimeSpan? songDuration)`; `internal static TimeSpan? SongIdentificationService.WavDuration(byte[] wav)`.

- [ ] **Step 1: Write the failing tests** — `MusicLyricsWidget.Tests/AcrCloudPositionTests.cs`:

```csharp
using MusicLyricsWidget.Services;
using Newtonsoft.Json.Linq;

namespace MusicLyricsWidget.Tests;

public class AcrCloudPositionTests
{
    private static readonly TimeSpan TenSeconds = TimeSpan.FromSeconds(10);

    private static JToken Music(object fields) => JObject.FromObject(fields);

    [Fact]
    public void Offsets_GiveMatchedDatabaseEndPlusTheRestOfTheSample()
    {
        var music = Music(new { db_end_time_offset_ms = 9280, sample_end_time_offset_ms = 9280, play_offset_ms = 9040 });

        Assert.Equal(TimeSpan.FromMilliseconds(10_000), SongIdentificationService.PositionAtSampleEnd(music, TenSeconds, null));
    }

    [Fact]
    public void WithoutDbAndSampleOffsets_PlayOffsetIsUsed()
    {
        var music = Music(new { play_offset_ms = 9040 });

        Assert.Equal(TimeSpan.FromMilliseconds(9040), SongIdentificationService.PositionAtSampleEnd(music, TenSeconds, null));
    }

    [Fact]
    public void WithoutSampleDuration_PlayOffsetIsUsed()
    {
        var music = Music(new { db_end_time_offset_ms = 9280, sample_end_time_offset_ms = 9280, play_offset_ms = 9040 });

        Assert.Equal(TimeSpan.FromMilliseconds(9040), SongIdentificationService.PositionAtSampleEnd(music, null, null));
    }

    [Fact]
    public void WithoutAnyOffset_PositionIsUnknown()
    {
        Assert.Null(SongIdentificationService.PositionAtSampleEnd(Music(new { title = "x" }), TenSeconds, null));
    }

    [Fact]
    public void Position_IsClampedToTheSongDuration()
    {
        var music = Music(new { play_offset_ms = 400_000 });

        Assert.Equal(TimeSpan.FromSeconds(300), SongIdentificationService.PositionAtSampleEnd(music, TenSeconds, TimeSpan.FromSeconds(300)));
    }

    [Fact]
    public void NegativePosition_IsClampedToZero()
    {
        var music = Music(new { db_end_time_offset_ms = 100, sample_end_time_offset_ms = 5000 });

        Assert.Equal(TimeSpan.Zero, SongIdentificationService.PositionAtSampleEnd(music, TimeSpan.FromSeconds(1), null));
    }

    [Fact]
    public void WavDuration_ReadsTheCanonicalHeader()
    {
        var wav = new AudioCaptureService(44100, 1, 1).WrapInWav(new byte[88_200]);

        Assert.Equal(TimeSpan.FromSeconds(1), SongIdentificationService.WavDuration(wav));
    }

    [Fact]
    public void WavDuration_OfTooShortData_IsUnknown()
    {
        Assert.Null(SongIdentificationService.WavDuration(new byte[10]));
    }

    [Fact]
    public void ParseResponse_FillsPositionAtSampleEnd()
    {
        const string json = """
            { "status": { "code": 0, "msg": "Success" },
              "metadata": { "music": [ { "title": "Come Together", "artists": [ { "name": "The Beatles" } ],
                "duration_ms": 259000, "play_offset_ms": 9040, "db_end_time_offset_ms": 9280, "sample_end_time_offset_ms": 9280 } ] } }
            """;

        var song = SongIdentificationService.ParseResponse(json, TenSeconds);

        Assert.NotNull(song);
        Assert.Equal(TimeSpan.FromMilliseconds(10_000), song.PositionAtSampleEnd);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test MusicLyricsWidget.Tests/MusicLyricsWidget.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~AcrCloudPositionTests"`
Expected: build FAILS (`PositionAtSampleEnd`, `WavDuration` and the 2-argument `ParseResponse` do not exist).

- [ ] **Step 3: Implement** — in `SongInfo.cs`, after `Duration`:

```csharp
    /// <summary>Song position when the identified sample ended (from ACRCloud's offsets); null when unknown.</summary>
    public TimeSpan? PositionAtSampleEnd { get; set; }
```

In `SongIdentificationService.cs`, add constants next to the existing ones:

```csharp
    // AudioCaptureService.WrapInWav writes the canonical 44-byte PCM header.
    private const int WavHeaderSize = 44;
    private const int WavByteRateOffset = 28;
    private const int WavDataSizeOffset = 40;
```

In `IdentifyAsync`, replace `return ParseResponse(json);` with `return ParseResponse(json, WavDuration(audioData));`. Change the signature to `internal static SongInfo? ParseResponse(string json, TimeSpan? sampleDuration = null)` and, after the `duration_ms` block, add `song.PositionAtSampleEnd = PositionAtSampleEnd(music, sampleDuration, song.Duration);`. Add:

```csharp
    /// <summary>
    /// Song position at the end of the sample: where the match ended in the song plus the part of the sample after
    /// the match. Falls back to play_offset_ms when that cannot be computed; null without any offset. Clamped to the song.
    /// </summary>
    internal static TimeSpan? PositionAtSampleEnd(JToken music, TimeSpan? sampleDuration, TimeSpan? songDuration)
    {
        var dbEnd = music["db_end_time_offset_ms"]?.Value<long?>();
        var sampleEnd = music["sample_end_time_offset_ms"]?.Value<long?>();
        var playOffset = music["play_offset_ms"]?.Value<long?>();

        TimeSpan? position = dbEnd != null && sampleEnd != null && sampleDuration != null
            ? TimeSpan.FromMilliseconds(dbEnd.Value) + (sampleDuration.Value - TimeSpan.FromMilliseconds(sampleEnd.Value))
            : playOffset != null ? TimeSpan.FromMilliseconds(playOffset.Value) : null;

        if (position == null)
            return null;
        if (position < TimeSpan.Zero)
            return TimeSpan.Zero;
        return songDuration != null && position > songDuration ? songDuration : position;
    }

    /// <summary>Length of a canonical PCM WAV (as WrapInWav writes it); null when the data is not one.</summary>
    internal static TimeSpan? WavDuration(byte[] wav)
    {
        if (wav.Length < WavHeaderSize)
            return null;

        var byteRate = BitConverter.ToInt32(wav, WavByteRateOffset);
        var dataSize = BitConverter.ToInt32(wav, WavDataSizeOffset);
        return byteRate > 0 ? TimeSpan.FromSeconds((double)dataSize / byteRate) : null;
    }
```

- [ ] **Step 4: Run the new tests, then the full suite** — all green, 0 warnings.
- [ ] **Step 5: Commit** — `Læs sangens position ved klippets slutning fra ACRCloud` (why: the anchor for synced lyrics).

### Task 2: Line cursor and lyrics window (pure functions)

**Files:**
- Create: `MusicLyricsWidget/Services/SyncedLyricsCursor.cs`
- Create: `MusicLyricsWidget/Widget/LyricsWindowText.cs`, `MusicLyricsWidget/Widget/LyricsWindow.cs`
- Modify: `MusicLyricsWidget.Tests/MusicLyricsWidget.Tests.csproj` (link the two new `Widget/` files like `WidgetStateManager.cs`)
- Create: `MusicLyricsWidget.Tests/SyncedLyricsCursorTests.cs`, `MusicLyricsWidget.Tests/LyricsWindowTests.cs`

**Interfaces:**
- Produces: `SyncedLyricsCursor.LineLead` (`TimeSpan`, 300 ms); `int SyncedLyricsCursor.IndexAt(IReadOnlyList<LyricsLine> lines, TimeSpan position)` (−1 before the first line); `TimeSpan? SyncedLyricsCursor.TimeUntilNextLine(IReadOnlyList<LyricsLine> lines, TimeSpan position)` (null after the last); `record LyricsWindowText(string PreviousLineMedium, string PreviousLinesLarge, string CurrentLine, string NextLines)`; `LyricsWindowText LyricsWindow.For(IReadOnlyList<LyricsLine> lines, int index)`; constants `LyricsWindow.LargePreviousLines = 3`, `LyricsWindow.NextLines = 11`, `LyricsWindow.IntroMarker = "♪"`.

- [ ] **Step 1: Write the failing tests**

`SyncedLyricsCursorTests.cs`:

```csharp
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
```

`LyricsWindowTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run to verify they fail** (build fails: types missing).
- [ ] **Step 3: Implement**

`Services/SyncedLyricsCursor.cs`:

```csharp
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
```

`Widget/LyricsWindowText.cs`:

```csharp
namespace MusicLyricsWidget.Widget;

/// <summary>The lyrics text the card shows around the current synced line ("\n"-separated; empty when none).</summary>
internal sealed record LyricsWindowText(string PreviousLineMedium, string PreviousLinesLarge, string CurrentLine, string NextLines);
```

`Widget/LyricsWindow.cs`:

```csharp
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
```

Link in the test csproj next to `WidgetStateManager.cs`:

```xml
    <Compile Include="..\MusicLyricsWidget\Widget\LyricsWindow.cs" Link="App\Widget\LyricsWindow.cs" />
    <Compile Include="..\MusicLyricsWidget\Widget\LyricsWindowText.cs" Link="App\Widget\LyricsWindowText.cs" />
```

- [ ] **Step 4: Run the new tests, then the full suite** — all green.
- [ ] **Step 5: Commit** — `Tilføj linjeberegning og lyrics-vindue til synkroniserede lyrics`.

### Task 3: LyricsClock (timer driven by TimeProvider)

**Files:**
- Create: `MusicLyricsWidget/Services/LyricsClock.cs`
- Modify: `MusicLyricsWidget.Tests/MusicLyricsWidget.Tests.csproj` (add `Microsoft.Extensions.TimeProvider.Testing`)
- Create: `MusicLyricsWidget.Tests/LyricsClockTests.cs`

**Interfaces:**
- Consumes: `SyncedLyricsCursor.IndexAt`, `SyncedLyricsCursor.TimeUntilNextLine`.
- Produces: `internal sealed class LyricsClock : IDisposable` with `LyricsClock(TimeProvider time)`, `int? CurrentLineIndex`, `event Action? LineChanged`, `bool Track(IReadOnlyList<LyricsLine> lines, TimeSpan positionAtTimestamp, long timestamp, TimeSpan? songDuration)`, `bool Refresh()`, `void Run()`, `void Halt()`, `void Reset()`, `void Dispose()`.

- [ ] **Step 1: Add the package** — `dotnet add MusicLyricsWidget.Tests/MusicLyricsWidget.Tests.csproj package Microsoft.Extensions.TimeProvider.Testing` (keep the version it picks if it restores for `net8.0-windows`).

- [ ] **Step 2: Write the failing tests** — `LyricsClockTests.cs`:

```csharp
using Microsoft.Extensions.Time.Testing;
using MusicLyricsWidget.Models;
using MusicLyricsWidget.Services;

namespace MusicLyricsWidget.Tests;

public class LyricsClockTests
{
    // Lines at 10, 15, 20 and 25 s: shown at 9.7, 14.7, 19.7 and 24.7 s (300 ms lead).
    private static readonly List<LyricsLine> Lines = new[] { 10, 15, 20, 25 }
        .Select(s => new LyricsLine { Timestamp = TimeSpan.FromSeconds(s), Text = $"at {s}" }).ToList();

    private readonly FakeTimeProvider _time = new();
    private readonly LyricsClock _clock;
    private int _changes;

    public LyricsClockTests()
    {
        _clock = new LyricsClock(_time);
        _clock.LineChanged += () => _changes++;
    }

    // Whole milliseconds (boundaries are exact). FakeTimeProvider.Advance moves the clock to the END of the step
    // before firing due timers, so a test that counts every line advances one line at a time.
    private static TimeSpan Ms(int milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    private bool TrackAt(int positionMs, int? durationMs = null) =>
        _clock.Track(Lines, Ms(positionMs), _time.GetTimestamp(), durationMs is { } d ? Ms(d) : null);

    [Fact]
    public void Track_ComputesTheLineWithoutRaisingAnEvent()
    {
        Assert.True(TrackAt(12_000));
        Assert.Equal(0, _clock.CurrentLineIndex);
        Assert.Equal(0, _changes);
    }

    [Fact]
    public void Run_RaisesExactlyAtEachLineChange_AndStopsAfterTheLastLine()
    {
        TrackAt(12_000);
        _clock.Run();

        _time.Advance(Ms(2_690));
        Assert.Equal(0, _changes);
        _time.Advance(Ms(10));                       // 14.7 s
        Assert.Equal(1, _changes);
        Assert.Equal(1, _clock.CurrentLineIndex);

        _time.Advance(Ms(5_000));                    // 19.7 s
        Assert.Equal(2, _changes);
        _time.Advance(Ms(5_000));                    // 24.7 s
        Assert.Equal(3, _changes);
        Assert.Equal(3, _clock.CurrentLineIndex);

        _time.Advance(Ms(60_000));
        Assert.Equal(3, _changes);
    }

    [Fact]
    public void BeforeTheFirstLine_IndexIsMinusOne_ThenTheFirstLineFollows()
    {
        TrackAt(5_000);
        Assert.Equal(-1, _clock.CurrentLineIndex);
        _clock.Run();

        _time.Advance(Ms(4_700));

        Assert.Equal(1, _changes);
        Assert.Equal(0, _clock.CurrentLineIndex);
    }

    [Fact]
    public void Halt_StopsTicking_AndRefreshCatchesUpLater()
    {
        TrackAt(12_000);
        _clock.Run();
        _clock.Halt();

        _time.Advance(Ms(10_000));

        Assert.Equal(0, _changes);
        Assert.True(_clock.Refresh());
        Assert.Equal(3, _clock.CurrentLineIndex);
    }

    [Fact]
    public void Track_AgainMovesTheAnchor()
    {
        TrackAt(12_000);

        Assert.True(TrackAt(21_000));
        Assert.Equal(2, _clock.CurrentLineIndex);
        Assert.False(TrackAt(22_000));
    }

    [Fact]
    public void Reset_ForgetsTheSong()
    {
        TrackAt(12_000);
        _clock.Run();

        _clock.Reset();
        _time.Advance(Ms(30_000));

        Assert.Null(_clock.CurrentLineIndex);
        Assert.Equal(0, _changes);
        Assert.False(_clock.Refresh());
    }

    [Fact]
    public void ALineStartingAfterTheSongEnds_IsNeverScheduled()
    {
        TrackAt(12_000, durationMs: 16_000);
        _clock.Run();

        _time.Advance(Ms(2_700));                     // 14.7 s: line 1
        _time.Advance(Ms(30_000));                    // the 19.7 s line would start after the 16 s end

        Assert.Equal(1, _changes);
        Assert.Equal(1, _clock.CurrentLineIndex);
    }

    [Fact]
    public void AThrowingSubscriber_DoesNotEscapeTheTimer_AndTheClockStops()
    {
        _clock.LineChanged += () => throw new InvalidOperationException("render failed");
        TrackAt(12_000);
        _clock.Run();

        var ex = Record.Exception(() =>
        {
            _time.Advance(Ms(2_700));
            _time.Advance(Ms(5_000));
        });

        Assert.Null(ex);
        Assert.Equal(1, _changes);                    // halted after the failing tick: no second event
    }
}
```

- [ ] **Step 3: Run to verify they fail** (build fails: `LyricsClock` missing).
- [ ] **Step 4: Implement** — `Services/LyricsClock.cs`:

```csharp
using System.Diagnostics;
using MusicLyricsWidget.Models;

namespace MusicLyricsWidget.Services;

/// <summary>
/// Follows the playback position of the current song from an anchor (song position at a timestamp) and raises
/// <see cref="LineChanged"/> when the synced lyrics line changes. One one-shot timer is set to the next line change;
/// every tick recomputes the line from the clock (never "+1"), so drift and jumps cannot accumulate.
/// </summary>
internal sealed class LyricsClock : IDisposable
{
    // Guards against a zero/negative delay from rounding turning into a busy loop.
    private static readonly TimeSpan MinimumDelay = TimeSpan.FromMilliseconds(10);

    private readonly TimeProvider _time;
    private readonly ITimer _timer;
    private readonly object _gate = new();
    private IReadOnlyList<LyricsLine>? _lines;
    private TimeSpan _anchorPosition;
    private long _anchorTimestamp;
    private TimeSpan? _songDuration;
    private int? _index;
    private bool _running;

    public LyricsClock(TimeProvider time)
    {
        _time = time;
        _timer = time.CreateTimer(_ => OnTick(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>The current line (-1 before the first); null when no synced song is tracked.</summary>
    public int? CurrentLineIndex
    {
        get { lock (_gate) return _index; }
    }

    /// <summary>Raised on the timer thread when <see cref="CurrentLineIndex"/> changes. Subscribers must not throw.</summary>
    public event Action? LineChanged;

    /// <summary>
    /// Starts tracking a song or moves its anchor, without raising <see cref="LineChanged"/>. Returns whether the
    /// current line changed. Ticking starts with <see cref="Run"/>.
    /// </summary>
    public bool Track(IReadOnlyList<LyricsLine> lines, TimeSpan positionAtTimestamp, long timestamp, TimeSpan? songDuration)
    {
        lock (_gate)
        {
            _lines = lines;
            _anchorPosition = positionAtTimestamp;
            _anchorTimestamp = timestamp;
            _songDuration = songDuration;
            return UpdateIndexLocked();
        }
    }

    /// <summary>Recomputes the line from the clock now, without an event. Returns whether it changed.</summary>
    public bool Refresh()
    {
        lock (_gate)
            return _lines != null && UpdateIndexLocked();
    }

    /// <summary>Ticks at each coming line change; stops after the last line or when the next line starts after the song.</summary>
    public void Run()
    {
        lock (_gate)
        {
            _running = true;
            ArmLocked();
        }
    }

    /// <summary>Stops ticking but keeps the song and anchor (pause, or the card left the lyrics).</summary>
    public void Halt()
    {
        lock (_gate)
        {
            _running = false;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Stops ticking and forgets the song.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _running = false;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _lines = null;
            _index = null;
        }
    }

    public void Dispose() => _timer.Dispose();

    private void OnTick()
    {
        try
        {
            bool changed;
            lock (_gate)
            {
                if (!_running || _lines == null)
                    return;
                changed = UpdateIndexLocked();
            }

            if (changed)
                LineChanged?.Invoke();

            lock (_gate)
            {
                if (_running)
                    ArmLocked();
            }
        }
        catch (Exception ex)
        {
            // A timer callback must never throw: it would take the widget process down. Stop and leave a trace.
            Halt();
            Trace.TraceError($"LyricsClock stopped after a failing tick: {ex}");
        }
    }

    private void ArmLocked()
    {
        if (_lines == null)
        {
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return;
        }

        var position = PositionLocked();
        var untilNext = SyncedLyricsCursor.TimeUntilNextLine(_lines, position);
        if (untilNext == null || (_songDuration != null && position + untilNext.Value > _songDuration.Value))
        {
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return;
        }

        _timer.Change(untilNext.Value < MinimumDelay ? MinimumDelay : untilNext.Value, Timeout.InfiniteTimeSpan);
    }

    private TimeSpan PositionLocked() => _anchorPosition + _time.GetElapsedTime(_anchorTimestamp);

    private bool UpdateIndexLocked()
    {
        var index = SyncedLyricsCursor.IndexAt(_lines!, PositionLocked());
        var changed = index != _index;
        _index = index;
        return changed;
    }
}
```

- [ ] **Step 5: Run the new tests (several times for flakiness), then the full suite** — all green, 0 warnings.
- [ ] **Step 6: Commit** — `Tilføj LyricsClock, der skifter linje til tiden ud fra et anker`.

### Task 4: Orchestrator owns the clock

**Files:**
- Modify: `MusicLyricsWidget/Services/MusicRecognitionOrchestrator.cs`
- Create: `MusicLyricsWidget.Tests/MusicRecognitionOrchestratorSyncTests.cs`

**Interfaces:**
- Consumes: `LyricsClock` (Task 3), `SongInfo.PositionAtSampleEnd` (Task 1), `LyricsResult.SyncedLyrics`/`HasSyncedLyrics`.
- Produces: constructor `MusicRecognitionOrchestrator(IAudioCapture audioCapture, ISongIdentifier songId, ILyricsProvider lyrics, TimeProvider? timeProvider = null)`; `public int? CurrentLineIndex`; `public event Action? LinePositionChanged`.

Behaviour (spec "Linjeur" + "Samspil"):
- `_time = timeProvider ?? TimeProvider.System`; `_clock = new LyricsClock(_time)`; `_clock.LineChanged += OnLineChanged`.
- In `RecognizeOnceAsync`, right after `CaptureAudioAsync` returns: `var captureEnd = _time.GetTimestamp();` and pass it to `HandleMatchAsync(song, captureEnd, cancellationToken)`.
- `private bool TrackLyrics(SongInfo song, long captureEnd)`: when `CurrentLyrics?.HasSyncedLyrics == true` and `song.PositionAtSampleEnd is { } position` → `return _clock.Track(CurrentLyrics.SyncedLyrics, position, captureEnd, song.Duration);` otherwise `_clock.Reset(); return false;`.
- New song: after the lyrics fetch, set `CurrentLyrics`, call `TrackLyrics(song, captureEnd)`, then `SetState(DisplayingLyrics)` (the index is known before the first render).
- Same song (resolved): `var lineChanged = TrackLyrics(song, captureEnd);` then if `State == DisplayingLyrics` → raise `LinePositionChanged` when `lineChanged` and call `_clock.Run()`; else `EnsureDisplayingLyrics()`.
- `SetState`: before invoking `StateChanged`, call `_clock.Run()` when the new state is `DisplayingLyrics`, otherwise `_clock.Halt()`.
- `HandleNoMatch` clearing branch and `Stop()`: `_clock.Reset()`.
- `Pause()`: `_clock.Halt()` first (even when the loop is not running), then the existing pause logic.
- `Resume()`: when it actually resumes and `State == DisplayingLyrics`: `if (_clock.Refresh()) LinePositionChanged?.Invoke(); _clock.Run();`, then `StartLoop()`.
- `Dispose()`: `Stop(); _clock.Dispose(); _audioCapture.Dispose();`.
- `OnLineChanged()`: `try { LinePositionChanged?.Invoke(); } catch (Exception ex) { FailDisplay(ex); }`.
- `FailDisplay(Exception ex)`: `ErrorMessage = ex.Message;` then `try { SetState(RecognitionState.Error); ErrorOccurred?.Invoke(ex.Message); } catch (Exception renderFailure) { Trace.TraceError($"Rendering the error card failed: {renderFailure}"); }` — a second failure can only be traced: the timer thread must not throw.

- [ ] **Step 1: Write the failing tests** — `MusicRecognitionOrchestratorSyncTests.cs`:

```csharp
using Microsoft.Extensions.Time.Testing;
using MusicLyricsWidget.Models;
using MusicLyricsWidget.Services;
using MusicLyricsWidget.Tests.Fakes;

namespace MusicLyricsWidget.Tests;

public class MusicRecognitionOrchestratorSyncTests
{
    private const int FarAwayIntervalSeconds = 3600;
    private static readonly TimeSpan GuardTimeout = TimeSpan.FromSeconds(10);

    // Lines at 10, 15, 20, 25 s (shown 300 ms early).
    private static readonly LyricsResult Synced = new()
    {
        Source = "LRCLIB",
        PlainLyrics = "a\nb\nc\nd",
        SyncedLyrics = new[] { 10, 15, 20, 25 }
            .Select(s => new LyricsLine { Timestamp = TimeSpan.FromSeconds(s), Text = $"at {s}" }).ToList(),
    };
    private static readonly LyricsResult PlainOnly = new() { Source = "LRCLIB", PlainLyrics = "a\nb" };

    private readonly FakeTimeProvider _time = new();
    private readonly FakeAudioCapture _capture = new();
    private readonly FakeSongIdentifier _identifier = new();
    private readonly FakeLyricsProvider _lyrics = new();
    private readonly MusicRecognitionOrchestrator _orchestrator;
    private readonly List<RecognitionState> _states = new();
    private readonly List<int?> _linesAtStateChange = new();
    private int _lineEvents;

    public MusicRecognitionOrchestratorSyncTests()
    {
        _orchestrator = new MusicRecognitionOrchestrator(_capture, _identifier, _lyrics, _time);
        _orchestrator.StateChanged += s =>
        {
            _states.Add(s);
            _linesAtStateChange.Add(_orchestrator.CurrentLineIndex);
        };
        _orchestrator.LinePositionChanged += () => _lineEvents++;
    }

    private static SongInfo Song(double? positionSeconds, double durationSeconds = 30) => new()
    {
        Title = "Song A",
        Artist = "Artist A",
        Duration = TimeSpan.FromSeconds(durationSeconds),
        PositionAtSampleEnd = positionSeconds is { } p ? TimeSpan.FromSeconds(p) : null,
    };

    // Whole seconds or milliseconds only: boundaries are exact. FakeTimeProvider.Advance moves to the end of the step
    // before firing due timers, so a test that counts each line advances one line at a time.
    private static TimeSpan S(int seconds) => TimeSpan.FromSeconds(seconds);
    private static TimeSpan Ms(int milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    private Task CycleAsync(SongInfo? song, LyricsResult? lyrics = null)
    {
        _identifier.Enqueue(song);
        if (lyrics != null)
            _lyrics.Enqueue(lyrics);
        return _orchestrator.RecognizeOnceAsync().WaitAsync(GuardTimeout);
    }

    [Fact]
    public async Task SyncedMatch_RendersTheCurrentLineFirst_ThenMovesOnByItself()
    {
        await CycleAsync(Song(12), Synced);

        Assert.Equal(RecognitionState.DisplayingLyrics, _states[^1]);
        Assert.Equal(0, _linesAtStateChange[^1]);

        _time.Advance(Ms(2_700));

        Assert.Equal(1, _lineEvents);
        Assert.Equal(1, _orchestrator.CurrentLineIndex);
        Assert.Equal(RecognitionState.DisplayingLyrics, _states[^1]);   // a line change is not a state change
    }

    [Fact]
    public async Task PlainLyrics_HaveNoLineIndex_AndNoLineEvents()
    {
        await CycleAsync(Song(12), PlainOnly);
        _time.Advance(S(60));

        Assert.Null(_orchestrator.CurrentLineIndex);
        Assert.Equal(0, _lineEvents);
    }

    [Fact]
    public async Task WithoutAPosition_ThereIsNoLineIndex()
    {
        await CycleAsync(Song(null), Synced);

        Assert.Null(_orchestrator.CurrentLineIndex);
    }

    [Fact]
    public async Task SameSongAgain_MovesTheAnchorSilently()
    {
        await CycleAsync(Song(12), Synced);
        _states.Clear();

        await CycleAsync(Song(21));

        Assert.Empty(_states);
        Assert.Equal(1, _lineEvents);
        Assert.Equal(2, _orchestrator.CurrentLineIndex);

        _time.Advance(Ms(3_700));
        Assert.Equal(3, _orchestrator.CurrentLineIndex);
    }

    [Fact]
    public async Task AnErrorStopsTheLines_UntilTheSameSongIsSeenAgain()
    {
        await CycleAsync(Song(12), Synced);
        _identifier.EnqueueThrow(new InvalidOperationException("network down"));
        await _orchestrator.RecognizeOnceAsync().WaitAsync(GuardTimeout);

        _time.Advance(S(10));
        Assert.Equal(0, _lineEvents);

        await CycleAsync(Song(22));
        Assert.Equal(RecognitionState.DisplayingLyrics, _states[^1]);
        Assert.Equal(2, _linesAtStateChange[^1]);
    }

    [Fact]
    public async Task Pause_StopsTheLines_AndResumeCatchesUp()
    {
        _identifier.Enqueue(Song(12));
        _lyrics.Enqueue(Synced);
        _orchestrator.Start(FarAwayIntervalSeconds);
        await WaitForAsync(() => _orchestrator.State == RecognitionState.DisplayingLyrics);
        var gate = new BlockingGate();
        _capture.Gate = gate;

        try
        {
            _orchestrator.Pause();
            _time.Advance(S(10));
            Assert.Equal(0, _lineEvents);

            _orchestrator.Resume();
            await gate.Entered.WaitAsync(GuardTimeout);

            Assert.Equal(1, _lineEvents);                       // one catch-up event, not one per missed line
            Assert.Equal(2, _orchestrator.CurrentLineIndex);
        }
        finally
        {
            _orchestrator.Stop();
            gate.Release();
        }
    }

    [Fact]
    public async Task Stop_ForgetsTheLine()
    {
        await CycleAsync(Song(12), Synced);

        _orchestrator.Stop();
        _time.Advance(S(30));

        Assert.Null(_orchestrator.CurrentLineIndex);
        Assert.Equal(0, _lineEvents);
    }

    [Fact]
    public async Task AFailingLineRender_BecomesAnErrorCard_WithoutCrashingTheTimer()
    {
        var errors = new List<string>();
        _orchestrator.ErrorOccurred += errors.Add;
        _orchestrator.LinePositionChanged += () => throw new InvalidOperationException("UpdateWidget failed");
        await CycleAsync(Song(12), Synced);

        var ex = Record.Exception(() => _time.Advance(S(30)));

        Assert.Null(ex);
        Assert.Equal(RecognitionState.Error, _orchestrator.State);
        Assert.Equal(new[] { "UpdateWidget failed" }, errors);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + GuardTimeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition not reached.");
            await Task.Yield();
        }
    }
}
```

- [ ] **Step 2: Run to verify they fail** (build fails: 4-argument constructor, `CurrentLineIndex`, `LinePositionChanged` missing).
- [ ] **Step 3: Implement** in `MusicRecognitionOrchestrator.cs` (add `using System.Diagnostics;`); keep every existing behaviour and test. New members and the changed methods:

```csharp
    private readonly TimeProvider _time;
    private readonly LyricsClock _clock;

    public MusicRecognitionOrchestrator(
        IAudioCapture audioCapture,
        ISongIdentifier songId,
        ILyricsProvider lyrics,
        TimeProvider? timeProvider = null)
    {
        _audioCapture = audioCapture;
        _songId = songId;
        _lyrics = lyrics;
        _time = timeProvider ?? TimeProvider.System;
        _clock = new LyricsClock(_time);
        _clock.LineChanged += OnLineChanged;
    }

    /// <summary>The synced line being sung (-1 before the first); null without synced lyrics or a known position.</summary>
    public int? CurrentLineIndex => _clock.CurrentLineIndex;

    /// <summary>Raised when the current synced line changes while lyrics are shown (from a timer or a re-sync).</summary>
    public event Action? LinePositionChanged;

    public void Pause()
    {
        _clock.Halt();
        if (StopLoop())
            Volatile.Write(ref _pauseState, Paused);
    }

    public void Resume()
    {
        if (Interlocked.CompareExchange(ref _pauseState, NotPaused, Paused) != Paused)
            return;

        if (State == RecognitionState.DisplayingLyrics)
        {
            if (_clock.Refresh())
                LinePositionChanged?.Invoke();
            _clock.Run();
        }
        StartLoop();
    }

    // Stop(): add `_clock.Reset();` next to the other resets.

    // RecognizeOnceAsync: right after `var pcmData = await _audioCapture.CaptureAudioAsync(cancellationToken);`
    //     var captureEnd = _time.GetTimestamp();
    // and call `await HandleMatchAsync(song, captureEnd, cancellationToken);`

    private async Task HandleMatchAsync(SongInfo song, long captureEnd, CancellationToken cancellationToken)
    {
        _consecutiveMisses = 0;

        if (song.Equals(_currentSong) && _lyricsResolved)
        {
            // Same song: move the anchor (fixes pauses, seeks and drift on the player) without a redraw of the state.
            var lineChanged = TrackLyrics(song, captureEnd);
            if (State == RecognitionState.DisplayingLyrics)
            {
                if (lineChanged)
                    LinePositionChanged?.Invoke();
                _clock.Run();
            }
            else
            {
                EnsureDisplayingLyrics();
            }
            return;
        }

        _currentSong = song;
        CurrentLyrics = null;
        _lyricsResolved = false;
        _clock.Reset();
        SetState(RecognitionState.FetchingLyrics);
        var lyrics = await _lyrics.GetLyricsAsync(song, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        _lyricsResolved = true;
        CurrentLyrics = lyrics;
        TrackLyrics(song, captureEnd);             // the first render already shows the right line
        SetState(RecognitionState.DisplayingLyrics);
        if (lyrics != null)
        {
            SongRecognized?.Invoke(song, lyrics);
        }
    }

    /// <summary>Anchors the clock to this match when the lyrics are synced and the position is known; returns whether the line changed.</summary>
    private bool TrackLyrics(SongInfo song, long captureEnd)
    {
        if (CurrentLyrics is { HasSyncedLyrics: true } lyrics && song.PositionAtSampleEnd is { } position)
            return _clock.Track(lyrics.SyncedLyrics, position, captureEnd, song.Duration);

        _clock.Reset();
        return false;
    }

    // HandleNoMatch clearing branch: add `_clock.Reset();` next to the other resets.

    private void SetState(RecognitionState newState)
    {
        State = newState;
        // Lines tick only while the lyrics card is up; any other state (error, no match, a new fetch) stops them.
        if (newState == RecognitionState.DisplayingLyrics)
            _clock.Run();
        else
            _clock.Halt();
        StateChanged?.Invoke(newState);
    }

    private void OnLineChanged()
    {
        try
        {
            LinePositionChanged?.Invoke();
        }
        catch (Exception ex)
        {
            FailDisplay(ex);
        }
    }

    /// <summary>A failing line render becomes the error card, like a failing cycle; it must never escape the timer.</summary>
    private void FailDisplay(Exception ex)
    {
        ErrorMessage = ex.Message;
        try
        {
            SetState(RecognitionState.Error);
            ErrorOccurred?.Invoke(ex.Message);
        }
        catch (Exception renderFailure)
        {
            // The error card could not be drawn either; the timer thread must not throw, so only a trace is left.
            Trace.TraceError($"Rendering the error card failed: {renderFailure}");
        }
    }

    public void Dispose()
    {
        Stop();
        _clock.Dispose();
        _audioCapture.Dispose();
    }
```
- [ ] **Step 4: Run the orchestrator test classes 5 times (flakiness), then the full suite** — all green, 0 warnings.
- [ ] **Step 5: Commit** — `Lad orchestratoren følge sangens linje med LyricsClock`.

### Task 5: Card data and size-aware template

**Files:**
- Modify: `MusicLyricsWidget/Widget/WidgetStateManager.cs`
- Replace: `MusicLyricsWidget/Widget/WidgetTemplates/LyricsTemplate.json`
- Modify: `MusicLyricsWidget.Tests/WidgetStateManagerTests.cs`, `MusicLyricsWidget.Tests/LyricsTemplateLayoutTests.cs`

**Interfaces:**
- Consumes: `LyricsWindow.For`, `LyricsWindowText` (Task 2).
- Produces: `GetWidgetContent(RecognitionState state, SongInfo? song = null, LyricsResult? lyrics = null, string? errorMessage = null, int? currentLineIndex = null)`; lyrics-card data fields exactly as in Global Constraints.

- [ ] **Step 1: Update/write the failing tests**

In `WidgetStateManagerTests.cs`, change the two DisplayingLyrics expectations from the old `lyrics` key to the full field set:
- with lyrics (`PlainLyrics = "Line one\nLine two"`, no synced lines): `songTitle`, `artist`, `source = "LRCLIB"`, `plainLyrics = "Line one\nLine two"`, `previousLineMedium = ""`, `previousLinesLarge = ""`, `currentLine = ""`, `nextLines = ""`.
- without lyrics: same, with `plainLyrics = "No lyrics found for this song."` and `source = ""`.
Add a case to the case table + placeholder theory: `["DisplayingLyrics synced"]` with synced lyrics (5 lines "L1".."L5" at 5 s steps) and `currentLineIndex = 2`, and a fact asserting `previousLineMedium = "L2"`, `previousLinesLarge = "L1\nL2"`, `currentLine = "L3"`, `nextLines = "L4\nL5"`, `plainLyrics = ""`. (Extend the `Case` record with `int? CurrentLineIndex = null` and pass it to `GetWidgetContent`.)

Replace `LyricsTemplateLayoutTests.cs` with:

```csharp
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
```

- [ ] **Step 2: Run to verify they fail.**
- [ ] **Step 3: Implement**

`LyricsTemplate.json` (replace the whole file):

```json
{
  "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
  "type": "AdaptiveCard",
  "version": "1.6",
  "body": [
    { "type": "TextBlock", "text": "${songTitle}", "weight": "Bolder", "size": "Medium", "wrap": true, "maxLines": 1 },
    { "type": "TextBlock", "text": "${artist}", "isSubtle": true, "spacing": "None", "wrap": true, "maxLines": 1 },
    {
      "type": "Container",
      "$when": "${$host.widgetSize==\"medium\"}",
      "spacing": "Small",
      "items": [
        { "type": "TextBlock", "$when": "${plainLyrics != ''}", "text": "${plainLyrics}", "wrap": true, "maxLines": 9, "size": "Small" },
        { "type": "TextBlock", "$when": "${previousLineMedium != ''}", "text": "${previousLineMedium}", "wrap": true, "maxLines": 1, "size": "Small", "isSubtle": true },
        { "type": "TextBlock", "$when": "${currentLine != ''}", "text": "${currentLine}", "wrap": true, "maxLines": 2, "size": "Small", "weight": "Bolder", "spacing": "None" },
        { "type": "TextBlock", "$when": "${nextLines != ''}", "text": "${nextLines}", "wrap": true, "maxLines": 6, "size": "Small", "spacing": "None" }
      ]
    },
    {
      "type": "Container",
      "$when": "${$host.widgetSize==\"large\"}",
      "spacing": "Small",
      "items": [
        { "type": "TextBlock", "$when": "${plainLyrics != ''}", "text": "${plainLyrics}", "wrap": true, "maxLines": 15, "size": "Small" },
        { "type": "TextBlock", "$when": "${previousLinesLarge != ''}", "text": "${previousLinesLarge}", "wrap": true, "maxLines": 3, "size": "Small", "isSubtle": true },
        { "type": "TextBlock", "$when": "${currentLine != ''}", "text": "${currentLine}", "wrap": true, "maxLines": 2, "size": "Small", "weight": "Bolder", "spacing": "None" },
        { "type": "TextBlock", "$when": "${nextLines != ''}", "text": "${nextLines}", "wrap": true, "maxLines": 11, "size": "Small", "spacing": "None" }
      ]
    },
    { "type": "TextBlock", "$when": "${$host.widgetSize==\"large\"}", "text": "Source: ${source}", "isSubtle": true, "size": "Small", "spacing": "Small" }
  ],
  "actions": [
    { "type": "Action.Execute", "title": "Listen Again", "verb": "listenNow" }
  ]
}
```

`WidgetStateManager.cs`: add `int? currentLineIndex = null` as the last parameter of `GetWidgetContent`; replace the two `DisplayingLyrics` arms with `(_lyricsTemplate, LyricsData(song, lyrics, currentLineIndex))` and `(_lyricsTemplate, LyricsData(song, null, null))`; replace the `MaxLyricsLines` constant and comment with:

```csharp
    // Plain lyrics sent: the large card shows 15 lines, the medium card's maxLines cuts it to 9. LyricsTemplate.json
    // caps every block per size, because the Widgets Board drops an element that does not fit plus all after it.
    private const int MaxPlainLyricsLines = 15;
    private const string NoLyricsMessage = "No lyrics found for this song.";
```

and add:

```csharp
    /// <summary>
    /// Lyrics-card data. With synced lines and a known current line: the window around it; otherwise plain text from
    /// the start (or the no-lyrics message). Every field is always present, so the template's $when tests are exact.
    /// </summary>
    private static string LyricsData(SongInfo song, LyricsResult? lyrics, int? currentLineIndex)
    {
        var window = lyrics is { HasSyncedLyrics: true } && currentLineIndex != null
            ? LyricsWindow.For(lyrics.SyncedLyrics, currentLineIndex.Value)
            : null;

        var plain = window != null ? string.Empty
            : lyrics == null ? NoLyricsMessage
            : FirstLines(lyrics.PlainLyrics, MaxPlainLyricsLines);

        return CreateData(new
        {
            songTitle = song.Title,
            artist = song.Artist,
            source = lyrics?.Source ?? string.Empty,
            plainLyrics = plain,
            previousLineMedium = window?.PreviousLineMedium ?? string.Empty,
            previousLinesLarge = window?.PreviousLinesLarge ?? string.Empty,
            currentLine = window?.CurrentLine ?? string.Empty,
            nextLines = window?.NextLines ?? string.Empty,
        });
    }
```

- [ ] **Step 4: Run the full suite** — all green (including `TemplateVerbTests` and the placeholder invariant theory), 0 warnings.
- [ ] **Step 5: Commit** — `Vis den aktuelle linje fremhævet med kontekst i et størrelsesbevidst kort`.

### Task 6: Provider wiring and documentation

**Files:**
- Modify: `MusicLyricsWidget/Widget/LyricsWidgetProvider.cs`
- Modify: `CLAUDE.md`

**Interfaces:**
- Consumes: `MusicRecognitionOrchestrator.LinePositionChanged`, `MusicRecognitionOrchestrator.CurrentLineIndex`, the 5-parameter `GetWidgetContent`.

- [ ] **Step 1: Wire the provider** — in `StartListening`, next to the `StateChanged` subscription: `orchestrator.LinePositionChanged += () => OnStateChanged(orchestrator, orchestrator.State);`. In `PushState`, pass `source?.CurrentLineIndex` as the new last argument of `GetWidgetContent`. No other provider change (lock rules in CLAUDE.md still hold: rendering happens outside `_gate`).
- [ ] **Step 2: Update `CLAUDE.md`** — Arkitektur: add `LyricsClock` (anchor + one-shot timer via `TimeProvider`, recomputes from the clock), `SyncedLyricsCursor` (300 ms `LineLead`), `LyricsWindow`; Fejlsemantik: a failing line render becomes an error card, never a timer exception; Eksterne API'er: replace "`play_offset_ms` … bliver ikke parset endnu" with the position formula; Widget-invarianter: the lyrics card's data fields and per-size budget (medium ≤ 9, large ≤ 16) pinned by `LyricsTemplateLayoutTests`. Keep CLAUDE.md ≤ 90 lines.
- [ ] **Step 3: Build and run the full suite** — 0 errors, 0 warnings, all green.
- [ ] **Step 4: Commit** — `Tegn kortet igen ved hvert linjeskift og dokumentér synkroniseringen`.

### After Task 6 (controller)

Final whole-branch review; deploy (stop all `MusicLyricsWidget.exe`, copy changed files into `AppX` from the appxrecipe); live check with the user: medium and large card follow the song, panel close/open, song change.
