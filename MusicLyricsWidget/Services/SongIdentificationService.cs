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
    private const int RequestTimeoutSeconds = 15;
    private const string UnknownErrorMessage = "unknown error";

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

        return ParseResponse(json);
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
    /// Maps an ACRCloud response to a song. Returns null when ACRCloud found no match; throws
    /// <see cref="AcrCloudException"/> for any other status, so a bad key or quota is not mistaken for silence.
    /// </summary>
    internal static SongInfo? ParseResponse(string json)
    {
        var root = JObject.Parse(json);
        var status = root["status"];
        var statusCode = status?["code"]?.Value<int?>();

        if (statusCode == null)
            throw new AcrCloudException("ACRCloud response contained no status code.");

        if (statusCode == StatusNoResult)
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

        var durationMs = music["duration_ms"]?.Value<int>();
        if (durationMs.HasValue)
            song.Duration = TimeSpan.FromMilliseconds(durationMs.Value);

        return song;
    }
}
