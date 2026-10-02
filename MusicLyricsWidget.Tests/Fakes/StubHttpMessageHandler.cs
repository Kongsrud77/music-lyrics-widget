using System.Net;
using System.Text;

namespace MusicLyricsWidget.Tests.Fakes;

/// <summary>One form-data part of a recorded multipart request body.</summary>
internal sealed record RecordedPart(string Name, string? FileName, byte[] Bytes)
{
    public string Text => Encoding.UTF8.GetString(Bytes);
}

/// <summary>
/// A request as the stub saw it. The body is captured while the request is in flight, because the caller
/// disposes its content as soon as the send returns.
/// </summary>
internal sealed record RecordedRequest(
    HttpMethod Method,
    Uri? Uri,
    IReadOnlyDictionary<string, string> Headers,
    IReadOnlyList<RecordedPart> Parts,
    string? Body)
{
    /// <summary>The single multipart part with this name; fails the test if it is missing or repeated.</summary>
    public RecordedPart Part(string name) => Parts.Single(p => p.Name == name);
}

/// <summary>
/// An <see cref="HttpMessageHandler"/> that never touches the network: a delegate maps each recorded request to a
/// response, and every request received is kept in <see cref="Requests"/>.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<RecordedRequest, HttpResponseMessage> _respond;
    private readonly List<RecordedRequest> _requests = new();

    /// <summary>Full control over the response, e.g. to set headers such as Retry-After.</summary>
    public StubHttpMessageHandler(Func<RecordedRequest, HttpResponseMessage> respond)
    {
        _respond = respond;
    }

    public StubHttpMessageHandler(Func<RecordedRequest, (HttpStatusCode Status, string Body)> respond)
        : this(request => Response(respond(request)))
    {
    }

    /// <summary>Answers every request with the same status and body.</summary>
    public StubHttpMessageHandler(HttpStatusCode status, string body)
        : this(_ => (status, body))
    {
    }

    /// <summary>The requests received, in arrival order.</summary>
    public IReadOnlyList<RecordedRequest> Requests => _requests;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var recorded = await RecordAsync(request, cancellationToken);
        _requests.Add(recorded);

        return _respond(recorded);
    }

    /// <summary>A response with this status and a UTF-8 text body.</summary>
    public static HttpResponseMessage Response((HttpStatusCode Status, string Body) answer) =>
        new(answer.Status) { Content = new StringContent(answer.Body, Encoding.UTF8) };

    private static async Task<RecordedRequest> RecordAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value));
        var parts = new List<RecordedPart>();
        string? body = null;

        if (request.Content is MultipartFormDataContent multipart)
        {
            foreach (var part in multipart)
            {
                var disposition = part.Headers.ContentDisposition;
                parts.Add(new RecordedPart(
                    disposition?.Name?.Trim('"') ?? string.Empty,
                    disposition?.FileName?.Trim('"'),
                    await part.ReadAsByteArrayAsync(cancellationToken)));
            }
        }
        else if (request.Content != null)
        {
            body = await request.Content.ReadAsStringAsync(cancellationToken);
        }

        return new RecordedRequest(request.Method, request.RequestUri, headers, parts, body);
    }
}
