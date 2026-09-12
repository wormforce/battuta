using System.IO;
using Battuta.Core.SoundPacks;
using Battuta.Windows.Diy.Audio;
using Battuta.Windows.Diy.Packages;
using Battuta.Windows.Tests.Audio;

namespace Battuta.Windows.Tests.Diy;

public sealed class DiySoundPackPackageTests
{
    private static readonly Guid BundledBcpPackId =
        Guid.Parse("15d04652-5265-4ea7-a376-8a7e11ff6813");

    [Fact]
    public async Task LibraryAndArchiveRoundTripAndApplyCollisionPolicies()
    {
        using var root = new TemporaryDirectory();
        var source = WaveFixture.WriteStereoSine(root.Combine("source.wav"));
        using var importer = new DiyAudioImportService(root.Combine("normalized"));
        var prepared = await importer.PrepareImportAsync(source);
        var manifest = ManifestFor(prepared, "Round trip");
        using var library = new DiySoundPackLibrary(
            root.Combine("library"),
            builtInDescriptors: []);

        var descriptor = await library.SaveAsync(
            manifest,
            new Dictionary<SoundPackAssetId, string>
            {
                [new SoundPackAssetId(prepared.AssetId)] = prepared.NormalizedFilePath,
            });
        var loaded = await library.LoadAsync(manifest.Id);

        Assert.Equal(manifest.Id, descriptor.CustomPackId);
        Assert.Equal("Round trip", loaded.Manifest.Name);
        Assert.Equal(prepared.AssetId, WaveFixture.Sha256(
            loaded.AssetPath(new SoundPackAssetId(prepared.AssetId))));
        var savedCustomPacks = (await library.DescriptorsAsync())
            .Where(item => !item.IsReadOnly)
            .ToArray();
        Assert.Equal(manifest.Id, Assert.Single(savedCustomPacks).CustomPackId);

        _ = await library.SaveAsync(loaded.Manifest with { Name = "Renamed" });
        Assert.Equal("Renamed", (await library.LoadAsync(manifest.Id)).Manifest.Name);

        var archive = new DiySoundPackArchiveService();
        var exportedPath = root.Combine("export.simuboardpack");
        _ = await archive.ExportAsync(manifest.Id, library, exportedPath);
        Assert.Equal("Renamed", archive.Validate(exportedPath).Manifest.Name);

        using var importedLibrary = new DiySoundPackLibrary(
            root.Combine("imported"),
            builtInDescriptors: []);
        var first = await archive.ImportAsync(exportedPath, importedLibrary);
        Assert.Equal(manifest.Id, first.CustomPackId);
        var duplicated = await archive.ImportAsync(exportedPath, importedLibrary);
        Assert.NotEqual(manifest.Id, duplicated.CustomPackId);
        Assert.Equal(
            2,
            (await importedLibrary.DescriptorsAsync()).Count(item => !item.IsReadOnly));

        var collision = await Assert.ThrowsAsync<SoundPackException>(() => archive.ImportAsync(
            exportedPath,
            importedLibrary,
            SoundPackImportCollisionPolicy.Reject));
        Assert.Equal(SoundPackErrorKind.PackAlreadyExists, collision.Kind);
    }

    [Fact]
    public void ValidateAllowsManifestOnlyBlankPackage()
    {
        using var root = new TemporaryDirectory();
        var package = root.Combine("Blank.simuboardpack");
        Directory.CreateDirectory(package);
        File.WriteAllBytes(
            Path.Combine(package, "manifest.json"),
            SoundPackManifestJson.Encode(new SoundPackManifest { Name = "Blank" }));

        var validated = new DiySoundPackPackageValidator().Validate(package);

        Assert.Empty(validated.AssetFiles);
        Assert.Equal("Blank", validated.Manifest.Name);
    }

    [Fact]
    public async Task ValidateRejectsUnknownFileAndHashTampering()
    {
        using var root = new TemporaryDirectory();
        var source = WaveFixture.WriteStereoSine(root.Combine("source.wav"));
        using var importer = new DiyAudioImportService(root.Combine("normalized"));
        var prepared = await importer.PrepareImportAsync(source);
        var manifest = ManifestFor(prepared, "Security");
        using var library = new DiySoundPackLibrary(
            root.Combine("library"),
            builtInDescriptors: []);
        _ = await library.SaveAsync(
            manifest,
            new Dictionary<SoundPackAssetId, string>
            {
                [new SoundPackAssetId(prepared.AssetId)] = prepared.NormalizedFilePath,
            });
        var archive = new DiySoundPackArchiveService();
        var package = root.Combine("security.simuboardpack");
        _ = await archive.ExportAsync(manifest.Id, library, package);

        await File.WriteAllTextAsync(Path.Combine(package, "unexpected.txt"), "not allowed");
        var unknown = Assert.Throws<SoundPackException>(() => archive.Validate(package));
        Assert.Equal(SoundPackErrorKind.UnsafePath, unknown.Kind);
        File.Delete(Path.Combine(package, "unexpected.txt"));

        var assetPath = Path.Combine(package, "assets", $"{prepared.AssetId}.wav");
        await File.AppendAllTextAsync(assetPath, "tamper");
        var tampered = Assert.Throws<SoundPackException>(() => archive.Validate(package));
        Assert.True(tampered.Kind is SoundPackErrorKind.HashMismatch or SoundPackErrorKind.InvalidAudio);
    }

    [Fact]
    public async Task ValidateRejectsDirectoryReparsePointWhenSupported()
    {
        using var root = new TemporaryDirectory();
        var package = root.Combine("links.simuboardpack");
        Directory.CreateDirectory(package);
        File.WriteAllBytes(
            Path.Combine(package, "manifest.json"),
            SoundPackManifestJson.Encode(new SoundPackManifest { Name = "Links" }));
        var target = root.Combine("outside");
        Directory.CreateDirectory(target);
        var link = Path.Combine(package, "licenses");
        try
        {
            _ = Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception error) when (
            error is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        var validator = new DiySoundPackPackageValidator();
        var rejected = Assert.Throws<SoundPackException>(() => validator.Validate(package));
        Assert.Equal(SoundPackErrorKind.UnsafeFile, rejected.Kind);
    }

    [Fact]
    public async Task DefaultRuntimePathIncludesBundledBcp()
    {
        using var root = new TemporaryDirectory();
        using var library = new DiySoundPackLibrary(root.Combine("library"));
        var descriptors = await library.DescriptorsAsync();
        var bcp = Assert.Single(descriptors, item => item.CustomPackId == BundledBcpPackId);
        Assert.Equal("BCP (Suit80)", bcp.Name);
        Assert.True(bcp.IsReadOnly);
        Assert.Equal(28, (await library.LoadAsync(BundledBcpPackId)).Manifest.Assets.Count);
    }

    [Fact]
    public async Task LibraryEnumeratesAndLoadsBundledReadOnlyPack()
    {
        using var root = new TemporaryDirectory();
        using var library = new DiySoundPackLibrary(
            root.Combine("library"),
            builtInDescriptors: [],
            bundledPackRootPath: BundledPackRoot());

        var descriptors = await library.DescriptorsAsync();
        var descriptor = Assert.Single(descriptors);

        Assert.Equal($"custom:{BundledBcpPackId:D}".ToLowerInvariant(), descriptor.SelectionId);
        Assert.Equal("BCP (Suit80)", descriptor.Name);
        Assert.True(descriptor.IsReadOnly);
        Assert.Equal(BundledBcpPackId, descriptor.CustomPackId);

        var loaded = await library.LoadAsync(BundledBcpPackId);
        Assert.Equal(BundledBcpPackId, loaded.Manifest.Id);
        Assert.Equal("BCP (Suit80)", loaded.Manifest.Name);
        Assert.Equal(28, loaded.Manifest.Assets.Count);
        Assert.Equal("线性", loaded.Manifest.Family);
        Assert.Equal("厚实、木感", loaded.Manifest.Tone);
    }

    [Fact]
    public async Task BundledPackHidesAndOverridesLocalDuplicateWithSameId()
    {
        using var root = new TemporaryDirectory();
        var localDuplicate = Path.Combine(root.Combine("library"), $"{BundledBcpPackId:D}.simuboardpack");
        Directory.CreateDirectory(localDuplicate);
        await File.WriteAllTextAsync(
            Path.Combine(localDuplicate, "manifest.json"),
            "{ \"name\": \"broken duplicate\" }");
        using var library = new DiySoundPackLibrary(
            root.Combine("library"),
            builtInDescriptors: [],
            bundledPackRootPath: BundledPackRoot());

        var descriptors = await library.DescriptorsAsync();
        Assert.Single(descriptors);

        var loaded = await library.LoadAsync(BundledBcpPackId);
        Assert.Equal("BCP (Suit80)", loaded.Manifest.Name);
        Assert.Equal(28, loaded.Manifest.Assets.Count);
    }

    private static SoundPackManifest ManifestFor(PreparedDiyAudio prepared, string name)
    {
        var id = new SoundPackAssetId(prepared.AssetId);
        var asset = new SoundPackAudioAsset
        {
            Id = id,
            RelativePath = $"assets/{prepared.AssetId}.wav",
            Sha256 = prepared.AssetId,
            OriginalFilename = prepared.OriginalFileName,
            DurationSeconds = prepared.AudioInfo.DurationSeconds,
            SampleRate = prepared.AudioInfo.SampleRate,
            ChannelCount = prepared.AudioInfo.ChannelCount,
            ByteCount = prepared.AudioInfo.ByteCount,
        };
        return new SoundPackManifest
        {
            Name = name,
            Press = new SoundPackPhaseAssignments { Generic = id },
            Release = new SoundPackPhaseAssignments { Generic = id },
            Assets = new Dictionary<string, SoundPackAudioAsset>(StringComparer.Ordinal)
            {
                [id.Value] = asset,
            },
        };
    }

    private static string BundledPackRoot() => Path.Combine(
        AudioTestFiles.FindRepositoryRoot(),
        "shared",
        "soundpacks",
        "bundled");
}
