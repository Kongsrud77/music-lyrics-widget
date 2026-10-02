using MusicLyricsWidget.Services;

namespace MusicLyricsWidget.Tests;

public class AcrCloudSignatureTests
{
    private const string Timestamp = "1700000000";
    private const string ExpectedStringToSign = "POST\n/v1/identify\ntestkey\naudio\n1\n1700000000";

    // Independently computed with Python: base64(hmac.new(b"testsecret", string_to_sign, sha1).digest()).
    private const string ExpectedSignature = "d9SrXTXWNeH1N0Axic0hvCXJlms=";

    // The constructor trims host, key and secret; the padding proves it.
    private readonly SongIdentificationService _service = new("host", " testkey ", " testsecret ");

    [Fact]
    public void BuildStringToSign_UsesTrimmedAccessKeyAndFixedFields()
    {
        Assert.Equal(ExpectedStringToSign, _service.BuildStringToSign(Timestamp));
    }

    [Fact]
    public void ComputeSignature_UsesTrimmedSecretAndMatchesReferenceHmac()
    {
        Assert.Equal(ExpectedSignature, _service.ComputeSignature(ExpectedStringToSign));
    }
}
