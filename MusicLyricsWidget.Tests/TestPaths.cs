namespace MusicLyricsWidget.Tests;

/// <summary>
/// Locates files in the repository from the test output directory.
/// </summary>
internal static class TestPaths
{
    private const string SolutionFileName = "MusicLyricsWidget.sln";

    /// <summary>
    /// The repository root: the nearest parent of <see cref="AppContext.BaseDirectory"/> that contains the solution file.
    /// </summary>
    public static string RepoRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, SolutionFileName)))
                    return directory.FullName;
                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException(
                $"Could not find '{SolutionFileName}' in or above '{AppContext.BaseDirectory}'.");
        }
    }
}
