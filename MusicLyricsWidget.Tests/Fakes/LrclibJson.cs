using Newtonsoft.Json.Linq;

namespace MusicLyricsWidget.Tests.Fakes;

/// <summary>Builders for LRCLIB track JSON, so tests state only the fields they care about.</summary>
internal static class LrclibJson
{
    /// <summary>One LRCLIB track. A null value becomes a JSON null, as LRCLIB sends for missing lyrics.</summary>
    public static JObject Track(double? duration, string? synced, string? plain) => new()
    {
        ["duration"] = duration,
        ["syncedLyrics"] = synced,
        ["plainLyrics"] = plain,
    };

    /// <summary>A JSON array body, as returned by <c>/api/search</c>.</summary>
    public static string SearchBody(params JObject[] tracks) => new JArray(tracks).ToString();
}
