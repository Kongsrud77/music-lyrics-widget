using MusicLyricsWidget.Models;
using MusicLyricsWidget.Services;

namespace MusicLyricsWidget.Tests.Fakes;

/// <summary>Returns queued results in order (lyrics, null for "none found", or a thrown exception).</summary>
internal sealed class FakeLyricsProvider : ILyricsProvider
{
    private readonly Queue<Func<LyricsResult?>> _results = new();

    public BlockingGate? Gate { get; set; }

    /// <summary>The songs lyrics were requested for, in call order.</summary>
    public List<SongInfo> RequestedSongs { get; } = new();

    public void Enqueue(LyricsResult? lyrics) => _results.Enqueue(() => lyrics);

    public void EnqueueThrow(Exception exception) => _results.Enqueue(() => throw exception);

    public async Task<LyricsResult?> GetLyricsAsync(SongInfo song, CancellationToken cancellationToken = default)
    {
        RequestedSongs.Add(song);
        if (Gate != null)
            await Gate.WaitAsync();
        if (_results.Count == 0)
            throw new InvalidOperationException("FakeLyricsProvider has no queued result.");
        return _results.Dequeue()();
    }
}
