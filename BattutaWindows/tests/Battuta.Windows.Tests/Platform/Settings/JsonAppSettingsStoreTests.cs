using System.IO;
using Battuta.Core.Input;
using Battuta.Windows.Settings;

namespace Battuta.Windows.Tests.Platform.Settings;

public sealed class JsonAppSettingsStoreTests
{
    [Fact]
    public async Task MissingFileReturnsMacCompatibleDefaults()
    {
        using var directory = new TestDirectory();
        using var store = new JsonAppSettingsStore(Path.Combine(directory.Path, "settings.json"));

        var settings = await store.LoadAsync();

        Assert.True(settings.IsEnabled);
        Assert.Equal("holypanda", settings.SelectedProfileId);
        Assert.Equal(0.42, settings.Volume, precision: 6);
        Assert.Equal(0.273, settings.PointerVolume, precision: 6);
        Assert.True(settings.PlaysReleaseSound);
        Assert.False(settings.PlaysKeyRepeatSound);
        Assert.True(settings.UsesPitchVariation);
        Assert.False(settings.IsPointerSoundEnabled);
        Assert.False(settings.IsTypingStatsEnabled);
        Assert.True(settings.IsLaunchAtLoginEnabled);
        Assert.Equal(AutomaticUpdateCheckPreference.Undecided, settings.AutomaticUpdateCheckPreference);
    }

    [Fact]
    public async Task LegacyFileDerivesPointerVolumeFromStoredKeyboardVolume()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        await File.WriteAllTextAsync(path, """
            {
              "schemaVersion": 1,
              "volume": 0.8,
              "selectedProfileId": "mxbrown"
            }
            """);
        using var store = new JsonAppSettingsStore(path);

        var settings = await store.LoadAsync();

        Assert.Equal(0.8, settings.Volume, precision: 6);
        Assert.Equal(0.52, settings.PointerVolume, precision: 6);
        Assert.Equal("mxbrown", settings.SelectedProfileId);
        Assert.False(settings.PlaysKeyRepeatSound);
    }

    [Fact]
    public async Task SaveNormalizesAndRoundTripsValues()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        using var store = new JsonAppSettingsStore(path);
        var source = new AppSettingsSnapshot
        {
            Volume = 4,
            PointerVolume = double.NaN,
            SelectedProfileId = "unknown",
            SelectedPointerProfileId = "GLASS",
            AutomaticUpdateCheckPreference = AutomaticUpdateCheckPreference.Enabled,
        };

        await store.SaveAsync(source);
        var loaded = await store.LoadAsync();

        Assert.Equal(1, loaded.Volume);
        Assert.Equal(0.65, loaded.PointerVolume, precision: 6);
        Assert.Equal("holypanda", loaded.SelectedProfileId);
        Assert.Equal("glass", loaded.SelectedPointerProfileId);
        Assert.Equal(AutomaticUpdateCheckPreference.Enabled, loaded.AutomaticUpdateCheckPreference);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task KeyRepeatPreferencePersists(bool enabled)
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        using var store = new JsonAppSettingsStore(path);
        await store.SaveAsync(new AppSettingsSnapshot { PlaysKeyRepeatSound = !enabled });
        await store.SaveAsync(new AppSettingsSnapshot { PlaysKeyRepeatSound = enabled });
        Assert.Equal(enabled, (await store.LoadAsync()).PlaysKeyRepeatSound);
    }

    [Fact]
    public void RepeatPlaybackHonorsPauseAndReleasePreferences()
    {
        var settings = new AppSettingsSnapshot();
        Assert.True(settings.ShouldPlayKeyboardSound(KeyPhase.Press, isRepeat: false));
        Assert.False(settings.ShouldPlayKeyboardSound(KeyPhase.Press, isRepeat: true));
        settings = settings with { PlaysKeyRepeatSound = true, PlaysReleaseSound = false };
        Assert.True(settings.ShouldPlayKeyboardSound(KeyPhase.Press, isRepeat: true));
        Assert.False(settings.ShouldPlayKeyboardSound(KeyPhase.Release, isRepeat: false));
        settings = settings with { PlaysReleaseSound = true };
        Assert.True(settings.ShouldPlayKeyboardSound(KeyPhase.Release, isRepeat: false));
        settings = settings with { IsEnabled = false };
        Assert.False(settings.ShouldPlayKeyboardSound(KeyPhase.Press, isRepeat: false));
        Assert.False(settings.ShouldPlayKeyboardSound(KeyPhase.Press, isRepeat: true));
        Assert.False(settings.ShouldPlayKeyboardSound(KeyPhase.Release, isRepeat: false));
    }

    [Fact]
    public async Task CorruptPrimaryIsPreservedAndBackupIsRecovered()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "settings.json");
        var backup = path + ".bak";
        await File.WriteAllTextAsync(path, "{not-json");
        await File.WriteAllTextAsync(backup, """
            { "selectedProfileId": "topre", "volume": 0.3 }
            """);
        using var store = new JsonAppSettingsStore(path, backup);

        var loaded = await store.LoadAsync();

        Assert.Equal("topre", loaded.SelectedProfileId);
        Assert.Single(Directory.GetFiles(directory.Path, "settings.corrupt-*.json"));
    }
}
