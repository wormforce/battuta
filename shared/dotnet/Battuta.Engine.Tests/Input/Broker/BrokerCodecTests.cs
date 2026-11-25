using System.Buffers.Binary;
using Battuta.Engine.Input.Broker;

namespace Battuta.Engine.Tests.Input.Broker;

/// <summary>
/// Codec behavior tests plus the golden fixture contract defined in
/// shared/contracts/LINUX_INPUT_BROKER_PROTOCOL.md. The Rust codec in
/// battuta-input-protocol must pass the same assertions.
/// </summary>
public sealed class BrokerCodecTests
{
    // ------------------------------------------------------------------
    // Roundtrips
    // ------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(AllContractMessages))]
    public void RoundTripPreservesMessage(BrokerMessage message)
    {
        var bytes = BrokerMessageCodec.Encode(message);

        Assert.True(BrokerMessageCodec.TryDecode(bytes, out var decoded, out var consumed, out var error));
        Assert.Null(error);
        Assert.Equal(bytes.Length, consumed);
        Assert.Equal(message, decoded);
    }

    public static TheoryData<BrokerMessage> AllContractMessages() => new()
    {
        BrokerTestMessages.Hello,
        BrokerTestMessages.HelloAck,
        BrokerTestMessages.KeyboardPress,
        BrokerTestMessages.KeyboardRepeat,
        BrokerTestMessages.PointerButton,
        BrokerTestMessages.Reset,
        BrokerTestMessages.Heartbeat,
        BrokerTestMessages.Error,
        new BrokerUnknownMessage(0xC8, "future-extension-data"u8.ToArray()),
    };

    [Fact]
    public void HeaderLayoutMatchesContract()
    {
        var bytes = BrokerMessageCodec.Encode(BrokerTestMessages.KeyboardPress);

        Assert.Equal(8 + 24, bytes.Length);
        Assert.Equal("BTTI"u8.ToArray(), bytes.AsSpan(0, 4).ToArray());
        Assert.Equal(BrokerProtocol.MessageTypeKeyboard, bytes[4]);
        Assert.Equal(0, bytes[5]); // flags
        Assert.Equal(24, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6, 2)));
    }

    [Fact]
    public void EncodeIntoTooSmallBufferThrows()
    {
        var message = BrokerTestMessages.KeyboardPress;
        var destination = new byte[BrokerMessageCodec.GetFrameLength(message) - 1];

        Assert.Throws<ArgumentException>(() => BrokerMessageCodec.Encode(message, destination));
    }

    [Fact]
    public void OversizedClientNameIsRejected()
    {
        var name = new string('x', BrokerProtocol.MaxClientNameLength + 1);
        Assert.Throws<ArgumentException>(() => BrokerMessageCodec.Encode(new BrokerHello(1, 0, 0, name)));
        Assert.Throws<ArgumentException>(() => BrokerMessageCodec.GetFrameLength(new BrokerHello(1, 0, 0, name)));
    }

    // ------------------------------------------------------------------
    // Golden fixtures (frozen in shared/contracts/fixtures/broker)
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("broker.v1.hello.bin", (byte)BrokerProtocol.MessageTypeHello)]
    [InlineData("broker.v1.hello-ack.bin", (byte)BrokerProtocol.MessageTypeHelloAck)]
    [InlineData("broker.v1.keyboard-press.bin", (byte)BrokerProtocol.MessageTypeKeyboard)]
    [InlineData("broker.v1.keyboard-repeat.bin", (byte)BrokerProtocol.MessageTypeKeyboard)]
    [InlineData("broker.v1.pointer-button.bin", (byte)BrokerProtocol.MessageTypePointerButton)]
    [InlineData("broker.v1.reset.bin", (byte)BrokerProtocol.MessageTypeReset)]
    [InlineData("broker.v1.heartbeat.bin", (byte)BrokerProtocol.MessageTypeHeartbeat)]
    [InlineData("broker.v1.error.bin", (byte)BrokerProtocol.MessageTypeError)]
    public void SingleFrameFixturesAreCanonical(string fileName, byte expectedType)
    {
        var bytes = BrokerTestFixtures.ReadAllBytes(fileName);

        Assert.Equal("BTTI"u8.ToArray(), bytes.AsSpan(0, 4).ToArray());
        Assert.Equal(expectedType, bytes[4]);
        Assert.True(BrokerMessageCodec.TryDecode(bytes, out var decoded, out var consumed, out var error));
        Assert.Null(error);
        Assert.Equal(bytes.Length, consumed);
        Assert.NotNull(decoded);
        // Canonical: re-encoding the decoded message reproduces the exact bytes.
        Assert.Equal(bytes, BrokerMessageCodec.Encode(decoded!));
    }

    [Fact]
    public void SessionSampleFixtureDecodesToExactTranscript()
    {
        var bytes = BrokerTestFixtures.ReadAllBytes("broker.v1.session-sample.bin");

        var decoded = new List<BrokerMessage>();
        var offset = 0;
        while (offset < bytes.Length)
        {
            Assert.True(
                BrokerMessageCodec.TryDecode(bytes.AsSpan(offset), out var message, out var consumed, out var error),
                error?.Message ?? "session-sample contained an incomplete frame");
            Assert.NotNull(message);
            decoded.Add(message!);
            offset += consumed;
        }

        Assert.Equal(BrokerTestMessages.SessionMessages, decoded);
    }

    [Fact]
    public void UnknownTypeFixtureIsSkippedAsUnknownMessage()
    {
        var bytes = BrokerTestFixtures.ReadAllBytes("broker.v1.unknown-skip.bin");

        Assert.True(BrokerMessageCodec.TryDecode(bytes, out var decoded, out _, out var error));
        Assert.Null(error);
        var unknown = Assert.IsType<BrokerUnknownMessage>(decoded);
        Assert.Equal(0xC8, unknown.Type);
        Assert.Equal("future-extension-data"u8.ToArray(), unknown.Payload);
    }

    // ------------------------------------------------------------------
    // Robustness (mirrors the Rust corpus/fuzz coverage)
    // ------------------------------------------------------------------

    [Fact]
    public void TruncatedHeaderNeedsMoreData()
    {
        Assert.False(BrokerMessageCodec.TryDecode("BTTI"u8.ToArray(), out _, out _, out var error));
        Assert.Null(error);
    }

    [Fact]
    public void TruncatedPayloadNeedsMoreData()
    {
        var bytes = BrokerMessageCodec.Encode(BrokerTestMessages.KeyboardPress);

        Assert.False(
            BrokerMessageCodec.TryDecode(bytes.AsSpan(0, bytes.Length - 1), out _, out _, out var error));
        Assert.Null(error);
    }

    [Fact]
    public void BadMagicIsAFatalError()
    {
        var bytes = BrokerMessageCodec.Encode(BrokerTestMessages.KeyboardPress);
        bytes[0] = 0x00;

        Assert.False(BrokerMessageCodec.TryDecode(bytes, out _, out _, out var error));
        Assert.Equal(BrokerErrorCode.BadMagic, error!.Code);
    }

    [Fact]
    public void OversizedPayloadLengthIsAFatalError()
    {
        var bytes = BrokerMessageCodec.Encode(BrokerTestMessages.KeyboardPress);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6, 2), BrokerProtocol.MaxPayloadLength + 1);

        Assert.False(BrokerMessageCodec.TryDecode(bytes, out _, out _, out var error));
        Assert.Equal(BrokerErrorCode.FrameTooLarge, error!.Code);
    }

    [Fact]
    public void TrailingBytesAfterFrameAreRejectedByDecodeFrame()
    {
        var bytes = BrokerMessageCodec.Encode(BrokerTestMessages.Heartbeat);

        Assert.Throws<BrokerProtocolException>(() => BrokerMessageCodec.DecodeFrame([.. bytes, 0x00]));
    }

    [Fact]
    public void NonZeroFlagsAreTolerated()
    {
        var bytes = BrokerMessageCodec.Encode(BrokerTestMessages.KeyboardPress);
        bytes[5] = 0x80;

        Assert.True(BrokerMessageCodec.TryDecode(bytes, out var decoded, out _, out var error));
        Assert.Null(error);
        Assert.Equal(BrokerTestMessages.KeyboardPress, decoded);
    }

    [Fact]
    public void UnknownEnumValuesMapToUnknownCounterparts()
    {
        // origin 3 -> Unknown, reason 8 -> Other, error code 99 -> Internal
        var keyboard = BrokerTestMessages.KeyboardPress;
        var bytes = BrokerMessageCodec.Encode(keyboard);
        bytes[8 + 20] = 3;
        Assert.True(BrokerMessageCodec.TryDecode(bytes, out var decodedKeyboard, out _, out _));
        Assert.Equal(BrokerEventOrigin.Unknown, Assert.IsType<BrokerKeyboardEvent>(decodedKeyboard).Origin);

        var resetBytes = BrokerMessageCodec.Encode(BrokerTestMessages.Reset);
        resetBytes[8 + 16] = 8;
        Assert.True(BrokerMessageCodec.TryDecode(resetBytes, out var decodedReset, out _, out _));
        Assert.Equal(BrokerResetReason.Other, Assert.IsType<BrokerReset>(decodedReset).Reason);

        var errorBytes = BrokerMessageCodec.Encode(BrokerTestMessages.Error);
        BinaryPrimitives.WriteUInt32LittleEndian(errorBytes.AsSpan(8), 99);
        Assert.True(BrokerMessageCodec.TryDecode(errorBytes, out var decodedError, out _, out _));
        Assert.Equal(BrokerErrorCode.Internal, Assert.IsType<BrokerError>(decodedError).Code);
    }

    [Fact]
    public void InvalidUtf8InNameIsRejected()
    {
        var hello = new BrokerHello(1, 0, 0, "ab");
        var bytes = BrokerMessageCodec.Encode(hello);
        // name_length=2 sits at header(8) + 8; corrupt one name byte to invalid UTF-8
        bytes[8 + 9] = 0xFF;

        Assert.False(BrokerMessageCodec.TryDecode(bytes, out _, out _, out var error));
        Assert.Equal(BrokerErrorCode.InvalidPayload, error!.Code);
    }

    [Fact]
    public void NameLengthBeyondPayloadIsRejected()
    {
        var hello = new BrokerHello(1, 0, 0, "ab");
        var bytes = BrokerMessageCodec.Encode(hello);
        bytes[8 + 8] = 200; // name_length beyond the payload

        Assert.False(BrokerMessageCodec.TryDecode(bytes, out _, out _, out var error));
        Assert.Equal(BrokerErrorCode.InvalidPayload, error!.Code);
    }

    [Fact]
    public void MinorExtensionPayloadBytesAreIgnored()
    {
        // A hypothetical minor revision appends bytes after the keyboard payload.
        var bytes = BrokerMessageCodec.Encode(BrokerTestMessages.KeyboardPress);
        var extended = new byte[bytes.Length + 4];
        bytes.CopyTo(extended, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(extended.AsSpan(6, 2), (ushort)(24 + 4));

        Assert.True(BrokerMessageCodec.TryDecode(extended, out var decoded, out var consumed, out var error));
        Assert.Null(error);
        Assert.Equal(extended.Length, consumed);
        Assert.Equal(BrokerTestMessages.KeyboardPress, decoded);
    }

    // ------------------------------------------------------------------
    // Incremental streaming decoder
    // ------------------------------------------------------------------

    [Fact]
    public void FrameDecoderReassemblesByteByByte()
    {
        var stream = BrokerTestFixtures.ReadAllBytes("broker.v1.session-sample.bin");
        var decoder = new BrokerFrameDecoder();
        var decoded = new List<BrokerMessage>();

        foreach (var b in stream)
        {
            decoder.Append([b]);
            while (decoder.TryRead(out var message, out _, out var error))
            {
                Assert.Null(error);
                Assert.NotNull(message);
                decoded.Add(message!);
            }
        }

        Assert.Equal(BrokerTestMessages.SessionMessages, decoded);
    }

    [Fact]
    public void FrameDecoderRejectsOversizedFrameHeader()
    {
        var decoder = new BrokerFrameDecoder();
        var frame = new byte[BrokerProtocol.HeaderLength + 8];
        "BTTI"u8.ToArray().CopyTo(frame, 0);
        frame[4] = BrokerProtocol.MessageTypeKeyboard;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(6, 2), BrokerProtocol.MaxPayloadLength + 1);

        decoder.Append(frame);
        Assert.False(decoder.TryRead(out _, out _, out var error));
        Assert.Equal(BrokerErrorCode.FrameTooLarge, error!.Code);
    }

    [Fact]
    public void FrameDecoderIsReusableAfterReset()
    {
        var decoder = new BrokerFrameDecoder();
        var frame = BrokerMessageCodec.Encode(BrokerTestMessages.Heartbeat);
        decoder.Append(frame[..4]); // incomplete
        decoder.Reset();

        decoder.Append(frame);
        Assert.True(decoder.TryRead(out var message, out _, out _));
        Assert.Equal(BrokerTestMessages.Heartbeat, message);
    }
}
