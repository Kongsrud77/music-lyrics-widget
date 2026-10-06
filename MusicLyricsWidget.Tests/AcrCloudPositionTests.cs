using MusicLyricsWidget.Services;
using Newtonsoft.Json.Linq;

namespace MusicLyricsWidget.Tests;

public class AcrCloudPositionTests
{
    private static readonly TimeSpan TenSeconds = TimeSpan.FromSeconds(10);

    private static JToken Music(object fields) => JObject.FromObject(fields);

    [Fact]
    public void Offsets_GiveMatchedDatabaseEndPlusTheRestOfTheSample()
    {
        var music = Music(new { db_end_time_offset_ms = 9280, sample_end_time_offset_ms = 9280, play_offset_ms = 9040 });

        Assert.Equal(TimeSpan.FromMilliseconds(10_000), SongIdentificationService.PositionAtSampleEnd(music, TenSeconds, null));
    }

    [Fact]
    public void WithoutDbAndSampleOffsets_PlayOffsetIsUsed()
    {
        var music = Music(new { play_offset_ms = 9040 });

        Assert.Equal(TimeSpan.FromMilliseconds(9040), SongIdentificationService.PositionAtSampleEnd(music, TenSeconds, null));
    }

    [Fact]
    public void WithoutSampleDuration_PlayOffsetIsUsed()
    {
        var music = Music(new { db_end_time_offset_ms = 9280, sample_end_time_offset_ms = 9280, play_offset_ms = 9040 });

        Assert.Equal(TimeSpan.FromMilliseconds(9040), SongIdentificationService.PositionAtSampleEnd(music, null, null));
    }

    [Fact]
    public void WithoutAnyOffset_PositionIsUnknown()
    {
        Assert.Null(SongIdentificationService.PositionAtSampleEnd(Music(new { title = "x" }), TenSeconds, null));
    }

    [Fact]
    public void Position_IsClampedToTheSongDuration()
    {
        var music = Music(new { play_offset_ms = 400_000 });

        Assert.Equal(TimeSpan.FromSeconds(300), SongIdentificationService.PositionAtSampleEnd(music, TenSeconds, TimeSpan.FromSeconds(300)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(999)]
    public void ASongDurationUnderOneSecond_IsTreatedAsUnknown_NotAsAClampToZero(int songDurationMs)
    {
        var music = Music(new { play_offset_ms = 9040 });

        Assert.Equal(
            TimeSpan.FromMilliseconds(9040),
            SongIdentificationService.PositionAtSampleEnd(music, TenSeconds, TimeSpan.FromMilliseconds(songDurationMs)));
    }

    [Fact]
    public void NegativePosition_IsClampedToZero()
    {
        var music = Music(new { db_end_time_offset_ms = 100, sample_end_time_offset_ms = 5000 });

        Assert.Equal(TimeSpan.Zero, SongIdentificationService.PositionAtSampleEnd(music, TimeSpan.FromSeconds(1), null));
    }

    [Fact]
    public void WavDuration_ReadsTheCanonicalHeader()
    {
        var wav = new AudioCaptureService(44100, 1, 1).WrapInWav(new byte[88_200]);

        Assert.Equal(TimeSpan.FromSeconds(1), SongIdentificationService.WavDuration(wav));
    }

    [Fact]
    public void WavDuration_OfTooShortData_IsUnknown()
    {
        Assert.Null(SongIdentificationService.WavDuration(new byte[10]));
    }

    [Fact]
    public void ParseResponse_FillsPositionAtSampleEnd()
    {
        const string json = """
            { "status": { "code": 0, "msg": "Success" },
              "metadata": { "music": [ { "title": "Come Together", "artists": [ { "name": "The Beatles" } ],
                "duration_ms": 259000, "play_offset_ms": 9040, "db_end_time_offset_ms": 9280, "sample_end_time_offset_ms": 9280 } ] } }
            """;

        var song = SongIdentificationService.ParseResponse(json, TenSeconds);

        Assert.NotNull(song);
        Assert.Equal(TimeSpan.FromMilliseconds(10_000), song.PositionAtSampleEnd);
    }

    [Fact]
    public void ParseResponse_WithAZeroDuration_KeepsThePositionAndLeavesTheDurationUnknown()
    {
        const string json = """
            { "status": { "code": 0, "msg": "Success" },
              "metadata": { "music": [ { "title": "Come Together", "artists": [ { "name": "The Beatles" } ],
                "duration_ms": 0, "play_offset_ms": 9040 } ] } }
            """;

        var song = SongIdentificationService.ParseResponse(json, TenSeconds);

        Assert.NotNull(song);
        Assert.Equal(TimeSpan.FromMilliseconds(9040), song.PositionAtSampleEnd);
        Assert.Null(song.Duration);                  // the line clock stops short of a "song" that ends before it starts
    }
}
