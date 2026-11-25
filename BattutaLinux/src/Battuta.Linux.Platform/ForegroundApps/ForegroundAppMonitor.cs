using Battuta.Engine.Input;
using Tmds.DBus.Protocol;

namespace Battuta.Linux.Platform.ForegroundApps;

/// <summary>Compositor adapters in plan §8.3 priority order.</summary>
public enum ForegroundAppSourceKind
{
    /// <summary>KWin scripting agent (KDE Plasma Wayland and X11).</summary>
    KWin,

    /// <summary>GNOME Shell extension (extension ships in a later PR).</summary>
    Gnome,

    /// <summary>Sway IPC.</summary>
    Sway,

    /// <summary>i3 IPC (same wire protocol as sway).</summary>
    I3,

    /// <summary>Hyprland IPC.</summary>
    Hyprland,

    /// <summary>X11 <c>_NET_ACTIVE_WINDOW</c> fallback.</summary>
    X11,

    /// <summary>No adapter matches the session; the application dimension is
    /// explicitly unknown (plan §8.3 / risk 7).</summary>
    None,
}

/// <summary>
/// Owns the single foreground-application adapter for this session. Detection
/// runs once at start: compositor-specific environment markers win (they are
/// unambiguous), then desktop bus services, then the X11 fallback, and
/// finally the explicit no-adapter state. The monitor exposes the shared
/// <see cref="ForegroundApplicationCache"/> consumed by the input pipeline —
/// adapters never participate in the per-key hot path.
/// </summary>
public sealed class ForegroundAppMonitor : IAsyncDisposable
{
    public const string GnomeShellBusName = "org.gnome.Shell";
    public const string KWinBusName = "org.kde.KWin";

    private readonly ForegroundApplicationCache cache;
    private readonly TimeProvider timeProvider;
    private readonly IReadOnlyDictionary<string, string?> environment;
    private readonly Func<IReadOnlyCollection<string>, CancellationToken, Task<IReadOnlyCollection<string>>>? busServicesProbe;
    private readonly string? kwinScriptFile;
    private readonly string? gnomeExtensionDirectory;
    private IForegroundAppSource? source;
    private ForegroundAppSourceStatus status = ForegroundAppSourceStatus.Disabled;
    private int started;
    private int disposed;

    public ForegroundAppMonitor(
        ForegroundApplicationCache? cache = null,
        TimeProvider? timeProvider = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        Func<IReadOnlyCollection<string>, CancellationToken, Task<IReadOnlyCollection<string>>>? busServicesProbe = null,
        string? kwinScriptFile = null,
        string? gnomeExtensionDirectory = null)
    {
        this.cache = cache ?? new ForegroundApplicationCache();
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.environment = environment ?? ReadProcessEnvironment();
        this.busServicesProbe = busServicesProbe;
        this.kwinScriptFile = kwinScriptFile;
        this.gnomeExtensionDirectory = gnomeExtensionDirectory;
    }

    public ForegroundApplicationCache Cache => cache;

    public IForegroundAppSource? Source => source;

    public ForegroundAppSourceStatus Status =>
        source?.Status ?? status;

    public event EventHandler? StatusChanged;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (Interlocked.Exchange(ref started, 1) != 0)
        {
            return;
        }

        var kind = await DetectAsync(cancellationToken).ConfigureAwait(false);
        IForegroundAppSource? newSource = kind switch
        {
            ForegroundAppSourceKind.KWin => CreateKWinSource(),
            ForegroundAppSourceKind.Gnome => CreateGnomeSource(),
            ForegroundAppSourceKind.Sway or ForegroundAppSourceKind.I3 =>
                new SwayForegroundAppSource(
                    cache,
                    socketPathResolver: () => EnvValue("SWAYSOCK") ?? EnvValue("I3SOCK"),
                    timeProvider: timeProvider),
            ForegroundAppSourceKind.Hyprland =>
                new HyprlandForegroundAppSource(
                    cache,
                    socketPathResolver: () => EnvValue("HYPRLAND_INSTANCE_SIGNATURE") is
                        { Length: > 0 } instance
                        ? Path.Combine(EnvValue("XDG_RUNTIME_DIR") ?? string.Empty, "hypr",
                            instance, ".socket.sock")
                        : null,
                    timeProvider: timeProvider),
            ForegroundAppSourceKind.X11 =>
                new X11ForegroundAppSource(cache, timeProvider: timeProvider),
            _ => null,
        };

        // Dispose can race the slow detection path; never leak a source that
        // was created after shutdown began.
        if (newSource is not null && Volatile.Read(ref disposed) != 0)
        {
            await newSource.DisposeAsync().ConfigureAwait(false);
            SetStatus(ForegroundAppSourceStatus.Disabled);
            return;
        }

        source = newSource;
        if (source is null)
        {
            SetStatus(new ForegroundAppSourceStatus(
                ForegroundAppSourceState.Unavailable,
                "未识别到可适配的合成器;应用维度将记录为未知"));
            return;
        }
        source.StatusChanged += SourceStatusChanged;
        await source.StartAsync(cancellationToken).ConfigureAwait(false);
        SetStatus(source.Status);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        if (source is not null)
        {
            source.StatusChanged -= SourceStatusChanged;
            await source.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Pure detection rule. Compositor-specific environment markers are
    /// unambiguous and checked first; then session desktop identities and bus
    /// services; then the X11 fallback when no Wayland session is present.
    /// </summary>
    public static ForegroundAppSourceKind Detect(
        IReadOnlyDictionary<string, string?> environment,
        bool hasKWinBusService = false,
        bool hasGnomeShellBusService = false)
    {
        if (Env(environment, "HYPRLAND_INSTANCE_SIGNATURE") is { Length: > 0 })
        {
            return ForegroundAppSourceKind.Hyprland;
        }

        if (Env(environment, "SWAYSOCK") is { Length: > 0 })
        {
            return ForegroundAppSourceKind.Sway;
        }

        if (Env(environment, "I3SOCK") is { Length: > 0 })
        {
            return ForegroundAppSourceKind.I3;
        }

        if (hasKWinBusService ||
            Env(environment, "XDG_CURRENT_DESKTOP")?
                .Split(':', StringSplitOptions.RemoveEmptyEntries)
                .Contains("KDE", StringComparer.OrdinalIgnoreCase) == true)
        {
            return ForegroundAppSourceKind.KWin;
        }

        if (hasGnomeShellBusService ||
            Env(environment, "XDG_CURRENT_DESKTOP")?
                .Split(':', StringSplitOptions.RemoveEmptyEntries)
                .Contains("GNOME", StringComparer.OrdinalIgnoreCase) == true)
        {
            return ForegroundAppSourceKind.Gnome;
        }

        if (Env(environment, "WAYLAND_DISPLAY") is { Length: > 0 })
        {
            // A Wayland session without a known adapter: statistics stay
            // functional and the application dimension is explicit unknown.
            return ForegroundAppSourceKind.None;
        }

        if (Env(environment, "DISPLAY") is { Length: > 0 })
        {
            return ForegroundAppSourceKind.X11;
        }

        return ForegroundAppSourceKind.None;
    }

    private async Task<ForegroundAppSourceKind> DetectAsync(CancellationToken cancellationToken)
    {
        string? kwinProbe = null;
        string? gnomeProbe = null;
        if (busServicesProbe is not null)
        {
            try
            {
                var services = await busServicesProbe(
                    [KWinBusName, GnomeShellBusName],
                    cancellationToken).ConfigureAwait(false);
                kwinProbe = services.FirstOrDefault(name =>
                    string.Equals(name, KWinBusName, StringComparison.Ordinal));
                gnomeProbe = services.FirstOrDefault(name =>
                    string.Equals(name, GnomeShellBusName, StringComparison.Ordinal));
            }
            catch
            {
                // Bus probing is best-effort; env markers still apply.
            }
        }

        return Detect(
            environment,
            hasKWinBusService: kwinProbe is not null,
            hasGnomeShellBusService: gnomeProbe is not null);
    }

    private KWinForegroundAppSource CreateKWinSource()
    {
        var scriptFile = kwinScriptFile
            ?? throw new InvalidOperationException(
                "The KWin agent script path was not provided by the composition root.");
        return new KWinForegroundAppSource(
            CreateAgentService(),
            scriptFile,
            timeProvider);
    }

    private GnomeForegroundAppSource CreateGnomeSource() =>
        new(CreateAgentService(), gnomeExtensionDirectory, timeProvider);

    private ForegroundAgentService CreateAgentService() =>
        new(cache);

    private static IReadOnlyDictionary<string, string?> ReadProcessEnvironment()
    {
        var entries = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
        {
            entries[(string)entry.Key] = entry.Value as string;
        }

        return entries;
    }

    private static string? Env(IReadOnlyDictionary<string, string?> environment, string name) =>
        environment.TryGetValue(name, out var value) ? value : null;

    /// <summary>Reads the monitored environment (injected in tests) so the
    /// polling sources do not reach into process state.</summary>
    private string? EnvValue(string name) => Env(environment, name);

    private void SourceStatusChanged(object? sender, EventArgs eventArgs) =>
        SetStatus(source?.Status ?? status);

    private void SetStatus(ForegroundAppSourceStatus value)
    {
        status = value;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
/// Best-effort session-bus service probe used by adapter detection in
/// production. Failure to connect means detection falls back to environment
/// markers only.
/// </summary>
public static class SessionBusServiceProbe
{
    public static async Task<IReadOnlyCollection<string>> ListServicesAsync(
        IReadOnlyCollection<string> names,
        CancellationToken cancellationToken = default)
    {
        var address = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
        if (string.IsNullOrWhiteSpace(address))
        {
            address = DBusAddress.Session
                ?? throw new InvalidOperationException("No session D-Bus is available.");
        }

        var connection = new DBusConnection(address);
        try
        {
            await connection.ConnectAsync().AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
            var services = await connection
                .ListServicesAsync()
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return services
                .Where(service => names.Contains(service, StringComparer.Ordinal))
                .ToArray();
        }
        finally
        {
            connection.Dispose();
        }
    }
}
