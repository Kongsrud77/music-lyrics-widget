using System.Globalization;
using System.Net;
using MusicLyricsWidget.Services;
using MusicLyricsWidget.Tests.Fakes;

namespace MusicLyricsWidget.Tests;

public class SongIdentificationServiceTests
{
    private const string Host = "identify-eu-west-1.acrcloud.com";
    private const string AccessKey = "testkey";
    private const string AccessSecret = "testsecret";

    private const string MatchJson = """
        {
          "status": { "msg": "Success", "code": 0, "version": "1.0" },
          "metadata": { "music": [ { "title": "Test Title", "artists": [ { "name": "Artist One" } ] } ] }
        }
        """;

    private const string InvalidKeyJson = """
        { "status": { "msg": "Missing/Invalid Access Key", "code": 3001, "version": "1.0" } }
        """;

    private const string NoResultJson = """
        { "status": { "msg": "No result", "code": 1001, "version": "1.0" } }
        """;

    private static readonly byte[] Audio = { 1, 2, 3, 4, 5, 6, 7 };

    private static SongIdentificationService CreateService(StubHttpMessageHandler handler) =>
        new(Host, AccessKey, AccessSecret, new HttpClient(handler));

    [Fact]
    public async Task IdentifyAsync_Http200WithStatusZero_ReturnsSong()
    {
        var service = CreateService(new StubHttpMessageHandler(HttpStatusCode.OK, MatchJson));

        var song = await service.IdentifyAsync(Audio);

        Assert.NotNull(song);
        Assert.Equal("Test Title", song.Title);
        Assert.Equal("Artist One", song.Artist);
        Assert.Equal(MatchJson, service.LastRawResponse);
    }

    [Fact]
    public async Task IdentifyAsync_SendsSignedMultipartPostToIdentifyEndpoint()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, MatchJson);
        var service = CreateService(handler);

        await service.IdentifyAsync(Audio);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"https://{Host}/v1/identify", request.Uri?.ToString());

        Assert.Equal(AccessKey, request.Part("access_key").Text);
        Assert.Equal("audio", request.Part("data_type").Text);
        Assert.Equal("1", request.Part("signature_version").Text);
        Assert.Equal(Audio.Length.ToString(CultureInfo.InvariantCulture), request.Part("sample_bytes").Text);

        var sample = request.Part("sample");
        Assert.Equal("audio.wav", sample.FileName);
        Assert.Equal(Audio, sample.Bytes);

        // The signature must be the one for the timestamp that was actually sent.
        var timestamp = request.Part("timestamp").Text;
        Assert.True(long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out _));
        Assert.Equal(
            service.ComputeSignature(service.BuildStringToSign(timestamp)),
            request.Part("signature").Text);
    }

    [Fact]
    public async Task IdentifyAsync_Http200WithNoResultStatus_ReturnsNull()
    {
        var service = CreateService(new StubHttpMessageHandler(HttpStatusCode.OK, NoResultJson));

        Assert.Null(await service.IdentifyAsync(Audio));
    }

    [Fact]
    public async Task IdentifyAsync_Http200WithErrorStatus_ThrowsAcrCloudExceptionAndKeepsRawResponse()
    {
        var service = CreateService(new StubHttpMessageHandler(HttpStatusCode.OK, InvalidKeyJson));

        var ex = await Assert.ThrowsAsync<AcrCloudException>(() => service.IdentifyAsync(Audio));

        Assert.Equal(3001, ex.StatusCode);
        Assert.Equal(InvalidKeyJson, service.LastRawResponse);
    }

    [Fact]
    public async Task IdentifyAsync_Http500_ThrowsAcrCloudExceptionMentioningStatusAndKeepsRawResponse()
    {
        const string body = "upstream exploded";
        var service = CreateService(new StubHttpMessageHandler(HttpStatusCode.InternalServerError, body));

        var ex = await Assert.ThrowsAsync<AcrCloudException>(() => service.IdentifyAsync(Audio));

        Assert.Contains("HTTP 500", ex.Message);
        Assert.Null(ex.StatusCode);
        Assert.Equal(body, service.LastRawResponse);
    }

    [Fact]
    public async Task IdentifyAsync_CancelledToken_ThrowsOperationCanceledInsteadOfWrapping()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, MatchJson);
        var service = CreateService(handler);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.IdentifyAsync(Audio, cts.Token));
    }
}
