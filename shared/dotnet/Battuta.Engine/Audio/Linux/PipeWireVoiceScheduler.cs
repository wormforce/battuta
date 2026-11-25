using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Battuta.Windows.Audio.Linux;

/// <summary>
/// Linux <see cref="IAudioVoiceScheduler"/> over the native PipeWire shim
/// (plan §6.1/§6.3). The class owns the native engine, lazily registers
/// prepared samples into the shim's preallocated bank, and forwards schedule
/// requests into the command ring. Registration pins the PCM only for the
/// duration of the native copy, which runs off the realtime path.
/// </summary>
public sealed class PipeWireVoiceScheduler : IAudioVoiceScheduler, IDisposable
{
    private readonly NativeAudioEngineHandle engine;
    private readonly NativeAudioShim.AudioEventCallback eventCallback;
    private readonly ConditionalWeakTable<PreparedPcmSample, NativeSampleHandle> registeredSamples = new();
    private readonly ConcurrentDictionary<uint, NativeSampleHandle> liveSamples = new();
    private volatile bool streamReady;
    private volatile bool streamFailed;

    /// <summary>Raised from the native event callback thread on stream errors.</summary>
    public event EventHandler? StreamFailed;

    /// <summary>Raised when the stream reaches the streaming state.</summary>
    public event EventHandler? StreamReady;

    /// <summary>
    /// Raised when the PipeWire default output device changed (plan §6.3).
    /// The runtime answers with <see cref="RebuildStream"/> — banks survive.
    /// Fires from the shim's device monitor thread, never the realtime path.
    /// </summary>
    public event EventHandler? StreamDeviceChanged;

    public unsafe PipeWireVoiceScheduler()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException(
                "The PipeWire audio shim is only available on Linux.");
        }

        if (!NativeAudioShim.IsAvailable())
        {
            throw new DllNotFoundException(
                "libbattuta_audio.so was not found; build the BattutaLinux native workspace.");
        }

        // Root the delegate for the lifetime of this scheduler; the native
        // engine keeps the raw function pointer.
        eventCallback = OnNativeEvent;
        var createResult = NativeAudioShim.battuta_audio_engine_create(
            eventCallback, null, out var nativeEngine);
        if (createResult != 0)
        {
            throw new InvalidOperationException(
                $"battuta_audio_engine_create failed with {createResult}.");
        }

        engine = new NativeAudioEngineHandle(nativeEngine);
    }

    /// <summary>Sets an extra stream property (e.g. tests use target.object).</summary>
    public unsafe void SetStreamProperty(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        var keyBytes = System.Text.Encoding.UTF8.GetBytes(key);
        var valueBytes = System.Text.Encoding.UTF8.GetBytes(value);
        int result;
        fixed (byte* keyPointer = keyBytes, valuePointer = valueBytes)
        {
            result = NativeAudioShim.battuta_audio_set_stream_property(
                engine.DangerousGetHandle(), keyPointer, valuePointer);
        }

        if (result != 0)
        {
            throw new InvalidOperationException(
                $"battuta_audio_set_stream_property({key}) failed with {result}.");
        }
    }

    /// <summary>
    /// Starts (or restarts) the default PipeWire output stream. Recovery
    /// after device changes or PipeWire restarts must reuse this method —
    /// the PCM banks are never re-decoded or dropped (plan §6.3).
    /// </summary>
    public int StartStream() =>
        NativeAudioShim.battuta_audio_stream_start(engine.DangerousGetHandle());

    public int StopStream() =>
        NativeAudioShim.battuta_audio_stream_stop(engine.DangerousGetHandle());

    /// <summary>Rebuilds the stream after an error or device change.</summary>
    public int RebuildStream()
    {
        streamFailed = false;
        streamReady = false;
        return NativeAudioShim.battuta_audio_stream_rebuild(engine.DangerousGetHandle());
    }

    /// <summary>Invalidates queued clicks; used when an output session restarts.</summary>
    public void ResetForOutputRestart() =>
        NativeAudioShim.battuta_audio_reset(engine.DangerousGetHandle());

    /// <inheritdoc />
    public bool TrySchedule(
        PreparedPcmSample sample,
        float gain,
        float rate,
        VoicePriority priority)
    {
        ArgumentNullException.ThrowIfNull(sample);
        // ConditionalWeakTable.GetValue runs the factory at most once per
        // sample instance, so concurrent first schedules share one slot.
        var nativeSample = registeredSamples.GetValue(sample, RegisterSample);

        var priorityValue = priority switch
        {
            VoicePriority.Keyboard => NativeAudioPriorities.Keyboard,
            VoicePriority.Pointer => NativeAudioPriorities.Pointer,
            _ => throw new ArgumentOutOfRangeException(nameof(priority)),
        };
        var accepted = NativeAudioShim.battuta_audio_schedule(
            engine.DangerousGetHandle(), nativeSample.Sample, gain, rate, priorityValue);
        return accepted == 1;
    }

    internal NativeAudioStats GetStats()
    {
        if (NativeAudioShim.battuta_audio_get_stats(
                engine.DangerousGetHandle(), out var stats) != 0)
        {
            throw new InvalidOperationException("battuta_audio_get_stats failed.");
        }

        return stats;
    }

    public (uint SampleRate, uint Channels) GetFormat()
    {
        if (NativeAudioShim.battuta_audio_get_format(out var rate, out var channels) != 0)
        {
            throw new InvalidOperationException("battuta_audio_get_format failed.");
        }

        return (rate, channels);
    }

    public bool IsStreamReady => streamReady;

    public bool HasStreamFailed => streamFailed;

    private unsafe NativeSampleHandle RegisterSample(PreparedPcmSample sample)
    {
        // The native bank copies the PCM into its own budgeted slot; the pin
        // only has to outlive the synchronous register call.
        var memoryHandle = sample.Samples.Pin();
        try
        {
            var registered = NativeAudioShim.battuta_audio_sample_register(
                engine.DangerousGetHandle(),
                (float*)memoryHandle.Pointer,
                (uint)sample.FrameCount);
            if (registered == 0)
            {
                throw new InvalidOperationException(
                    "battuta_audio_sample_register failed (bank full or invalid data).");
            }

            var handle = new NativeSampleHandle(engine, registered);
            liveSamples[registered] = handle;
            return handle;
        }
        finally
        {
            memoryHandle.Dispose();
        }
    }

    private unsafe void OnNativeEvent(int eventId, void* userData)
    {
        switch (eventId)
        {
            case NativeAudioEvents.StreamReady:
                streamReady = true;
                StreamReady?.Invoke(this, EventArgs.Empty);
                break;
            case NativeAudioEvents.StreamError:
                streamFailed = true;
                StreamFailed?.Invoke(this, EventArgs.Empty);
                break;
            case NativeAudioEvents.DeviceChanged:
                streamReady = false;
                StreamDeviceChanged?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    public void Dispose()
    {
        // Release samples deterministically while the engine still exists;
        // the SafeHandle finalizers are a safety net, not the primary path.
        foreach (var handle in liveSamples.Values)
        {
            handle.Dispose();
        }

        liveSamples.Clear();
        engine.Dispose();
    }
}
