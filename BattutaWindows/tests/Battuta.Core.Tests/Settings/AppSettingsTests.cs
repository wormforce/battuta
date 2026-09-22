using Battuta.Core.Audio;
using Battuta.Core.Settings;

namespace Battuta.Core.Tests.Settings;

public sealed class AppSettingsTests
{
    [Fact]
    public void DefaultsMatchMacApplication()
    {
        var settings = AppSettingsDefaults.Create();

        Assert.True(settings.Enabled);
        Assert.Equal("holypanda", settings.SelectedSoundPack.Value);
        Assert.Equal(0.42, settings.KeyboardVolume, 6);
        Assert.True(settings.PlaysKeyboardReleaseSound);
        Assert.True(settings.UsesNaturalVariation);
        Assert.False(settings.PointerSoundEnabled);
        Assert.Equal(PointerSoundProfiles.Classic, settings.PointerProfile);
        Assert.Equal(0.42 * 0.65, settings.PointerVolume, 6);
        Assert.True(settings.PlaysPointerReleaseSound);
        Assert.False(settings.TypingStatsEnabled);
        Assert.True(settings.LaunchAtLoginEnabled);
    }

    [Fact]
    public void MissingPointerVolumeMigratesFromKeyboardVolumeExactlyOnce()
    {
        var first = AppSettingsMigration.Normalize(new StoredAppSettings
        {
            SchemaVersion = StoredAppSettings.CurrentSchemaVersion,
            Volume = 0.8,
            SelectedProfile = "holypanda",
            SelectedPointerProfile = "classic",
        });

        Assert.Equal(0.52, first.Settings.PointerVolume, 6);
        Assert.True(first.RequiresWriteBack);

        var changedKeyboard = first.NormalizedDocument with { Volume = 0.4 };
        var second = AppSettingsMigration.Normalize(changedKeyboard);
        Assert.Equal(0.4, second.Settings.KeyboardVolume, 6);
        Assert.Equal(0.52, second.Settings.PointerVolume, 6);
    }

    [Fact]
    public void PointerAndKeyboardVolumesAreClampedAndIndependent()
    {
        var migrated = AppSettingsMigration.Normalize(new StoredAppSettings
        {
            SchemaVersion = 1,
            Volume = 4,
            PointerVolume = -2,
            SelectedProfile = "holypanda",
            SelectedPointerProfile = "classic",
        });

        Assert.Equal(2, migrated.Settings.KeyboardVolume);
        Assert.Equal(0, migrated.Settings.PointerVolume);
        Assert.True(migrated.RequiresWriteBack);
    }

    [Theory]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void BoostedKeyboardVolumePersistsWithoutChangingPointerVolume(double volume)
    {
        var settings = AppSettingsDefaults.Create() with
        {
            KeyboardVolume = volume,
            PointerVolume = 0.35,
        };

        var stored = AppSettingsMigration.ToStored(settings);
        var loaded = AppSettingsMigration.Normalize(stored);

        Assert.Equal(volume, loaded.Settings.KeyboardVolume);
        Assert.Equal(0.35, loaded.Settings.PointerVolume);
        Assert.False(loaded.RequiresWriteBack);
    }

    [Fact]
    public void PointerVolumeCannotUseKeyboardBoostRange()
    {
        var settings = AppSettingsDefaults.Create() with
        {
            KeyboardVolume = 2,
            PointerVolume = 2,
        };

        var loaded = AppSettingsMigration.Normalize(AppSettingsMigration.ToStored(settings));

        Assert.Equal(2, loaded.Settings.KeyboardVolume);
        Assert.Equal(1, loaded.Settings.PointerVolume);
    }

    [Fact]
    public void InvalidSelectionsAreRepairedToDefaults()
    {
        var result = AppSettingsMigration.Normalize(new StoredAppSettings
        {
            SchemaVersion = 1,
            SelectedProfile = "not-a-profile",
            SelectedPointerProfile = "future-pointer",
            PointerVolume = 0.2,
        });

        Assert.Equal(SoundPackSelectionId.Default, result.Settings.SelectedSoundPack);
        Assert.Equal(PointerSoundProfiles.Classic, result.Settings.PointerProfile);
        Assert.Equal("holypanda", result.NormalizedDocument.SelectedProfile);
        Assert.Equal("classic", result.NormalizedDocument.SelectedPointerProfile);
        Assert.True(result.RequiresWriteBack);
    }

    [Fact]
    public void CustomSelectionUsesLowercaseStableWireValue()
    {
        var id = Guid.Parse("ABCDEF12-3456-7890-ABCD-EF1234567890");
        var selection = SoundPackSelectionId.FromCustom(id);

        Assert.Equal("custom:abcdef12-3456-7890-abcd-ef1234567890", selection.Value);
        Assert.Equal(SoundPackSelectionKind.Custom, selection.Kind);
        Assert.Equal(id, selection.CustomPackId);
        Assert.True(SoundPackSelectionId.TryParse(selection.Value, out var reparsed));
        Assert.Equal(selection, reparsed);
    }

    [Fact]
    public void TypingStatsRemainsExplicitOptInAcrossPersistence()
    {
        var defaults = AppSettingsDefaults.Create();
        Assert.False(defaults.TypingStatsEnabled);

        var optedIn = defaults with { TypingStatsEnabled = true };
        var reloaded = AppSettingsMigration.Normalize(AppSettingsMigration.ToStored(optedIn));
        Assert.True(reloaded.Settings.TypingStatsEnabled);
    }

    [Fact]
    public void FullyNormalizedDocumentDoesNotRequestAnotherWrite()
    {
        var normalized = AppSettingsMigration.ToStored(AppSettingsDefaults.Create());
        Assert.False(AppSettingsMigration.Normalize(normalized).RequiresWriteBack);
    }
}
