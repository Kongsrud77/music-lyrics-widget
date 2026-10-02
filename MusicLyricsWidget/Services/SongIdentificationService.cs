using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using MusicLyricsWidget.Models;
using Newtonsoft.Json.Linq;

namespace MusicLyricsWidget.Services;

public class SongIdentificationService
{
    private readonly HttpClient _httpClient;
    private readonly string _host;
    private readonly string _accessKey;
    private readonly string _accessSecret;

    public SongIdentificationService(string host, string accessKey, string accessSecret)
    {
        _host = host.Trim();
        _accessKey = accessKey.Trim();
        _accessSecret = accessSecret.Trim();
        _httpClient = new HttpClient();
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

        var response = await _httpClient.PostAsync(
            $"https://{_host}/v1/identify",
            content,
            cancellationToken);

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        LastRawResponse = json;
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

    internal static SongInfo? ParseResponse(string json)
    {
        var root = JObject.Parse(json);
        var statusCode = root["status"]?["code"]?.Value<int>();

        if (statusCode != 0)
            return null; // No match or error

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
