using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using MusicLyricsWidget.Models;
using Newtonsoft.Json.Linq;

namespace MusicLyricsWidget.Services;

public class LyricsService : ILyricsProvider
{
    private const string BaseUrl = "https://lrclib.net/api";
    private const string GetPath = "/get";
    private const string SearchPath = "/search";
    private const string SourceName = "LRCLIB";
    private const string UserAgent = "MusicLyricsWidget/1.0";

    // Query parameters and track fields of the LRCLIB API.
    private const string TrackNameParam = "track_name";
    private const string ArtistNameParam = "artist_name";
    private const string AlbumNameParam = "album_name";
    private const string DurationParam = "duration";
    private const string SyncedLyricsField = "syncedLyrics";
    private const string PlainLyricsField = "plainLyrics";
    private const string DurationField = "duration";
    private const int RequestTimeoutSeconds = 10;

    // A duration outside a plausible song length is more likely bad data than the real value: leave it out of the
    // query rather than let it make the exact lookup miss.
    private const int MinDurationSeconds = 1;
    private const int MaxDurationSeconds = 3600;

    // One client for the whole process: HttpClient is meant to be shared (a new one per instance leaks sockets).
    // The User-Agent is set here, once; the client is never mutated afterwards.
    private static readonly HttpClient SharedHttpClient = CreateSharedHttpClient();

    private readonly HttpClient _httpClient;

    public LyricsService() : this(SharedHttpClient)
    {
    }

    internal LyricsService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    private static HttpClient CreateSharedHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(RequestTimeoutSeconds) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    /// <summary>
    /// Fetches lyrics from LRCLIB. First asks for the exact track (title, artist, album and duration), because
    /// a search often returns many versions with different timings; if LRCLIB has no exact match (or the match
    /// has no lyrics) it searches by title and artist and picks the version whose duration is closest.
    /// Returns null when LRCLIB has no usable lyrics; throws when the lookup itself fails (HTTP error, timeout,
    /// unreadable response) so the caller can tell "no lyrics" from "could not look up".
    /// </summary>
    public async Task<LyricsResult?> GetLyricsAsync(SongInfo song, CancellationToken cancellationToken = default)
    {
        var exact = await GetExactMatchAsync(song, cancellationToken);
        if (exact != null)
            return exact;

        return await SearchAsync(song, cancellationToken);
    }

    private async Task<LyricsResult?> GetExactMatchAsync(SongInfo song, CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl}{GetPath}?{BuildTrackQuery(song)}";

        if (!string.IsNullOrWhiteSpace(song.Album))
            url += $"&{AlbumNameParam}={Uri.EscapeDataString(song.Album)}";

        var durationSeconds = ToQueryDurationSeconds(song.Duration);
        if (durationSeconds != null)
            url += $"&{DurationParam}={durationSeconds.Value.ToString(CultureInfo.InvariantCulture)}";

        using var response = await _httpClient.GetAsync(url, cancellationToken);

        // LRCLIB answers 404 TrackNotFound when nothing matches: that is "no exact match", not a failure.
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return ToLyricsResult(JObject.Parse(json));
    }

    private async Task<LyricsResult?> SearchAsync(SongInfo song, CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl}{SearchPath}?{BuildTrackQuery(song)}";

        using var response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var best = SelectBestMatch(JArray.Parse(json), song.Duration);
        return best == null ? null : ToLyricsResult(best);
    }

    private static string BuildTrackQuery(SongInfo song) =>
        $"{TrackNameParam}={Uri.EscapeDataString(song.Title)}&{ArtistNameParam}={Uri.EscapeDataString(song.Artist)}";

    /// <summary>Whole seconds for the /get query, or null when the duration is unknown or implausible.</summary>
    private static int? ToQueryDurationSeconds(TimeSpan? duration)
    {
        if (duration == null)
            return null;

        var seconds = Math.Round(duration.Value.TotalSeconds);
        if (seconds < MinDurationSeconds || seconds > MaxDurationSeconds)
            return null;

        return (int)seconds;
    }

    /// <summary>
    /// Picks the search result to use. Only entries with synced or plain lyrics count. With a known duration
    /// the entry whose duration differs least wins (an entry without a duration is the worst match), and a tie
    /// goes to synced lyrics, then to the earlier entry. Without a duration the first synced entry wins,
    /// else the first plain one. Returns null when no entry has lyrics.
    /// </summary>
    internal static JToken? SelectBestMatch(JArray results, TimeSpan? duration)
    {
        var candidates = results.OfType<JObject>().Where(HasAnyLyrics).ToList();

        if (duration == null)
            return candidates.FirstOrDefault(HasSyncedLyrics) ?? candidates.FirstOrDefault();

        var target = duration.Value.TotalSeconds;
        return candidates
            .OrderBy(c => DurationDifference(c, target))
            .ThenBy(c => HasSyncedLyrics(c) ? 0 : 1)
            .FirstOrDefault();
    }

    private static double DurationDifference(JToken track, double targetSeconds)
    {
        var trackSeconds = track[DurationField]?.Value<double?>();
        return trackSeconds == null ? double.PositiveInfinity : Math.Abs(trackSeconds.Value - targetSeconds);
    }

    private static bool HasSyncedLyrics(JToken track) => HasText(track, SyncedLyricsField);

    private static bool HasAnyLyrics(JToken track) => HasSyncedLyrics(track) || HasText(track, PlainLyricsField);

    private static bool HasText(JToken track, string field) =>
        !string.IsNullOrWhiteSpace(track[field]?.Value<string>());

    /// <summary>
    /// Converts an LRCLIB track to a result, preferring synced lyrics. Returns null when the track has no usable
    /// lyrics (instrumental, blank, or synced text without any timestamped line and no plain text).
    /// </summary>
    internal static LyricsResult? ToLyricsResult(JToken track)
    {
        var syncedLyrics = track[SyncedLyricsField]?.Value<string>();
        if (!string.IsNullOrWhiteSpace(syncedLyrics))
        {
            var lines = ParseLrc(syncedLyrics);
            if (lines.Count > 0)
            {
                return new LyricsResult
                {
                    Source = SourceName,
                    SyncedLyrics = lines,
                    PlainLyrics = string.Join("\n", lines.Select(l => l.Text))
                };
            }
        }

        var plainLyrics = track[PlainLyricsField]?.Value<string>();
        if (!string.IsNullOrWhiteSpace(plainLyrics))
            return new LyricsResult { Source = SourceName, PlainLyrics = plainLyrics };

        return null;
    }

    /// <summary>
    /// Parses LRC format lyrics into timestamped lines.
    /// Example: [01:23.45] Some lyrics text
    /// </summary>
    internal static List<LyricsLine> ParseLrc(string lrcText)
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
