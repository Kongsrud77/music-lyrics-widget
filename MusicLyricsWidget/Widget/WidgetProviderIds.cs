namespace MusicLyricsWidget.Widget;

/// <summary>
/// Identifiers shared between the widget provider code, <c>Package.appxmanifest</c> and the card templates.
/// Plain constants only: this file is also compiled into the test project.
/// </summary>
internal static class WidgetProviderIds
{
    /// <summary>
    /// CLSID of the widget provider COM class. Must equal <c>com:Class/@Id</c> and <c>CreateInstance/@ClassId</c>
    /// in <c>Package.appxmanifest</c>.
    /// </summary>
    public const string ProviderClsid = "3B37F72B-9C04-418C-88AE-BEB6058AE758";

    /// <summary>
    /// Verb of the <c>Action.Execute</c> buttons in every widget template.
    /// </summary>
    public const string ListenNowVerb = "listenNow";

    /// <summary>
    /// Command-line argument that makes the exe serve the widget provider instead of opening the settings window.
    /// Must equal <c>com:ExeServer/@Arguments</c> in <c>Package.appxmanifest</c>: COM starts the exe with that
    /// argument, and without it <c>Program.Main</c> never registers the class object.
    /// </summary>
    public const string ComServerArgument = "-RegisterProcessAsComServer";
}
