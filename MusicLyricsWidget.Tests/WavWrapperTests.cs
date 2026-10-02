using System.Text;
using MusicLyricsWidget.Services;

namespace MusicLyricsWidget.Tests;

public class WavWrapperTests
{
    private const int HeaderSize = 44;
    private const int RiffSizeOverhead = 36; // RIFF size = file length minus the 8-byte "RIFF"+size prefix = 36 header bytes + data

    private static byte[] CreatePayload(int length) =>
        Enumerable.Range(0, length).Select(i => (byte)(i % 251)).ToArray();

    [Fact]
    public void WrapInWav_Mono44100_WritesExpectedHeaderAndPayload()
    {
        using var service = new AudioCaptureService(44100, 1, 10);
        var pcm = CreatePayload(100);

        var wav = service.WrapInWav(pcm);

        Assert.Equal(HeaderSize + 100, wav.Length);
        AssertHeader(wav, channels: 1, sampleRate: 44100, byteRate: 88200, blockAlign: 2, dataSize: 100);
        Assert.Equal(pcm, wav.Skip(HeaderSize).ToArray());
    }

    [Fact]
    public void WrapInWav_Stereo16000_WritesExpectedByteRateAndBlockAlign()
    {
        using var service = new AudioCaptureService(16000, 2, 10);
        var pcm = CreatePayload(64);

        var wav = service.WrapInWav(pcm);

        Assert.Equal(HeaderSize + 64, wav.Length);
        AssertHeader(wav, channels: 2, sampleRate: 16000, byteRate: 64000, blockAlign: 4, dataSize: 64);
        Assert.Equal(pcm, wav.Skip(HeaderSize).ToArray());
    }

    private static void AssertHeader(byte[] wav, short channels, int sampleRate, int byteRate, short blockAlign, int dataSize)
    {
        using var reader = new BinaryReader(new MemoryStream(wav));

        Assert.Equal("RIFF", ReadTag(reader));
        Assert.Equal(RiffSizeOverhead + dataSize, reader.ReadInt32());
        Assert.Equal("WAVE", ReadTag(reader));
        Assert.Equal("fmt ", ReadTag(reader));
        Assert.Equal(16, reader.ReadInt32());
        Assert.Equal((short)1, reader.ReadInt16());
        Assert.Equal(channels, reader.ReadInt16());
        Assert.Equal(sampleRate, reader.ReadInt32());
        Assert.Equal(byteRate, reader.ReadInt32());
        Assert.Equal(blockAlign, reader.ReadInt16());
        Assert.Equal((short)16, reader.ReadInt16());
        Assert.Equal("data", ReadTag(reader));
        Assert.Equal(dataSize, reader.ReadInt32());
        Assert.Equal(HeaderSize, reader.BaseStream.Position);
    }

    private static string ReadTag(BinaryReader reader) =>
        Encoding.ASCII.GetString(reader.ReadBytes(4));
}
