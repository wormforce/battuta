using System.Buffers.Binary;
using System.Text;

namespace Battuta.Engine.Input.Broker;

/// <summary>A fatal wire-level protocol violation. Carries the contract error code.</summary>
public sealed class BrokerProtocolException(BrokerErrorCode code, string message) : Exception(message)
{
    public BrokerErrorCode Code { get; } = code;
}

/// <summary>
/// Encoder and single-frame decoder for broker protocol v1 frames. Byte layout
/// is frozen by shared/contracts/LINUX_INPUT_BROKER_PROTOCOL.md; the Rust
/// implementation in battuta-input-protocol must stay byte-identical.
/// </summary>
public static class BrokerMessageCodec
{
    private static readonly Encoding Utf8Strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Exact encoded frame length for a message, for buffer sizing.</summary>
    public static int GetFrameLength(BrokerMessage message) => message switch
    {
        BrokerHello hello => BrokerProtocol.HeaderLength + 9 + Utf8ByteCount(hello.ClientName, BrokerProtocol.MaxClientNameLength),
        BrokerHelloAck ack => BrokerProtocol.HeaderLength + 13 + Utf8ByteCount(ack.Seat, BrokerProtocol.MaxSeatNameLength),
        BrokerKeyboardEvent => BrokerProtocol.HeaderLength + BrokerProtocol.EventPayloadLength,
        BrokerPointerButtonEvent => BrokerProtocol.HeaderLength + BrokerProtocol.EventPayloadLength,
        BrokerReset => BrokerProtocol.HeaderLength + BrokerProtocol.ResetPayloadLength,
        BrokerHeartbeat => BrokerProtocol.HeaderLength + BrokerProtocol.HeartbeatPayloadLength,
        BrokerError error => BrokerProtocol.HeaderLength + 5 + Utf8ByteCount(error.Message, BrokerProtocol.MaxDiagnosticMessageLength),
        BrokerUnknownMessage unknown => BrokerProtocol.HeaderLength + unknown.Payload.Length,
        _ => throw new ArgumentException($"Unknown broker message type: {message.GetType().Name}", nameof(message)),
    };

    /// <summary>Encodes a frame into a new array.</summary>
    public static byte[] Encode(BrokerMessage message)
    {
        var buffer = new byte[GetFrameLength(message)];
        var written = Encode(message, buffer);
        return buffer[..written];
    }

    /// <summary>Encodes one frame at the start of <paramref name="destination"/>.</summary>
    public static int Encode(BrokerMessage message, Span<byte> destination)
    {
        var required = GetFrameLength(message);
        if (destination.Length < required)
        {
            throw new ArgumentException(
                $"Destination buffer is too small: needs {required} bytes, has {destination.Length}.",
                nameof(destination));
        }

        var payloadLength = required - BrokerProtocol.HeaderLength;
        WriteHeader(destination, message.MessageType, (ushort)payloadLength);
        var payload = destination.Slice(BrokerProtocol.HeaderLength, payloadLength);

        switch (message)
        {
            case BrokerHello hello:
                BinaryPrimitives.WriteUInt16LittleEndian(payload, ValidateMajor(hello.ProtocolMajor));
                BinaryPrimitives.WriteUInt16LittleEndian(payload.Slice(2), hello.ProtocolMinor);
                BinaryPrimitives.WriteUInt32LittleEndian(payload.Slice(4), hello.Caps);
                WriteLengthPrefixedUtf8(payload.Slice(8), hello.ClientName, BrokerProtocol.MaxClientNameLength, "client name");
                break;

            case BrokerHelloAck ack:
                BinaryPrimitives.WriteUInt16LittleEndian(payload, ValidateMajor(ack.ProtocolMajor));
                BinaryPrimitives.WriteUInt16LittleEndian(payload.Slice(2), ack.ProtocolMinor);
                BinaryPrimitives.WriteUInt32LittleEndian(payload.Slice(4), ack.Caps);
                BinaryPrimitives.WriteUInt32LittleEndian(payload.Slice(8), ack.HeartbeatIntervalMilliseconds);
                WriteLengthPrefixedUtf8(payload.Slice(12), ack.Seat, BrokerProtocol.MaxSeatNameLength, "seat");
                break;

            case BrokerKeyboardEvent keyboard:
                WriteEventPayload(payload, keyboard.Sequence, keyboard.MonotonicTimestampNanoseconds, keyboard.Code, keyboard.Value, keyboard.Origin);
                break;

            case BrokerPointerButtonEvent pointer:
                WriteEventPayload(payload, pointer.Sequence, pointer.MonotonicTimestampNanoseconds, pointer.Code, pointer.Value, pointer.Origin);
                break;

            case BrokerReset reset:
                BinaryPrimitives.WriteUInt64LittleEndian(payload, reset.Sequence);
                BinaryPrimitives.WriteUInt64LittleEndian(payload.Slice(8), reset.MonotonicTimestampNanoseconds);
                payload[16] = (byte)ValidateResetReason(reset.Reason);
                payload.Slice(17, BrokerProtocol.ResetPayloadLength - 17).Clear();
                break;

            case BrokerHeartbeat heartbeat:
                BinaryPrimitives.WriteUInt64LittleEndian(payload, heartbeat.Sequence);
                BinaryPrimitives.WriteUInt64LittleEndian(payload.Slice(8), heartbeat.MonotonicTimestampNanoseconds);
                break;

            case BrokerError error:
                BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)error.Code);
                WriteLengthPrefixedUtf8(payload.Slice(4), error.Message, BrokerProtocol.MaxDiagnosticMessageLength, "error message");
                break;

            case BrokerUnknownMessage unknown:
                unknown.Payload.CopyTo(payload);
                break;

            default:
                throw new ArgumentException($"Unknown broker message type: {message.GetType().Name}", nameof(message));
        }

        return required;
    }

    /// <summary>
    /// Decodes one frame from the front of <paramref name="source"/>. Returns
    /// false with <paramref name="error"/> set when the frame is invalid;
    /// returns false with a null error when <paramref name="source"/> may
    /// merely contain an incomplete frame (streaming case).
    /// </summary>
    public static bool TryDecode(
        ReadOnlySpan<byte> source,
        out BrokerMessage? message,
        out int consumed,
        out BrokerProtocolException? error)
    {
        message = null;
        consumed = 0;
        error = null;

        if (source.Length < BrokerProtocol.HeaderLength)
        {
            return false;
        }

        if (!source[..4].SequenceEqual(BrokerProtocol.Magic))
        {
            error = new BrokerProtocolException(BrokerErrorCode.BadMagic, "Frame magic mismatch.");
            return false;
        }

        var messageType = source[4];
        var payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(6, 2));
        var frameLength = BrokerProtocol.HeaderLength + payloadLength;
        if (payloadLength > BrokerProtocol.MaxPayloadLength)
        {
            error = new BrokerProtocolException(
                BrokerErrorCode.FrameTooLarge,
                $"Payload length {payloadLength} exceeds the {BrokerProtocol.MaxPayloadLength}-byte limit.");
            return false;
        }

        if (source.Length < frameLength)
        {
            return false;
        }

        var payload = source.Slice(BrokerProtocol.HeaderLength, payloadLength);
        BrokerMessage decoded;
        try
        {
            decoded = messageType switch
            {
                BrokerProtocol.MessageTypeHello => DecodeHello(payload),
                BrokerProtocol.MessageTypeHelloAck => DecodeHelloAck(payload),
                BrokerProtocol.MessageTypeKeyboard => DecodeKeyboardEvent(payload),
                BrokerProtocol.MessageTypePointerButton => DecodePointerEvent(payload),
                BrokerProtocol.MessageTypeReset => DecodeReset(payload),
                BrokerProtocol.MessageTypeHeartbeat => DecodeHeartbeat(payload),
                BrokerProtocol.MessageTypeError => DecodeError(payload),
                _ => new BrokerUnknownMessage(messageType, payload.ToArray()),
            };
        }
        catch (BrokerProtocolException exception)
        {
            error = exception;
            return false;
        }

        message = decoded;
        consumed = frameLength;
        return true;
    }

    /// <summary>Decodes exactly one complete frame; throws on any violation.</summary>
    public static BrokerMessage DecodeFrame(ReadOnlySpan<byte> frame)
    {
        if (!TryDecode(frame, out var message, out var consumed, out var error) || message is null)
        {
            throw error ?? new BrokerProtocolException(
                BrokerErrorCode.InvalidPayload,
                $"Frame is incomplete: {frame.Length} bytes, need at least {BrokerProtocol.HeaderLength}.");
        }

        if (consumed != frame.Length)
        {
            throw new BrokerProtocolException(
                BrokerErrorCode.InvalidPayload,
                $"Trailing bytes after frame: frame is {frame.Length} bytes, decoded {consumed}.");
        }

        return message;
    }

    private static void WriteHeader(Span<byte> destination, byte messageType, ushort payloadLength)
    {
        BrokerProtocol.Magic.CopyTo(destination);
        destination[4] = messageType;
        destination[5] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(6, 2), payloadLength);
    }

    private static void WriteEventPayload(
        Span<byte> payload,
        ulong sequence,
        ulong monotonicTimestampNanoseconds,
        ushort code,
        ushort value,
        BrokerEventOrigin origin)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(payload, sequence);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.Slice(8), monotonicTimestampNanoseconds);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.Slice(16, 2), code);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.Slice(18, 2), value);
        payload[20] = (byte)origin;
        payload[21] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.Slice(22, 2), 0);
    }

    private static BrokerHello DecodeHello(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 9)
        {
            throw InvalidPayload("hello payload too short.");
        }

        return new BrokerHello(
            BinaryPrimitives.ReadUInt16LittleEndian(payload),
            BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(2)),
            BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4)),
            ReadLengthPrefixedUtf8(payload.Slice(8), BrokerProtocol.MaxClientNameLength, "client name"));
    }

    private static BrokerHelloAck DecodeHelloAck(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 13)
        {
            throw InvalidPayload("hello_ack payload too short.");
        }

        return new BrokerHelloAck(
            BinaryPrimitives.ReadUInt16LittleEndian(payload),
            BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(2)),
            BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4)),
            BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(8)),
            ReadLengthPrefixedUtf8(payload.Slice(12), BrokerProtocol.MaxSeatNameLength, "seat"));
    }

    private static BrokerKeyboardEvent DecodeKeyboardEvent(ReadOnlySpan<byte> payload)
    {
        var (sequence, monotonic, code, value, origin) = DecodeEventPayload(payload, "keyboard");
        return new BrokerKeyboardEvent(sequence, monotonic, code, value, origin);
    }

    private static BrokerPointerButtonEvent DecodePointerEvent(ReadOnlySpan<byte> payload)
    {
        var (sequence, monotonic, code, value, origin) = DecodeEventPayload(payload, "pointer_button");
        return new BrokerPointerButtonEvent(sequence, monotonic, code, value, origin);
    }

    private static (ulong Sequence, ulong Monotonic, ushort Code, ushort Value, BrokerEventOrigin Origin) DecodeEventPayload(
        ReadOnlySpan<byte> payload, string name)
    {
        if (payload.Length < BrokerProtocol.EventPayloadLength)
        {
            throw InvalidPayload($"{name} payload too short.");
        }

        return (
            BinaryPrimitives.ReadUInt64LittleEndian(payload),
            BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(8)),
            BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(16, 2)),
            BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(18, 2)),
            ValidateOrigin(payload[20]));
    }

    private static BrokerReset DecodeReset(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < BrokerProtocol.ResetPayloadLength)
        {
            throw InvalidPayload("reset payload too short.");
        }

        var reason = (BrokerResetReason)payload[16];
        if (reason is < BrokerResetReason.SequenceGap or > BrokerResetReason.Other)
        {
            reason = BrokerResetReason.Other;
        }

        return new BrokerReset(
            BinaryPrimitives.ReadUInt64LittleEndian(payload),
            BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(8)),
            reason);
    }

    private static BrokerHeartbeat DecodeHeartbeat(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < BrokerProtocol.HeartbeatPayloadLength)
        {
            throw InvalidPayload("heartbeat payload too short.");
        }

        return new BrokerHeartbeat(
            BinaryPrimitives.ReadUInt64LittleEndian(payload),
            BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(8)));
    }

    private static BrokerError DecodeError(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 5)
        {
            throw InvalidPayload("error payload too short.");
        }

        var code = (BrokerErrorCode)BinaryPrimitives.ReadUInt32LittleEndian(payload);
        if (code is < BrokerErrorCode.VersionMismatch or > BrokerErrorCode.Internal)
        {
            code = BrokerErrorCode.Internal;
        }

        return new BrokerError(code, ReadLengthPrefixedUtf8(payload.Slice(4), BrokerProtocol.MaxDiagnosticMessageLength, "error message"));
    }

    private static void WriteLengthPrefixedUtf8(Span<byte> destination, string value, int maxLength, string fieldName)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        if (byteCount > maxLength)
        {
            throw new ArgumentException($"{fieldName} exceeds {maxLength} bytes when UTF-8 encoded.", nameof(value));
        }

        destination[0] = (byte)byteCount;
        Encoding.UTF8.GetBytes(value, destination.Slice(1));
    }

    private static string ReadLengthPrefixedUtf8(ReadOnlySpan<byte> payload, int maxLength, string fieldName)
    {
        var length = payload[0];
        if (length > maxLength || payload.Length < 1 + length)
        {
            throw InvalidPayload($"{fieldName} length {length} is out of range.");
        }

        try
        {
            return Utf8Strict.GetString(payload.Slice(1, length));
        }
        catch (DecoderFallbackException exception)
        {
            throw InvalidPayload($"{fieldName} is not valid UTF-8: {exception.Message}");
        }
    }

    private static int Utf8ByteCount(string value, int maxLength)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        if (byteCount > maxLength)
        {
            throw new ArgumentException($"Value exceeds {maxLength} bytes when UTF-8 encoded.", nameof(value));
        }

        return byteCount;
    }

    private static ushort ValidateMajor(ushort major) => major == BrokerProtocol.ProtocolMajor
        ? major
        : throw new ArgumentException($"Only protocol major {BrokerProtocol.ProtocolMajor} can be encoded.", nameof(major));

    private static BrokerEventOrigin ValidateOrigin(byte value) => value <= (byte)BrokerEventOrigin.Unknown
        ? (BrokerEventOrigin)value
        : BrokerEventOrigin.Unknown;

    private static BrokerResetReason ValidateResetReason(BrokerResetReason value)
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentException($"Value {value} is not defined for BrokerResetReason.", nameof(value));
        }

        return value;
    }

    private static BrokerProtocolException InvalidPayload(string message) =>
        new(BrokerErrorCode.InvalidPayload, message);
}
