using System.Buffers.Binary;
using System.Text;
using Battuta.Engine.Input.Broker;

namespace Battuta.Engine.Tests.Input.Broker;

/// <summary>
/// Stream double for broker client tests: the "server" side pushes scripted
/// bytes to the client and records what the client writes back. Reads return
/// the script and then a clean EOF, which drives reconnect behavior.
/// </summary>
public sealed class ScriptedServerStream : Stream
{
    private readonly byte[] _serverBytes;
    private int _position;

    public ScriptedServerStream(params BrokerMessage[] serverMessages)
        : this(serverMessages.SelectMany(BrokerMessageCodec.Encode).ToArray())
    {
    }

    public ScriptedServerStream(byte[] serverBytes)
    {
        _serverBytes = serverBytes;
        WrittenByClient = [];
    }

    public List<byte[]> WrittenByClient { get; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var remaining = _serverBytes.Length - _position;
        if (remaining == 0)
        {
            return ValueTask.FromResult(0); // clean EOF
        }

        var count = Math.Min(remaining, buffer.Length);
        _serverBytes.AsSpan(_position, count).CopyTo(buffer.Span);
        _position += count;
        return ValueTask.FromResult(count);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        WrittenByClient.Add(buffer.ToArray());
        return ValueTask.CompletedTask;
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("Use async overloads.");

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("Use async overloads.");

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>Stream that never completes a read, for heartbeat timeout tests.</summary>
public sealed class StallingStream : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return 0;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}

public static class BrokerTestMessages
{
    public static readonly BrokerHello Hello = new(1, 0, 0, "battuta-test/1.0");

    public static readonly BrokerHelloAck HelloAck = new(1, 0, 0, HeartbeatIntervalMilliseconds: 1000, Seat: "seat0");

    public static readonly BrokerKeyboardEvent KeyboardPress =
        new(Sequence: 1, MonotonicTimestampNanoseconds: 100_000_000_000, Code: 30, Value: 1, BrokerEventOrigin.Hardware);

    public static readonly BrokerKeyboardEvent KeyboardRepeat =
        new(Sequence: 42, MonotonicTimestampNanoseconds: 100_000_050_000, Code: 30, Value: 2, BrokerEventOrigin.Hardware);

    public static readonly BrokerPointerButtonEvent PointerButton =
        new(Sequence: 7, MonotonicTimestampNanoseconds: 100_000_025_000, Code: BrokerProtocol.EvdevBtnLeft, Value: 1, BrokerEventOrigin.Hardware);

    public static readonly BrokerReset Reset =
        new(Sequence: 9, MonotonicTimestampNanoseconds: 100_000_100_000, BrokerResetReason.BacklogDrop);

    public static readonly BrokerHeartbeat Heartbeat =
        new(Sequence: 12, MonotonicTimestampNanoseconds: 100_000_200_000);

    public static readonly BrokerError Error =
        new(BrokerErrorCode.Busy, "another subscriber is active");

    public static readonly BrokerKeyboardEvent KeyboardRelease =
        new(Sequence: 13, MonotonicTimestampNanoseconds: 100_000_300_000, Code: 30, Value: 0, BrokerEventOrigin.Hardware);

    /// <summary>Full session transcript matching broker.v1.session-sample.bin.</summary>
    public static readonly BrokerMessage[] SessionMessages =
    [
        Hello,
        HelloAck,
        KeyboardPress,
        KeyboardRepeat,
        PointerButton,
        Heartbeat,
        Reset,
        KeyboardRelease,
    ];
}

public static class BrokerTestFixtures
{
    /// <summary>
    /// Walks up from the test output (or working) directory to the repository
    /// root that contains shared/contracts/fixtures/broker.
    /// </summary>
    public static string PathFor(string fileName)
    {
        foreach (var root in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var directory = new DirectoryInfo(root);
            while (directory is not null
                   && !Directory.Exists(Path.Combine(directory.FullName, "shared", "contracts", "fixtures", "broker")))
            {
                directory = directory.Parent;
            }

            if (directory is not null)
            {
                return Path.Combine(
                    directory.FullName,
                    "shared",
                    "contracts",
                    "fixtures",
                    "broker",
                    fileName);
            }
        }

        throw new InvalidOperationException(
            "Could not locate shared/contracts/fixtures/broker above the test output or working directory.");
    }

    public static byte[] ReadAllBytes(string fileName) => File.ReadAllBytes(PathFor(fileName));

    public static string[] AllFixtureFileNames()
    {
        var directory = PathFor(".");
        return Directory.GetFiles(directory, "broker.v1.*.bin")
            .Select(Path.GetFileName)
            .OrderBy(name => name)
            .ToArray()!;
    }
}
