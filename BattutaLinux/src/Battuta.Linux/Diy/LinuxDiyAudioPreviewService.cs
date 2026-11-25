using System.IO;

using Battuta.Windows.Audio;
using Battuta.Windows.Audio.Linux;
using Battuta.Windows.Diy.Audio;
using Battuta.Windows.Diy.ViewModels;

namespace Battuta.Linux.Diy;

/// <summary>
/// Connects the Linux DIY editor's preview button to the PipeWire mixer.
/// Normalized custom assets and split exports are decoded by the managed
/// WAV reader; built-in MP3 sources resolve to their prep products through
/// the runtime's <see cref="IAudioSampleLoader"/> (plan §6.2).
/// </summary>
public sealed class LinuxDiyAudioPreviewService : IDiyAudioPreviewService
{
    private const long MaximumPreviewPcmBytes = 64 * 1_024 * 1_024;
    private const double MaximumPreviewDurationSeconds = 30;

    private readonly KeyboardAudioEngine engine;
    private readonly IAudioSampleLoader builtInLoader;
    private readonly Func<double> volumeProvider;

    public LinuxDiyAudioPreviewService(
        KeyboardAudioEngine engine,
        IAudioSampleLoader? builtInLoader = null,
        Func<double>? volumeProvider = null)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.builtInLoader = builtInLoader ?? new PreparedAudioProductLoader();
        this.volumeProvider = volumeProvider ?? (() => 0.42);
    }

    public async Task PreviewAsync(
        string audioPath,
        CancellationToken cancellationToken = default)
    {
        var sample = await Task.Run(
            () => Decode(audioPath, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!engine.Preview(sample, volumeProvider()))
        {
            throw new InvalidOperationException("Audio preview could not be queued for playback.");
        }
    }

    private PreparedPcmSample Decode(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase))
        {
            using var stream = File.OpenRead(Path.GetFullPath(path));
            var samples = WavDiyAudioDecodePipeline.DecodeStream(
                stream,
                MaximumPreviewPcmBytes,
                minimumDurationSeconds: 0,
                MaximumPreviewDurationSeconds,
                minimumSampleRate: 1_000,
                maximumSampleRate: 384_000,
                maximumChannelCount: 8,
                cancellationToken);
            return new PreparedPcmSample(samples);
        }

        // Built-in MP3/WAV sources resolve to their .p48m prep products.
        return builtInLoader.Decode(
            path,
            trimKeyboardLeadingSilence: false,
            cancellationToken);
    }
}
