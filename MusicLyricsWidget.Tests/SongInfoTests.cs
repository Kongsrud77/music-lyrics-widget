using MusicLyricsWidget.Models;

namespace MusicLyricsWidget.Tests;

public class SongInfoTests
{
    private static SongInfo Song(string title, string artist, string album = "") =>
        new() { Title = title, Artist = artist, Album = album };

    [Fact]
    public void Equals_IgnoresCaseOfTitleAndArtist()
    {
        Assert.Equal(Song("Hello", "Adele"), Song("HELLO", "adele"));
    }

    [Fact]
    public void Equals_IgnoresAlbum()
    {
        Assert.Equal(Song("Hello", "Adele", "25"), Song("Hello", "Adele", "Other Album"));
    }

    [Fact]
    public void Equals_DifferentTitleOrArtist_IsNotEqual()
    {
        Assert.NotEqual(Song("Hello", "Adele"), Song("Hello Again", "Adele"));
        Assert.NotEqual(Song("Hello", "Adele"), Song("Hello", "Lionel Richie"));
    }

    [Fact]
    public void Equals_NullOrOtherType_IsNotEqual()
    {
        var song = Song("Hello", "Adele");

        Assert.False(song.Equals(null));
        Assert.False(song.Equals("Adele - Hello"));
    }

    [Fact]
    public void GetHashCode_EqualObjects_HaveEqualHashCodes()
    {
        Assert.Equal(
            Song("Hello", "Adele", "25").GetHashCode(),
            Song("HELLO", "ADELE", "Other Album").GetHashCode());
    }

    [Fact]
    public void ToString_IsArtistDashTitle()
    {
        Assert.Equal("Adele - Hello", Song("Hello", "Adele").ToString());
    }
}
