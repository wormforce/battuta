namespace Battuta.Engine.Input.Broker;

/// <summary>
/// Incremental frame decoder for byte-stream transports. The primary Linux
/// transport (SOCK_SEQPACKET) delivers whole frames per read and can use
/// <see cref="BrokerMessageCodec.TryDecode"/> directly; this decoder exists
/// for stream fallbacks, tests, and fuzzing.
/// </summary>
public sealed class BrokerFrameDecoder
{
    private byte[] _buffer = new byte[BrokerProtocol.MaxFrameLength];
    private int _start;
    private int _end;

    /// <summary>Number of buffered bytes not yet decoded.</summary>
    public int BufferedLength => _end - _start;

    /// <summary>Appends bytes read from the stream.</summary>
    public void Append(ReadOnlySpan<byte> bytes)
    {
        EnsureCapacity(_end - _start + bytes.Length);
        bytes.CopyTo(_buffer.AsSpan(_end));
        _end += bytes.Length;
    }

    /// <summary>
    /// Attempts to decode the next complete frame from the buffered bytes.
    /// Returns false when more bytes are needed, or when a violation was
    /// detected (then <paramref name="error"/> is set and the decoder is
    /// unusable). <paramref name="consumed"/> reports bytes of the returned
    /// frame.
    /// </summary>
    public bool TryRead(out BrokerMessage? message, out int consumed, out BrokerProtocolException? error)
    {
        message = null;
        consumed = 0;
        error = null;

        if (!BrokerMessageCodec.TryDecode(_buffer.AsSpan(_start, _end - _start), out message, out var frameLength, out error))
        {
            return false;
        }

        _start += frameLength;
        consumed = frameLength;
        Compact();
        return true;
    }

    /// <summary>Drops all buffered bytes (for resynchronization tests).</summary>
    public void Reset()
    {
        _start = 0;
        _end = 0;
    }

    private void EnsureCapacity(int required)
    {
        if (required <= _buffer.Length)
        {
            return;
        }

        // Oversized frames must be rejected, but a decoder cannot know a frame
        // is oversized until its header arrives; cap buffered growth at the
        // maximum frame size + one header, which is enough to observe any
        // legal header and its length field.
        var cap = BrokerProtocol.MaxFrameLength + BrokerProtocol.HeaderLength;
        if (required > cap)
        {
            throw new BrokerProtocolException(
                BrokerErrorCode.FrameTooLarge,
                $"Buffered {required} bytes without a complete frame; exceeds the {BrokerProtocol.MaxFrameLength}-byte frame limit.");
        }

        var grown = new byte[Math.Max(required, _buffer.Length * 2)];
        var buffered = _end - _start;
        _buffer.AsSpan(_start, buffered).CopyTo(grown);
        _buffer = grown;
        _start = 0;
        _end = buffered;
    }

    private void Compact()
    {
        if (_start == 0)
        {
            return;
        }

        _buffer.AsSpan(_start, _end - _start).CopyTo(_buffer);
        _end -= _start;
        _start = 0;
    }
}
