namespace MusicLyricsWidget.Tests;

public class TestPathsTests
{
    [Fact]
    public void RepoRoot_ContainsTheSolutionFile()
    {
        Assert.True(File.Exists(Path.Combine(TestPaths.RepoRoot, "MusicLyricsWidget.sln")));
    }
}
