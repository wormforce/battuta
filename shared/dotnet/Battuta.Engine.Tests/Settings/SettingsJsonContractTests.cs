using System.Text.Json;
using Battuta.Windows.Settings;
using Battuta.TestSupport.IO;

namespace Battuta.Engine.Tests.Settings;

/// <summary>
/// Contract tests locking the settings JSON v1 document defined in
/// shared/contracts/SETTINGS_FORMAT.md. Fixtures live in
/// shared/contracts/fixtures/settings/ and are shared across platforms.
/// </summary>
public sealed class SettingsJsonContractTests
{
    private static readonly string[] ContractFieldNames =
    [
        "schemaVersion",
        "isEnabled",
        "selectedProfileId",
        "volume",
        "playsReleaseSound",
        "usesPitchVariation",
        "isPointerSoundEnabled",
        "selectedPointerProfileId",
        "pointerVolume",
        "playsPointerReleaseSound",
        "isTypingStatsEnabled",
        "isLaunchAtLoginEnabled",
        "automaticUpdateCheckPreference",
        "playsKeyRepeatSound",
    ];

    [Fact]
    public async Task FullFixtureLoadsToExpectedSnapshot()
    {
        var fixture = FixturePath("settings.v1.full.json");
        using var store = new JsonAppSettingsStore(fixture);

        var settings = await store.LoadAsync();

        Assert.Equal(1, settings.SchemaVersion);
        Assert.True(settings.IsEnabled);
        Assert.Equal("mxbrown", settings.SelectedProfileId);
        Assert.Equal(0.8, settings.Volume, precision: 6);
        Assert.False(settings.PlaysReleaseSound);
        Assert.False(settings.UsesPitchVariation);
        Assert.True(settings.IsPointerSoundEnabled);
        Assert.Equal("crisp", settings.SelectedPointerProfileId);
        Assert.Equal(0.52, settings.PointerVolume, precision: 6);
        Assert.False(settings.PlaysPointerReleaseSound);
        Assert.True(settings.IsTypingStatsEnabled);
        Assert.False(settings.IsLaunchAtLoginEnabled);
        Assert.Equal(AutomaticUpdateCheckPreference.Enabled, settings.AutomaticUpdateCheckPreference);
    }

    [Fact]
    public async Task SaveProducesCanonicalFixtureBytes()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        using var store = new JsonAppSettingsStore(path);
        var snapshot = new AppSettingsSnapshot
        {
            IsEnabled = true,
            SelectedProfileId = "mxbrown",
            Volume = 0.8,
            PlaysReleaseSound = false,
            UsesPitchVariation = false,
            IsPointerSoundEnabled = true,
            SelectedPointerProfileId = "crisp",
            PointerVolume = 0.52,
            PlaysPointerReleaseSound = false,
            IsTypingStatsEnabled = true,
            IsLaunchAtLoginEnabled = false,
            AutomaticUpdateCheckPreference = AutomaticUpdateCheckPreference.Enabled,
        };

        await store.SaveAsync(snapshot);

        var actual = await File.ReadAllTextAsync(path);
        var expected = await File.ReadAllTextAsync(FixturePath("settings.v1.full.json"));
        Assert.Equal(expected.TrimEnd('\r', '\n'), actual.TrimEnd('\r', '\n'));
    }

    [Fact]
    public async Task PartialLegacyFixtureAppliesDocumentedDefaults()
    {
        var fixture = FixturePath("settings.v1.partial-legacy.json");
        using var store = new JsonAppSettingsStore(fixture);

        var settings = await store.LoadAsync();

        Assert.Equal(0.8, settings.Volume, precision: 6);
        Assert.Equal("mxbrown", settings.SelectedProfileId);
        Assert.False(settings.PlaysReleaseSound);
        Assert.Equal(0.52, settings.PointerVolume, precision: 6);
        Assert.True(settings.IsEnabled);
        Assert.True(settings.UsesPitchVariation);
        Assert.False(settings.IsPointerSoundEnabled);
        Assert.Equal("classic", settings.SelectedPointerProfileId);
        Assert.True(settings.PlaysPointerReleaseSound);
        Assert.False(settings.IsTypingStatsEnabled);
        Assert.True(settings.IsLaunchAtLoginEnabled);
        Assert.Equal(AutomaticUpdateCheckPreference.Undecided, settings.AutomaticUpdateCheckPreference);
    }

    [Fact]
    public async Task UnknownFieldsAreIgnoredAndDroppedOnNextSave()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        File.Copy(FixturePath("settings.v1.unknown-fields.json"), path);
        using var store = new JsonAppSettingsStore(path);

        var settings = await store.LoadAsync();
        Assert.Equal("mxbrown", settings.SelectedProfileId);
        Assert.Equal(AutomaticUpdateCheckPreference.Enabled, settings.AutomaticUpdateCheckPreference);

        await store.SaveAsync(settings);

        var saved = await File.ReadAllTextAsync(path);
        var expected = await File.ReadAllTextAsync(FixturePath("settings.v1.full.json"));
        Assert.Equal(expected.TrimEnd('\r', '\n'), saved.TrimEnd('\r', '\n'));
    }

    [Fact]
    public async Task UnknownEnumValuePreservesFileAndFallsBackToBackup()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        var backup = path + ".bak";
        await File.WriteAllTextAsync(
            path,
            """{ "automaticUpdateCheckPreference": "weekly" }""");
        await File.WriteAllTextAsync(backup, """{ "selectedProfileId": "topre" }""");
        using var store = new JsonAppSettingsStore(path, backup);

        var settings = await store.LoadAsync();

        Assert.Equal("topre", settings.SelectedProfileId);
        Assert.Equal(AutomaticUpdateCheckPreference.Undecided, settings.AutomaticUpdateCheckPreference);
        Assert.Single(Directory.GetFiles(directory.Path, "settings.corrupt-*.json"));
    }

    [Fact]
    public async Task SavedDocumentContainsExactlyTheContractFieldsInOrder()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        using var store = new JsonAppSettingsStore(path);

        await store.SaveAsync(new AppSettingsSnapshot());

        await using var stream = File.OpenRead(path);
        var document = await JsonDocument.ParseAsync(stream);
        var names = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
        Assert.Equal(ContractFieldNames, names);
    }

    private static string FixturePath(string fileName)
    {
        foreach (var root in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var directory = new DirectoryInfo(root);
            while (directory is not null
                   && !Directory.Exists(Path.Combine(directory.FullName, "shared", "contracts", "fixtures")))
            {
                directory = directory.Parent;
            }

            if (directory is not null)
            {
                return Path.Combine(
                    directory.FullName,
                    "shared",
                    "contracts",
                    "fixtures",
                    "settings",
                    fileName);
            }
        }

        throw new InvalidOperationException(
            "Could not locate shared/contracts/fixtures above the test output or working directory.");
    }
}
