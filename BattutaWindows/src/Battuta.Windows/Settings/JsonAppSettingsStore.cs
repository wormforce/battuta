using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using System.IO;
using Battuta.Windows.Paths;

namespace Battuta.Windows.Settings;

/// <summary>
/// Versioned JSON settings with serialized access, atomic replacement, backup
/// recovery, and preservation of malformed input for diagnostics.
/// </summary>
public sealed class JsonAppSettingsStore : IAppSettingsStore, IDisposable
{
    private readonly string _settingsPath;
    private readonly string _backupPath;
    private readonly SemaphoreSlim _fileGate = new(1, 1);
    private readonly JsonSerializerOptions _serializerOptions;
    private bool _disposed;

    public JsonAppSettingsStore(AppPaths paths)
        : this(paths?.SettingsFile ?? throw new ArgumentNullException(nameof(paths)), paths.SettingsBackupFile)
    {
    }

    public JsonAppSettingsStore(string settingsPath, string? backupPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        _settingsPath = Path.GetFullPath(settingsPath);
        _backupPath = Path.GetFullPath(backupPath ?? settingsPath + ".bak");
        _serializerOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
        };
        _serializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    }

    public async Task<AppSettingsSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _fileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await TryLoadFileAsync(_settingsPath, cancellationToken).ConfigureAwait(false);
            if (loaded is not null)
            {
                return loaded;
            }

            if (File.Exists(_settingsPath))
            {
                PreserveCorruptSettings(_settingsPath);
            }

            loaded = await TryLoadFileAsync(_backupPath, cancellationToken).ConfigureAwait(false);
            return loaded ?? new AppSettingsSnapshot();
        }
        finally
        {
            _fileGate.Release();
        }
    }

    public async Task SaveAsync(
        AppSettingsSnapshot settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _fileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporaryPath = null;
        try
        {
            var directory = Path.GetDirectoryName(_settingsPath)
                ?? throw new InvalidOperationException("Settings path does not have a parent directory.");
            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_settingsPath)}.{Guid.NewGuid():N}.tmp");

            var normalized = settings.Normalize();
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                options: FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    StoredAppSettings.FromSnapshot(normalized),
                    _serializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_settingsPath))
            {
                File.Replace(temporaryPath, _settingsPath, _backupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, _settingsPath);
            }

            temporaryPath = null;
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                    // A failed temporary-file cleanup is harmless and can be
                    // removed during a later maintenance pass.
                }
            }

            _fileGate.Release();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _fileGate.Dispose();
    }

    private async Task<AppSettingsSnapshot?> TryLoadFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);
            var stored = await JsonSerializer.DeserializeAsync<StoredAppSettings>(
                stream,
                _serializerOptions,
                cancellationToken).ConfigureAwait(false);
            return stored?.ToSnapshot();
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static void PreserveCorruptSettings(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        var timestamp = DateTimeOffset.UtcNow.ToString(
            "yyyyMMdd-HHmmss-fff",
            CultureInfo.InvariantCulture);
        var preservedPath = Path.Combine(directory, $"settings.corrupt-{timestamp}.json");
        try
        {
            File.Move(path, preservedPath);
        }
        catch (IOException)
        {
            // Leave the original in place when another process owns it.
        }
    }

    private sealed class StoredAppSettings
    {
        public int? SchemaVersion { get; init; }
        public bool? IsEnabled { get; init; }
        public string? SelectedProfileId { get; init; }
        public double? Volume { get; init; }
        public bool? PlaysReleaseSound { get; init; }
        public bool? PlaysKeyRepeatSound { get; init; }
        public bool? UsesPitchVariation { get; init; }
        public bool? IsPointerSoundEnabled { get; init; }
        public string? SelectedPointerProfileId { get; init; }
        public double? PointerVolume { get; init; }
        public bool? PlaysPointerReleaseSound { get; init; }
        public bool? IsTypingStatsEnabled { get; init; }
        public bool? IsLaunchAtLoginEnabled { get; init; }
        public AutomaticUpdateCheckPreference? AutomaticUpdateCheckPreference { get; init; }

        public AppSettingsSnapshot ToSnapshot()
        {
            var keyboardVolume = Volume ?? AppSettingsSnapshot.DefaultKeyboardVolume;
            return new AppSettingsSnapshot
            {
                SchemaVersion = SchemaVersion ?? AppSettingsSnapshot.CurrentSchemaVersion,
                IsEnabled = IsEnabled ?? true,
                SelectedProfileId = SelectedProfileId ?? "holypanda",
                Volume = keyboardVolume,
                PlaysReleaseSound = PlaysReleaseSound ?? true,
                PlaysKeyRepeatSound = PlaysKeyRepeatSound ?? false,
                UsesPitchVariation = UsesPitchVariation ?? true,
                IsPointerSoundEnabled = IsPointerSoundEnabled ?? false,
                SelectedPointerProfileId = SelectedPointerProfileId ?? "classic",
                PointerVolume = PointerVolume
                    ?? keyboardVolume * AppSettingsSnapshot.PointerVolumeMigrationFactor,
                PlaysPointerReleaseSound = PlaysPointerReleaseSound ?? true,
                IsTypingStatsEnabled = IsTypingStatsEnabled ?? false,
                IsLaunchAtLoginEnabled = IsLaunchAtLoginEnabled ?? true,
                AutomaticUpdateCheckPreference = AutomaticUpdateCheckPreference
                    ?? global::Battuta.Windows.Settings.AutomaticUpdateCheckPreference.Undecided,
            }.Normalize();
        }

        public static StoredAppSettings FromSnapshot(AppSettingsSnapshot value)
        {
            return new StoredAppSettings
            {
                SchemaVersion = value.SchemaVersion,
                IsEnabled = value.IsEnabled,
                SelectedProfileId = value.SelectedProfileId,
                Volume = value.Volume,
                PlaysReleaseSound = value.PlaysReleaseSound,
                PlaysKeyRepeatSound = value.PlaysKeyRepeatSound,
                UsesPitchVariation = value.UsesPitchVariation,
                IsPointerSoundEnabled = value.IsPointerSoundEnabled,
                SelectedPointerProfileId = value.SelectedPointerProfileId,
                PointerVolume = value.PointerVolume,
                PlaysPointerReleaseSound = value.PlaysPointerReleaseSound,
                IsTypingStatsEnabled = value.IsTypingStatsEnabled,
                IsLaunchAtLoginEnabled = value.IsLaunchAtLoginEnabled,
                AutomaticUpdateCheckPreference = value.AutomaticUpdateCheckPreference,
            };
        }
    }
}
