using System.Text.Json.Serialization;
using Battuta.Core.Audio;

namespace Battuta.Core.Settings;

public sealed record AppSettingsState(
    bool Enabled,
    SoundPackSelectionId SelectedSoundPack,
    double KeyboardVolume,
    bool PlaysKeyboardReleaseSound,
    bool UsesNaturalVariation,
    bool PointerSoundEnabled,
    PointerSoundProfileId PointerProfile,
    double PointerVolume,
    bool PlaysPointerReleaseSound,
    bool TypingStatsEnabled,
    bool LaunchAtLoginEnabled);

/// <summary>Versioned nullable persistence DTO used to apply defaults and migrations.</summary>
public sealed record StoredAppSettings
{
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("schemaVersion")]
    public int? SchemaVersion { get; init; }

    [JsonPropertyName("enabled")]
    public bool? Enabled { get; init; }

    [JsonPropertyName("selectedProfile")]
    public string? SelectedProfile { get; init; }

    [JsonPropertyName("volume")]
    public double? Volume { get; init; }

    [JsonPropertyName("releaseSound")]
    public bool? ReleaseSound { get; init; }

    [JsonPropertyName("pitchVariation")]
    public bool? PitchVariation { get; init; }

    [JsonPropertyName("pointerSoundEnabled")]
    public bool? PointerSoundEnabled { get; init; }

    [JsonPropertyName("selectedPointerProfile")]
    public string? SelectedPointerProfile { get; init; }

    [JsonPropertyName("pointerVolume")]
    public double? PointerVolume { get; init; }

    [JsonPropertyName("pointerReleaseSound")]
    public bool? PointerReleaseSound { get; init; }

    [JsonPropertyName("typingStatsEnabled")]
    public bool? TypingStatsEnabled { get; init; }

    [JsonPropertyName("launchAtLoginEnabled")]
    public bool? LaunchAtLoginEnabled { get; init; }
}

public sealed record AppSettingsMigrationResult(
    AppSettingsState Settings,
    StoredAppSettings NormalizedDocument,
    bool RequiresWriteBack);

public static class AppSettingsDefaults
{
    public const double KeyboardVolume = 0.42;
    public const double PointerVolumeRatio = 0.65;
    public const double MaximumKeyboardVolume = 2;
    public const double MaximumPointerVolume = 1;

    public static AppSettingsState Create() => AppSettingsMigration.Normalize(null).Settings;
}

public static class AppSettingsMigration
{
    public static AppSettingsMigrationResult Normalize(StoredAppSettings? stored)
    {
        stored ??= new StoredAppSettings();

        var keyboardVolume = ClampKeyboardVolume(stored.Volume ?? AppSettingsDefaults.KeyboardVolume);
        var seededPointerVolume = stored.PointerVolume is null;
        var pointerVolume = ClampPointerVolume(
            stored.PointerVolume ?? keyboardVolume * AppSettingsDefaults.PointerVolumeRatio);

        var selectionWasValid = SoundPackSelectionId.TryParse(
            stored.SelectedProfile,
            out var selection);
        if (!selectionWasValid)
        {
            selection = SoundPackSelectionId.Default;
        }

        var pointerProfileWasValid = PointerSoundProfileCatalog.TryGet(
            stored.SelectedPointerProfile,
            out var pointerProfile);
        if (!pointerProfileWasValid)
        {
            pointerProfile = PointerSoundProfileCatalog.Default;
        }

        var settings = new AppSettingsState(
            stored.Enabled ?? true,
            selection,
            keyboardVolume,
            stored.ReleaseSound ?? true,
            stored.PitchVariation ?? true,
            stored.PointerSoundEnabled ?? false,
            pointerProfile.Id,
            pointerVolume,
            stored.PointerReleaseSound ?? true,
            stored.TypingStatsEnabled ?? false,
            stored.LaunchAtLoginEnabled ?? true);

        var normalized = ToStored(settings);
        var requiresWriteBack = seededPointerVolume
            || !selectionWasValid
            || !string.Equals(stored.SelectedProfile, selection.Value, StringComparison.Ordinal)
            || !pointerProfileWasValid
            || !string.Equals(stored.SelectedPointerProfile, pointerProfile.Id.Value, StringComparison.Ordinal)
            || stored.SchemaVersion != StoredAppSettings.CurrentSchemaVersion
            || stored.Volume != keyboardVolume
            || stored.PointerVolume != pointerVolume
            || HasMissingValues(stored);

        return new AppSettingsMigrationResult(settings, normalized, requiresWriteBack);
    }

    public static StoredAppSettings ToStored(AppSettingsState settings) => new()
    {
        SchemaVersion = StoredAppSettings.CurrentSchemaVersion,
        Enabled = settings.Enabled,
        SelectedProfile = settings.SelectedSoundPack.Value,
        Volume = ClampKeyboardVolume(settings.KeyboardVolume),
        ReleaseSound = settings.PlaysKeyboardReleaseSound,
        PitchVariation = settings.UsesNaturalVariation,
        PointerSoundEnabled = settings.PointerSoundEnabled,
        SelectedPointerProfile = settings.PointerProfile.Value,
        PointerVolume = ClampPointerVolume(settings.PointerVolume),
        PointerReleaseSound = settings.PlaysPointerReleaseSound,
        TypingStatsEnabled = settings.TypingStatsEnabled,
        LaunchAtLoginEnabled = settings.LaunchAtLoginEnabled,
    };

    public static double ClampKeyboardVolume(double volume) =>
        ClampVolume(volume, AppSettingsDefaults.MaximumKeyboardVolume);

    public static double ClampPointerVolume(double volume) =>
        ClampVolume(volume, AppSettingsDefaults.MaximumPointerVolume);

    private static double ClampVolume(double volume, double maximum) =>
        double.IsFinite(volume) ? Math.Clamp(volume, 0, maximum) : 0;

    private static bool HasMissingValues(StoredAppSettings stored) =>
        stored.Enabled is null
        || stored.SelectedProfile is null
        || stored.Volume is null
        || stored.ReleaseSound is null
        || stored.PitchVariation is null
        || stored.PointerSoundEnabled is null
        || stored.SelectedPointerProfile is null
        || stored.PointerVolume is null
        || stored.PointerReleaseSound is null
        || stored.TypingStatsEnabled is null
        || stored.LaunchAtLoginEnabled is null;
}
