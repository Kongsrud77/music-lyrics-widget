namespace MusicLyricsWidget.Services;

/// <summary>
/// ACRCloud could not be used: the HTTP request failed, or ACRCloud answered with an error status.
/// The message is meant to be shown to the user as-is.
/// </summary>
public class AcrCloudException : Exception
{
    /// <summary>The ACRCloud <c>status.code</c>, or null when the failure was not an ACRCloud status (e.g. an HTTP error).</summary>
    public int? StatusCode { get; }

    public AcrCloudException(string message, int? statusCode = null)
        : base(message)
    {
        StatusCode = statusCode;
    }
}
