using Microsoft.Windows.Widgets.Providers;
using MusicLyricsWidget.Services;

namespace MusicLyricsWidget.Widget;

/// <summary>
/// Widget provider called by the Widget Host on arbitrary RPC threads, while the orchestrator raises its events
/// from timer and thread-pool threads. All shared state is therefore guarded by <see cref="_gate"/>.
/// </summary>
public class LyricsWidgetProvider : IWidgetProvider
{
    // Seconds between recognition cycles while the widget is listening.
    private const int PollIntervalSeconds = 30;

    private readonly WidgetStateManager _stateManager = new();
    private readonly Action _onLastWidgetDeleted;

    // Guards _activeWidgetId and _orchestrator. Never held across WidgetManager (a cross-process COM call) or an
    // orchestrator Start/Stop/Dispose (they raise StateChanged synchronously, which would re-enter the provider).
    private readonly object _gate = new();
    private MusicRecognitionOrchestrator? _orchestrator;
    private string? _activeWidgetId;

    /// <summary>
    /// Creates the provider and recovers the widget that is already running after a provider restart.
    /// </summary>
    /// <param name="onLastWidgetDeleted">Invoked when no widget is left, so the COM server process can exit.</param>
    public LyricsWidgetProvider(Action onLastWidgetDeleted)
    {
        _onLastWidgetDeleted = onLastWidgetDeleted;

        var runningWidgets = WidgetManager.GetDefault().GetWidgetInfos();
        if (runningWidgets is { Length: > 0 })
        {
            _activeWidgetId = runningWidgets[0].WidgetContext.Id;
        }
    }

    public void CreateWidget(WidgetContext widgetContext)
    {
        var widgetId = widgetContext.Id;
        lock (_gate)
        {
            _activeWidgetId = widgetId;
        }

        PushState(widgetId, RecognitionState.Idle, source: null);
    }

    public void DeleteWidget(string widgetId, string customState)
    {
        // Detach first: the Idle event of the old orchestrator is then ignored as stale instead of being pushed
        // to the widget that is being deleted.
        MusicRecognitionOrchestrator? stale = null;
        lock (_gate)
        {
            if (_activeWidgetId == widgetId)
            {
                stale = _orchestrator;
                _orchestrator = null;
                _activeWidgetId = null;
            }
        }

        try
        {
            StopAndDispose(stale);
        }
        finally
        {
            // Also for an untracked id, and even when Stop/Dispose throws: the process must not stay alive.
            if (GetActiveWidgetId() == null)
            {
                _onLastWidgetDeleted();
            }
        }
    }

    public void OnActionInvoked(WidgetActionInvokedArgs actionInvokedArgs)
    {
        if (actionInvokedArgs.Verb == WidgetProviderIds.ListenNowVerb)
        {
            StartListening();
        }
    }

    public void OnWidgetContextChanged(WidgetContextChangedArgs contextChangedArgs)
    {
        // Handle widget size changes if needed
    }

    public void Activate(WidgetContext widgetContext)
    {
        var widgetId = widgetContext.Id;
        MusicRecognitionOrchestrator? orchestrator;
        lock (_gate)
        {
            _activeWidgetId = widgetId;
            orchestrator = _orchestrator;
        }

        PushState(widgetId, orchestrator?.State ?? RecognitionState.Idle, orchestrator);
    }

    public void Deactivate(string widgetId)
    {
        GetOrchestrator()?.Stop();
    }

    private void StartListening()
    {
        // A new listen always replaces the running one, also when the keys turn out to be missing.
        StopAndDispose(DetachOrchestrator());

        var settings = SettingsManager.Load();
        if (string.IsNullOrEmpty(settings.AcrCloudHost) ||
            string.IsNullOrEmpty(settings.AcrCloudAccessKey) ||
            string.IsNullOrEmpty(settings.AcrCloudAccessSecret))
        {
            UpdateWidgetError("Please configure ACRCloud API keys in the companion app.");
            return;
        }

        var audioCapture = new AudioCaptureService();
        var songId = new SongIdentificationService(
            settings.AcrCloudHost,
            settings.AcrCloudAccessKey,
            settings.AcrCloudAccessSecret);
        var lyrics = new LyricsService();

        var orchestrator = new MusicRecognitionOrchestrator(audioCapture, songId, lyrics);
        orchestrator.StateChanged += state => OnStateChanged(orchestrator, state);

        MusicRecognitionOrchestrator? replaced;
        lock (_gate)
        {
            replaced = _orchestrator;
            _orchestrator = orchestrator;
        }

        // Only set when a concurrent StartListening installed its orchestrator in the meantime.
        StopAndDispose(replaced);

        orchestrator.Start(PollIntervalSeconds);

        // A DeleteWidget or newer StartListening between the install and Start() detached this orchestrator and
        // stopped it before the loop began; stop it again so the microphone loop cannot outlive its widget.
        if (!ReferenceEquals(orchestrator, GetOrchestrator()))
        {
            StopAndDispose(orchestrator);
        }
    }

    private void OnStateChanged(MusicRecognitionOrchestrator source, RecognitionState state)
    {
        string? widgetId;
        lock (_gate)
        {
            if (!ReferenceEquals(source, _orchestrator)) return;
            widgetId = _activeWidgetId;
        }

        if (widgetId != null)
        {
            PushState(widgetId, state, source);
        }
    }

    /// <summary>
    /// Renders <paramref name="state"/> with the song, lyrics and error of <paramref name="source"/>, the
    /// orchestrator that raised it. An error is rendered here only: there is no separate error event handler.
    /// </summary>
    private void PushState(string widgetId, RecognitionState state, MusicRecognitionOrchestrator? source)
    {
        var (template, data) = _stateManager.GetWidgetContent(
            state,
            source?.CurrentSong,
            source?.CurrentLyrics,
            source?.ErrorMessage);

        PushToWidget(widgetId, template, data);
    }

    private void UpdateWidgetError(string message)
    {
        var widgetId = GetActiveWidgetId();
        if (widgetId == null) return;

        var (template, data) = _stateManager.GetWidgetContent(
            RecognitionState.Error,
            errorMessage: message);

        PushToWidget(widgetId, template, data);
    }

    private static void PushToWidget(string widgetId, string template, string data)
    {
        WidgetManager.GetDefault().UpdateWidget(new WidgetUpdateRequestOptions(widgetId)
        {
            Template = template,
            Data = data
        });
    }

    private static void StopAndDispose(MusicRecognitionOrchestrator? orchestrator)
    {
        if (orchestrator == null) return;

        orchestrator.Stop();
        orchestrator.Dispose();
    }

    private string? GetActiveWidgetId()
    {
        lock (_gate)
        {
            return _activeWidgetId;
        }
    }

    private MusicRecognitionOrchestrator? GetOrchestrator()
    {
        lock (_gate)
        {
            return _orchestrator;
        }
    }

    private MusicRecognitionOrchestrator? DetachOrchestrator()
    {
        lock (_gate)
        {
            var orchestrator = _orchestrator;
            _orchestrator = null;
            return orchestrator;
        }
    }
}
