using MusicLyricsWidget.Models;

namespace MusicLyricsWidget.Services;

public enum RecognitionState
{
    Idle,
    Listening,
    Identifying,
    FetchingLyrics,
    DisplayingLyrics,
    Error
}

public class MusicRecognitionOrchestrator : IDisposable
{
    private readonly IAudioCapture _audioCapture;
    private readonly ISongIdentifier _songId;
    private readonly ILyricsProvider _lyrics;
    private CancellationTokenSource? _cts;
    private Timer? _pollTimer;
    private SongInfo? _currentSong;

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
    public void Start(int intervalSeconds = 30)
    {
        Stop();
        _cts = new CancellationTokenSource();
        // Run first recognition immediately, then on interval
        _ = RecognizeOnceAsync(_cts.Token);
        _pollTimer = new Timer(
            async _ => await RecognizeOnceAsync(_cts.Token),
            null,
            TimeSpan.FromSeconds(intervalSeconds),
            TimeSpan.FromSeconds(intervalSeconds));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _pollTimer?.Dispose();
        _pollTimer = null;
        SetState(RecognitionState.Idle);
    }

    /// <summary>
    /// Runs a single recognize cycle: capture → identify → fetch lyrics.
    /// </summary>
    public async Task RecognizeOnceAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Step 1: Capture audio
            SetState(RecognitionState.Listening);
            var pcmData = await _audioCapture.CaptureAudioAsync(cancellationToken);
            var wavData = _audioCapture.WrapInWav(pcmData);

            // Step 2: Identify song
            SetState(RecognitionState.Identifying);
            var song = await _songId.IdentifyAsync(wavData, cancellationToken);

            if (song == null)
            {
                SetState(RecognitionState.Idle);
                return; // No music detected, stay idle
            }

            // Skip lyrics fetch if same song is still playing
            if (_currentSong != null && _currentSong.Equals(song) && CurrentLyrics != null)
            {
                SetState(RecognitionState.DisplayingLyrics);
                return;
            }

            // Step 3: Fetch lyrics
            _currentSong = song;
            SetState(RecognitionState.FetchingLyrics);
            var lyrics = await _lyrics.GetLyricsAsync(song, cancellationToken);

            CurrentLyrics = lyrics;
            SetState(RecognitionState.DisplayingLyrics);
            if (lyrics != null)
            {
                SongRecognized?.Invoke(song, lyrics);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on stop
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            SetState(RecognitionState.Error);
            ErrorOccurred?.Invoke(ex.Message);
        }
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
