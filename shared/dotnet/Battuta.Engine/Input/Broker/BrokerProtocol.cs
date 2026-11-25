namespace Battuta.Engine.Input.Broker;

/// <summary>
/// Wire constants for the Battuta Linux input broker protocol v1, frozen by
/// shared/contracts/LINUX_INPUT_BROKER_PROTOCOL.md. Both the Rust broker and
/// the C# client must honor these exact values.
/// </summary>
public static class BrokerProtocol
{
    /// <summary>Magic bytes at the start of every frame: ASCII "BTTI".</summary>
    public static readonly byte[] Magic = [0x42, 0x54, 0x54, 0x49];

    /// <summary>Current protocol major. A mismatch is a hard error.</summary>
    public const ushort ProtocolMajor = 1;

    /// <summary>Current protocol minor. Larger minors may append payload bytes.</summary>
    public const ushort ProtocolMinor = 0;

    /// <summary>Frame header length: magic(4) + type(1) + flags(1) + payload_length(2).</summary>
    public const int HeaderLength = 8;

    /// <summary>Maximum payload length in bytes.</summary>
    public const int MaxPayloadLength = 4096;

    /// <summary>Maximum total frame length (header + payload).</summary>
    public const int MaxFrameLength = HeaderLength + MaxPayloadLength;

    public const byte MessageTypeHello = 0x01;
    public const byte MessageTypeHelloAck = 0x02;
    public const byte MessageTypeKeyboard = 0x03;
    public const byte MessageTypePointerButton = 0x04;
    public const byte MessageTypeReset = 0x05;
    public const byte MessageTypeHeartbeat = 0x06;
    public const byte MessageTypeError = 0x07;

    /// <summary>Maximum client_name length in hello.</summary>
    public const int MaxClientNameLength = 64;

    /// <summary>Maximum seat name length in hello_ack.</summary>
    public const int MaxSeatNameLength = 32;

    /// <summary>Maximum diagnostic message length in error.</summary>
    public const int MaxDiagnosticMessageLength = 128;

    /// <summary>Fixed payload length of keyboard and pointer_button.</summary>
    public const int EventPayloadLength = 24;

    /// <summary>Fixed payload length of reset (sequence + monotonic + reason + reserved).</summary>
    public const int ResetPayloadLength = 24;

    /// <summary>Fixed payload length of heartbeat (sequence + monotonic only).</summary>
    public const int HeartbeatPayloadLength = 16;

    /// <summary>evdev value for a release transition.</summary>
    public const ushort EvdevValueRelease = 0;

    /// <summary>evdev value for a press transition.</summary>
    public const ushort EvdevValuePress = 1;

    /// <summary>evdev value for an auto-repeat event.</summary>
    public const ushort EvdevValueRepeat = 2;

    /// <summary>Raw evdev BTN_* codes carried by pointer_button frames (v1 allow list).</summary>
    public const ushort EvdevBtnLeft = 0x110;
    public const ushort EvdevBtnRight = 0x111;
    public const ushort EvdevBtnMiddle = 0x112;
    public const ushort EvdevBtnSide = 0x113;
    public const ushort EvdevBtnExtra = 0x114;
}

public enum BrokerEventOrigin : byte
{
    Hardware = 0,
    Synthetic = 1,
    Unknown = 2,
}

public enum BrokerResetReason : byte
{
    SequenceGap = 1,
    DeviceChange = 2,
    SessionPause = 3,
    SessionResume = 4,
    BacklogDrop = 5,
    Reconnect = 6,
    Other = 7,
}

public enum BrokerErrorCode : uint
{
    VersionMismatch = 1,
    FrameTooLarge = 2,
    BadMagic = 3,
    Unauthorized = 4,
    Busy = 5,
    InvalidPayload = 6,
    SequenceRegression = 7,
    Internal = 8,
}
