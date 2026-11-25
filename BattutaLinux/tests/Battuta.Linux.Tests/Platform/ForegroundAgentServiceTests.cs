using System.Diagnostics;
using Battuta.Engine.Input;
using Battuta.Linux.Platform.ForegroundApps;
using Tmds.DBus.Protocol;
using Xunit;

namespace Battuta.Linux.Tests.Platform;

/// <summary>
/// Exercises the agent D-Bus service end-to-end over a private bus (same
/// requirement as the Rust broker request-name e2e: a real bus, not a mock).
/// Skipped when no dbus-daemon binary is available.
/// </summary>
public sealed class ForegroundAgentServiceTests : IDisposable
{
    private readonly PrivateSessionBus bus = new();

    public void Dispose() => bus.Dispose();

    [Fact]
    public async Task StartAsyncClaimsNameAndServesPushes()
    {
        if (!bus.Available)
        {
            // Environment-conditional e2e (mirrors the Rust broker e2e):
            // passes vacuously where dbus-daemon is unavailable.
            Console.WriteLine("[agent-test] skipped: dbus-daemon unavailable");
            return;
        }

        var cache = new ForegroundApplicationCache();
        await using var agent = new ForegroundAgentService(cache, busAddress: bus.Address);
        await agent.StartAsync();

        // A second agent cannot take the name (the GUI keeps it exclusive).
        await using var second = new ForegroundAgentService(
            new ForegroundApplicationCache(),
            busAddress: bus.Address);
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.StartAsync());

        // A foreign client pushes a typed (su) ActiveWindow call.
        var connection = new DBusConnection(bus.Address);
        await connection.ConnectAsync().AsTask();
        MessageBuffer message;
        using (var writer = connection.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(
                destination: ForegroundAgentService.BusName,
                path: ForegroundAgentService.ObjectPath,
                @interface: ForegroundAgentService.InterfaceName,
                signature: "su",
                member: "ActiveWindow");
            writer.WriteString("org.example.Editor");
            writer.WriteUInt32(4242);
            message = writer.CreateMessage();
        }

        await connection.CallMethodAsync(message, static (_, _) => (object?)null);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (cache.Current.ProcessKey != "app:org.example.editor" &&
               DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.Equal("app:org.example.editor", cache.Current.ProcessKey);
        Assert.Equal("org.example.Editor", cache.Current.DisplayName);
        Assert.Equal(4242, cache.Current.ProcessId);
        connection.Dispose();
    }

    [Fact]
    public async Task EmptyClassPublishesUnknown()
    {
        if (!bus.Available)
        {
            Console.WriteLine("[agent-test] skipped: dbus-daemon unavailable");
            return;
        }

        var cache = new ForegroundApplicationCache();
        await using var agent = new ForegroundAgentService(cache, busAddress: bus.Address);
        await agent.StartAsync();

        agent.Publish(string.Empty, 0);

        Assert.Equal(ForegroundApplicationSnapshot.Unknown, cache.Current);
    }
}
