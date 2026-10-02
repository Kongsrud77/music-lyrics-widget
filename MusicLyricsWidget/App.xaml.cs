using Microsoft.UI.Xaml;

namespace MusicLyricsWidget;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        this.InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Show the companion settings app
        _window = new MainWindow();
        _window.Activate();
    }
}
