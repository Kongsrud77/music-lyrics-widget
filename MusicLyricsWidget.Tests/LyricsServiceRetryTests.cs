using System.Net;
using System.Net.Http.Headers;
using MusicLyricsWidget.Models;
using MusicLyricsWidget.Services;
using MusicLyricsWidget.Tests.Fakes;

namespace MusicLyricsWidget.Tests;

/// <summary>
/// LRCLIB answers 503 ServerOverloaded with "Retry-After: 1" when an uncached lookup cannot get a database slot in
/// time (seen in production: the widget showed the 503 right after recognising a song). These tests pin that the
/// service honours Retry-After instead of failing on the first busy answer.
/// </summary>
public class LyricsServiceRetryTests
{
    private const string GetPath = "/api/get";
    private const string SearchPath = "/api/search";
    private const string ServerOverloadedJson = """{ "message": "The server is busy, please retry in a moment", "name": "ServerOverloaded", "statusCode": 503 }""";
    private const string TrackNotFoundJson = """{ "name": "TrackNotFound" }""";

    private static readonly string SyncedTrackJson =
        LrclibJson.Track(355, "[00:01.00] Hello\n[00:02.50] World", "Hello\nWorld").ToString();

    private readonly List<TimeSpan> _delays = new();

    private static SongInfo Song() => new() { Title = "Test Title", Artist = "Artist One", Duration = TimeSpan.FromSeconds(355) };

    private LyricsService CreateService(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler), (delay, _) =>
        {
            _delays.Add(delay);
            return Task.CompletedTask;
        });

    private static HttpResponseMessage Busy(HttpStatusCode status = HttpStatusCode.ServiceUnavailable, TimeSpan? retryAfter = null)
    {
        var response = StubHttpMessageHandler.Response((status, ServerOverloadedJson));
        if (retryAfter != null)
            response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter.Value);
        return response;
    }

    private static HttpResponseMessage Ok(string body) => StubHttpMessageHandler.Response((HttpStatusCode.OK, body));

    /// <summary>Answers each path from its own queue of responses, in order; an exhausted queue fails the test.</summary>
    private static StubHttpMessageHandler Lrclib(Queue<HttpResponseMessage> get, Queue<HttpResponseMessage>? search = null) =>
        new(request => request.Uri!.AbsolutePath switch
        {
            GetPath => get.Dequeue(),
            SearchPath => (search ?? throw new InvalidOperationException("Unexpected search")).Dequeue(),
            _ => throw new InvalidOperationException($"Unexpected request: {request.Uri}"),
        });

    private static int Calls(StubHttpMessageHandler handler, string path) =>
        handler.Requests.Count(r => r.Uri!.AbsolutePath == path);

    [Fact]
    public async Task ExactMatchBusyOnce_WaitsRetryAfterAndReturnsLyrics()
    {
        var handler = Lrclib(new(new[] { Busy(retryAfter: TimeSpan.FromSeconds(1)), Ok(SyncedTrackJson) }));

        var result = await CreateService(handler).GetLyricsAsync(Song());

        Assert.NotNull(result);
        Assert.True(result.HasSyncedLyrics);
        Assert.Equal(2, Calls(handler, GetPath));
        Assert.Equal(new[] { TimeSpan.FromSeconds(1) }, _delays);
    }

    [Fact]
    public async Task ExactMatchStaysBusy_ThrowsAfterThreeAttemptsWithoutSearching()
    {
        var handler = Lrclib(new(new[] { Busy(), Busy(), Busy() }));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => CreateService(handler).GetLyricsAsync(Song()));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
        Assert.Equal(3, Calls(handler, GetPath));
        Assert.Equal(0, Calls(handler, SearchPath));
        Assert.Equal(2, _delays.Count);
    }

    [Fact]
    public async Task SearchBusyOnce_IsRetriedToo()
    {
        var search = LrclibJson.SearchBody(LrclibJson.Track(355, "[00:01.00] Hi", null));
        var handler = Lrclib(
            new(new[] { StubHttpMessageHandler.Response((HttpStatusCode.NotFound, TrackNotFoundJson)) }),
            new(new[] { Busy(retryAfter: TimeSpan.FromSeconds(1)), Ok(search) }));

        var result = await CreateService(handler).GetLyricsAsync(Song());

        Assert.NotNull(result);
        Assert.Equal(2, Calls(handler, SearchPath));
    }

    [Fact]
    public async Task TooManyRequests_IsRetriedLikeBusy()
    {
        var handler = Lrclib(new(new[] { Busy(HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(2)), Ok(SyncedTrackJson) }));

        Assert.NotNull(await CreateService(handler).GetLyricsAsync(Song()));
        Assert.Equal(new[] { TimeSpan.FromSeconds(2) }, _delays);
    }

    [Fact]
    public async Task BusyWithoutRetryAfter_WaitsOneSecond()
    {
        var handler = Lrclib(new(new[] { Busy(), Ok(SyncedTrackJson) }));

        Assert.NotNull(await CreateService(handler).GetLyricsAsync(Song()));
        Assert.Equal(new[] { TimeSpan.FromSeconds(1) }, _delays);
    }

    [Fact]
    public async Task RetryAfterLongerThanWeWait_FailsAtOnceInsteadOfIgnoringTheServer()
    {
        var handler = Lrclib(new(new[] { Busy(retryAfter: TimeSpan.FromSeconds(30)) }));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => CreateService(handler).GetLyricsAsync(Song()));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
        Assert.Equal(1, Calls(handler, GetPath));
        Assert.Empty(_delays);
    }

    [Fact]
    public async Task OtherServerError_IsNotRetried()
    {
        var handler = Lrclib(new(new[] { StubHttpMessageHandler.Response((HttpStatusCode.InternalServerError, "oops")) }));

        await Assert.ThrowsAsync<HttpRequestException>(() => CreateService(handler).GetLyricsAsync(Song()));

        Assert.Equal(1, Calls(handler, GetPath));
        Assert.Empty(_delays);
    }

    [Fact]
    public async Task CancelledWhileWaiting_PropagatesCancellationAndStopsRetrying()
    {
        using var cts = new CancellationTokenSource();
        var handler = Lrclib(new(new[] { Busy(), Ok(SyncedTrackJson) }));
        var service = new LyricsService(new HttpClient(handler), (_, token) =>
        {
            cts.Cancel();
            return Task.FromCanceled(token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetLyricsAsync(Song(), cts.Token));

        Assert.Equal(1, Calls(handler, GetPath));
    }
}
