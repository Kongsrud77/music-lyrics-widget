namespace MusicLyricsWidget.Widget;

/// <summary>
/// Decides from the command line how the process was launched. Pure string logic, no WinRT: this file is also
/// compiled into the test project.
/// </summary>
internal static class LaunchArguments
{
    // COM's classic launch flag for a local server (a plain CLSID registration passes it). The manifest passes
    // WidgetProviderIds.ComServerArgument; accepting these too keeps the provider reachable if COM launches the
    // exe without the manifest arguments. Windows command-line flags are case-insensitive.
    private const string EmbeddingDashArgument = "-Embedding";
    private const string EmbeddingSlashArgument = "/Embedding";

    /// <summary>True when the process was started by COM to serve the widget provider, not by the user.</summary>
    public static bool IsComServerLaunch(IEnumerable<string> args) =>
        args.Any(IsComServerArgument);

    private static bool IsComServerArgument(string argument) =>
        argument.Equals(WidgetProviderIds.ComServerArgument, StringComparison.OrdinalIgnoreCase)
        || argument.Equals(EmbeddingDashArgument, StringComparison.OrdinalIgnoreCase)
        || argument.Equals(EmbeddingSlashArgument, StringComparison.OrdinalIgnoreCase);
}
