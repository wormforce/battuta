using System.Net.Sockets;

namespace Battuta.Linux.Platform.Net;

/// <summary>
/// Stream over an AF_UNIX SOCK_SEQPACKET socket. <see cref="NetworkStream"/>
/// refuses non-stream socket types, so the broker transport (SEQPACKET per
/// the battuta-inputd contract) needs this thin adapter: one packet per
/// Read/Write, message boundaries preserved by the socket.
/// </summary>
public sealed class SeqPacketSocketStream(
    Socket socket,
    bool ownsSocket,
    Action? onFirstReceive = null) : Stream
{
    private readonly Socket socket =
        socket ?? throw new ArgumentNullException(nameof(socket));

    private int firstReceiveSignalled;

    public override bool CanRead => socket.Connected;

    public override bool CanSeek => false;

    public override bool CanWrite => socket.Connected;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var received = await socket
            .ReceiveAsync(buffer, SocketFlags.None, cancellationToken)
            .ConfigureAwait(false);
        if (received > 0
            && onFirstReceive is not null
            && Interlocked.Exchange(ref firstReceiveSignalled, 1) == 0)
        {
            onFirstReceive();
        }

        return received;
    }

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default) =>
        await socket.SendAsync(buffer, SocketFlags.None, cancellationToken)
            .ConfigureAwait(false);

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("Use async overloads.");

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("Use async overloads.");

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override async ValueTask DisposeAsync()
    {
        if (ownsSocket)
        {
            // Shut down both directions so the peer sees EOF, then dispose.
            try
            {
                socket.Shutdown(SocketShutdown.Both);
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }

            socket.Dispose();
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }
}
