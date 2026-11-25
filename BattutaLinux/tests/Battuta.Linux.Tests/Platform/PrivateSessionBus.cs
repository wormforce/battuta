using System.Diagnostics;

namespace Battuta.Linux.Tests.Platform;

/// <summary>
/// Spawns a private <c>dbus-daemon</c> for session-bus end-to-end tests
/// (the same requirement as the Rust broker request-name e2e: a real bus,
/// not a mock). <see cref="Available"/> is false — and every test using the
/// fixture passes vacuously — when no dbus-daemon binary exists.
/// </summary>
public sealed class PrivateSessionBus : IDisposable
{
    private readonly Process? daemon;

    public PrivateSessionBus()
    {
        var dbusDaemon = FindDbusDaemon();
        if (dbusDaemon is null)
        {
            return;
        }

        var config = Path.Combine(Path.GetTempPath(), $"battuta-dbus-{Guid.NewGuid():N}.conf");
        File.WriteAllText(config, $"""
        <!DOCTYPE busconfig PUBLIC "-//freedesktop//DTD D-Bus Bus Configuration 1.0//EN"
          "http://www.freedesktop.org/standards/dbus/1.0/busconfig.dtd">
        <busconfig>
          <listen>unix:tmpdir=/tmp</listen>
          <policy context="default">
            <allow send_destination="*" eavesdrop="true"/>
            <allow eavesdrop="true"/>
            <allow own="*"/>
          </policy>
        </busconfig>
        """);
        Console.WriteLine($"[agent-test] config={config}");

        var startInfo = new ProcessStartInfo
        {
            FileName = dbusDaemon,
            // Without --fork the daemon stays in the foreground and killable;
            // --print-address=1 puts the bus address on stdout. Note: never
            // combine --session with --config-file (dbus-daemon rejects it).
            Arguments = $"--nopidfile --print-address=1 --config-file={config}",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        daemon = Process.Start(startInfo);
        var address = daemon.StandardOutput.ReadLine();
        if (string.IsNullOrWhiteSpace(address) || !address.StartsWith("unix:", StringComparison.Ordinal))
        {
            daemon.Kill();
            daemon.Dispose();
            daemon = null;
            try { File.Delete(config); } catch { }
            return;
        }

        Address = address.Trim();
        Console.WriteLine($"[agent-test] bus={Address}");
    }

    /// <summary>The private bus address, or null when the fixture is
    /// unavailable (no dbus-daemon on this machine).</summary>
    public string? Address { get; }

    public bool Available => Address is not null;

    public void Dispose()
    {
        if (daemon is not null && !daemon.HasExited)
        {
            daemon.Kill();
        }

        daemon?.Dispose();
    }

    private static string? FindDbusDaemon()
    {
        foreach (var candidate in new[] { "/usr/bin/dbus-daemon", "/bin/dbus-daemon" })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return path
            .Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory, "dbus-daemon"))
            .FirstOrDefault(File.Exists);
    }
}
