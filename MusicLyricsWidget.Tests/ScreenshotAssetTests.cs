using System.Buffers.Binary;

namespace MusicLyricsWidget.Tests;

public class ScreenshotAssetTests
{
    private const string ProjectFolderName = "MusicLyricsWidget";
    private const string AssetsFolderName = "Assets";
    private const string ScreenshotFileName = "WidgetScreenshot.png";
    private const int ExpectedWidth = 300;
    private const int ExpectedHeight = 304;
    private const byte ColourTypeRgba = 6;

    // PNG layout: 8-byte signature, then the IHDR chunk: 4-byte length, 4-byte type, 13 bytes of data
    // (width, height, bit depth, colour type, ...). Multi-byte integers are big-endian.
    private const int ChunkTypeOffset = 12;
    private const int WidthOffset = 16;
    private const int HeightOffset = 20;
    private const int ColourTypeOffset = 25;
    private const int HeaderLength = 33;
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] IhdrChunkType = "IHDR"u8.ToArray();

    private readonly byte[] _header;

    public ScreenshotAssetTests()
    {
        var path = Path.Combine(TestPaths.RepoRoot, ProjectFolderName, AssetsFolderName, ScreenshotFileName);
        using var stream = File.OpenRead(path);
        _header = new byte[HeaderLength];
        stream.ReadExactly(_header);
    }

    [Fact]
    public void Screenshot_IsAPngStartingWithIhdr()
    {
        Assert.Equal(PngSignature, _header[..PngSignature.Length]);
        Assert.Equal(IhdrChunkType, _header[ChunkTypeOffset..WidthOffset]);
    }

    [Fact]
    public void Screenshot_Is300By304Pixels()
    {
        Assert.Equal(ExpectedWidth, BinaryPrimitives.ReadInt32BigEndian(_header.AsSpan(WidthOffset)));
        Assert.Equal(ExpectedHeight, BinaryPrimitives.ReadInt32BigEndian(_header.AsSpan(HeightOffset)));
    }

    [Fact]
    public void Screenshot_IsRgba()
    {
        Assert.Equal(ColourTypeRgba, _header[ColourTypeOffset]);
    }
}
