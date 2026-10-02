using System.Text.RegularExpressions;
using System.Web;
using MusicLyricsWidget.Models;
using Newtonsoft.Json.Linq;

namespace MusicLyricsWidget.Services;

public class LyricsService
{
    private readonly HttpClient _httpClient;

    public LyricsService()
    {
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "MusicLyricsWidget/1.0");
    }

    /// <summary>
    /// Fetches lyrics from LRCLIB (primary) with plain-text fallback.
    /// </summary>
    public async Task<LyricsResult?> GetLyricsAsync(SongInfo song, CancellationToken cancellationToken = default)
    {
        // Try LRCLIB first
        var result = await TryLrclibAsync(song, cancellationToken);
        if (result != null)
            return result;

        return null;
    }

    private async Task<LyricsResult?> TryLrclibAsync(SongInfo song, CancellationToken cancellationToken)
    {
        try
        {
            var trackName = HttpUtility.UrlEncode(song.Title);
            var artistName = HttpUtility.UrlEncode(song.Artist);
            var url = $"https://lrclib.net/api/search?track_name={trackName}&artist_name={artistName}";

            var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var results = JArray.Parse(json);

            if (results.Count == 0)
                return null;

            // Pick the best match (first result)
            var best = results[0];

            var lyricsResult = new LyricsResult
            {
                Source = "LRCLIB"
            };

            // Prefer synced lyrics
            var syncedLyrics = best["syncedLyrics"]?.Value<string>();
            if (!string.IsNullOrWhiteSpace(syncedLyrics))
            {
                lyricsResult.SyncedLyrics = ParseLrc(syncedLyrics);
                lyricsResult.PlainLyrics = string.Join("\n",
                    lyricsResult.SyncedLyrics.Select(l => l.Text));
            }
            else
            {
                var plainLyrics = best["plainLyrics"]?.Value<string>();
                if (!string.IsNullOrWhiteSpace(plainLyrics))
                {
                    lyricsResult.PlainLyrics = plainLyrics;
                }
                else
                {
                    return null;
                }
            }

            return lyricsResult;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Parses LRC format lyrics into timestamped lines.
    /// Example: [01:23.45] Some lyrics text
    /// </summary>
    private static List<LyricsLine> ParseLrc(string lrcText)
    {
        var lines = new List<LyricsLine>();
        var regex = new Regex(@"\[(\d{2}):(\d{2})\.(\d{2,3})\]\s*(.*)");

        foreach (var line in lrcText.Split('\n'))
        {
            var match = regex.Match(line);
            if (match.Success)
            {
                var minutes = int.Parse(match.Groups[1].Value);
                var seconds = int.Parse(match.Groups[2].Value);
                var msStr = match.Groups[3].Value;
                var milliseconds = msStr.Length == 2
                    ? int.Parse(msStr) * 10
                    : int.Parse(msStr);
                var text = match.Groups[4].Value.Trim();

                if (!string.IsNullOrEmpty(text))
                {
                    lines.Add(new LyricsLine
                    {
                        Timestamp = new TimeSpan(0, 0, minutes, seconds, milliseconds),
                        Text = text
                    });
                }
            }
        }

        return lines.OrderBy(l => l.Timestamp).ToList();
    }
}
