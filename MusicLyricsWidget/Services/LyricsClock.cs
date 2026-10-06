using System.Diagnostics;
using MusicLyricsWidget.Models;

namespace MusicLyricsWidget.Services;

/// <summary>
/// Follows the playback position of the current song from an anchor (song position at a timestamp) and raises
/// <see cref="LineChanged"/> when the synced lyrics line changes. One one-shot timer is set to the next line change;
/// every tick recomputes the line from the clock (never "+1"), so drift and jumps cannot accumulate.
/// </summary>
internal sealed class LyricsClock : IDisposable
{
    // Guards against a zero/negative delay from rounding turning into a busy loop.
    private static readonly TimeSpan MinimumDelay = TimeSpan.FromMilliseconds(10);

    private readonly TimeProvider _time;
    private readonly ITimer _timer;
    private readonly object _gate = new();
    private IReadOnlyList<LyricsLine>? _lines;
    private TimeSpan _anchorPosition;
    private long _anchorTimestamp;
    private TimeSpan? _songDuration;
    private int? _index;
    private bool _running;

    public LyricsClock(TimeProvider time)
    {
        _time = time;
        _timer = time.CreateTimer(_ => OnTick(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>The current line (-1 before the first); null when no synced song is tracked.</summary>
    public int? CurrentLineIndex
    {
        get { lock (_gate) return _index; }
    }

    /// <summary>Raised on the timer thread when <see cref="CurrentLineIndex"/> changes. Subscribers must not throw.</summary>
    public event Action? LineChanged;

    /// <summary>
    /// Starts tracking a song or moves its anchor, without raising <see cref="LineChanged"/>. Returns whether the
    /// current line changed. Ticking starts with <see cref="Run"/>.
    /// </summary>
    public bool Track(IReadOnlyList<LyricsLine> lines, TimeSpan positionAtTimestamp, long timestamp, TimeSpan? songDuration)
    {
        lock (_gate)
        {
            _lines = lines;
            _anchorPosition = positionAtTimestamp;
            _anchorTimestamp = timestamp;
            _songDuration = songDuration;
            return UpdateIndexLocked();
        }
    }

    /// <summary>Recomputes the line from the clock now, without an event. Returns whether it changed.</summary>
    public bool Refresh()
    {
        lock (_gate)
            return _lines != null && UpdateIndexLocked();
    }

    /// <summary>Ticks at each coming line change; stops after the last line or when the next line starts after the song.</summary>
    public void Run()
    {
        lock (_gate)
        {
            _running = true;
            ArmLocked();
        }
    }

    /// <summary>Stops ticking but keeps the song and anchor (pause, or the card left the lyrics).</summary>
    public void Halt()
    {
        lock (_gate)
        {
            _running = false;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Stops ticking and forgets the song.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _running = false;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _lines = null;
            _index = null;
        }
    }

    public void Dispose() => _timer.Dispose();

    private void OnTick()
    {
        try
        {
            bool changed;
            lock (_gate)
            {
                if (!_running || _lines == null)
                    return;
                changed = UpdateIndexLocked();
            }

            if (changed)
                LineChanged?.Invoke();

            lock (_gate)
            {
                if (_running)
                    ArmLocked();
            }
        }
        catch (Exception ex)
        {
            // A timer callback must never throw: it would take the widget process down. Stop and leave a trace.
            Halt();
            Trace.TraceError($"LyricsClock stopped after a failing tick: {ex}");
        }
    }

    private void ArmLocked()
    {
        if (_lines == null)
        {
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return;
        }

        var position = PositionLocked();
        var untilNext = SyncedLyricsCursor.TimeUntilNextLine(_lines, position);
        if (untilNext == null || (_songDuration != null && position + untilNext.Value > _songDuration.Value))
        {
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return;
        }

        _timer.Change(untilNext.Value < MinimumDelay ? MinimumDelay : untilNext.Value, Timeout.InfiniteTimeSpan);
    }

    private TimeSpan PositionLocked() => _anchorPosition + _time.GetElapsedTime(_anchorTimestamp);

    private bool UpdateIndexLocked()
    {
        var index = SyncedLyricsCursor.IndexAt(_lines!, PositionLocked());
        var changed = index != _index;
        _index = index;
        return changed;
    }
}
