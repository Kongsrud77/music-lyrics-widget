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

        _time.Advance(Ms(13_000));                    // 25 s: past the 24.7 s line (the brief's 10 s only reached 22 s = line 2)

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
