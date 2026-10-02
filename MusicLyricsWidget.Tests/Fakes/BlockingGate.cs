namespace MusicLyricsWidget.Tests.Fakes;

/// <summary>
/// Holds a fake inside an await until the test releases it, so a cycle can be paused at an exact point.
/// Deliberately ignores cancellation, like a collaborator that finishes its work although the caller has given up.
/// </summary>
internal sealed class BlockingGate
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once a fake has reached the gate.</summary>
    public Task Entered => _entered.Task;

    /// <summary>Called by the fake: signals <see cref="Entered"/>, then waits for <see cref="Release"/>.</summary>
    public async Task WaitAsync()
    {
        _entered.TrySetResult();
        await _released.Task;
    }

    /// <summary>Lets every fake waiting at (or arriving at) the gate continue.</summary>
    public void Release() => _released.TrySetResult();
}
