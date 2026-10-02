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
    public void ParseResponse_NonZeroStatus_ReturnsNull()
    {
        Assert.Null(SongIdentificationService.ParseResponse(NoResultJson));
    }

    [Fact]
    public void ParseResponse_StatusZeroWithEmptyMusicArray_ReturnsNull()
    {
        Assert.Null(SongIdentificationService.ParseResponse(EmptyMusicJson));
    }
}
