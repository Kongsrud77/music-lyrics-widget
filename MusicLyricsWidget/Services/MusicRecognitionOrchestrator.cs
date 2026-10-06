using System.Diagnostics;
using MusicLyricsWidget.Models;

namespace MusicLyricsWidget.Services;

public enum RecognitionState
{
    Idle,
    Listening,
    Identifying,
    FetchingLyrics,
    DisplayingLyrics,
    Error,

    /// <summary>A cycle finished without recognising any music and there is no song left to show.</summary>
    NoMatch
}

public class MusicRecognitionOrchestrator : IDisposable
{
    /// <summary>
    /// Consecutive cycles without a match tolerated before the displayed song and lyrics are cleared.
    /// Music drops out briefly (silence, talking, a fade), and one miss should not blank the card.
    /// </summary>
    internal const int MissesBeforeClearingLyrics = 2;

    private const int CycleIdle = 0;
    private const int CycleRunning = 1;
    private const int NotPaused = 0;
    private const int Paused = 1;
    private const int DefaultIntervalSeconds = 30;

    private readonly IAudioCapture _audioCapture;
    private readonly ISongIdentifier _songId;
    private readonly ILyricsProvider _lyrics;
    private readonly TimeProvider _time;
    private readonly LyricsClock _clock;
    private CancellationTokenSource? _cts;
    private Timer? _pollTimer;
    private SongInfo? _currentSong;
    private int _consecutiveMisses;
    private bool _lyricsResolved;
    private int _cycleState = CycleIdle;
    private int _pauseState = NotPaused;
    private int _intervalSeconds = DefaultIntervalSeconds;

    public RecognitionState State { get; private set; } = RecognitionState.Idle;
    public SongInfo? CurrentSong => _currentSong;
    public LyricsResult? CurrentLyrics { get; private set; }
    public string? ErrorMessage { get; private set; }

    public event Action<RecognitionState>? StateChanged;
    public event Action<SongInfo, LyricsResult>? SongRecognized;
    public event Action<string>? ErrorOccurred;

    /// <summary>The synced line being sung (-1 before the first); null without synced lyrics or a known position.</summary>
    public int? CurrentLineIndex => _clock.CurrentLineIndex;

    /// <summary>Raised when the current synced line changes while lyrics are shown (from a timer or a re-sync).</summary>
    public event Action? LinePositionChanged;

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

    /// <summary>
    /// Starts the recognition loop. Polls every <paramref name="intervalSeconds"/> seconds.
    /// </summary>
    public void Start(int intervalSeconds = DefaultIntervalSeconds)
    {
        Stop();
        _intervalSeconds = intervalSeconds;
        StartLoop();
    }

    /// <summary>True between <see cref="Pause"/> and <see cref="Resume"/>/<see cref="Stop"/>/<see cref="Start"/>.</summary>
    public bool IsPaused => Volatile.Read(ref _pauseState) == Paused;

    /// <summary>
    /// Stops listening (the microphone turns off) but keeps the song, lyrics and state on display, so
    /// <see cref="Resume"/> can continue where it left off. Does nothing unless the loop is running.
    /// An in-flight cycle is cancelled and emits nothing further.
    /// </summary>
    public void Pause()
    {
        _clock.Halt();
        if (StopLoop())
            Volatile.Write(ref _pauseState, Paused);
    }

    /// <summary>Restarts a paused loop, with the first cycle immediately. Does nothing unless paused.</summary>
    public void Resume()
    {
        if (Interlocked.CompareExchange(ref _pauseState, NotPaused, Paused) != Paused)
            return;

        if (State == RecognitionState.DisplayingLyrics)
        {
            // One catch-up event for the time spent paused, then the clock ticks again.
            if (_clock.Refresh())
                LinePositionChanged?.Invoke();
            _clock.Run();
        }
        StartLoop();
    }

    /// <summary>
    /// Stops the loop and forgets the current song. An in-flight cycle is cancelled and emits nothing further.
    /// </summary>
    public void Stop()
    {
        Volatile.Write(ref _pauseState, NotPaused);
        StopLoop();
        _currentSong = null;
        CurrentLyrics = null;
        _consecutiveMisses = 0;
        _lyricsResolved = false;
        _clock.Reset();
        SetState(RecognitionState.Idle);
    }

    private void StartLoop()
    {
        var cts = new CancellationTokenSource();
        _cts = cts;
        var token = cts.Token;
        var interval = TimeSpan.FromSeconds(_intervalSeconds);

        // Not an async lambda: RecognizeOnceAsync never throws, so nothing can escape onto the thread pool.
        _pollTimer = new Timer(_ => _ = RecognizeOnceAsync(token), null, interval, interval);

        // Run first recognition immediately, then on interval
        _ = RecognizeOnceAsync(token);
    }

    /// <summary>Disposes the timer and cancels the loop's token. Returns whether a loop was running.</summary>
    private bool StopLoop()
    {
        Interlocked.Exchange(ref _pollTimer, null)?.Dispose();
        // Cancel but do not dispose: an in-flight cycle still holds the token.
        var cts = Interlocked.Exchange(ref _cts, null);
        cts?.Cancel();
        return cts != null;
    }

    /// <summary>
    /// Runs a single recognize cycle: capture → identify → fetch lyrics.
    /// Returns immediately when a cycle is already running. Never throws: cancellation is silent and any other
    /// failure ends in <see cref="RecognitionState.Error"/>.
    /// </summary>
    public async Task RecognizeOnceAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _cycleState, CycleRunning, CycleIdle) != CycleIdle)
            return;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Decided once, up front: a cycle that starts on a displayed song runs silently so the card does not flicker.
            var showingSong = State == RecognitionState.DisplayingLyrics && _currentSong != null;

            // Step 1: Capture audio
            if (!showingSong)
                SetState(RecognitionState.Listening);
            var pcmData = await _audioCapture.CaptureAudioAsync(cancellationToken);
            var captureEnd = _time.GetTimestamp();   // the song position from ACRCloud belongs to this instant
            cancellationToken.ThrowIfCancellationRequested();
            var wavData = _audioCapture.WrapInWav(pcmData);

            // Step 2: Identify song
            if (!showingSong)
                SetState(RecognitionState.Identifying);
            var song = await _songId.IdentifyAsync(wavData, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (song == null)
                HandleNoMatch();
            else
                await HandleMatchAsync(song, captureEnd, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected on stop. A cancellation the token did not ask for (e.g. an HttpClient timeout) is a real
            // failure and falls through to the Error handling below.
        }
        catch (Exception ex)
        {
            // A failure that surfaces after Stop belongs to the cancellation, not to the user.
            if (cancellationToken.IsCancellationRequested)
                return;

            ErrorMessage = ex.Message;
            SetState(RecognitionState.Error);
            ErrorOccurred?.Invoke(ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _cycleState, CycleIdle);
        }
    }

    private async Task HandleMatchAsync(SongInfo song, long captureEnd, CancellationToken cancellationToken)
    {
        _consecutiveMisses = 0;

        // Same song still playing and its lookup finished (even if it found nothing): no refetch, and no redraw
        // unless an error moved the state. A lookup that failed or was aborted is retried on the next match.
        if (song.Equals(_currentSong) && _lyricsResolved)
        {
            // Move the anchor (fixes pauses, seeks and drift on the player) without a redraw of the state.
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

        // Step 3: Fetch lyrics
        _currentSong = song;
        CurrentLyrics = null;
        _lyricsResolved = false;
        _clock.Reset();
        SetState(RecognitionState.FetchingLyrics);
        var lyrics = await _lyrics.GetLyricsAsync(song, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        _lyricsResolved = true;
        CurrentLyrics = lyrics;
        TrackLyrics(song, captureEnd);   // the first render already shows the right line
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

    private void HandleNoMatch()
    {
        _consecutiveMisses++;

        // Only a song whose lyrics lookup finished may stay on the card: after a failed or aborted lookup there is
        // nothing to restore, and showing DisplayingLyrics would claim "no lyrics found" for a song never looked up.
        if (_currentSong != null && _lyricsResolved && _consecutiveMisses < MissesBeforeClearingLyrics)
        {
            EnsureDisplayingLyrics();
            return;
        }

        // The next match refetches: it sees no current song.
        _currentSong = null;
        CurrentLyrics = null;
        _lyricsResolved = false;
        _clock.Reset();
        SetState(RecognitionState.NoMatch);
    }

    private void EnsureDisplayingLyrics()
    {
        if (State != RecognitionState.DisplayingLyrics)
            SetState(RecognitionState.DisplayingLyrics);
    }

    private void SetState(RecognitionState newState)
    {
        State = newState;
        // Lines tick only while the lyrics card is up; any other state (error, no match, a new fetch) stops them.
        if (newState == RecognitionState.DisplayingLyrics)
        {
            // A card restored after the clock was halted must show the line for now, not the one it stopped on.
            // No event: the render for this state change is about to read the index.
            _clock.Refresh();
            _clock.Run();
        }
        else
        {
            _clock.Halt();
        }
        StateChanged?.Invoke(newState);
    }

    private void OnLineChanged()
    {
        // A tick can arrive just after the clock was halted; it must not redraw a card that is no longer the lyrics.
        if (State != RecognitionState.DisplayingLyrics)
            return;

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
}
