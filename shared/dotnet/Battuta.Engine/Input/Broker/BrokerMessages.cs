namespace Battuta.Engine.Input.Broker;

/// <summary>Base type of every decodable broker frame payload.</summary>
public abstract record BrokerMessage
{
    /// <summary>The wire message type byte this record encodes to.</summary>
    public abstract byte MessageType { get; }
}

public sealed record BrokerHello(
    ushort ProtocolMajor,
    ushort ProtocolMinor,
    uint Caps,
    string ClientName) : BrokerMessage
{
    public override byte MessageType => BrokerProtocol.MessageTypeHello;
}

public sealed record BrokerHelloAck(
    ushort ProtocolMajor,
    ushort ProtocolMinor,
    uint Caps,
    uint HeartbeatIntervalMilliseconds,
    string Seat) : BrokerMessage
{
    public override byte MessageType => BrokerProtocol.MessageTypeHelloAck;
}

/// <summary>Raw keyboard event. Key mapping happens client side, not on the wire.</summary>
public sealed record BrokerKeyboardEvent(
    ulong Sequence,
    ulong MonotonicTimestampNanoseconds,
    ushort Code,
    ushort Value,
    BrokerEventOrigin Origin) : BrokerMessage
{
    public override byte MessageType => BrokerProtocol.MessageTypeKeyboard;

    public bool IsPress => Value == BrokerProtocol.EvdevValuePress;

    public bool IsRelease => Value == BrokerProtocol.EvdevValueRelease;

    public bool IsRepeat => Value == BrokerProtocol.EvdevValueRepeat;
}

/// <summary>Raw pointer button event restricted to the v1 BTN_* allow list.</summary>
public sealed record BrokerPointerButtonEvent(
    ulong Sequence,
    ulong MonotonicTimestampNanoseconds,
    ushort Code,
    ushort Value,
    BrokerEventOrigin Origin) : BrokerMessage
{
    public override byte MessageType => BrokerProtocol.MessageTypePointerButton;

    public bool IsPress => Value == BrokerProtocol.EvdevValuePress;

    public bool IsRelease => Value == BrokerProtocol.EvdevValueRelease;
}

public sealed record BrokerReset(
    ulong Sequence,
    ulong MonotonicTimestampNanoseconds,
    BrokerResetReason Reason) : BrokerMessage
{
    public override byte MessageType => BrokerProtocol.MessageTypeReset;
}

public sealed record BrokerHeartbeat(
    ulong Sequence,
    ulong MonotonicTimestampNanoseconds) : BrokerMessage
{
    public override byte MessageType => BrokerProtocol.MessageTypeHeartbeat;
}

public sealed record BrokerError(
    BrokerErrorCode Code,
    string Message) : BrokerMessage
{
    public override byte MessageType => BrokerProtocol.MessageTypeError;
}

/// <summary>
/// A frame whose message type is unknown to this implementation. Per contract,
/// unknown messages are skipped after delivery of this record; re-encoding
/// reproduces the original bytes. Payload equality is by content.
/// </summary>
public sealed record BrokerUnknownMessage(byte Type, byte[] Payload) : BrokerMessage
{
    public override byte MessageType => Type;

    public bool Equals(BrokerUnknownMessage? other) =>
        other is not null && Type == other.Type && Payload.AsSpan().SequenceEqual(other.Payload);

    public override int GetHashCode() => HashCode.Combine(Type, Payload.Length);
}
