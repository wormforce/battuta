using Xunit;
using Battuta.Linux.Platform.SingleInstance;

namespace Battuta.Linux.Tests.Platform;

public sealed class SingleInstanceServiceTests : IDisposable
{
    private readonly string runtimeDir =
        Path.Combine(Path.GetTempPath(), $"battuta-single-{Guid.NewGuid():N}");

    public SingleInstanceServiceTests() => Directory.CreateDirectory(runtimeDir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(runtimeDir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private string SocketPath => Path.Combine(runtimeDir, "battuta.sock");

    [Fact]
    public async Task FirstInstanceWinsSecondReceivesActivation()
    {
        var activationCommands = new List<string>();
        await using var primary = new SingleInstanceService(SocketPath);
        primary.ActivationReceived += (_, e) => activationCommands.Add(e.Command);
        Assert.True(await primary.TryAcquireAsync());

        await using var second = new SingleInstanceService(SocketPath);
        Assert.False(await second.TryAcquireAsync("show"));

        // The activation arrives via the primary's listener loop.
        await WaitForAsync(() => activationCommands.Count >= 1);
        Assert.Equal("show", activationCommands[0]);
    }

    [Fact]
    public async Task LaterInstancesDeliverTheirActivationCommands()
    {
        var activationCommands = new List<string>();
        await using var primary = new SingleInstanceService(SocketPath);
        primary.ActivationReceived += (_, e) => activationCommands.Add(e.Command);
        Assert.True(await primary.TryAcquireAsync());

        await using (var second = new SingleInstanceService(SocketPath))
        {
            Assert.False(await second.TryAcquireAsync("show"));
        }
        Assert.True(File.Exists(SocketPath));

        await using var third = new SingleInstanceService(SocketPath);
        Assert.False(await third.TryAcquireAsync("quit"));

        await WaitForAsync(() => activationCommands.Count >= 2);
        Assert.Equal(["show", "quit"], activationCommands);
    }

    [Fact]
    public async Task StaleSocketNodeIsCleanedUp()
    {
        // A leftover file where the socket would bind (crashed process).
        await File.WriteAllTextAsync(SocketPath, "stale");

        await using var service = new SingleInstanceService(SocketPath);
        Assert.True(await service.TryAcquireAsync());
    }

    [Fact]
    public async Task DisposeRemovesSocketNode()
    {
        var service = new SingleInstanceService(SocketPath);
        Assert.True(await service.TryAcquireAsync());
        Assert.True(File.Exists(SocketPath));

        await service.DisposeAsync();
        Assert.False(File.Exists(SocketPath));

        // The socket is free for the next process.
        await using var next = new SingleInstanceService(SocketPath);
        Assert.True(await next.TryAcquireAsync());
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "condition not met within timeout");
    }
}
