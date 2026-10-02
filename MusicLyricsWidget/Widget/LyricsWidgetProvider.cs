using Microsoft.Windows.Widgets.Providers;
using MusicLyricsWidget.Services;

namespace MusicLyricsWidget.Widget;

public class LyricsWidgetProvider : IWidgetProvider
{
    private readonly WidgetStateManager _stateManager = new();
    private MusicRecognitionOrchestrator? _orchestrator;
    private string? _activeWidgetId;

    public void CreateWidget(WidgetContext widgetContext)
    {
        _activeWidgetId = widgetContext.Id;
        UpdateWidget(RecognitionState.Idle);
    }

    public void DeleteWidget(string widgetId, string customState)
    {
        if (_activeWidgetId == widgetId)
        {
            _orchestrator?.Stop();
            _orchestrator?.Dispose();
            _orchestrator = null;
            _activeWidgetId = null;
        }
    }

    public void OnActionInvoked(WidgetActionInvokedArgs actionInvokedArgs)
    {
        if (actionInvokedArgs.Verb == "listenNow")
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
        _activeWidgetId = widgetContext.Id;
    }

    public void Deactivate(string widgetId)
    {
        _orchestrator?.Stop();
    }

    private void StartListening()
    {
        if (_orchestrator != null)
        {
            _orchestrator.Stop();
            _orchestrator.Dispose();
        }

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

        _orchestrator = new MusicRecognitionOrchestrator(audioCapture, songId, lyrics);
        _orchestrator.StateChanged += state => UpdateWidget(state);
        _orchestrator.ErrorOccurred += msg => UpdateWidgetError(msg);

        _orchestrator.Start(intervalSeconds: 30);
    }

    private void UpdateWidget(RecognitionState state)
    {
        if (_activeWidgetId == null) return;

        var (template, data) = _stateManager.GetWidgetContent(
            state,
            _orchestrator?.CurrentSong,
            _orchestrator?.CurrentLyrics);

        WidgetManager.GetDefault().UpdateWidget(new WidgetUpdateRequestOptions(_activeWidgetId)
        {
            Template = template,
            Data = data
        });
    }

    private void UpdateWidgetError(string message)
    {
        if (_activeWidgetId == null) return;

        var (template, data) = _stateManager.GetWidgetContent(
            RecognitionState.Error,
            errorMessage: message);

        WidgetManager.GetDefault().UpdateWidget(new WidgetUpdateRequestOptions(_activeWidgetId)
        {
            Template = template,
            Data = data
        });
    }
}
