using MusicLyricsWidget.Widget;

namespace MusicLyricsWidget.Tests;

public class LaunchArgumentsTests
{
    [Theory]
    [InlineData("-RegisterProcessAsComServer")]
    [InlineData("-registerprocessascomserver")]
    [InlineData("-Embedding")]
    [InlineData("-embedding")]
    [InlineData("/Embedding")]
    [InlineData("/EMBEDDING")]
    public void IsComServerLaunch_ComServerOrEmbeddingArgument_IsTrue(string argument)
    {
        Assert.True(LaunchArguments.IsComServerLaunch(new[] { argument }));
    }

    [Fact]
    public void IsComServerLaunch_ManifestArgument_IsTrue()
    {
        Assert.True(LaunchArguments.IsComServerLaunch(new[] { WidgetProviderIds.ComServerArgument }));
    }

    [Fact]
    public void IsComServerLaunch_ComServerArgumentAmongOthers_IsTrue()
    {
        Assert.True(LaunchArguments.IsComServerLaunch(new[] { "--verbose", WidgetProviderIds.ComServerArgument }));
    }

    [Fact]
    public void IsComServerLaunch_NoArguments_IsFalse()
    {
        Assert.False(LaunchArguments.IsComServerLaunch(Array.Empty<string>()));
    }

    [Theory]
    [InlineData("--verbose")]
    [InlineData("Embedding")]
    [InlineData("-RegisterProcessAsComServer2")]
    [InlineData("")]
    public void IsComServerLaunch_UnrelatedArgument_IsFalse(string argument)
    {
        Assert.False(LaunchArguments.IsComServerLaunch(new[] { argument }));
    }
}
