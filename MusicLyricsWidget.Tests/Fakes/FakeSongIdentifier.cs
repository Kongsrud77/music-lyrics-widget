using MusicLyricsWidget.Models;
using MusicLyricsWidget.Services;

namespace MusicLyricsWidget.Tests.Fakes;

/// <summary>Returns queued results in order (a song, null for "no match", or a thrown exception).</summary>
internal sealed class FakeSongIdentifier : ISongIdentifier
{
    private readonly Queue<Func<SongInfo?>> _results = new();

    public BlockingGate? Gate { get; set; }

    public int IdentifyCalls { get; private set; }

    public void Enqueue(SongInfo? song) => _results.Enqueue(() => song);

    public void EnqueueThrow(Exception exception) => _results.Enqueue(() => throw exception);

    public async Task<SongInfo?> IdentifyAsync(byte[] audioData, CancellationToken cancellationToken = default)
    {
        IdentifyCalls++;
        if (Gate != null)
            await Gate.WaitAsync();
        if (_results.Count == 0)
            throw new InvalidOperationException("FakeSongIdentifier has no queued result.");
        return _results.Dequeue()();
    }
}
