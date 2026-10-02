using MusicLyricsWidget.Services;

namespace MusicLyricsWidget.Tests;

public class AudioCaptureServiceTests
{
    // 1000 Hz, mono, 1 s of 16-bit audio = 2000 bytes: small enough to script exactly, no real recording time.
    private const int SampleRate = 1000;
    private const int Channels = 1;
    private const int Seconds = 1;
    private const int BitsPerSample = 16;
    private const int TargetBytes = SampleRate * Channels * 2 * Seconds;
    private const string InProgressMessage = "An audio capture is already in progress.";
    private const string NoMicrophoneText = "No microphone";

    // Only bounds a hang if a regression deadlocks; passing tests never wait on it.
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);

    private readonly List<FakeWaveIn> _created = new();
    private int _deviceCount = 1;
    private Action<FakeWaveIn> _configure = _ => { };

    private AudioCaptureService CreateService() =>
        new(SampleRate, Channels, Seconds,
            format =>
            {
                var fake = new FakeWaveIn { WaveFormat = format };
                _configure(fake);
                _created.Add(fake);
                return fake;
            },
            () => _deviceCount);

    private static Task<byte[]> Completed(Task<byte[]> capture) => capture.WaitAsync(HangGuard);

    private void DeliverFullTargetOnStart() => _configure = fake => fake.ChunksOnStart.Add(TargetBytes);

    [Fact]
    public async Task CaptureAudioAsync_FakeDeliversMoreThanTarget_ReturnsExactlyTargetBytesAndDisposes()
    {
        _configure = fake =>
        {
            fake.ChunksOnStart.Add(1500);
            fake.ChunksOnStart.Add(1500);
            fake.ChunksOnStart.Add(500);
        };
        using var service = CreateService();

        var pcm = await Completed(service.CaptureAudioAsync());

        Assert.Equal(FakeWaveIn.Pattern(TargetBytes), pcm);
        var fake = Assert.Single(_created);
        Assert.Equal(1, fake.StopCalls);
        Assert.Equal(1, fake.DisposeCalls);
        Assert.False(fake.HasSubscribers);
    }

    [Fact]
    public async Task CaptureAudioAsync_RequestsConfiguredWaveFormat()
    {
        DeliverFullTargetOnStart();
        using var service = CreateService();

        await Completed(service.CaptureAudioAsync());

        var format = Assert.Single(_created).WaveFormat;
        Assert.Equal(SampleRate, format.SampleRate);
        Assert.Equal(Channels, format.Channels);
        Assert.Equal(BitsPerSample, format.BitsPerSample);
    }

    [Fact]
    public async Task CaptureAudioAsync_SecondConcurrentCall_ThrowsInvalidOperationAndFirstStillCompletes()
    {
        using var service = CreateService();
        var first = service.CaptureAudioAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Completed(service.CaptureAudioAsync()));
        Assert.Equal(InProgressMessage, ex.Message);
        Assert.Single(_created); // the rejected call must not have opened a second device

        _created[0].Deliver(TargetBytes);
        Assert.Equal(FakeWaveIn.Pattern(TargetBytes), await Completed(first));
        Assert.Equal(1, _created[0].DisposeCalls);
    }

    [Fact]
    public async Task CaptureAudioAsync_AfterFirstCompletes_NewCaptureWorksWithItsOwnDevice()
    {
        DeliverFullTargetOnStart();
        using var service = CreateService();

        var first = await Completed(service.CaptureAudioAsync());
        var second = await Completed(service.CaptureAudioAsync());

        Assert.Equal(TargetBytes, first.Length);
        Assert.Equal(TargetBytes, second.Length);
        Assert.Equal(2, _created.Count);
        Assert.All(_created, fake => Assert.Equal(1, fake.DisposeCalls));
    }

    [Fact]
    public async Task CaptureAudioAsync_Cancelled_ThrowsCancellationStopsRecordingAndDisposes()
    {
        using var service = CreateService();
        using var cts = new CancellationTokenSource();
        var capture = service.CaptureAudioAsync(cts.Token);
        var fake = Assert.Single(_created);
        // Partial data: the RecordingStopped raised synchronously by StopRecording must not turn it into a result.
        fake.Deliver(TargetBytes / 2);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Completed(capture));
        Assert.Equal(1, fake.StopCalls);
        Assert.Equal(1, fake.DisposeCalls);
        Assert.False(fake.HasSubscribers);
    }

    [Fact]
    public async Task CaptureAudioAsync_TokenAlreadyCancelled_ThrowsWithoutOpeningADevice()
    {
        using var service = CreateService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CaptureAudioAsync(cts.Token));

        Assert.Empty(_created);
    }

    [Fact]
    public async Task CaptureAudioAsync_AfterCancellation_NewCaptureSucceeds()
    {
        using var service = CreateService();
        using var cts = new CancellationTokenSource();
        var cancelled = service.CaptureAudioAsync(cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Completed(cancelled));
        DeliverFullTargetOnStart();

        var pcm = await Completed(service.CaptureAudioAsync());

        Assert.Equal(TargetBytes, pcm.Length);
    }

    [Fact]
    public async Task CaptureAudioAsync_RecordingStoppedWithException_PropagatesThatExceptionAndDisposes()
    {
        using var service = CreateService();
        var capture = service.CaptureAudioAsync();
        var fake = Assert.Single(_created);
        var failure = new IOException("device removed");

        fake.RaiseRecordingStopped(failure);

        var thrown = await Assert.ThrowsAsync<IOException>(() => Completed(capture));
        Assert.Same(failure, thrown);
        Assert.Equal(1, fake.DisposeCalls);
        Assert.False(fake.HasSubscribers);
    }

    [Fact]
    public async Task CaptureAudioAsync_AfterRecordingError_NewCaptureSucceeds()
    {
        using var service = CreateService();
        var failed = service.CaptureAudioAsync();
        _created[0].RaiseRecordingStopped(new IOException("device removed"));
        await Assert.ThrowsAsync<IOException>(() => Completed(failed));
        DeliverFullTargetOnStart();

        var pcm = await Completed(service.CaptureAudioAsync());

        Assert.Equal(TargetBytes, pcm.Length);
    }

    [Fact]
    public async Task CaptureAudioAsync_StartRecordingThrows_PropagatesDisposesAndAllowsNewCapture()
    {
        var failure = new InvalidOperationException("start failed");
        _configure = fake => fake.ExceptionOnStart = failure;
        using var service = CreateService();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CaptureAudioAsync());

        Assert.Same(failure, thrown);
        var fake = Assert.Single(_created);
        Assert.Equal(1, fake.DisposeCalls);
        Assert.Equal(0, fake.StopCalls); // never started, so nothing to stop (and nothing to mask the start error)
        DeliverFullTargetOnStart();
        Assert.Equal(TargetBytes, (await Completed(service.CaptureAudioAsync())).Length);
    }

    [Fact]
    public async Task Handlers_FiringAfterCompletion_DoNotThrow()
    {
        DeliverFullTargetOnStart();
        using var service = CreateService();
        await Completed(service.CaptureAudioAsync());
        var fake = Assert.Single(_created);

        var error = Record.Exception(() =>
        {
            fake.RaiseOnStaleSubscribers(TargetBytes, exception: null);
            fake.RaiseOnStaleSubscribers(TargetBytes, new IOException("late"));
        });

        Assert.Null(error);
        Assert.Equal(1, fake.StopCalls); // a late data chunk must not request another stop on the disposed device
    }

    [Fact]
    public async Task CaptureAudioAsync_NoMicrophone_ThrowsAndNeverCreatesWaveIn()
    {
        _deviceCount = 0;
        using var service = CreateService();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CaptureAudioAsync());

        Assert.Contains(NoMicrophoneText, ex.Message);
        Assert.Empty(_created);
    }

    [Fact]
    public async Task CaptureAudioAsync_AfterNoMicrophone_NewCaptureWorksOnceADeviceAppears()
    {
        _deviceCount = 0;
        using var service = CreateService();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CaptureAudioAsync());
        _deviceCount = 1;
        DeliverFullTargetOnStart();

        var pcm = await Completed(service.CaptureAudioAsync());

        Assert.Equal(TargetBytes, pcm.Length);
    }
}
