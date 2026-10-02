using MusicLyricsWidget.Widget;
using Newtonsoft.Json.Linq;

namespace MusicLyricsWidget.Tests;

public class TemplateVerbTests
{
    private const string ActionsPath = "$.actions[*]";
    private const string VerbProperty = "verb";

    public static TheoryData<string> TemplateFiles => new()
    {
        "ListeningTemplate.json",
        "LyricsTemplate.json",
        "ErrorTemplate.json",
    };

    [Theory]
    [MemberData(nameof(TemplateFiles))]
    public void EveryTemplateAction_UsesTheListenNowVerb(string fileName)
    {
        var template = JObject.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Widget", "WidgetTemplates", fileName)));

        var actions = template.SelectTokens(ActionsPath).ToList();

        Assert.NotEmpty(actions);
        Assert.All(actions, action => Assert.Equal(WidgetProviderIds.ListenNowVerb, (string?)action[VerbProperty]));
    }
}
