using NAudio.Wave;

namespace MusicLyricsWidget.Tests;

/// <summary>
/// Scriptable <see cref="IWaveIn"/>: delivers data synchronously and raises RecordingStopped from StopRecording,
/// like NAudio does on some paths. Deliberately keeps delivering after StopRecording, like the real record thread.
/// </summary>
internal sealed class FakeWaveIn : IWaveIn
{
    private const int PatternPeriod = 251;

    private EventHandler<WaveInEventArgs>? _dataAvailable;
    private EventHandler<StoppedEventArgs>? _recordingStopped;
    private EventHandler<WaveInEventArgs>? _everSubscribedData;
    private EventHandler<StoppedEventArgs>? _everSubscribedStopped;
    private int _nextByte;

    public WaveFormat WaveFormat { get; set; } = new(8000, 16, 1);

    /// <summary>Chunk sizes (bytes) delivered synchronously from <see cref="StartRecording"/>.</summary>
    public List<int> ChunksOnStart { get; } = new();

    /// <summary>Exception reported by the RecordingStopped raised from <see cref="StopRecording"/>.</summary>
    public Exception? ExceptionOnStop { get; set; }

    /// <summary>Exception thrown from <see cref="StartRecording"/> instead of delivering data.</summary>
    public Exception? ExceptionOnStart { get; set; }

    public int StartCalls { get; private set; }
    public int StopCalls { get; private set; }
    public int DisposeCalls { get; private set; }

    /// <summary>True while the code under test still has a handler subscribed.</summary>
    public bool HasSubscribers => _dataAvailable != null || _recordingStopped != null;

    public event EventHandler<WaveInEventArgs>? DataAvailable
    {
        add { _dataAvailable += value; _everSubscribedData += value; }
        remove { _dataAvailable -= value; }
    }

    public event EventHandler<StoppedEventArgs>? RecordingStopped
    {
        add { _recordingStopped += value; _everSubscribedStopped += value; }
        remove { _recordingStopped -= value; }
    }

    /// <summary>Expected payload of the first <paramref name="count"/> bytes ever delivered (a repeating byte pattern).</summary>
    public static byte[] Pattern(int count) => Enumerable.Range(0, count).Select(PatternByte).ToArray();

    public void StartRecording()
    {
        StartCalls++;
        if (ExceptionOnStart != null)
            throw ExceptionOnStart;

        foreach (var chunk in ChunksOnStart)
            Deliver(chunk);
    }

    public void StopRecording()
    {
        StopCalls++;
        RaiseRecordingStopped(ExceptionOnStop);
    }

    public void Dispose() => DisposeCalls++;

    /// <summary>Raises DataAvailable with <paramref name="bytes"/> bytes of the running pattern.</summary>
    public void Deliver(int bytes) => _dataAvailable?.Invoke(this, CreateArgs(bytes));

    public void RaiseRecordingStopped(Exception? exception) =>
        _recordingStopped?.Invoke(this, new StoppedEventArgs(exception));

    /// <summary>Raises both events on handlers that were subscribed at some point, even if since unsubscribed (an in-flight callback).</summary>
    public void RaiseOnStaleSubscribers(int bytes, Exception? exception)
    {
        _everSubscribedData?.Invoke(this, CreateArgs(bytes));
        _everSubscribedStopped?.Invoke(this, new StoppedEventArgs(exception));
    }

    private WaveInEventArgs CreateArgs(int bytes)
    {
        var data = new byte[bytes];
        for (var i = 0; i < bytes; i++)
            data[i] = PatternByte(_nextByte++);
        return new WaveInEventArgs(data, bytes);
    }

    private static byte PatternByte(int index) => (byte)(index % PatternPeriod);
}
