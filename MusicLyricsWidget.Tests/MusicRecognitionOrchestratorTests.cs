using MusicLyricsWidget.Models;
using MusicLyricsWidget.Services;
using MusicLyricsWidget.Tests.Fakes;

namespace MusicLyricsWidget.Tests;

public class MusicRecognitionOrchestratorTests
{
    private const int FarAwayIntervalSeconds = 3600;
    private static readonly TimeSpan GuardTimeout = TimeSpan.FromSeconds(10);

    private static readonly SongInfo SongA = new() { Title = "Song A", Artist = "Artist A" };
    private static readonly SongInfo SongB = new() { Title = "Song B", Artist = "Artist B" };
    private static readonly LyricsResult LyricsA = new() { PlainLyrics = "Lyrics A", Source = "LRCLIB" };
    private static readonly LyricsResult LyricsB = new() { PlainLyrics = "Lyrics B", Source = "LRCLIB" };

    private static readonly RecognitionState[] FullCycle =
    {
        RecognitionState.Listening,
        RecognitionState.Identifying,
        RecognitionState.FetchingLyrics,
        RecognitionState.DisplayingLyrics,
    };

    private readonly FakeAudioCapture _capture = new();
    private readonly FakeSongIdentifier _identifier = new();
    private readonly FakeLyricsProvider _lyrics = new();
    private readonly MusicRecognitionOrchestrator _orchestrator;
    private readonly List<(RecognitionState State, SongInfo? Song, LyricsResult? Lyrics)> _emitted = new();
    private readonly List<(SongInfo Song, LyricsResult Lyrics)> _recognized = new();
    private readonly List<string> _errors = new();

    public MusicRecognitionOrchestratorTests()
    {
        _orchestrator = new MusicRecognitionOrchestrator(_capture, _identifier, _lyrics);
        _orchestrator.StateChanged += state =>
            _emitted.Add((state, _orchestrator.CurrentSong, _orchestrator.CurrentLyrics));
        _orchestrator.SongRecognized += (song, lyrics) => _recognized.Add((song, lyrics));
        _orchestrator.ErrorOccurred += _errors.Add;
    }

    private List<RecognitionState> States => _emitted.Select(e => e.State).ToList();

    /// <summary>Runs one cycle; the timeout turns a hang into a failing test instead of a stuck test run.</summary>
    private Task RunCycleAsync() => _orchestrator.RecognizeOnceAsync().WaitAsync(GuardTimeout);

    /// <summary>Shows SongA with LyricsA and forgets the states emitted while getting there.</summary>
    private async Task ShowSongAAsync()
    {
        _identifier.Enqueue(SongA);
        _lyrics.Enqueue(LyricsA);
        await RunCycleAsync();
        _emitted.Clear();
    }

    [Fact]
    public async Task FirstMatch_EmitsFullStateSequence_SetsLyrics_AndRaisesSongRecognizedOnce()
    {
        _identifier.Enqueue(SongA);
        _lyrics.Enqueue(LyricsA);

        await RunCycleAsync();

        Assert.Equal(FullCycle, States);
        Assert.Equal(RecognitionState.DisplayingLyrics, _orchestrator.State);
        Assert.Equal(SongA, _orchestrator.CurrentSong);
        Assert.Same(LyricsA, _orchestrator.CurrentLyrics);
        var recognized = Assert.Single(_recognized);
        Assert.Equal(SongA, recognized.Song);
        Assert.Same(LyricsA, recognized.Lyrics);
        Assert.Empty(_errors);
    }

    [Fact]
    public async Task SecondCycleWithSameSong_EmitsNoStates_AndFetchesLyricsOnce()
    {
        await ShowSongAAsync();
        _identifier.Enqueue(new SongInfo { Title = "SONG a", Artist = "artist A" });

        await RunCycleAsync();

        Assert.Empty(_emitted);
        Assert.Equal(RecognitionState.DisplayingLyrics, _orchestrator.State);
        Assert.Same(LyricsA, _orchestrator.CurrentLyrics);
        Assert.Single(_lyrics.RequestedSongs);
        Assert.Single(_recognized);
    }

    [Fact]
    public async Task SameSongWithoutLyrics_IsNotRefetched_AndSongRecognizedIsNotRaised()
    {
        _identifier.Enqueue(SongA);
        _lyrics.Enqueue(null);
        await RunCycleAsync();
        Assert.Equal(FullCycle, States);
        _emitted.Clear();
        _identifier.Enqueue(SongA);

        await RunCycleAsync();

        Assert.Empty(_emitted);
        Assert.Null(_orchestrator.CurrentLyrics);
        Assert.Equal(RecognitionState.DisplayingLyrics, _orchestrator.State);
        Assert.Single(_lyrics.RequestedSongs);
        Assert.Empty(_recognized);
    }

    [Fact]
    public async Task DifferentSongWhileShowing_RunsSilentlyUntilFetching_AndClearsOldLyricsFirst()
    {
        await ShowSongAAsync();
        _identifier.Enqueue(SongB);
        _lyrics.Enqueue(LyricsB);

        await RunCycleAsync();

        Assert.Equal(
            new[] { RecognitionState.FetchingLyrics, RecognitionState.DisplayingLyrics },
            States);
        Assert.Equal((RecognitionState.FetchingLyrics, SongB, (LyricsResult?)null), _emitted[0]);
        Assert.Equal(SongB, _orchestrator.CurrentSong);
        Assert.Same(LyricsB, _orchestrator.CurrentLyrics);
        Assert.Equal(2, _recognized.Count);
        Assert.Equal(SongB, _recognized[1].Song);
    }

    [Fact]
    public async Task MissesWhileShowing_KeepLyricsUntilThreshold_ThenEnterNoMatch()
    {
        await ShowSongAAsync();

        for (var miss = 1; miss < MusicRecognitionOrchestrator.MissesBeforeClearingLyrics; miss++)
        {
            _identifier.Enqueue(null);
            await RunCycleAsync();

            Assert.Equal(RecognitionState.DisplayingLyrics, _orchestrator.State);
            Assert.Equal(SongA, _orchestrator.CurrentSong);
            Assert.Same(LyricsA, _orchestrator.CurrentLyrics);
        }
        Assert.Empty(_emitted);

        _identifier.Enqueue(null);
        await RunCycleAsync();

        Assert.Equal(new[] { RecognitionState.NoMatch }, States);
        Assert.Equal(RecognitionState.NoMatch, _orchestrator.State);
        Assert.Null(_orchestrator.CurrentSong);
        Assert.Null(_orchestrator.CurrentLyrics);
    }

    [Fact]
    public async Task MatchBetweenMisses_ResetsTheMissCounter()
    {
        await ShowSongAAsync();
        for (var round = 0; round < MusicRecognitionOrchestrator.MissesBeforeClearingLyrics; round++)
        {
            for (var miss = 1; miss < MusicRecognitionOrchestrator.MissesBeforeClearingLyrics; miss++)
                _identifier.Enqueue(null);
            _identifier.Enqueue(SongA);
        }

        for (var cycle = 0;
             cycle < MusicRecognitionOrchestrator.MissesBeforeClearingLyrics * MusicRecognitionOrchestrator.MissesBeforeClearingLyrics;
             cycle++)
        {
            await RunCycleAsync();
        }

        Assert.Equal(RecognitionState.DisplayingLyrics, _orchestrator.State);
        Assert.Equal(SongA, _orchestrator.CurrentSong);
        Assert.Empty(_emitted);
    }

    [Fact]
    public async Task MissWithNothingShown_EndsInNoMatch_NeverIdle()
    {
        _identifier.Enqueue(null);

        await RunCycleAsync();

        Assert.Equal(
            new[] { RecognitionState.Listening, RecognitionState.Identifying, RecognitionState.NoMatch },
            States);
        Assert.Equal(RecognitionState.NoMatch, _orchestrator.State);
        Assert.Null(_orchestrator.CurrentSong);
        Assert.Null(_orchestrator.CurrentLyrics);
        Assert.Empty(_lyrics.RequestedSongs);
    }

    [Fact]
    public async Task IdentifierThrows_EndsInError_AndRaisesErrorOccurredOnce()
    {
        _identifier.EnqueueThrow(new InvalidOperationException("identify failed"));

        await RunCycleAsync();

        Assert.Equal(
            new[] { RecognitionState.Listening, RecognitionState.Identifying, RecognitionState.Error },
            States);
        Assert.Equal(RecognitionState.Error, _orchestrator.State);
        Assert.Equal("identify failed", _orchestrator.ErrorMessage);
        Assert.Equal(new[] { "identify failed" }, _errors);
    }

    [Fact]
    public async Task LyricsProviderThrows_EndsInError_AndRaisesErrorOccurredOnce()
    {
        _identifier.Enqueue(SongA);
        _lyrics.EnqueueThrow(new InvalidOperationException("lyrics failed"));

        await RunCycleAsync();

        Assert.Equal(RecognitionState.Error, _orchestrator.State);
        Assert.Equal("lyrics failed", _orchestrator.ErrorMessage);
        Assert.Equal(new[] { "lyrics failed" }, _errors);
        Assert.Empty(_recognized);
    }

    [Fact]
    public async Task IdentifierTimesOut_WithTokenNotCancelled_EndsInError_AndRaisesErrorOccurredOnce()
    {
        var timeout = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.");
        _identifier.EnqueueThrow(timeout);

        await RunCycleAsync();

        Assert.Equal(
            new[] { RecognitionState.Listening, RecognitionState.Identifying, RecognitionState.Error },
            States);
        Assert.Equal(timeout.Message, _orchestrator.ErrorMessage);
        Assert.Equal(new[] { timeout.Message }, _errors);
    }

    [Fact]
    public async Task LyricsProviderTimesOut_WithTokenNotCancelled_EndsInError_AndRaisesErrorOccurredOnce()
    {
        var timeout = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.");
        _identifier.Enqueue(SongA);
        _lyrics.EnqueueThrow(timeout);

        await RunCycleAsync();

        Assert.Equal(RecognitionState.Error, _orchestrator.State);
        Assert.Equal(timeout.Message, _orchestrator.ErrorMessage);
        Assert.Equal(new[] { timeout.Message }, _errors);
        Assert.Empty(_recognized);
    }

    [Fact]
    public async Task SameSongAfterFailedLyricsFetch_RefetchesLyrics()
    {
        _identifier.Enqueue(SongA);
        _lyrics.EnqueueThrow(new InvalidOperationException("lyrics down"));
        await RunCycleAsync();
        Assert.Equal(RecognitionState.Error, _orchestrator.State);
        _emitted.Clear();
        _identifier.Enqueue(SongA);
        _lyrics.Enqueue(LyricsA);

        await RunCycleAsync();

        Assert.Equal(FullCycle, States);
        Assert.Equal(RecognitionState.DisplayingLyrics, _orchestrator.State);
        Assert.Same(LyricsA, _orchestrator.CurrentLyrics);
        Assert.Equal(2, _lyrics.RequestedSongs.Count);
        var recognized = Assert.Single(_recognized);
        Assert.Same(LyricsA, recognized.Lyrics);
    }

    [Fact]
    public async Task SameSongAfterAbortedLyricsFetch_RefetchesLyrics()
    {
        var gate = new BlockingGate();
        _lyrics.Gate = gate;
        _identifier.Enqueue(SongA);
        _lyrics.Enqueue(LyricsA);
        using var cts = new CancellationTokenSource();
        var aborted = _orchestrator.RecognizeOnceAsync(cts.Token);
        await gate.Entered.WaitAsync(GuardTimeout);
        cts.Cancel();
        gate.Release();
        await aborted.WaitAsync(GuardTimeout);
        Assert.Empty(_recognized);
        _identifier.Enqueue(SongA);
        _lyrics.Enqueue(LyricsB);

        await RunCycleAsync();

        Assert.Equal(RecognitionState.DisplayingLyrics, _orchestrator.State);
        Assert.Same(LyricsB, _orchestrator.CurrentLyrics);
        Assert.Equal(2, _lyrics.RequestedSongs.Count);
        Assert.Single(_recognized);
    }

    [Fact]
    public async Task SameSongAfterError_RestoresDisplayingLyrics_WithoutRefetching()
    {
        await ShowSongAAsync();
        _identifier.EnqueueThrow(new InvalidOperationException("network down"));
        await RunCycleAsync();
        Assert.Equal(RecognitionState.Error, _orchestrator.State);
        Assert.Equal(SongA, _orchestrator.CurrentSong);
        _emitted.Clear();
        _identifier.Enqueue(SongA);

        await RunCycleAsync();

        Assert.Equal(
            new[] { RecognitionState.Listening, RecognitionState.Identifying, RecognitionState.DisplayingLyrics },
            States);
        Assert.Same(LyricsA, _orchestrator.CurrentLyrics);
        Assert.Single(_lyrics.RequestedSongs);
        Assert.Single(_recognized);
    }

    [Fact]
    public async Task MissAfterError_WithSongStillCurrent_RestoresDisplayingLyrics()
    {
        await ShowSongAAsync();
        _identifier.EnqueueThrow(new InvalidOperationException("network down"));
        await RunCycleAsync();
        _identifier.Enqueue(null);

        await RunCycleAsync();

        Assert.Equal(RecognitionState.DisplayingLyrics, _orchestrator.State);
        Assert.Equal(SongA, _orchestrator.CurrentSong);
        Assert.Same(LyricsA, _orchestrator.CurrentLyrics);
    }

    [Fact]
    public async Task OverlappingCycle_ReturnsImmediately_AndDoesNotCaptureAgain()
    {
        var gate = new BlockingGate();
        _capture.Gate = gate;
        _identifier.Enqueue(SongA);
        _lyrics.Enqueue(LyricsA);
        var first = _orchestrator.RecognizeOnceAsync();
        await gate.Entered.WaitAsync(GuardTimeout);

        await RunCycleAsync();

        Assert.Equal(1, _capture.CaptureCalls);
        Assert.Equal(new[] { RecognitionState.Listening }, States);
        Assert.False(first.IsCompleted);

        gate.Release();
        await first.WaitAsync(GuardTimeout);

        Assert.Equal(FullCycle, States);
        Assert.Equal(1, _capture.CaptureCalls);
    }

    [Fact]
    public async Task CycleAfterACompletedCycle_IsNotBlockedByTheOverlapGuard()
    {
        await ShowSongAAsync();
        _identifier.EnqueueThrow(new InvalidOperationException("boom"));
        await RunCycleAsync();
        _identifier.Enqueue(SongA);

        await RunCycleAsync();

        Assert.Equal(3, _capture.CaptureCalls);
        Assert.Equal(RecognitionState.DisplayingLyrics, _orchestrator.State);
    }

    public enum CancelStage
    {
        DuringCapture,
        DuringIdentify,
        DuringLyricsFetch,
    }

    [Theory]
    [InlineData(CancelStage.DuringCapture)]
    [InlineData(CancelStage.DuringIdentify)]
    [InlineData(CancelStage.DuringLyricsFetch)]
    public async Task CancelledCycle_EmitsNoStateAndChangesNothing_AfterTheCancel(CancelStage stage)
    {
        var gate = new BlockingGate();
        var statesBeforeCancel = stage switch
        {
            CancelStage.DuringCapture => new[] { RecognitionState.Listening },
            CancelStage.DuringIdentify => new[] { RecognitionState.Listening, RecognitionState.Identifying },
            _ => new[] { RecognitionState.Listening, RecognitionState.Identifying, RecognitionState.FetchingLyrics },
        };
        switch (stage)
        {
            case CancelStage.DuringCapture: _capture.Gate = gate; break;
            case CancelStage.DuringIdentify: _identifier.Gate = gate; break;
            default: _lyrics.Gate = gate; break;
        }
        _identifier.Enqueue(SongA);
        _lyrics.Enqueue(LyricsA);
        using var cts = new CancellationTokenSource();
        var cycle = _orchestrator.RecognizeOnceAsync(cts.Token);
        await gate.Entered.WaitAsync(GuardTimeout);
        var songAtCancel = _orchestrator.CurrentSong;
        var lyricsAtCancel = _orchestrator.CurrentLyrics;

        cts.Cancel();
        gate.Release();
        await cycle.WaitAsync(GuardTimeout);

        Assert.Equal(statesBeforeCancel, States);
        Assert.Same(songAtCancel, _orchestrator.CurrentSong);
        Assert.Same(lyricsAtCancel, _orchestrator.CurrentLyrics);
        Assert.Null(_orchestrator.ErrorMessage);
        Assert.Empty(_recognized);
        Assert.Empty(_errors);
    }

    [Fact]
    public async Task FailureAfterCancel_IsSilent()
    {
        var gate = new BlockingGate();
        _identifier.Gate = gate;
        _identifier.EnqueueThrow(new InvalidOperationException("late failure"));
        using var cts = new CancellationTokenSource();
        var cycle = _orchestrator.RecognizeOnceAsync(cts.Token);
        await gate.Entered.WaitAsync(GuardTimeout);

        cts.Cancel();
        gate.Release();
        await cycle.WaitAsync(GuardTimeout);

        Assert.Equal(new[] { RecognitionState.Listening, RecognitionState.Identifying }, States);
        Assert.Null(_orchestrator.ErrorMessage);
        Assert.Empty(_errors);
    }

    [Fact]
    public async Task AlreadyCancelledToken_EmitsNothingAndCapturesNothing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await _orchestrator.RecognizeOnceAsync(cts.Token).WaitAsync(GuardTimeout);

        Assert.Empty(_emitted);
        Assert.Equal(0, _capture.CaptureCalls);
    }

    [Fact]
    public async Task CycleAfterACancelledCycle_IsNotBlockedByTheOverlapGuard()
    {
        var gate = new BlockingGate();
        _capture.Gate = gate;
        using var cts = new CancellationTokenSource();
        var cancelled = _orchestrator.RecognizeOnceAsync(cts.Token);
        await gate.Entered.WaitAsync(GuardTimeout);
        cts.Cancel();
        gate.Release();
        await cancelled.WaitAsync(GuardTimeout);
        _identifier.Enqueue(SongA);
        _lyrics.Enqueue(LyricsA);

        await RunCycleAsync();

        Assert.Equal(2, _capture.CaptureCalls);
        Assert.Equal(RecognitionState.DisplayingLyrics, _orchestrator.State);
    }

    [Fact]
    public async Task Stop_AfterAMatch_ClearsSongAndLyrics_AndEmitsIdle()
    {
        await ShowSongAAsync();

        _orchestrator.Stop();

        Assert.Equal(new[] { RecognitionState.Idle }, States);
        Assert.Equal(RecognitionState.Idle, _orchestrator.State);
        Assert.Null(_orchestrator.CurrentSong);
        Assert.Null(_orchestrator.CurrentLyrics);
    }

    [Fact]
    public void Stop_WithoutStart_AndRepeatedStop_DoNotThrow()
    {
        _orchestrator.Stop();
        _orchestrator.Stop();

        Assert.Equal(RecognitionState.Idle, _orchestrator.State);
    }

    [Fact]
    public async Task Start_RunsTheFirstCycleImmediately_AndStopEndsIdle()
    {
        var displaying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _orchestrator.StateChanged += state =>
        {
            if (state == RecognitionState.DisplayingLyrics)
                displaying.TrySetResult();
        };
        _identifier.Enqueue(SongA);
        _lyrics.Enqueue(LyricsA);

        try
        {
            _orchestrator.Start(FarAwayIntervalSeconds);
            await displaying.Task.WaitAsync(GuardTimeout);
        }
        finally
        {
            _orchestrator.Stop();
        }

        Assert.Equal(FullCycle.Append(RecognitionState.Idle), States.SkipWhile(s => s == RecognitionState.Idle));
        Assert.Equal(1, _capture.CaptureCalls);
        Assert.Null(_orchestrator.CurrentSong);
    }
}
