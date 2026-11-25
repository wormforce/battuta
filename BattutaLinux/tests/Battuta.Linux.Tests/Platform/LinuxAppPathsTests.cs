using Xunit;
using Battuta.Linux.Platform.Paths;

namespace Battuta.Linux.Tests.Platform;

public sealed class LinuxAppPathsTests : IDisposable
{
    private readonly string tempRoot;
    private readonly Dictionary<string, string?> savedEnvironment = [];

    public LinuxAppPathsTests()
    {
        tempRoot = Path.Combine(Path.GetTempPath(), $"battuta-paths-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Save("XDG_CONFIG_HOME");
        Save("XDG_DATA_HOME");
        Save("XDG_STATE_HOME");
        Save("XDG_CACHE_HOME");
        Save("XDG_RUNTIME_DIR");
        Save("HOME");
    }

    public void Dispose()
    {
        foreach (var (name, value) in savedEnvironment)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        try
        {
            Directory.Delete(tempRoot, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private void Save(string name) =>
        savedEnvironment[name] = Environment.GetEnvironmentVariable(name);

    private string Set(string name, string? value)
    {
        var path = Path.Combine(tempRoot, name.ToLowerInvariant());
        if (value is not null)
        {
            Directory.CreateDirectory(path);
            Environment.SetEnvironmentVariable(name, path);
        }
        else
        {
            Environment.SetEnvironmentVariable(name, null);
        }

        return path;
    }

    [Fact]
    public void ResolvesXdgRootsFromEnvironment()
    {
        var configRoot = Set("XDG_CONFIG_HOME", "config");
        var dataRoot = Set("XDG_DATA_HOME", "data");
        var stateRoot = Set("XDG_STATE_HOME", "state");
        var cacheRoot = Set("XDG_CACHE_HOME", "cache");
        var runtimeRoot = Set("XDG_RUNTIME_DIR", "runtime");

        var paths = LinuxAppPaths.ForCurrentProcess();

        Assert.StartsWith(configRoot, paths.SettingsFile, StringComparison.Ordinal);
        Assert.EndsWith("settings.json", paths.SettingsFile, StringComparison.Ordinal);
        Assert.EndsWith("settings.json.bak", paths.SettingsBackupFile, StringComparison.Ordinal);
        Assert.StartsWith(dataRoot, paths.StatisticsDatabaseFile, StringComparison.Ordinal);
        Assert.EndsWith("typing-stats.sqlite3", paths.StatisticsDatabaseFile, StringComparison.Ordinal);
        Assert.StartsWith(stateRoot, paths.LogsDirectory, StringComparison.Ordinal);
        Assert.StartsWith(cacheRoot, paths.TemporaryDirectory, StringComparison.Ordinal);
        Assert.StartsWith(runtimeRoot, paths.SingleInstanceSocket, StringComparison.Ordinal);
    }

    [Fact]
    public void FallsBackToHomeWhenXdgUnset()
    {
        Set("XDG_CONFIG_HOME", null);
        Set("XDG_DATA_HOME", null);
        Set("XDG_STATE_HOME", null);
        Set("XDG_CACHE_HOME", null);
        Set("XDG_RUNTIME_DIR", null);
        var home = Set("HOME", tempRoot);

        var paths = LinuxAppPaths.ForCurrentProcess();

        Assert.Equal(
            Path.Combine(home, ".config", "battuta", "settings.json"),
            paths.SettingsFile);
        Assert.Equal(
            Path.Combine(home, ".local", "share", "battuta", "typing-stats.sqlite3"),
            paths.StatisticsDatabaseFile);
        Assert.Equal(
            Path.Combine(home, ".local", "state", "battuta", "Logs"),
            paths.LogsDirectory);
        Assert.Equal(
            Path.Combine(home, ".cache", "battuta-runtime", "battuta"),
            Path.GetDirectoryName(paths.SingleInstanceSocket));
    }

    [Fact]
    public void EnsureCreatedMakesAllDirectories()
    {
        Set("XDG_CONFIG_HOME", "config");
        Set("XDG_DATA_HOME", "data");
        Set("XDG_STATE_HOME", "state");
        Set("XDG_CACHE_HOME", "cache");
        Set("XDG_RUNTIME_DIR", null);

        var paths = LinuxAppPaths.ForCurrentProcess();
        paths.EnsureCreated();

        Assert.True(Directory.Exists(Path.GetDirectoryName(paths.SettingsFile)));
        Assert.True(Directory.Exists(Path.GetDirectoryName(paths.StatisticsDatabaseFile)));
        Assert.True(Directory.Exists(paths.LogsDirectory));
        Assert.True(Directory.Exists(paths.TemporaryDirectory));
        Assert.True(Directory.Exists(Path.GetDirectoryName(paths.SingleInstanceSocket)));
        // A second call is idempotent.
        paths.EnsureCreated();
    }
}
