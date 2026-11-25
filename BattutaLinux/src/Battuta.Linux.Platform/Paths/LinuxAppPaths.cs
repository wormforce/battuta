using System.IO;

namespace Battuta.Linux.Platform.Paths;

/// <summary>
/// XDG base-directory layout for all mutable Battuta data (plan §8.1).
/// Settings live under the config root, statistics and sound packs under the
/// data root, logs under the state root, and temporary files under the cache
/// root. Missing environment variables fall back to the XDG defaults;
/// Windows LocalAppData and macOS Application Support are never read.
/// </summary>
public sealed class LinuxAppPaths
{
    public const string ProductDirectoryName = "battuta";

    public LinuxAppPaths(
        string configRoot,
        string dataRoot,
        string stateRoot,
        string cacheRoot,
        string? runtimeRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);

        ConfigRoot = Path.GetFullPath(configRoot);
        DataRoot = Path.GetFullPath(dataRoot);
        StateRoot = Path.GetFullPath(stateRoot);
        CacheRoot = Path.GetFullPath(cacheRoot);
        RuntimeRoot = string.IsNullOrWhiteSpace(runtimeRoot)
            ? null
            : Path.GetFullPath(runtimeRoot);
    }

    /// <summary>$XDG_CONFIG_HOME (or ~/.config).</summary>
    public string ConfigRoot { get; }

    /// <summary>$XDG_DATA_HOME (or ~/.local/share).</summary>
    public string DataRoot { get; }

    /// <summary>$XDG_STATE_HOME (or ~/.local/state).</summary>
    public string StateRoot { get; }

    /// <summary>$XDG_CACHE_HOME (or ~/.cache).</summary>
    public string CacheRoot { get; }

    /// <summary>$XDG_RUNTIME_DIR; null when unavailable (no single-instance
    /// socket in that case — the fallback is a cache-root socket).</summary>
    public string? RuntimeRoot { get; }

    public string SettingsFile => Path.Combine(ConfigRoot, ProductDirectoryName, "settings.json");

    public string SettingsBackupFile => Path.Combine(ConfigRoot, ProductDirectoryName, "settings.json.bak");

    public string StatisticsDatabaseFile =>
        Path.Combine(DataRoot, ProductDirectoryName, "typing-stats.sqlite3");

    public string SoundPacksDirectory =>
        Path.Combine(DataRoot, ProductDirectoryName, "SoundPacks");

    /// <summary>
    /// The KWin scripting agent pushed to <c>org.kde.kwin.Scripting</c> for
    /// foreground-application identity (plan §8.3). Lives under the XDG data
    /// root so KWin can always read it while the app owns the content.
    /// </summary>
    public string KwinForegroundAgentScriptFile =>
        Path.Combine(DataRoot, ProductDirectoryName, "kwin", "foreground-agent.js");

    /// <summary>
    /// The GNOME Shell extension directory the app writes for foreground
    /// application identity (plan §8.3). Under the XDG data root — GNOME
    /// scans <c>~/.local/share/gnome-shell/extensions</c>, and the app keeps
    /// the copy version-matched; enabling it stays a user action.
    /// </summary>
    public string GnomeForegroundAgentExtensionDirectory =>
        Path.Combine(
            DataRoot,
            ProductDirectoryName,
            "gnome-shell",
            "extensions",
            "io.github.wormforce.Battuta@wormforce.github.io");

    public string LogsDirectory => Path.Combine(StateRoot, ProductDirectoryName, "Logs");

    public string TemporaryDirectory => Path.Combine(CacheRoot, ProductDirectoryName, "Temp");

    /// <summary>
    /// The single-instance activation socket. Prefers the runtime root
    /// (cleaned on logout); falls back to a per-user cache directory so the
    /// feature still works when <c>XDG_RUNTIME_DIR</c> is unset.
    /// </summary>
    public string SingleInstanceSocket
    {
        get
        {
            var root = RuntimeRoot ?? Path.Combine(CacheRoot, ProductDirectoryName + "-runtime");
            return Path.Combine(root, ProductDirectoryName, "app.sock");
        }
    }

    /// <summary>Builds the paths from the process environment.</summary>
    public static LinuxAppPaths ForCurrentProcess()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
        {
            throw new InvalidOperationException("The user home directory is unavailable.");
        }

        return new LinuxAppPaths(
            configRoot: Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
                ?? Path.Combine(home, ".config"),
            dataRoot: Environment.GetEnvironmentVariable("XDG_DATA_HOME")
                ?? Path.Combine(home, ".local", "share"),
            stateRoot: Environment.GetEnvironmentVariable("XDG_STATE_HOME")
                ?? Path.Combine(home, ".local", "state"),
            cacheRoot: Environment.GetEnvironmentVariable("XDG_CACHE_HOME")
                ?? Path.Combine(home, ".cache"),
            runtimeRoot: Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"));
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
        Directory.CreateDirectory(SoundPacksDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(TemporaryDirectory);
        var socketDirectory = Path.GetDirectoryName(SingleInstanceSocket);
        if (!string.IsNullOrEmpty(socketDirectory))
        {
            Directory.CreateDirectory(socketDirectory);
        }
    }
}
