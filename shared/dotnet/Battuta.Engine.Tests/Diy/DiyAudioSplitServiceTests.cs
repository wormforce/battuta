using Battuta.TestSupport;
using System.IO;
using Battuta.Windows.Diy.Audio;

namespace Battuta.Engine.Tests.Diy;

public sealed class DiyAudioSplitServiceTests
{
    [Fact]
    public async Task AnalyzeAndExportProducesTwoNormalizedSegments()
    {
        using var root = new TemporaryDirectory();
        var source = WaveFixture.WriteCompleteKeystroke(root.Combine("complete.wav"));
        using var splitter = new DiyAudioSplitService();

        var analysis = await splitter.AnalyzeAsync(source);

        Assert.Equal(48_000, analysis.SampleRate);
        Assert.True(analysis.FrameCount > 0);
        Assert.InRange(analysis.Suggestion.SplitTimeSeconds, 0.012, analysis.DurationSeconds - 0.012);
        Assert.Equal(256, analysis.Waveform.Count);
        Assert.True(analysis.EnergyEnvelope.Count > 0);

        var press = root.Combine("press.wav");
        var release = root.Combine("release.wav");
        var splitTime = analysis.DurationSeconds / 2;
        var exported = await splitter.ExportSplitAsync(
            source,
            splitTime,
            analysis.DurationSeconds,
            press,
            release);

        Assert.True(exported.PressFrameCount > 0);
        Assert.True(exported.ReleaseFrameCount > 0);
        using var importer = new DiyAudioImportService(root.Combine("validation-cache"));
        Assert.Equal(48_000, importer.ValidateNormalizedAudio(press).SampleRate);
        Assert.Equal(48_000, importer.ValidateNormalizedAudio(release).SampleRate);

        await Assert.ThrowsAsync<DiyAudioException>(() => splitter.ExportSplitAsync(
            source,
            splitTime,
            analysis.DurationSeconds,
            press,
            release));
        _ = await splitter.ExportSplitAsync(
            source,
            splitTime,
            analysis.DurationSeconds,
            press,
            release,
            overwriteExisting: true);
        Assert.True(File.Exists(press));
        Assert.True(File.Exists(release));
    }

    [Fact]
    public async Task AnalyzeRejectsDecodedMemoryAmplification()
    {
        using var root = new TemporaryDirectory();
        var source = WaveFixture.WriteCompleteKeystroke(root.Combine("complete.wav"));
        using var splitter = new DiyAudioSplitService(new AudioSplitConfiguration(
            MaximumDecodedBytes: 1_024));

        var error = await Assert.ThrowsAsync<DiyAudioException>(() => splitter.AnalyzeAsync(source));

        Assert.Contains("64 MiB", error.Message, StringComparison.Ordinal);
    }
}
