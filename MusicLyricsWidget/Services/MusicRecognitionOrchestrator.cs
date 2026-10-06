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

    public MusicRecognitionOrchestrator(
        IAudioCapture audioCapture,
        ISongIdentifier songId,
        ILyricsProvider lyrics)
    {
        _audioCapture = audioCapture;
        _songId = songId;
        _lyrics = lyrics;
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
        if (StopLoop())
            Volatile.Write(ref _pauseState, Paused);
    }

    /// <summary>Restarts a paused loop, with the first cycle immediately. Does nothing unless paused.</summary>
    public void Resume()
    {
        if (Interlocked.CompareExchange(ref _pauseState, NotPaused, Paused) == Paused)
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
                await HandleMatchAsync(song, cancellationToken);
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

    private async Task HandleMatchAsync(SongInfo song, CancellationToken cancellationToken)
    {
        _consecutiveMisses = 0;

        // Same song still playing and its lookup finished (even if it found nothing): no refetch, and no redraw
        // unless an error moved the state. A lookup that failed or was aborted is retried on the next match.
        if (song.Equals(_currentSong) && _lyricsResolved)
        {
            EnsureDisplayingLyrics();
            return;
        }

        // Step 3: Fetch lyrics
        _currentSong = song;
        CurrentLyrics = null;
        _lyricsResolved = false;
        SetState(RecognitionState.FetchingLyrics);
        var lyrics = await _lyrics.GetLyricsAsync(song, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        _lyricsResolved = true;
        CurrentLyrics = lyrics;
        SetState(RecognitionState.DisplayingLyrics);
        if (lyrics != null)
        {
            SongRecognized?.Invoke(song, lyrics);
        }
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
        StateChanged?.Invoke(newState);
    }

    public void Dispose()
    {
        Stop();
        _audioCapture.Dispose();
    }
}
