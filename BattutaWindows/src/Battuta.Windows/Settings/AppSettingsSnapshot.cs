using Battuta.Core.Input;

namespace Battuta.Windows.Settings;

public enum AutomaticUpdateCheckPreference
{
    Undecided,
    Enabled,
    Disabled,
}

/// <summary>
/// Durable settings and macOS-compatible defaults. Runtime-only UI state must
/// not be added to this record.
/// </summary>
public sealed record AppSettingsSnapshot
{
    public const int CurrentSchemaVersion = 1;
    public const double DefaultKeyboardVolume = 0.42;
    public const double PointerVolumeMigrationFactor = 0.65;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public bool IsEnabled { get; init; } = true;

    public string SelectedProfileId { get; init; } = "holypanda";

    public double Volume { get; init; } = DefaultKeyboardVolume;

    public bool PlaysReleaseSound { get; init; } = true;

    public bool PlaysKeyRepeatSound { get; init; }

    public bool UsesPitchVariation { get; init; } = true;

    public bool IsPointerSoundEnabled { get; init; }

    public string SelectedPointerProfileId { get; init; } = "classic";

    public double PointerVolume { get; init; } = DefaultKeyboardVolume * PointerVolumeMigrationFactor;

    public bool PlaysPointerReleaseSound { get; init; } = true;

    public bool IsTypingStatsEnabled { get; init; }

    public bool IsLaunchAtLoginEnabled { get; init; } = true;

    public AutomaticUpdateCheckPreference AutomaticUpdateCheckPreference { get; init; }
        = AutomaticUpdateCheckPreference.Undecided;

    public bool ShouldPlayKeyboardSound(KeyPhase phase, bool isRepeat) =>
        IsEnabled && (phase == KeyPhase.Press
            ? !isRepeat || PlaysKeyRepeatSound
            : PlaysReleaseSound);

    public AppSettingsSnapshot Normalize()
    {
        var keyboardVolume = ClampFinite(Volume, DefaultKeyboardVolume);
        var pointerVolume = ClampFinite(
            PointerVolume,
            keyboardVolume * PointerVolumeMigrationFactor);

        return this with
        {
            SchemaVersion = CurrentSchemaVersion,
            SelectedProfileId = NormalizeKeyboardProfile(SelectedProfileId),
            Volume = keyboardVolume,
            SelectedPointerProfileId = NormalizePointerProfile(SelectedPointerProfileId),
            PointerVolume = pointerVolume,
            AutomaticUpdateCheckPreference = Enum.IsDefined(AutomaticUpdateCheckPreference)
                ? AutomaticUpdateCheckPreference
                : AutomaticUpdateCheckPreference.Undecided,
        };
    }

    private static double ClampFinite(double value, double fallback)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            value = fallback;
        }

        return Math.Clamp(value, 0, 1);
    }

    private static string NormalizeKeyboardProfile(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "holypanda";
        }

        var normalized = value.Trim().ToLowerInvariant();
        if (KnownKeyboardProfiles.Contains(normalized))
        {
            return normalized;
        }

        if (normalized.StartsWith("custom:", StringComparison.Ordinal)
            && Guid.TryParse(normalized["custom:".Length..], out _))
        {
            return normalized;
        }

        return "holypanda";
    }

    private static string NormalizePointerProfile(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized is not null && KnownPointerProfiles.Contains(normalized)
            ? normalized
            : "classic";
    }

    private static readonly HashSet<string> KnownKeyboardProfiles = new(StringComparer.Ordinal)
    {
        "holypanda", "mxbrown", "mxclear", "g915brown", "studiotactile",
        "mxblue", "boxnavy", "boxwhite", "lowprofileblue", "bluealps",
        "studioclicky", "cream", "alpaca", "blackink", "redink", "mxblack",
        "turquoise", "keychronred", "topre", "buckling",
    };

    private static readonly HashSet<string> KnownPointerProfiles = new(StringComparer.Ordinal)
    {
        "classic", "silent", "crisp", "heavy", "glass",
    };
}
