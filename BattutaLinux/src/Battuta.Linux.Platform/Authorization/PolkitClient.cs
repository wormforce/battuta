using System.Globalization;
using System.IO;
using Tmds.DBus.Protocol;

namespace Battuta.Linux.Platform.Authorization;

/// <summary>Outcome of a polkit <c>CheckAuthorization</c> call.</summary>
public enum PolkitDecision
{
    Authorized,
    /// <summary>Not authorized, but an interactive authorization could
    /// succeed — the session's polkit agent may prompt.</summary>
    Challenge,
    Denied,
}

/// <summary>Why a <c>CheckAuthorization</c> call could not even reach a
/// decision: the deployment is missing a piece, not the user's answer.</summary>
public enum PolkitRequestFailure
{
    /// <summary>The action id is unknown to polkitd — the .policy file from
    /// the Battuta system package is not installed on this machine.</summary>
    ActionNotRegistered,
    /// <summary>No authority answered on the system bus (polkitd absent).</summary>
    AuthorityUnavailable,
}

/// <summary>A deployment-shaped polkit failure, as opposed to a denial the
/// user can act on: the GUI maps <see cref="Failure"/> to guidance instead
/// of surfacing a raw D-Bus error.</summary>
public sealed class PolkitRequestException : Exception
{
    public PolkitRequestFailure Failure { get; }

    public PolkitRequestException(
        PolkitRequestFailure failure,
        string message,
        Exception? innerException = null)
        : base(message, innerException) => Failure = failure;
}

/// <summary>
/// Minimal polkit client for the GUI authorization flow (plan §5.2, broker
/// contract §8): the app checks (and, on a challenge, interactively
/// authorizes) its own process for <c>io.github.wormforce.Battuta.Input1.subscribe</c>
/// before retrying the broker handshake. The headless broker itself never
/// requests interaction — only the app does, from its own connection.
/// </summary>
public sealed class PolkitClient : IAsyncDisposable
{
    private const string Destination = "org.freedesktop.PolicyKit1";
    private const string AuthorityPath = "/org/freedesktop/PolicyKit1/Authority";
    private const string AuthorityInterface = "org.freedesktop.PolicyKit1.Authority";
    private const string SubjectKindUnixProcess = "unix-process";
    private const string PidKey = "pid";
    private const string StartTimeKey = "start-time";

    /// <summary>The action the broker requires for subscribers.</summary>
    public const string SubscribeActionId = "io.github.wormforce.Battuta.Input1.subscribe";

    private readonly DBusConnection connection;
    private bool disposed;

    private PolkitClient(DBusConnection connection) => this.connection = connection;

    /// <summary>Connects to the system bus. Honors
    /// <c>DBUS_SYSTEM_BUS_ADDRESS</c> for private-bus testing.</summary>
    public static async Task<PolkitClient> ConnectAsync(
        CancellationToken cancellationToken = default)
    {
        var address = Environment.GetEnvironmentVariable("DBUS_SYSTEM_BUS_ADDRESS");
        if (string.IsNullOrWhiteSpace(address))
        {
            address = DBusAddress.System
                ?? throw new InvalidOperationException("No system D-Bus is available.");
        }

        var connection = new DBusConnection(address);
        await connection.ConnectAsync().AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
        return new PolkitClient(connection);
    }

    /// <summary>
    /// Checks this process for the broker's subscribe action. With
    /// <paramref name="interactive"/> set, polkit may prompt through the
    /// session agent (the app's own process is the subject, so the dialog
    /// appears in the caller's session).
    /// </summary>
    public Task<PolkitDecision> CheckSubscribeAsync(
        bool interactive,
        CancellationToken cancellationToken = default) =>
        CheckAuthorizeAsync(SubscribeActionId, interactive, cancellationToken);

    /// <summary>
    /// Runs <c>CheckAuthorization</c> for a unix-process subject (this
    /// process) against <paramref name="actionId"/>. Reply shape is
    /// <c>(bba{ss})</c>: is_authorized, is_challenge, details.
    /// </summary>
    public async Task<PolkitDecision> CheckAuthorizeAsync(
        string actionId,
        bool interactive,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);

        // CheckAuthorization(subject, action_id, details, flags, cancellation_id)
        // signature (sa{sv})sa{ss}us; reply (bba{ss}).
        MessageBuffer message;
        // MessageWriter is a ref struct: build the message in a scope that
        // contains no awaits.
        using (var writer = connection.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(
                destination: Destination,
                path: AuthorityPath,
                @interface: AuthorityInterface,
                signature: "(sa{sv})sa{ss}us",
                member: "CheckAuthorization");

            // Subject: ("unix-process", {"pid": u32, "start-time": u64}).
            // polkit rejects a unix-process subject without "start-time"
            // ("Didn't find value for key 'start-time' of type 't'"): the
            // field identifies the exact process incarnation so a recycled
            // pid cannot inherit an authorization decision.
            writer.WriteStructureStart();
            writer.WriteString(SubjectKindUnixProcess);
            var subject = writer.WriteDictionaryStart();
            writer.WriteDictionaryEntryStart();
            writer.WriteString(PidKey);
            writer.WriteVariant((VariantValue)(uint)Environment.ProcessId);

            writer.WriteDictionaryEntryStart();
            writer.WriteString(StartTimeKey);
            writer.WriteVariant((VariantValue)ReadProcessStartTime(Environment.ProcessId));
            writer.WriteDictionaryEnd(subject);

            writer.WriteString(actionId);

            // Details: empty a{ss}.
            var details = writer.WriteDictionaryStart();
            writer.WriteDictionaryEnd(details);

            // Flags: bit 0 = interactive.
            writer.WriteUInt32(interactive ? 1u : 0u);
            writer.WriteString(string.Empty);
            message = writer.CreateMessage();
        }

        try
        {
            return await connection
                .CallMethodAsync(message, ReadDecision)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (DBusErrorReplyException error) when (
            ClassifyAuthorityError(error.Message) is { } failure)
        {
            throw new PolkitRequestException(failure, error.Message, error);
        }
    }

    /// <summary>
    /// Classifies a D-Bus error reply from the authority: <c>null</c> when
    /// the error says nothing about deployment (propagate as-is), otherwise
    /// the deployment failure it describes. polkit has no dedicated error
    /// name for unknown actions — it replies
    /// <c>org.freedesktop.PolicyKit1.Error.Failed</c> with prose, so the
    /// classification is text based.
    /// </summary>
    internal static PolkitRequestFailure? ClassifyAuthorityError(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return null;
        }

        if (message.Contains("is not registered", StringComparison.Ordinal))
        {
            return PolkitRequestFailure.ActionNotRegistered;
        }

        if (message.Contains(
                "org.freedesktop.DBus.Error.ServiceUnknown",
                StringComparison.Ordinal) ||
            message.Contains(
                "org.freedesktop.DBus.Error.NameHasNoOwner",
                StringComparison.Ordinal))
        {
            return PolkitRequestFailure.AuthorityUnavailable;
        }

        return null;
    }

    /// <summary>
    /// Reads the process start time polkit expects in the unix-process
    /// subject: the raw <c>starttime</c> field (field 22) of
    /// <c>/proc/&lt;pid&gt;/stat</c> — clock ticks since boot, exactly as
    /// polkitd re-reads it when validating the subject. Throws when the
    /// process entry is unreadable; callers surface that as an authorization
    /// failure instead of sending a subject polkit would refuse.
    /// </summary>
    internal static ulong ReadProcessStartTime(int pid)
    {
        if (pid <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pid));
        }

        // comm (field 2) may contain spaces and parentheses, so parsing
        // starts after the field's closing ')'; field N (N ≥ 3) is then the
        // whitespace token at index N − 3, making starttime (22) index 19.
        var content = File.ReadAllText($"/proc/{pid}/stat");
        var commEnd = content.LastIndexOf(')');
        if (commEnd < 0)
        {
            throw new IOException($"malformed /proc/{pid}/stat");
        }

        var fields = content[(commEnd + 1)..].Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length <= 19)
        {
            throw new IOException($"malformed /proc/{pid}/stat: missing starttime");
        }

        return ulong.Parse(fields[19], CultureInfo.InvariantCulture);
    }

    private static PolkitDecision ReadDecision(Message message, object? state)
    {
        var reader = message.GetBodyReader();
        reader.AlignStruct();
        var isAuthorized = reader.ReadBool();
        var isChallenge = reader.ReadBool();
        return (isAuthorized, isChallenge) switch
        {
            (true, _) => PolkitDecision.Authorized,
            (false, true) => PolkitDecision.Challenge,
            _ => PolkitDecision.Denied,
        };
    }

    public ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return ValueTask.CompletedTask;
        }

        disposed = true;
        connection.Dispose();
        return ValueTask.CompletedTask;
    }
}
