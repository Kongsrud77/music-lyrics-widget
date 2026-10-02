using System.Xml.Linq;
using MusicLyricsWidget.Widget;

namespace MusicLyricsWidget.Tests;

public class ManifestTests
{
    private const string ProjectFolderName = "MusicLyricsWidget";
    private const string ManifestFileName = "Package.appxmanifest";
    private const string ExpectedAllowMultiple = "false";
    private const string ExpectedExecutable = "MusicLyricsWidget.exe";
    private const char ManifestPathSeparator = '\\';

    private readonly string _projectFolder;
    private readonly XElement _manifest;

    public ManifestTests()
    {
        _projectFolder = Path.Combine(TestPaths.RepoRoot, ProjectFolderName);
        _manifest = XDocument.Load(Path.Combine(_projectFolder, ManifestFileName)).Root
            ?? throw new InvalidOperationException($"'{ManifestFileName}' has no root element.");
    }

    [Fact]
    public void ComClassId_AndCreateInstanceClassId_EqualTheProviderClsid()
    {
        var comClass = Assert.Single(Descendants(_manifest, "Class"));
        var createInstance = Assert.Single(Descendants(_manifest, "CreateInstance"));

        Assert.Equal(WidgetProviderIds.ProviderClsid, Attribute(comClass, "Id"), ignoreCase: true);
        Assert.Equal(WidgetProviderIds.ProviderClsid, Attribute(createInstance, "ClassId"), ignoreCase: true);
    }

    [Fact]
    public void ExeServer_LaunchesTheAppExeWithTheComServerArgument()
    {
        var exeServer = Assert.Single(Descendants(_manifest, "ExeServer"));

        Assert.Equal(ExpectedExecutable, Attribute(exeServer, "Executable"));
        Assert.Contains(WidgetProviderIds.ComServerArgument, Attribute(exeServer, "Arguments"));
    }

    [Fact]
    public void EveryDefinition_DisallowsMultipleInstances()
    {
        var definitions = Descendants(_manifest, "Definition");

        Assert.NotEmpty(definitions);
        Assert.All(definitions, d => Assert.Equal(ExpectedAllowMultiple, (string?)d.Attribute("AllowMultiple")));
    }

    [Fact]
    public void EveryDefinition_HasAScreenshotThatExistsOnDisk()
    {
        var definitions = Descendants(_manifest, "Definition");

        Assert.NotEmpty(definitions);
        Assert.All(definitions, definition =>
        {
            var screenshots = Descendants(definition, "Screenshots")
                .SelectMany(s => s.Elements().Where(e => e.Name.LocalName == "Screenshot"))
                .ToList();

            Assert.NotEmpty(screenshots);
            Assert.All(screenshots, s => AssertExistsInProject(Attribute(s, "Path")));
        });
    }

    [Fact]
    public void EveryIcon_ExistsOnDisk()
    {
        var icons = Descendants(_manifest, "Icon");

        Assert.NotEmpty(icons);
        Assert.All(icons, icon => AssertExistsInProject(Attribute(icon, "Path")));
    }

    private static List<XElement> Descendants(XElement parent, string localName) =>
        parent.Descendants().Where(e => e.Name.LocalName == localName).ToList();

    private static string Attribute(XElement element, string name) =>
        (string?)element.Attribute(name)
        ?? throw new InvalidOperationException($"<{element.Name.LocalName}> has no '{name}' attribute.");

    private void AssertExistsInProject(string manifestPath)
    {
        var fullPath = Path.Combine(
            _projectFolder,
            manifestPath.Replace(ManifestPathSeparator, Path.DirectorySeparatorChar));
        Assert.True(File.Exists(fullPath), $"Manifest path '{manifestPath}' does not exist at '{fullPath}'.");
    }
}
