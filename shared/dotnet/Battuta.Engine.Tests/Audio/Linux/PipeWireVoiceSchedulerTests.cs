using Battuta.Windows.Audio;
using Battuta.Windows.Audio.Linux;

namespace Battuta.Engine.Tests.Audio.Linux;

/// <summary>
/// ABI-level checks for the native PipeWire shim on Linux (plan §6.1).
/// Skipped everywhere the cdylib is unavailable (e.g. Windows CI). Streaming
/// behaviour is covered by the Rust-side virtual-sink e2e
/// (<c>battuta-audio/tests/pipewire_e2e.rs</c>) so this test never opens an
/// audible stream.
/// </summary>
public sealed class PipeWireVoiceSchedulerTests
{
    private static bool TrySkip(out string reason)
    {
        if (!OperatingSystem.IsLinux())
        {
            reason = "requires Linux";
            return true;
        }

        if (!NativeAudioShim.IsAvailable())
        {
            reason = "libbattuta_audio.so not built";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    [Fact]
    public void ReportsTheNegotiatedDefaultFormat()
    {
        if (TrySkip(out var reason))
        {
            Assert.Skip(reason);
        }

        using var scheduler = new PipeWireVoiceScheduler();
        var (sampleRate, channels) = scheduler.GetFormat();

        Assert.Equal(48_000u, sampleRate);
        Assert.Equal(2u, channels);
    }

    [Fact]
    public void AcceptsScheduleCommandsAndTracksStats()
    {
        if (TrySkip(out var reason))
        {
            Assert.Skip(reason);
        }

        using var scheduler = new PipeWireVoiceScheduler();
        var pcm = new PreparedPcmSample(Sine(480));
        Assert.True(scheduler.TrySchedule(pcm, 0.8f, 1f, VoicePriority.Keyboard));
        Assert.True(scheduler.TrySchedule(pcm, 0.5f, 2f, VoicePriority.Pointer));

        var stats = scheduler.GetStats();
        Assert.Equal(0, stats.DroppedCommands);
        Assert.True(stats.RenderedFrames == 0, "no stream is running in this test");

        scheduler.ResetForOutputRestart();
        var restartedStats = scheduler.GetStats();
        Assert.Equal(stats.RenderedFrames, restartedStats.RenderedFrames);
    }

    [Fact]
    public void RejectsNonFiniteArguments()
    {
        if (TrySkip(out var reason))
        {
            Assert.Skip(reason);
        }

        using var scheduler = new PipeWireVoiceScheduler();
        var pcm = new PreparedPcmSample(Sine(240));

        // The native shim mirrors the Windows scheduler: non-finite gain or
        // rate rejects the command before it enters the ring.
        Assert.False(scheduler.TrySchedule(pcm, float.PositiveInfinity, 1f, VoicePriority.Keyboard));
        Assert.False(scheduler.TrySchedule(pcm, 0.8f, float.NaN, VoicePriority.Keyboard));
    }

    private static float[] Sine(int frames)
    {
        var samples = new float[frames];
        for (var index = 0; index < frames; index++)
        {
            samples[index] = 0.5f * MathF.Sin(2 * MathF.PI * 440f * index / 48_000f);
        }

        return samples;
    }
}
