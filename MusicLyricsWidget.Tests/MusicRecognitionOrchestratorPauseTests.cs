using MusicLyricsWidget.Models;
using MusicLyricsWidget.Services;
using MusicLyricsWidget.Tests.Fakes;

namespace MusicLyricsWidget.Tests;

/// <summary>
/// Pause/Resume back the widget's "panel closed = microphone off, panel reopened = listen on" behaviour:
/// pausing must keep the song and lyrics on display, and only a paused loop may resume.
/// </summary>
public class MusicRecognitionOrchestratorPauseTests
{
    private const int FarAwayIntervalSeconds = 3600;
    private static readonly TimeSpan GuardTimeout = TimeSpan.FromSeconds(10);
    private static readonly SongInfo SongA = new() { Title = "Song A", Artist = "Artist A" };
    private static readonly LyricsResult LyricsA = new() { PlainLyrics = "Lyrics A", Source = "LRCLIB" };

    private readonly FakeAudioCapture _capture = new();
    private readonly FakeSongIdentifier _identifier = new();
    private readonly FakeLyricsProvider _lyrics = new();
    private readonly MusicRecognitionOrchestrator _orchestrator;
    private readonly List<RecognitionState> _emitted = new();

    public MusicRecognitionOrchestratorPauseTests()
    {
        _orchestrator = new MusicRecognitionOrchestrator(_capture, _identifier, _lyrics);
        _orchestrator.StateChanged += _emitted.Add;
    }

    /// <summary>Starts the loop (timer far away, so only the immediate cycle runs) and waits until SongA is shown.</summary>
    private async Task StartAndShowSongAAsync()
    {
        var displaying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _orchestrator.StateChanged += state =>
        {
            if (state == RecognitionState.DisplayingLyrics)
                displaying.TrySetResult();
        };
        _identifier.Enqueue(SongA);
        _lyrics.Enqueue(LyricsA);

        _orchestrator.Start(FarAwayIntervalSeconds);
        await displaying.Task.WaitAsync(GuardTimeout);
        _emitted.Clear();
    }

    [Fact]
    public async Task Pause_WhileShowingLyrics_KeepsSongLyricsAndState_AndEmitsNothing()
    {
        await StartAndShowSongAAsync();

        _orchestrator.Pause();

        Assert.True(_orchestrator.IsPaused);
        Assert.Empty(_emitted);
        Assert.Equal(RecognitionState.DisplayingLyrics, _orchestrator.State);
        Assert.Same(SongA, _orchestrator.CurrentSong);
        Assert.Same(LyricsA, _orchestrator.CurrentLyrics);
        _orchestrator.Stop();
    }

    [Fact]
    public async Task Resume_AfterPause_ListensAgainImmediately_WithoutResettingTheDisplay()
    {
        await StartAndShowSongAAsync();
        _orchestrator.Pause();
        var gate = new BlockingGate();
        _capture.Gate = gate;

        try
        {
            _orchestrator.Resume();
            await gate.Entered.WaitAsync(GuardTimeout);

            Assert.False(_orchestrator.IsPaused);
            Assert.Equal(2, _capture.CaptureCalls);
            Assert.Empty(_emitted);                       // no Idle, and the song stays shown while listening
            Assert.Same(LyricsA, _orchestrator.CurrentLyrics);
        }
        finally
        {
            _orchestrator.Stop();
            gate.Release();
        }
    }

    [Fact]
    public void Pause_WhenNotRunning_IsANoOp_SoResumeDoesNotStartListening()
    {
        _orchestrator.Pause();
        _orchestrator.Resume();

        Assert.False(_orchestrator.IsPaused);
        Assert.Equal(0, _capture.CaptureCalls);
        Assert.Empty(_emitted);
    }

    [Fact]
    public async Task Stop_AfterPause_ClearsTheSong_AndResumeThenDoesNothing()
    {
        await StartAndShowSongAAsync();
        _orchestrator.Pause();

        _orchestrator.Stop();
        _orchestrator.Resume();

        Assert.False(_orchestrator.IsPaused);
        Assert.Equal(new[] { RecognitionState.Idle }, _emitted);
        Assert.Null(_orchestrator.CurrentSong);
        Assert.Equal(1, _capture.CaptureCalls);
    }

    [Fact]
    public async Task Start_AfterPause_BeginsAFreshLoop_NotPaused()
    {
        await StartAndShowSongAAsync();
        _orchestrator.Pause();
        var gate = new BlockingGate();
        _capture.Gate = gate;

        try
        {
            _orchestrator.Start(FarAwayIntervalSeconds);
            await gate.Entered.WaitAsync(GuardTimeout);

            Assert.False(_orchestrator.IsPaused);
            Assert.Null(_orchestrator.CurrentSong);          // Start resets like a new "Listen Now"
        }
        finally
        {
            _orchestrator.Stop();
            gate.Release();
        }
    }
}
