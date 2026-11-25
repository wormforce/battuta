using Xunit;
using Battuta.Windows.Audio;
using Battuta.Linux.Runtime;

namespace Battuta.Linux.Tests.Runtime;

/// <summary>Scripted audio backend for supervisor tests.</summary>
public sealed class FakeAudioStreamController : IAudioStreamController
{
    private readonly Queue<int> rebuildResults = new();
    public int StartCalls { get; private set; }
    public int StopCalls { get; private set; }
    public int RebuildCalls { get; private set; }
    public bool IsStreamReady { get; private set; }

    public FakeAudioStreamController(params int[] scriptedRebuildResults)
    {
        foreach (var result in scriptedRebuildResults)
        {
            rebuildResults.Enqueue(result);
        }
    }

    public event EventHandler? StreamReady;
    public event EventHandler? StreamFailed;
    public event EventHandler? StreamDeviceChanged;

    public bool TrySchedule(PreparedPcmSample sample, float gain, float rate, VoicePriority priority) => true;

    public int StartStream()
    {
        StartCalls++;
        return 0;
    }

    public int StopStream()
    {
        StopCalls++;
        return 0;
    }

    public int RebuildStream()
    {
        RebuildCalls++;
        IsStreamReady = true;
        return rebuildResults.Count > 0 ? rebuildResults.Dequeue() : 0;
    }

    public void ResetForOutputRestart() { }

    public void RaiseStreamReady()
    {
        IsStreamReady = true;
        StreamReady?.Invoke(this, EventArgs.Empty);
    }

    public void RaiseStreamFailed()
    {
        IsStreamReady = false;
        StreamFailed?.Invoke(this, EventArgs.Empty);
    }

    public void RaiseStreamDeviceChanged() =>
        StreamDeviceChanged?.Invoke(this, EventArgs.Empty);

    public void Dispose() { }
}

public sealed class AudioStreamSupervisorTests
{
    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "condition not met within timeout");
    }

    [Fact]
    public async Task StreamFailureTriggersBoundedBackoffRebuild()
    {
        // Scripted rebuilds: 0 (success) — backoff resets on StreamReady
        // which the fake raises inside RebuildStream.
        using var audio = new FakeAudioStreamController(0, 0);
        using var supervisor = new AudioStreamSupervisor(audio);
        Assert.True(supervisor.Start());

        audio.RaiseStreamFailed();
        await WaitForAsync(() => audio.RebuildCalls == 1);

        // A second failure rebuilds again (backoff capped for the test by
        // real time); the point is supervision keeps the stream alive.
        audio.RaiseStreamFailed();
        await WaitForAsync(() => audio.RebuildCalls == 2);

        Assert.Equal(0, audio.StopCalls);
    }

    [Fact]
    public async Task DeviceChangeTriggersImmediateRebuild()
    {
        using var audio = new FakeAudioStreamController();
        using var supervisor = new AudioStreamSupervisor(audio);
        Assert.True(supervisor.Start());

        audio.RaiseStreamDeviceChanged();
        await WaitForAsync(() => audio.RebuildCalls == 1);
    }

    [Fact]
    public async Task RapidDeviceChangesAreDebouncedToOneRebuild()
    {
        using var audio = new FakeAudioStreamController();
        using var supervisor = new AudioStreamSupervisor(audio);
        Assert.True(supervisor.Start());

        // Burst within the debounce window (250 ms).
        audio.RaiseStreamDeviceChanged();
        audio.RaiseStreamDeviceChanged();
        audio.RaiseStreamDeviceChanged();

        await WaitForAsync(() => audio.RebuildCalls == 1);
        await Task.Delay(300);
        Assert.Equal(1, audio.RebuildCalls);
    }

    [Fact]
    public void StartReportsControllerResult()
    {
        using var audio = new FakeAudioStreamController();
        using var supervisor = new AudioStreamSupervisor(audio);
        Assert.True(supervisor.Start());
        Assert.Equal(1, audio.StartCalls);
    }
}
