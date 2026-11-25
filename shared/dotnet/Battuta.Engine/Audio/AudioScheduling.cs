namespace Battuta.Windows.Audio;

/// <summary>
/// Scheduling priority for a playback request. Keyboard voices may steal the
/// oldest active keyboard voice; pointer voices never steal keyboard voices.
/// </summary>
public enum VoicePriority
{
    Keyboard,
    Pointer,
}

/// <summary>
/// Platform-neutral voice scheduling contract. Windows implements it on top of
/// the in-process PolyphonicSampleProvider mixer; Linux implements it on top
/// of the native PipeWire shim command ring. Implementations must be safe to
/// call from the input hot path and must never allocate in the realtime path.
/// </summary>
public interface IAudioVoiceScheduler
{
    bool TrySchedule(PreparedPcmSample sample, float gain, float rate, VoicePriority priority);
}

/// <summary>
/// Platform-neutral decode seam for built-in and custom audio resources.
/// Windows implements it with the NAudio-based PcmSampleDecoder; Linux
/// implements it with its build-time PCM products and a non-realtime decode
/// service. The input hot path never calls this interface — it only runs
/// during profile bank preparation.
/// </summary>
public interface IAudioSampleLoader
{
    PreparedPcmSample Decode(
        string path,
        bool trimKeyboardLeadingSilence,
        CancellationToken cancellationToken = default);
}
