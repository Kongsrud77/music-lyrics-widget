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
    public async Task SameSongAgainWithoutAPosition_DropsTheLine_AndRedrawsTheCard()
    {
        await CycleAsync(Song(12), Synced);
        Assert.Equal(0, _orchestrator.CurrentLineIndex);

        await CycleAsync(Song(null));

        Assert.Equal(1, _lineEvents);                 // the card must stop showing a line that is no longer tracked
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
    public async Task ARestoredCard_ShowsTheLineForNow_NotTheOneFromBeforeTheError()
    {
        await CycleAsync(Song(12), Synced);
        _identifier.EnqueueThrow(new InvalidOperationException("network down"));
        await _orchestrator.RecognizeOnceAsync().WaitAsync(GuardTimeout);
        _time.Advance(S(10));

        await CycleAsync(null);   // one miss: the song stays, the card comes back without a new match

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
    public async Task Resume_RestartsTheLoop_BeforeTheCatchUpRender()
    {
        var capturesAtEvent = new List<int>();
        _orchestrator.LinePositionChanged += () => capturesAtEvent.Add(_capture.CaptureCalls);
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

            _orchestrator.Resume();
            await gate.Entered.WaitAsync(GuardTimeout);

            // The render is a slow cross-process call; a Deactivate arriving meanwhile must find a running loop to
            // pause. One capture from the first cycle, the second already started when the catch-up event fires.
            Assert.Equal(new[] { 2 }, capturesAtEvent);
        }
        finally
        {
            _orchestrator.Stop();
            gate.Release();
        }
    }

    [Fact]
    public async Task APauseDuringTheCatchUpRender_LeavesTheOrchestratorPaused()
    {
        _identifier.Enqueue(Song(12));
        _lyrics.Enqueue(Synced);
        _orchestrator.Start(FarAwayIntervalSeconds);
        await WaitForAsync(() => _orchestrator.State == RecognitionState.DisplayingLyrics);
        var gate = new BlockingGate();
        _capture.Gate = gate;
        _orchestrator.LinePositionChanged += () => _orchestrator.Pause();   // the panel closes mid-render

        try
        {
            _orchestrator.Pause();
            _time.Advance(S(10));

            _orchestrator.Resume();

            Assert.True(_orchestrator.IsPaused);   // the microphone must not keep running behind a closed panel
        }
        finally
        {
            _orchestrator.Stop();
            gate.Release();
        }
    }

    [Fact]
    public async Task AFailingCatchUpRenderOnResume_BecomesAnErrorCard_AndTheLoopStillRestarts()
    {
        var errors = new List<string>();
        var stateAtError = RecognitionState.Idle;
        _orchestrator.ErrorOccurred += message =>
        {
            errors.Add(message);
            stateAtError = _orchestrator.State;
        };
        _orchestrator.LinePositionChanged += () => throw new InvalidOperationException("UpdateWidget failed");
        _identifier.Enqueue(Song(12));
        _lyrics.Enqueue(Synced);
        _orchestrator.Start(FarAwayIntervalSeconds);
        await WaitForAsync(() => _orchestrator.State == RecognitionState.DisplayingLyrics);
        var gate = new BlockingGate();
        _capture.Gate = gate;

        try
        {
            _orchestrator.Pause();
            _time.Advance(S(10));                               // the line moves on while paused

            var ex = Record.Exception(() => _orchestrator.Resume());
            await gate.Entered.WaitAsync(GuardTimeout);         // the loop restarted despite the failing render

            Assert.Null(ex);
            Assert.False(_orchestrator.IsPaused);
            Assert.Equal(new[] { "UpdateWidget failed" }, errors);
            Assert.Equal(RecognitionState.Error, stateAtError);
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
