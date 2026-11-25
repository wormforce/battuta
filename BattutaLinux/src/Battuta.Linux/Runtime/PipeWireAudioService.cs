using Battuta.Windows.Audio;
using Battuta.Windows.Audio.Linux;

namespace Battuta.Linux.Runtime;

/// <summary>
/// Stream lifecycle control over an audio backend. The production
/// implementation wraps the native PipeWire shim; tests substitute fakes.
/// The <see cref="IAudioVoiceScheduler"/> part carries the input hot path;
/// this interface carries recovery (plan §6.3: device change / PipeWire
/// restart / sleep resume rebuild the stream, banks are never re-decoded).
/// </summary>
public interface IAudioStreamController : IAudioVoiceScheduler, IDisposable
{
    /// <summary>Starts (or restarts) the default output stream. Returns 0
    /// when the request was accepted; readiness arrives via
    /// <see cref="StreamReady"/>.</summary>
    int StartStream();

    /// <summary>Stops the stream; loaded banks survive.</summary>
    int StopStream();

    /// <summary>Tears down and recreates the stream. Returns 0 on success.</summary>
    int RebuildStream();

    /// <summary>Invalidates queued playback commands (output restart).</summary>
    void ResetForOutputRestart();

    /// <summary>Raised when the stream reaches the streaming state.</summary>
    event EventHandler? StreamReady;

    /// <summary>Raised on stream errors; the supervisor answers with a
    /// bounded-backoff rebuild.</summary>
    event EventHandler? StreamFailed;

    /// <summary>Raised when the default output device changed; the supervisor
    /// answers with a rebuild.</summary>
    event EventHandler? StreamDeviceChanged;

    bool IsStreamReady { get; }
}

/// <summary>
/// Production controller over the native PipeWire shim. All calls are thin
/// forwards; the realtime rules live in the shim (plan §6.1).
/// </summary>
public sealed class PipeWireAudioService : IAudioVoiceScheduler, IAudioStreamController
{
    private readonly PipeWireVoiceScheduler scheduler;

    public PipeWireAudioService()
    {
        scheduler = new PipeWireVoiceScheduler();
    }

    /// <summary>Sets an extra stream property before the first start (tests
    /// use <c>target.object</c>).</summary>
    public void SetStreamProperty(string key, string value) =>
        scheduler.SetStreamProperty(key, value);

    /// <inheritdoc />
    public bool TrySchedule(PreparedPcmSample sample, float gain, float rate, VoicePriority priority) =>
        scheduler.TrySchedule(sample, gain, rate, priority);

    public int StartStream() => scheduler.StartStream();

    public int StopStream() => scheduler.StopStream();

    public int RebuildStream() => scheduler.RebuildStream();

    public void ResetForOutputRestart() => scheduler.ResetForOutputRestart();

    public event EventHandler? StreamReady
    {
        add => scheduler.StreamReady += value;
        remove => scheduler.StreamReady -= value;
    }

    public event EventHandler? StreamFailed
    {
        add => scheduler.StreamFailed += value;
        remove => scheduler.StreamFailed -= value;
    }

    public event EventHandler? StreamDeviceChanged
    {
        add => scheduler.StreamDeviceChanged += value;
        remove => scheduler.StreamDeviceChanged -= value;
    }

    public bool IsStreamReady => scheduler.IsStreamReady;

    public void Dispose() => scheduler.Dispose();
}
