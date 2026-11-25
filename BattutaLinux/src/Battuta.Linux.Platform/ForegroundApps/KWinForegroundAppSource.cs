using Tmds.DBus.Protocol;

namespace Battuta.Linux.Platform.ForegroundApps;

/// <summary>
/// KDE Plasma adapter (plan §8.3 priority 1). Wayland gives clients no
/// protocol to observe other windows, so a tiny KWin script is loaded through
/// the session-bus <c>org.kde.kwin.Scripting</c> interface; the script pushes
/// the active window's resource class and PID to our
/// <see cref="ForegroundAgentService"/> on every activation. KWin restarts are
/// handled by re-loading the script when <c>org.kde.KWin</c> reappears on the
/// bus. Window titles are never read.
/// </summary>
public sealed class KWinForegroundAppSource : IForegroundAppSource
{
    public const string PluginName = "battuta-foreground-agent";

    private const string KWinDestination = "org.kde.KWin";
    private const string ScriptingPath = "/Scripting";
    private const string ScriptingInterface = "org.kde.kwin.Scripting";
    private const string FreeDesktopDestination = "org.freedesktop.DBus";
    private const string FreeDesktopPath = "/org/freedesktop/DBus";
    private const string FreeDesktopInterface = "org.freedesktop.DBus";
    private static readonly TimeSpan OwnerPollInterval = TimeSpan.FromSeconds(5);

    public const string ScriptText = $$"""
        // Battuta foreground agent ({{PluginName}}).
        // Reports the active window's resource class and PID over D-Bus.
        // Window titles are deliberately never read (plan §8.3).
        const SERVICE = "{{ForegroundAgentService.BusName}}";
        const PATH = "{{ForegroundAgentService.ObjectPath}}";
        const IFACE = "{{ForegroundAgentService.InterfaceName}}";

        function reportActiveWindow(window) {
            if (!window) {
                return;
            }
            callDBus(SERVICE, PATH, IFACE, "ActiveWindow",
                     String(window.resourceClass || ""),
                     String(window.pid || 0));
        }

        reportActiveWindow(workspace.activeWindow);
        workspace.windowActivated.connect(reportActiveWindow);
        """;

    private readonly ForegroundAgentService agent;
    private readonly string scriptFile;
    private readonly TimeProvider timeProvider;
    private readonly SemaphoreSlim loadGate = new(1, 1);
    private ITimer? ownerPollTimer;
    private bool scriptLoaded;
    private int started;
    private int disposed;

    public KWinForegroundAppSource(
        ForegroundAgentService agent,
        string scriptFile,
        TimeProvider? timeProvider = null)
    {
        this.agent = agent ?? throw new ArgumentNullException(nameof(agent));
        this.scriptFile = scriptFile;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.agent.StatusChanged += AgentStatusChanged;
    }

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

        Directory.CreateDirectory(Path.GetDirectoryName(scriptFile)!);
        await File.WriteAllTextAsync(scriptFile, ScriptText, cancellationToken)
            .ConfigureAwait(false);

        await agent.StartAsync(cancellationToken).ConfigureAwait(false);

        await TryLoadScriptAsync(cancellationToken).ConfigureAwait(false);

        ownerPollTimer = timeProvider.CreateTimer(
            OnOwnerPoll,
            null,
            OwnerPollInterval,
            OwnerPollInterval);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        ownerPollTimer?.Dispose();
        ownerPollTimer = null;
        agent.StatusChanged -= AgentStatusChanged;
        await agent.DisposeAsync().ConfigureAwait(false);
        loadGate.Dispose();
    }

    private async void OnOwnerPoll(object? state)
    {
        try
        {
            await TryLoadScriptAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Polling errors stay silent; the status line reports the outcome.
        }
    }

    private async Task TryLoadScriptAsync(CancellationToken cancellationToken)
    {
        await loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var hasOwner = await CallNameHasOwnerAsync(cancellationToken).ConfigureAwait(false);
            if (!hasOwner)
            {
                if (scriptLoaded)
                {
                    scriptLoaded = false;
                    SetStatus(ForegroundAppSourceState.Waiting, "KWin 正在重启,等待重新装载脚本");
                }

                return;
            }

            if (scriptLoaded)
            {
                return;
            }

            await UnloadStaleScriptAsync(cancellationToken).ConfigureAwait(false);
            await LoadScriptAsync(cancellationToken).ConfigureAwait(false);
            scriptLoaded = true;
            SetStatus(ForegroundAppSourceState.Running, $"KWin agent 脚本已装载({PluginName})");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            scriptLoaded = false;
            SetStatus(ForegroundAppSourceState.Failed, $"KWin 脚本装载失败:{error.Message}");
        }
        finally
        {
            _ = loadGate.Release();
        }
    }

    private Task UnloadStaleScriptAsync(CancellationToken cancellationToken) =>
        CallAsync(
            KWinDestination,
            ScriptingPath,
            ScriptingInterface,
            "unloadScript",
            "s",
            new CallBody.String(PluginName),
            ReadBoolReply,
            cancellationToken);

    private async Task LoadScriptAsync(CancellationToken cancellationToken)
    {
        await CallAsync(
            KWinDestination,
            ScriptingPath,
            ScriptingInterface,
            "loadScript",
            "ss",
            new CallBody.TwoStrings(scriptFile, PluginName),
            ReadInt32Reply,
            cancellationToken).ConfigureAwait(false);
        await CallAsync(
            KWinDestination,
            ScriptingPath,
            ScriptingInterface,
            "start",
            string.Empty,
            CallBody.Empty.Instance,
            ReadEmptyReply,
            cancellationToken).ConfigureAwait(false);
    }

    private Task<bool> CallNameHasOwnerAsync(CancellationToken cancellationToken) =>
        CallAsync(
            FreeDesktopDestination,
            FreeDesktopPath,
            FreeDesktopInterface,
            "NameHasOwner",
            "s",
            new CallBody.String(KWinDestination),
            ReadBoolReply,
            cancellationToken);

    /// <summary>
    /// Message bodies are written through a struct passed by reference.
    /// <see cref="MessageWriter"/> is a mutable ref struct: handing it to an
    /// <c>Action&lt;MessageWriter&gt;</c> delegate would copy it, the body
    /// writes would advance the copy's cursor, and the serialized message
    /// would carry a stale body length — the bus broker kills the connection
    /// for that malformed frame.
    /// </summary>
    private interface ICallBody
    {
        void Write(ref MessageWriter writer);
    }

    private sealed class CallBody
    {
        public readonly struct Empty : ICallBody
        {
            public static readonly Empty Instance = new();

            public void Write(ref MessageWriter writer)
            {
            }
        }

        public readonly struct String(string value) : ICallBody
        {
            public void Write(ref MessageWriter writer) => writer.WriteString(value);
        }

        public readonly struct TwoStrings(string first, string second) : ICallBody
        {
            public void Write(ref MessageWriter writer)
            {
                writer.WriteString(first);
                writer.WriteString(second);
            }
        }
    }

    private async Task<T> CallAsync<T>(
        string destination,
        string path,
        string @interface,
        string member,
        string signature,
        ICallBody body,
        MessageValueReader<T> readReply,
        CancellationToken cancellationToken)
    {
        var connection = agent.Connection
            ?? throw new InvalidOperationException("The agent D-Bus connection is not started.");
        MessageBuffer message;
        // A using-variable cannot be passed by ref, so dispose manually.
        var writer = connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(
                destination: destination,
                path: path,
                @interface: @interface,
                signature: signature,
                member: member);
            body.Write(ref writer);
            message = writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }

        return await connection
            .CallMethodAsync(message, readReply)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static readonly MessageValueReader<object?> ReadEmptyReply =
        (_, _) => null;

    private static readonly MessageValueReader<bool> ReadBoolReply =
        (message, _) => message.GetBodyReader().ReadBool();

    private static readonly MessageValueReader<int> ReadInt32Reply =
        (message, _) => message.GetBodyReader().ReadInt32();

    private void AgentStatusChanged(object? sender, EventArgs eventArgs) =>
        SetStatus(agent.Status);

    private void SetStatus(ForegroundAppSourceStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetStatus(ForegroundAppSourceState state, string detail) =>
        SetStatus(new ForegroundAppSourceStatus(state, detail));
}
