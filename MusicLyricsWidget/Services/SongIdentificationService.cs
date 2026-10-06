using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using MusicLyricsWidget.Models;
using Newtonsoft.Json.Linq;

namespace MusicLyricsWidget.Services;

public class SongIdentificationService : ISongIdentifier
{
    private const int StatusSuccess = 0;
    private const int StatusNoResult = 1001;

    // "Unable to generate fingerprint": silence or too little audio in the sample, i.e. no music, not a failure.
    private const int StatusNoFingerprint = 2004;
    private const int RequestTimeoutSeconds = 15;
    private const string UnknownErrorMessage = "unknown error";

    // AudioCaptureService.WrapInWav writes the canonical 44-byte PCM header.
    private const int WavHeaderSize = 44;
    private const int WavByteRateOffset = 28;
    private const int WavDataSizeOffset = 40;

    // A song shorter than this is a data glitch (ACRCloud reports duration_ms 0 for some tracks), not a real length.
    // Mirrors LyricsService's own minimum for a plausible duration.
    private static readonly TimeSpan MinPlausibleSongDuration = TimeSpan.FromSeconds(1);

    // One client for the whole process: HttpClient is meant to be shared (a new one per instance leaks sockets).
    // It is never mutated after creation: no BaseAddress and no default headers, every request carries an absolute URL.
    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(RequestTimeoutSeconds)
    };

    private readonly HttpClient _httpClient;
    private readonly string _host;
    private readonly string _accessKey;
    private readonly string _accessSecret;

    public SongIdentificationService(string host, string accessKey, string accessSecret)
        : this(host, accessKey, accessSecret, SharedHttpClient)
    {
    }

    internal SongIdentificationService(string host, string accessKey, string accessSecret, HttpClient httpClient)
    {
        _host = host.Trim();
        _accessKey = accessKey.Trim();
        _accessSecret = accessSecret.Trim();
        _httpClient = httpClient;
    }

    /// <summary>
    /// Identifies a song from audio data using ACRCloud.
    /// </summary>
    public string LastRawResponse { get; private set; } = string.Empty;
    public string LastDebugInfo { get; private set; } = string.Empty;

    public async Task<SongInfo?> IdentifyAsync(byte[] audioData, CancellationToken cancellationToken = default)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var stringToSign = BuildStringToSign(timestamp);
        var signature = ComputeSignature(stringToSign);

        LastDebugInfo = $"string_to_sign: [{stringToSign.Replace("\n", "\\n")}]\nsignature: [{signature}]\ntimestamp: [{timestamp}]";

        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(audioData)
        {
            Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") }
        }, "sample", "audio.wav");
        content.Add(new StringContent(_accessKey), "access_key");
        content.Add(new StringContent("audio"), "data_type");
        content.Add(new StringContent(signature), "signature");
        content.Add(new StringContent("1"), "signature_version");
        content.Add(new StringContent(timestamp), "timestamp");
        content.Add(new StringContent(audioData.Length.ToString()), "sample_bytes");

        using var response = await _httpClient.PostAsync(
            $"https://{_host}/v1/identify",
            content,
            cancellationToken);

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        LastRawResponse = json;

        if (!response.IsSuccessStatusCode)
            throw new AcrCloudException($"ACRCloud request failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

        return ParseResponse(json, WavDuration(audioData));
    }

    internal string BuildStringToSign(string timestamp)
    {
        return $"POST\n/v1/identify\n{_accessKey}\naudio\n1\n{timestamp}";
    }

    internal string ComputeSignature(string stringToSign)
    {
        var keyBytes = Encoding.UTF8.GetBytes(_accessSecret);
        var dataBytes = Encoding.UTF8.GetBytes(stringToSign);

        using var hmac = new HMACSHA1(keyBytes);
        var hash = hmac.ComputeHash(dataBytes);
        return Convert.ToBase64String(hash);
    }

    /// <summary>
    /// Maps an ACRCloud response to a song. Returns null when ACRCloud found no match or could not
    /// fingerprint the sample (silence); throws <see cref="AcrCloudException"/> for any other status, so a bad key or quota is not mistaken for silence.
    /// </summary>
    internal static SongInfo? ParseResponse(string json, TimeSpan? sampleDuration = null)
    {
        var root = JObject.Parse(json);
        var status = root["status"];
        var statusCode = status?["code"]?.Value<int?>();

        if (statusCode == null)
            throw new AcrCloudException("ACRCloud response contained no status code.");

        if (statusCode == StatusNoResult || statusCode == StatusNoFingerprint)
            return null;

        if (statusCode != StatusSuccess)
        {
            var message = status?["msg"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(message))
                message = UnknownErrorMessage;
            throw new AcrCloudException($"ACRCloud error {statusCode}: {message}", statusCode);
        }

        var music = root["metadata"]?["music"]?.FirstOrDefault();
        if (music == null)
            return null;

        var song = new SongInfo
        {
            Title = music["title"]?.Value<string>() ?? string.Empty,
            Artist = music["artists"]?.FirstOrDefault()?["name"]?.Value<string>() ?? string.Empty,
            Album = music["album"]?["name"]?.Value<string>() ?? string.Empty,
            AcrId = music["acrid"]?.Value<string>(),
        };

        // An implausible duration stays unknown: the line clock would otherwise stop ticking at once, "after the song".
        var durationMs = music["duration_ms"]?.Value<int>();
        if (durationMs.HasValue && TimeSpan.FromMilliseconds(durationMs.Value) is var duration && duration >= MinPlausibleSongDuration)
            song.Duration = duration;

        song.PositionAtSampleEnd = PositionAtSampleEnd(music, sampleDuration, song.Duration);

        return song;
    }

    /// <summary>
    /// Song position at the end of the sample: where the match ended in the song plus the part of the sample after
    /// the match. Falls back to play_offset_ms when that cannot be computed; null without any offset. Clamped to the
    /// song, unless its duration is unknown or implausibly short (under one second), which clamps nothing.
    /// </summary>
    internal static TimeSpan? PositionAtSampleEnd(JToken music, TimeSpan? sampleDuration, TimeSpan? songDuration)
    {
        var dbEnd = music["db_end_time_offset_ms"]?.Value<long?>();
        var sampleEnd = music["sample_end_time_offset_ms"]?.Value<long?>();
        var playOffset = music["play_offset_ms"]?.Value<long?>();

        TimeSpan? position = dbEnd != null && sampleEnd != null && sampleDuration != null
            ? TimeSpan.FromMilliseconds(dbEnd.Value) + (sampleDuration.Value - TimeSpan.FromMilliseconds(sampleEnd.Value))
            : playOffset != null ? TimeSpan.FromMilliseconds(playOffset.Value) : null;

        if (position == null)
            return null;
        if (position < TimeSpan.Zero)
            return TimeSpan.Zero;
        return songDuration is { } duration && duration >= MinPlausibleSongDuration && position > duration ? duration : position;
    }

    /// <summary>Length of a canonical PCM WAV (as WrapInWav writes it); null when the data is not one.</summary>
    internal static TimeSpan? WavDuration(byte[] wav)
    {
        if (wav.Length < WavHeaderSize)
            return null;

        var byteRate = BitConverter.ToInt32(wav, WavByteRateOffset);
        var dataSize = BitConverter.ToInt32(wav, WavDataSizeOffset);
        return byteRate > 0 ? TimeSpan.FromSeconds((double)dataSize / byteRate) : null;
    }
}
