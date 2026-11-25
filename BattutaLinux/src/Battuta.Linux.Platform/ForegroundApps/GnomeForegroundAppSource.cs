using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Battuta.Linux.Platform.ForegroundApps;

/// <summary>
/// GNOME adapter (plan §8.3 priority 2). Wayland gives clients no protocol
/// to observe other windows, so a small GNOME Shell extension pushes the
/// focused window's application identity to our
/// <see cref="ForegroundAgentService"/> with the same <c>ActiveWindow</c>
/// contract the KWin script uses. The extension text is embedded here and
/// written to the user's <c>~/.local/share/gnome-shell/extensions</c>
/// search path at startup — the same pattern as the KWin script — so a
/// packaged or dev-run app always ships a matching version. Enabling it
/// stays a user action (plan §7.2: no silently-installed shell add-ons);
/// until it is enabled the source reports <c>Waiting</c> with the exact
/// command, and statistics keep the application dimension at
/// <c>unknown</c>. Window titles are never read.
/// </summary>
public sealed class GnomeForegroundAppSource : IForegroundAppSource
{
    /// <summary>GNOME extension UUID (reverse-DNS, matching the app id).</summary>
    public const string ExtensionUuid = "io.github.wormforce.Battuta@wormforce.github.io";

    public const string ExtensionMetadata = """
        {
          "uuid": "io.github.wormforce.Battuta@wormforce.github.io",
          "name": "Battuta foreground agent",
          "description": "Pushes the focused window's application identity to the Battuta statistics agent. Never reads window titles.",
          "url": "https://github.com/wormforce/battuta",
          "shell-version": ["45", "46", "47", "48", "49", "50"]
        }
        """;

    public const string ExtensionScript = $$"""
        // Battuta foreground agent (GNOME Shell extension, plan §8.3).
        // Pushes the focused window's application identity to the Battuta
        // agent D-Bus service on every focus change. Window titles are
        // deliberately never read.
        import GLib from 'gi://GLib';
        import Gio from 'gi://Gio';

        const SERVICE = '{{ForegroundAgentService.BusName}}';
        const PATH = '{{ForegroundAgentService.ObjectPath}}';
        const IFACE = '{{ForegroundAgentService.InterfaceName}}';
        const CALL_TIMEOUT_MS = 500;

        export default class BattutaForegroundAgentExtension {
            enable() {
                this._focusConnectionId = global.display.connect(
                    'notify::focus-window', () => this._pushFocusedWindow());
                // One immediate push so identities converge with the window
                // that was focused before the extension got enabled.
                this._pushFocusedWindow();
            }

            disable() {
                if (this._focusConnectionId) {
                    global.display.disconnect(this._focusConnectionId);
                    this._focusConnectionId = 0;
                }
                // The application dimension stays honest about the gap:
                // nothing is tracked while the extension is disabled.
                this._callAgent('', 0);
            }

            _pushFocusedWindow() {
                const window = global.display.focus_window;
                if (!window) {
                    // Desktop shell focused: no application owns the input.
                    this._callAgent('', 0);
                    return;
                }

                let applicationClass = '';
                try {
                    // GTK/Wayland app id first, X11 WM_CLASS as fallback.
                    applicationClass = String(
                        window.get_gtk_application_id?.() ||
                        window.get_wm_class?.() ||
                        '');
                } catch {
                    applicationClass = '';
                }

                let pid = 0;
                try {
                    pid = Math.max(0, window.get_pid?.() ?? 0);
                } catch {
                    pid = 0;
                }

                this._callAgent(applicationClass, pid);
            }

            _callAgent(applicationClass, pid) {
                try {
                    // Fire-and-forget: Battuta may not be running, and the
                    // agent restarts transparently on the next focus change.
                    Gio.DBus.session.call(
                        SERVICE, PATH, IFACE, 'ActiveWindow',
                        new GLib.Variant('(su)', [applicationClass, pid]),
                        null,
                        Gio.DBusCallFlags.NONE,
                        CALL_TIMEOUT_MS,
                        null,
                        (_sourceObject, result) => {
                            try {
                                _sourceObject.call_finish(result);
                            } catch {
                                // Agent absent (Battuta closed) or restarting.
                            }
                        });
                } catch {
                    // The shell is shutting down mid-call; nothing to do.
                }
            }
        }
        """;

    private static readonly TimeSpan DefaultRescanInterval = TimeSpan.FromSeconds(10);
    private static readonly Regex EnabledExtensionRegex = new("\"([^\"]+@[^\"]+)\"", RegexOptions.Compiled);

    private readonly ForegroundAgentService agent;
    private readonly string? extensionDirectory;
    private readonly Func<CancellationToken, Task<IReadOnlyCollection<string>>> enabledExtensionsProbe;
    private readonly TimeProvider timeProvider;
    private ITimer? rescanTimer;
    private int started;
    private int disposed;

    public GnomeForegroundAppSource(
        ForegroundAgentService agent,
        string? extensionDirectory = null,
        TimeProvider? timeProvider = null,
        Func<CancellationToken, Task<IReadOnlyCollection<string>>>? enabledExtensionsProbe = null)
    {
        this.agent = agent ?? throw new ArgumentNullException(nameof(agent));
        this.extensionDirectory = extensionDirectory;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.enabledExtensionsProbe =
            enabledExtensionsProbe ?? ReadEnabledExtensionsAsync;
        this.agent.StatusChanged += AgentStatusChanged;
    }

    /// <summary>How often the enablement state is re-checked (the user may
    /// enable or disable the extension at any time). Tests shrink this.</summary>
    public TimeSpan RescanInterval { get; init; } = DefaultRescanInterval;

    public ForegroundAppSourceStatus Status { get; private set; } =
        new(ForegroundAppSourceState.Waiting, "正在连接会话总线…");

    public event EventHandler? StatusChanged;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (Interlocked.Exchange(ref started, 1) != 0)
        {
            return;
        }

        await agent.StartAsync(cancellationToken).ConfigureAwait(false);
        await InstallAsync(cancellationToken).ConfigureAwait(false);
        await RescanAsync(cancellationToken).ConfigureAwait(false);

        rescanTimer = timeProvider.CreateTimer(
            OnRescan,
            null,
            RescanInterval,
            RescanInterval);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        rescanTimer?.Dispose();
        rescanTimer = null;
        agent.StatusChanged -= AgentStatusChanged;
        await agent.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Writes the extension into the user's GNOME extension search
    /// path (<c>$XDG_DATA_HOME/battuta/gnome-shell/extensions/&lt;uuid&gt;</c>
    /// via the injected directory). GNOME picks user extensions up on the
    /// next shell start; a failure only degrades the application dimension
    /// to unknown, it never blocks the app.</summary>
    private async Task InstallAsync(CancellationToken cancellationToken)
    {
        if (extensionDirectory is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(extensionDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(extensionDirectory, "metadata.json"),
                ExtensionMetadata,
                cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Combine(extensionDirectory, "extension.js"),
                ExtensionScript,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            SetStatus(ForegroundAppSourceState.NeedsExtension,
                $"无法写入 GNOME Shell 扩展目录:{error.Message};应用维度将记录为未知");
        }
    }

    private bool IsInstalled()
    {
        if (extensionDirectory is null)
        {
            return false;
        }

        return File.Exists(Path.Combine(extensionDirectory, "metadata.json")) &&
               File.Exists(Path.Combine(extensionDirectory, "extension.js"));
    }

    private async void OnRescan(object? state)
    {
        try
        {
            await RescanAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Probe errors stay silent; the status line reports the outcome.
        }
    }

    private async Task RescanAsync(CancellationToken cancellationToken)
    {
        if (agent.Status.State == ForegroundAppSourceState.Failed)
        {
            SetStatus(agent.Status);
            return;
        }

        if (!IsInstalled())
        {
            SetStatus(
                ForegroundAppSourceState.NeedsExtension,
                "GNOME Shell 扩展未安装;应用维度将记录为未知");
            return;
        }

        var enabled = (await enabledExtensionsProbe(cancellationToken)
            .ConfigureAwait(false)).Contains(ExtensionUuid, StringComparer.Ordinal);
        if (enabled)
        {
            SetStatus(
                ForegroundAppSourceState.Running,
                "GNOME Shell 扩展已启用,正在推送前台窗口标识");
        }
        else
        {
            SetStatus(
                ForegroundAppSourceState.Waiting,
                $"扩展已安装但未启用:运行 gnome-extensions enable {ExtensionUuid}" +
                "(Wayland 下需注销后重新登录生效)");
        }
    }

    /// <summary>Reads GNOME's enabled-extension list via gsettings. A missing
    /// binary or a non-GNOME session yields an empty list, which keeps the
    /// source in Waiting with actionable guidance.</summary>
    private static async Task<IReadOnlyCollection<string>> ReadEnabledExtensionsAsync(
        CancellationToken cancellationToken)
    {
        using var process = Process.Start(new ProcessStartInfo(
            "gsettings",
            "get org.gnome.shell enabled-extensions")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        });
        if (process is null)
        {
            return [];
        }

        var output = await process.StandardOutput
            .ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            return [];
        }

        return [.. EnabledExtensionRegex.Matches(output)
            .Select(match => match.Groups[1].Value)];
    }

    private void AgentStatusChanged(object? sender, EventArgs eventArgs)
    {
        // Transport failures surface immediately; the extension probe decides
        // everything else.
        if (agent.Status.State == ForegroundAppSourceState.Failed)
        {
            SetStatus(agent.Status);
        }
    }

    private void SetStatus(ForegroundAppSourceStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetStatus(ForegroundAppSourceState state, string detail) =>
        SetStatus(new ForegroundAppSourceStatus(state, detail));
}
