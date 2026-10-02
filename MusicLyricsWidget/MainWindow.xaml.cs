using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MusicLyricsWidget.Services;

namespace MusicLyricsWidget;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        this.InitializeComponent();

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
        appWindow.Resize(new Windows.Graphics.SizeInt32(550, 600));

        Title = "Music Lyrics Widget - Settings";
        LoadSettings();
        SaveButton.Click += SaveButton_Click;
        TestButton.Click += TestButton_Click;
    }

    private void LoadSettings()
    {
        var settings = SettingsManager.Load();
        HostInput.Text = settings.AcrCloudHost;
        AccessKeyInput.Text = settings.AcrCloudAccessKey;
        AccessSecretInput.Password = settings.AcrCloudAccessSecret;
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = new AppSettings
            {
                AcrCloudHost = HostInput.Text.Trim(),
                AcrCloudAccessKey = AccessKeyInput.Text.Trim(),
                AcrCloudAccessSecret = AccessSecretInput.Password.Trim()
            };

            SettingsManager.Save(settings);

            var dialog = new ContentDialog
            {
                Title = "Success",
                Content = "Settings saved!",
                CloseButtonText = "OK",
                XamlRoot = Content.XamlRoot
            };
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error saving: {ex.Message}";
        }
    }

    private async void TestButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = SettingsManager.Load();
        if (string.IsNullOrEmpty(settings.AcrCloudAccessKey))
        {
            StatusText.Text = "Save your API keys first!";
            return;
        }

        TestButton.IsEnabled = false;
        LyricsText.Text = "";

        try
        {
            // Step 1: Capture audio
            StatusText.Text = "Listening for music (7 seconds)...";
            using var audioCapture = new AudioCaptureService();
            var pcmData = await audioCapture.CaptureAudioAsync();
            var wavData = audioCapture.WrapInWav(pcmData);

            // Check audio level
            double maxSample = 0;
            for (int i = 0; i < pcmData.Length - 1; i += 2)
            {
                var sample = Math.Abs((short)(pcmData[i] | (pcmData[i + 1] << 8)));
                if (sample > maxSample) maxSample = sample;
            }
            var levelPercent = (maxSample / 32768.0) * 100;
            StatusText.Text = $"Audio captured: {pcmData.Length} bytes, peak level: {levelPercent:F1}% — Identifying song...";
            var songId = new SongIdentificationService(
                settings.AcrCloudHost,
                settings.AcrCloudAccessKey,
                settings.AcrCloudAccessSecret);
            var song = await songId.IdentifyAsync(wavData);

            if (song == null)
            {
                StatusText.Text += "\nNo music detected by ACRCloud.";
                LyricsText.Text = $"Host: [{settings.AcrCloudHost}]\nKey: [{settings.AcrCloudAccessKey}]\nSecret length: {settings.AcrCloudAccessSecret.Length}\n\n{songId.LastDebugInfo}\n\nACRCloud response:\n{songId.LastRawResponse}";
                return;
            }

            StatusText.Text = $"Found: {song.Artist} - {song.Title}";

            // Step 3: Fetch lyrics
            var lyricsService = new LyricsService();
            var lyrics = await lyricsService.GetLyricsAsync(song);

            if (lyrics != null)
            {
                LyricsText.Text = lyrics.PlainLyrics;
                StatusText.Text = $"{song.Artist} - {song.Title} (lyrics from {lyrics.Source})";
            }
            else
            {
                LyricsText.Text = "No lyrics found for this song.";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }
}
