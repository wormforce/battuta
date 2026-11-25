using Battuta.Windows.Diy.Audio;
using Battuta.Linux.Diy;
using Battuta.TestSupport;

namespace Battuta.Linux.Tests.Diy;

public sealed class LinuxDiyAudioDecodePipelineTests
{
    [Fact]
    public async Task DecodeNormalizesStereo44KhzWave()
    {
        using var root = new TemporaryDirectory();
        var source = WaveFixture.WriteStereoSine(root.Combine("source.wav"));
        var pipeline = CreatePipeline();

        var samples = await Task.Run(() => pipeline.DecodeMono48Khz(
            source,
            DiyAudioImportLimits.SoundPack.MaximumSourceBytes,
            DiyAudioImportLimits.SoundPack.MaximumDecodedBytes,
            DiyAudioImportLimits.SoundPack.MinimumDurationSeconds,
            DiyAudioImportLimits.SoundPack.MaximumDurationSeconds,
            DiyAudioImportLimits.SoundPack.MinimumSampleRate,
            DiyAudioImportLimits.SoundPack.MaximumSampleRate,
            DiyAudioImportLimits.SoundPack.MaximumChannelCount,
            CancellationToken.None));

        Assert.Equal(48_000 * 0.12, samples.Length, 0);
    }

    [Fact]
    public async Task DecodeRejectsOverlongSource()
    {
        using var root = new TemporaryDirectory();
        var source = WaveFixture.WriteStereoSine(
            root.Combine("too-long.wav"),
            durationSeconds: 5.2);
        var pipeline = CreatePipeline();

        var error = await Assert.ThrowsAsync<DiyAudioException>(() => Task.Run(
            () => pipeline.DecodeMono48Khz(
                source,
                DiyAudioImportLimits.SoundPack.MaximumSourceBytes,
                DiyAudioImportLimits.SoundPack.MaximumDecodedBytes,
                DiyAudioImportLimits.SoundPack.MinimumDurationSeconds,
                DiyAudioImportLimits.SoundPack.MaximumDurationSeconds,
                DiyAudioImportLimits.SoundPack.MinimumSampleRate,
                DiyAudioImportLimits.SoundPack.MaximumSampleRate,
                DiyAudioImportLimits.SoundPack.MaximumChannelCount,
                CancellationToken.None)));

        Assert.Contains("秒", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DecodeRejectsUnknownExtensionWithoutSpawningFfmpeg()
    {
        using var root = new TemporaryDirectory();
        var playlist = root.Combine("remote.m3u");
        await File.WriteAllTextAsync(playlist, "https://example.invalid/audio.mp3");
        var pipeline = CreatePipeline();

        var error = await Assert.ThrowsAsync<DiyAudioException>(() => Task.Run(
            () => pipeline.DecodeMono48Khz(
                playlist,
                DiyAudioImportLimits.SoundPack.MaximumSourceBytes,
                DiyAudioImportLimits.SoundPack.MaximumDecodedBytes,
                DiyAudioImportLimits.SoundPack.MinimumDurationSeconds,
                DiyAudioImportLimits.SoundPack.MaximumDurationSeconds,
                DiyAudioImportLimits.SoundPack.MinimumSampleRate,
                DiyAudioImportLimits.SoundPack.MaximumSampleRate,
                DiyAudioImportLimits.SoundPack.MaximumChannelCount,
                CancellationToken.None)));

        Assert.Contains("不支持", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompressedExtensionWithoutFfmpegSuggestsInstallingFfmpeg()
    {
        using var root = new TemporaryDirectory();
        var source = WaveFixture.WriteStereoSine(root.Combine("source.mp3"));
        var pipeline = CreatePipeline(ffmpegPath: null);

        var error = await Assert.ThrowsAsync<DiyAudioException>(() => Task.Run(
            () => pipeline.DecodeMono48Khz(
                source,
                DiyAudioImportLimits.SoundPack.MaximumSourceBytes,
                DiyAudioImportLimits.SoundPack.MaximumDecodedBytes,
                DiyAudioImportLimits.SoundPack.MinimumDurationSeconds,
                DiyAudioImportLimits.SoundPack.MaximumDurationSeconds,
                DiyAudioImportLimits.SoundPack.MinimumSampleRate,
                DiyAudioImportLimits.SoundPack.MaximumSampleRate,
                DiyAudioImportLimits.SoundPack.MaximumChannelCount,
                CancellationToken.None)));

        Assert.Contains("ffmpeg", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FfmpegFailureSurfacesStderrFirstLine()
    {
        using var root = new TemporaryDirectory();
        var garbage = root.Combine("source.aiff");
        await File.WriteAllBytesAsync(garbage, "definitely not audio"u8.ToArray());
        var pipeline = CreatePipeline(ffmpegPath: "/bin/false");

        var error = await Assert.ThrowsAsync<DiyAudioException>(() => Task.Run(
            () => pipeline.DecodeMono48Khz(
                garbage,
                DiyAudioImportLimits.SoundPack.MaximumSourceBytes,
                DiyAudioImportLimits.SoundPack.MaximumDecodedBytes,
                DiyAudioImportLimits.SoundPack.MinimumDurationSeconds,
                DiyAudioImportLimits.SoundPack.MaximumDurationSeconds,
                DiyAudioImportLimits.SoundPack.MinimumSampleRate,
                DiyAudioImportLimits.SoundPack.MaximumSampleRate,
                DiyAudioImportLimits.SoundPack.MaximumChannelCount,
                CancellationToken.None)));

        Assert.Contains("ffmpeg", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FfmpegBranchTranscodesWavContentUnderCompressedExtension()
    {
        // ffmpeg sniffs content, so a PCM WAV saved as .mp3 exercises the
        // subprocess path without needing an encoder in the test image.
        var ffmpegPath = LinuxDiyAudioDecodePipeline.LocateFfmpegOnPath();
        if (ffmpegPath is null)
        {
            Console.WriteLine("[linux-diy-test] ffmpeg not installed; skipped transcode test.");
            return;
        }

        using var root = new TemporaryDirectory();
        var source = WaveFixture.WriteStereoSine(root.Combine("source.mp3"));
        var pipeline = CreatePipeline(ffmpegPath);

        var samples = await Task.Run(() => pipeline.DecodeMono48Khz(
            source,
            DiyAudioImportLimits.SoundPack.MaximumSourceBytes,
            DiyAudioImportLimits.SoundPack.MaximumDecodedBytes,
            DiyAudioImportLimits.SoundPack.MinimumDurationSeconds,
            DiyAudioImportLimits.SoundPack.MaximumDurationSeconds,
            DiyAudioImportLimits.SoundPack.MinimumSampleRate,
            DiyAudioImportLimits.SoundPack.MaximumSampleRate,
            DiyAudioImportLimits.SoundPack.MaximumChannelCount,
            CancellationToken.None));

        Assert.Equal(48_000 * 0.12, samples.Length, 0);
    }

    [Fact]
    public void LocatorFindsFfmpegWhenInstalled()
    {
        var expected = File.Exists("/usr/bin/ffmpeg") ? "/usr/bin/ffmpeg" : null;
        Assert.Equal(expected, LinuxDiyAudioDecodePipeline.LocateFfmpegOnPath());
    }

    private static LinuxDiyAudioDecodePipeline CreatePipeline(string? ffmpegPath = "/bin/true") =>
        ffmpegPath is null
            ? new LinuxDiyAudioDecodePipeline(ffmpegLocator: () => null)
            : new LinuxDiyAudioDecodePipeline(ffmpegLocator: () => ffmpegPath);
}
