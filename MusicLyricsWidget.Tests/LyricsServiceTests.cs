using System.Net;
using System.Web;
using MusicLyricsWidget.Models;
using MusicLyricsWidget.Services;
using MusicLyricsWidget.Tests.Fakes;
using Newtonsoft.Json;

namespace MusicLyricsWidget.Tests;

public class LyricsServiceTests
{
    private const string GetPath = "/api/get";
    private const string SearchPath = "/api/search";

    private const string Synced = "[00:01.00] Hello\n[00:02.50] World";
    private const string Plain = "Hello\nWorld";

    private const string TrackNotFoundJson = """{ "code": 404, "name": "TrackNotFound", "message": "Failed to find specified track" }""";
    private const string ServerOverloadedJson = """{ "code": 503, "name": "ServerOverloaded", "message": "Server is overloaded" }""";

    private static readonly string SyncedTrackJson = LrclibJson.Track(355, Synced, Plain).ToString();

    private static SongInfo Song(double? durationSeconds = 355, string album = "") => new()
    {
        Title = "Test Title",
        Artist = "Artist One",
        Album = album,
        Duration = durationSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null,
    };

    /// <summary>A stub that answers <c>/api/get</c> and <c>/api/search</c> separately and rejects any other path.</summary>
    private static StubHttpMessageHandler Lrclib(
        HttpStatusCode getStatus,
        string getBody,
        HttpStatusCode searchStatus = HttpStatusCode.OK,
        string searchBody = "[]") =>
        new(request => request.Uri!.AbsolutePath switch
        {
            GetPath => (getStatus, getBody),
            SearchPath => (searchStatus, searchBody),
            _ => throw new InvalidOperationException($"Unexpected request: {request.Uri}"),
        });

    private static LyricsService CreateService(StubHttpMessageHandler handler) => new(new HttpClient(handler));

    private static IEnumerable<RecordedRequest> Calls(StubHttpMessageHandler handler, string path) =>
        handler.Requests.Where(r => r.Uri!.AbsolutePath == path);

    [Fact]
    public async Task GetLyricsAsync_ExactMatchWithSyncedLyrics_ReturnsLinesAndNeverSearches()
    {
        var handler = Lrclib(HttpStatusCode.OK, SyncedTrackJson);

        var result = await CreateService(handler).GetLyricsAsync(Song());

        Assert.NotNull(result);
        Assert.Equal("LRCLIB", result.Source);
        Assert.True(result.HasSyncedLyrics);
        Assert.Equal(new[] { "Hello", "World" }, result.SyncedLyrics.Select(l => l.Text));
        Assert.Single(handler.Requests);
        Assert.Empty(Calls(handler, SearchPath));
    }

    [Fact]
    public async Task GetLyricsAsync_ExactMatchWithOnlyPlainLyrics_ReturnsPlainText()
    {
        var handler = Lrclib(HttpStatusCode.OK, LrclibJson.Track(355, null, Plain).ToString());

        var result = await CreateService(handler).GetLyricsAsync(Song());

        Assert.NotNull(result);
        Assert.False(result.HasSyncedLyrics);
        Assert.Equal(Plain, result.PlainLyrics);
        Assert.Empty(Calls(handler, SearchPath));
    }

    [Fact]
    public async Task GetLyricsAsync_SendsGetWithDurationAndWithoutAlbumWhenAlbumIsEmpty()
    {
        var handler = Lrclib(HttpStatusCode.OK, SyncedTrackJson);

        await CreateService(handler).GetLyricsAsync(Song(durationSeconds: 355, album: ""));

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("lrclib.net", request.Uri!.Host);
        Assert.Contains("duration=355", request.Uri.Query);
        Assert.DoesNotContain("album_name", request.Uri.Query);

        var query = HttpUtility.ParseQueryString(request.Uri.Query);
        Assert.Equal("Test Title", query["track_name"]);
        Assert.Equal("Artist One", query["artist_name"]);
    }

    [Fact]
    public async Task GetLyricsAsync_NonEmptyAlbum_IsIncludedInTheGetQuery()
    {
        var handler = Lrclib(HttpStatusCode.OK, SyncedTrackJson);

        await CreateService(handler).GetLyricsAsync(Song(album: "Greatest Hits"));

        var query = HttpUtility.ParseQueryString(Assert.Single(handler.Requests).Uri!.Query);
        Assert.Equal("Greatest Hits", query["album_name"]);
    }

    [Fact]
    public async Task GetLyricsAsync_QueryValuesAreEscaped_SoSpecialCharactersSurviveTheRoundTrip()
    {
        var handler = Lrclib(HttpStatusCode.OK, SyncedTrackJson);
        var song = new SongInfo { Title = "Rock & Roll=Fun? #1", Artist = "Sigur Rós + Friends", Album = "100% Æbler" };

        await CreateService(handler).GetLyricsAsync(song);

        var query = HttpUtility.ParseQueryString(Assert.Single(handler.Requests).Uri!.Query);
        Assert.Equal(song.Title, query["track_name"]);
        Assert.Equal(song.Artist, query["artist_name"]);
        Assert.Equal(song.Album, query["album_name"]);
        Assert.DoesNotContain("duration", query.AllKeys);
    }

    [Theory]
    [InlineData(355.0, "355")]
    [InlineData(354.6, "355")]
    [InlineData(262.4, "262")]
    [InlineData(1.0, "1")]
    [InlineData(3600.0, "3600")]
    public async Task GetLyricsAsync_Duration_IsSentAsWholeSeconds(double seconds, string expected)
    {
        var handler = Lrclib(HttpStatusCode.OK, SyncedTrackJson);

        await CreateService(handler).GetLyricsAsync(Song(seconds));

        var query = HttpUtility.ParseQueryString(Assert.Single(handler.Requests).Uri!.Query);
        Assert.Equal(expected, query["duration"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(0.4)]
    [InlineData(3600.6)]
    [InlineData(7200.0)]
    public async Task GetLyricsAsync_UnknownOrOutOfRangeDuration_IsOmitted(double? seconds)
    {
        var handler = Lrclib(HttpStatusCode.OK, SyncedTrackJson);

        await CreateService(handler).GetLyricsAsync(Song(seconds));

        var query = HttpUtility.ParseQueryString(Assert.Single(handler.Requests).Uri!.Query);
        Assert.DoesNotContain("duration", query.AllKeys);
    }

    [Fact]
    public async Task GetLyricsAsync_ExactMatchNotFound_SearchesByTitleAndArtist()
    {
        var searchBody = LrclibJson.SearchBody(
            LrclibJson.Track(351, Synced, "far one"),
            LrclibJson.Track(263, Synced, "right one"),
            LrclibJson.Track(317, Synced, "other one"));
        var handler = Lrclib(HttpStatusCode.NotFound, TrackNotFoundJson, searchBody: searchBody);

        var result = await CreateService(handler).GetLyricsAsync(Song(durationSeconds: 263));

        Assert.NotNull(result);
        Assert.Equal("LRCLIB", result.Source);
        Assert.Equal(2, result.SyncedLyrics.Count);

        var search = Assert.Single(Calls(handler, SearchPath));
        Assert.Equal(HttpMethod.Get, search.Method);
        var query = HttpUtility.ParseQueryString(search.Uri!.Query);
        Assert.Equal("Test Title", query["track_name"]);
        Assert.Equal("Artist One", query["artist_name"]);
    }

    [Fact]
    public async Task GetLyricsAsync_SearchPicksClosestDuration_ReturnsThatEntrysLyrics()
    {
        var searchBody = LrclibJson.SearchBody(
            LrclibJson.Track(351, null, "far one"),
            LrclibJson.Track(263, null, "right one"),
            LrclibJson.Track(317, null, "other one"));
        var handler = Lrclib(HttpStatusCode.NotFound, TrackNotFoundJson, searchBody: searchBody);

        var result = await CreateService(handler).GetLyricsAsync(Song(durationSeconds: 263));

        Assert.NotNull(result);
        Assert.Equal("right one", result.PlainLyrics);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.4)]
    [InlineData(3600.6)]
    [InlineData(7200.0)]
    public async Task GetLyricsAsync_ImplausibleDuration_IsNotUsedToRankSearchResults(double seconds)
    {
        // The first synced entry is neither the shortest nor the longest, so a bogus target at either extreme picks another one.
        var searchBody = LrclibJson.SearchBody(
            LrclibJson.Track(200, "[00:01.00] first synced", null),
            LrclibJson.Track(120, "[00:01.00] shortest", null),
            LrclibJson.Track(300, "[00:01.00] longest", null));
        var handler = Lrclib(HttpStatusCode.NotFound, TrackNotFoundJson, searchBody: searchBody);

        var result = await CreateService(handler).GetLyricsAsync(Song(seconds));

        Assert.NotNull(result);
        Assert.Equal("first synced", result.PlainLyrics);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetLyricsAsync_BlankTitle_ReturnsNullWithoutAnyRequest(string title)
    {
        var handler = Lrclib(HttpStatusCode.OK, SyncedTrackJson);
        var song = new SongInfo { Title = title, Artist = "Artist One" };

        var result = await CreateService(handler).GetLyricsAsync(song);

        Assert.Null(result);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetLyricsAsync_BlankArtist_ReturnsNullWithoutAnyRequest(string artist)
    {
        var handler = Lrclib(HttpStatusCode.OK, SyncedTrackJson);
        var song = new SongInfo { Title = "Test Title", Artist = artist };

        var result = await CreateService(handler).GetLyricsAsync(song);

        Assert.Null(result);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task GetLyricsAsync_ExactMatchWithoutLyrics_FallsThroughToSearch()
    {
        const string instrumentalJson = """{ "instrumental": true, "plainLyrics": null, "syncedLyrics": null, "duration": 355 }""";
        var searchBody = LrclibJson.SearchBody(LrclibJson.Track(355, null, "from search"));
        var handler = Lrclib(HttpStatusCode.OK, instrumentalJson, searchBody: searchBody);

        var result = await CreateService(handler).GetLyricsAsync(Song());

        Assert.NotNull(result);
        Assert.Equal("from search", result.PlainLyrics);
        Assert.Single(Calls(handler, GetPath));
        Assert.Single(Calls(handler, SearchPath));
    }

    [Fact]
    public async Task GetLyricsAsync_ExactMatchWithBlankLyrics_FallsThroughToSearchAndReturnsNullWhenNothingThere()
    {
        var handler = Lrclib(HttpStatusCode.OK, LrclibJson.Track(355, " ", "").ToString());

        var result = await CreateService(handler).GetLyricsAsync(Song());

        Assert.Null(result);
        Assert.Single(Calls(handler, SearchPath));
    }

    [Fact]
    public async Task GetLyricsAsync_ExactMatchServerOverloaded_ThrowsAndDoesNotSearch()
    {
        var handler = Lrclib(HttpStatusCode.ServiceUnavailable, ServerOverloadedJson);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => CreateService(handler).GetLyricsAsync(Song()));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
        Assert.Empty(Calls(handler, SearchPath));
    }

    [Fact]
    public async Task GetLyricsAsync_ExactMatchOtherClientError_ThrowsAndDoesNotSearch()
    {
        var handler = Lrclib(HttpStatusCode.BadRequest, """{ "name": "ValidationError" }""");

        await Assert.ThrowsAsync<HttpRequestException>(() => CreateService(handler).GetLyricsAsync(Song()));

        Assert.Empty(Calls(handler, SearchPath));
    }

    [Fact]
    public async Task GetLyricsAsync_ExactMatchBodyIsNotJson_ThrowsInsteadOfReportingNoLyrics()
    {
        var handler = Lrclib(HttpStatusCode.OK, "<html>maintenance</html>");

        await Assert.ThrowsAnyAsync<JsonException>(() => CreateService(handler).GetLyricsAsync(Song()));

        Assert.Empty(Calls(handler, SearchPath));
    }

    [Fact]
    public async Task GetLyricsAsync_SearchReturnsEmptyArray_ReturnsNull()
    {
        var handler = Lrclib(HttpStatusCode.NotFound, TrackNotFoundJson, searchBody: "[]");

        var result = await CreateService(handler).GetLyricsAsync(Song());

        Assert.Null(result);
        Assert.Single(Calls(handler, SearchPath));
    }

    [Fact]
    public async Task GetLyricsAsync_SearchEntriesWithoutLyrics_ReturnsNull()
    {
        var searchBody = LrclibJson.SearchBody(LrclibJson.Track(355, null, null), LrclibJson.Track(356, "", " "));
        var handler = Lrclib(HttpStatusCode.NotFound, TrackNotFoundJson, searchBody: searchBody);

        Assert.Null(await CreateService(handler).GetLyricsAsync(Song()));
    }

    [Fact]
    public async Task GetLyricsAsync_SearchServerError_Throws()
    {
        var handler = Lrclib(HttpStatusCode.NotFound, TrackNotFoundJson, HttpStatusCode.InternalServerError, "oops");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => CreateService(handler).GetLyricsAsync(Song()));

        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
    }

    [Fact]
    public async Task GetLyricsAsync_SearchBodyIsNotAnArray_Throws()
    {
        var handler = Lrclib(HttpStatusCode.NotFound, TrackNotFoundJson, searchBody: """{ "name": "Oops" }""");

        await Assert.ThrowsAnyAsync<JsonException>(() => CreateService(handler).GetLyricsAsync(Song()));
    }

    [Fact]
    public async Task GetLyricsAsync_AlreadyCancelled_PropagatesCancellationWithoutSearching()
    {
        var handler = Lrclib(HttpStatusCode.NotFound, TrackNotFoundJson);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateService(handler).GetLyricsAsync(Song(), cts.Token));

        Assert.Empty(Calls(handler, SearchPath));
    }
}
