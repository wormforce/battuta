using Battuta.TestSupport;
using System.IO;
using Battuta.Windows.Diy.Audio;

namespace Battuta.Engine.Tests.Diy;

public sealed class DiyAudioImportServiceTests
{
    [Fact]
    public async Task PrepareImportNormalizesAndDeduplicatesStereoWave()
    {
        using var root = new TemporaryDirectory();
        var source = WaveFixture.WriteStereoSine(root.Combine("source.wav"));
        using var importer = new DiyAudioImportService(root.Combine("cache"));

        var first = await importer.PrepareImportAsync(source);
        var second = await importer.PrepareImportAsync(source);

        Assert.Equal(48_000, first.AudioInfo.SampleRate);
        Assert.Equal(1, first.AudioInfo.ChannelCount);
        Assert.Equal(16, first.AudioInfo.BitsPerSample);
        Assert.InRange(first.AudioInfo.DurationSeconds, 0.117, 0.123);
        Assert.Equal(first.AssetId, WaveFixture.Sha256(first.NormalizedFilePath));
        Assert.Equal(first.AssetId, second.AssetId);
        Assert.Equal(first.NormalizedFilePath, second.NormalizedFilePath);
        Assert.Single(Directory.EnumerateFiles(root.Combine("cache"), "*.wav"));
    }

    [Fact]
    public async Task PrepareImportRejectsOverlongAudioAndCleansTemporaryFile()
    {
        using var root = new TemporaryDirectory();
        var source = WaveFixture.WriteStereoSine(
            root.Combine("too-long.wav"),
            durationSeconds: 5.2);
        using var importer = new DiyAudioImportService(root.Combine("cache"));

        await Assert.ThrowsAsync<DiyAudioException>(() => importer.PrepareImportAsync(source));

        Assert.Empty(Directory.EnumerateFiles(root.Combine("cache"), ".import-*"));
    }

    [Fact]
    public async Task PrepareImportRejectsPlaylistWithoutStartingAProtocolHandler()
    {
        using var root = new TemporaryDirectory();
        var playlist = root.Combine("remote.m3u");
        await File.WriteAllTextAsync(playlist, "https://example.invalid/audio.mp3");
        using var importer = new DiyAudioImportService(root.Combine("cache"));

        var error = await Assert.ThrowsAsync<DiyAudioException>(
            () => importer.PrepareImportAsync(playlist));

        Assert.Contains("不支持", error.Message, StringComparison.Ordinal);
    }
}
