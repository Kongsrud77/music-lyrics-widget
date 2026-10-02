using MusicLyricsWidget.Services;

namespace MusicLyricsWidget.Tests;

public class AcrCloudResponseTests
{
    private const string MatchJson = """
        {
          "status": { "msg": "Success", "code": 0, "version": "1.0" },
          "metadata": {
            "music": [
              {
                "title": "Test Title",
                "artists": [ { "name": "Artist One" }, { "name": "Artist Two" } ],
                "album": { "name": "Test Album" },
                "acrid": "abc123",
                "duration_ms": 215000
              }
            ]
          }
        }
        """;

    private const string NoResultJson = """
        { "status": { "msg": "No result", "code": 1001, "version": "1.0" } }
        """;

    private const string InvalidKeyJson = """
        { "status": { "msg": "Missing/Invalid Access Key", "code": 3001, "version": "1.0" } }
        """;

    private const string EmptyMusicJson = """
        { "status": { "msg": "Success", "code": 0 }, "metadata": { "music": [] } }
        """;

    [Fact]
    public void ParseResponse_StatusZeroWithMusic_MapsAllFields()
    {
        var song = SongIdentificationService.ParseResponse(MatchJson);

        Assert.NotNull(song);
        Assert.Equal("Test Title", song.Title);
        Assert.Equal("Artist One", song.Artist);
        Assert.Equal("Test Album", song.Album);
        Assert.Equal("abc123", song.AcrId);
        Assert.Equal(TimeSpan.FromSeconds(215), song.Duration);
    }

    [Fact]
    public void ParseResponse_NoResultStatus_ReturnsNull()
    {
        Assert.Null(SongIdentificationService.ParseResponse(NoResultJson));
    }

    [Fact]
    public void ParseResponse_InvalidAccessKeyStatus_ThrowsWithCodeAndAcrCloudMessage()
    {
        var ex = Assert.Throws<AcrCloudException>(() => SongIdentificationService.ParseResponse(InvalidKeyJson));

        Assert.Equal(3001, ex.StatusCode);
        Assert.Contains("3001", ex.Message);
        Assert.Contains("Missing/Invalid Access Key", ex.Message);
    }

    [Fact]
    public void ParseResponse_ErrorStatusWithoutMessage_FallsBackToUnknownError()
    {
        var ex = Assert.Throws<AcrCloudException>(() =>
            SongIdentificationService.ParseResponse("""{ "status": { "code": 3003 } }"""));

        Assert.Equal(3003, ex.StatusCode);
        Assert.Equal("ACRCloud error 3003: unknown error", ex.Message);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "status": { "msg": "odd" } }""")]
    [InlineData("""{ "status": { "code": null } }""")]
    public void ParseResponse_MissingStatusCode_ThrowsWithoutStatusCode(string json)
    {
        var ex = Assert.Throws<AcrCloudException>(() => SongIdentificationService.ParseResponse(json));

        Assert.Null(ex.StatusCode);
        Assert.Equal("ACRCloud response contained no status code.", ex.Message);
    }

    [Fact]
    public void ParseResponse_StatusZeroWithEmptyMusicArray_ReturnsNull()
    {
        Assert.Null(SongIdentificationService.ParseResponse(EmptyMusicJson));
    }
}
