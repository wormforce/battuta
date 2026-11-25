namespace Battuta.Windows.Diy.Audio;

/// <summary>
/// Normalizes any supported source audio file into mono 48 kHz float samples
/// for the non-realtime DIY import and split services (plan §6.2). The
/// playback hot path never uses this abstraction: sound-pack assets are
/// already normalized WAV files.
/// </summary>
/// <remarks>
/// Platforms supply their own implementation: Windows uses NAudio, Linux
/// uses a managed WAV reader plus an ffmpeg fallback for compressed formats.
/// The default on every platform is the managed
/// <see cref="WavDiyAudioDecodePipeline"/>, which only decodes PCM WAV.
/// </remarks>
public interface IDiyAudioDecodePipeline
{
    /// <summary>Decodes <paramref name="sourcePath"/> to mono 48 kHz samples.</summary>
    /// <exception cref="DiyAudioException">The source is unsafe or undecodable.</exception>
    float[] DecodeMono48Khz(
        string sourcePath,
        long maximumSourceBytes,
        long maximumDecodedBytes,
        double minimumDurationSeconds,
        double maximumDurationSeconds,
        int minimumSampleRate,
        int maximumSampleRate,
        int maximumChannelCount,
        CancellationToken cancellationToken);
}

/// <summary>Shared constants for DIY audio decoding.</summary>
public static class DiyAudioDecoding
{
    /// <summary>The normalized sample rate every DIY asset is resampled to.</summary>
    public const int OutputSampleRate = 48_000;
}
