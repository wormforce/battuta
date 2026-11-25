using System.Text;
using System.Text.Json;
using Battuta.Linux.Platform.ForegroundApps;
using Xunit;

namespace Battuta.Linux.Tests.Platform;

public sealed class SwayIpcTests
{
    [Fact]
    public void FormatMessageUsesI3WireLayout()
    {
        var frame = SwayIpc.FormatMessage(SwayIpc.MessageType.GetTree, "payload");

        var magic = Encoding.ASCII.GetString(frame, 0, 6);
        Assert.Equal("i3-ipc", magic);
        var length = BitConverter.ToUInt32(frame, 6);
        Assert.Equal((uint)7, length);
        var type = BitConverter.ToUInt32(frame, 10);
        Assert.Equal((uint)SwayIpc.MessageType.GetTree, type);
        Assert.Equal("payload", Encoding.UTF8.GetString(frame, 14, 7));
        Assert.Equal(SwayIpc.HeaderLength + 7, frame.Length);
    }

    [Fact]
    public void TryParseMessageRoundTrips()
    {
        var frame = SwayIpc.FormatMessage(SwayIpc.MessageType.GetVersion, "{\"v\":1}");

        var parsed = SwayIpc.TryParseMessage(
            frame,
            out var type,
            out var payload,
            out var consumed);

        Assert.True(parsed);
        Assert.Equal(SwayIpc.MessageType.GetVersion, type);
        Assert.Equal("{\"v\":1}", payload);
        Assert.Equal(frame.Length, consumed);
    }

    [Fact]
    public void TryParseMessageRejectsTruncatedAndGarbage()
    {
        Assert.False(SwayIpc.TryParseMessage([1, 2, 3], out _, out _, out _));
        Assert.False(SwayIpc.TryParseMessage(
            SwayIpc.FormatMessage(SwayIpc.MessageType.GetTree, "abc")[..10],
            out _,
            out _,
            out _));
        var garbage = "nope1234"u8.ToArray();
        Assert.False(SwayIpc.TryParseMessage(garbage, out _, out _, out _));
    }

    private static JsonElement Tree(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void FindFocusedPrefersNativeAppId()
    {
        var tree = Tree("""
        {
          "nodes": [
            {
              "nodes": [
                { "focused": false, "app_id": "kitty", "pid": 100 },
                { "focused": true, "app_id": "org.example.Editor", "pid": 200 }
              ]
            }
          ]
        }
        """);

        var identity = SwayIpc.TryFindFocusedApplication(tree);

        Assert.Equal(("org.example.Editor", 200u), identity);
    }

    [Fact]
    public void FindFocusedUsesWindowPropertiesClassForXWayland()
    {
        var tree = Tree("""
        {
          "floating_nodes": [
            { "focused": true, "window_properties": { "class": "Gimp" }, "pid": 300 }
          ]
        }
        """);

        var identity = SwayIpc.TryFindFocusedApplication(tree);

        Assert.Equal(("Gimp", 300u), identity);
    }

    [Fact]
    public void FindFocusedReturnsNullWithoutIdentity()
    {
        var tree = Tree("""{ "focused": true, "app_id": null, "pid": 1 }""");
        Assert.Null(SwayIpc.TryFindFocusedApplication(tree));

        var emptyWorkspace = Tree("""{ "nodes": [ { "focused": false } ] }""");
        Assert.Null(SwayIpc.TryFindFocusedApplication(emptyWorkspace));
    }

    [Fact]
    public void FindFocusedSearchesNestedContainers()
    {
        var tree = Tree("""
        {
          "nodes": [
            { "nodes": [ { "nodes": [ { "focused": true, "app_id": "foot", "pid": 9 } ] } ] }
          ]
        }
        """);

        var identity = SwayIpc.TryFindFocusedApplication(tree);

        Assert.Equal(("foot", 9u), identity);
    }
}

public sealed class HyprlandForegroundAppSourceTests
{
    [Fact]
    public void TryParseActivewindowReadsClassAndPidOnly()
    {
        // Shape mirrors the real reply, which also carries window/title —
        // those fields must not influence the identity.
        const string reply = """
        {
          "address": "0x55f0",
          "class": "org.telegram.desktop",
          "initialClass": "org.telegram.desktop",
          "title": "Secret chat window title that must never be stored",
          "initialTitle": "Telegram",
          "pid": 12345,
          "workspace": { "id": 3, "name": "3" },
          "floating": false
        }
        """;

        var identity = HyprlandForegroundAppSource.TryParseActivewindow(reply);

        Assert.Equal(("org.telegram.desktop", 12345u), identity);
    }

    [Fact]
    public void TryParseActivewindowHandlesEmptyAndInvalid()
    {
        Assert.Null(HyprlandForegroundAppSource.TryParseActivewindow("Invalid"));
        Assert.Null(HyprlandForegroundAppSource.TryParseActivewindow(""));
        Assert.Null(HyprlandForegroundAppSource.TryParseActivewindow("{}"));
        Assert.Null(HyprlandForegroundAppSource.TryParseActivewindow("""{"class": ""}"""));
    }

    [Fact]
    public void TryParseActivewindowFallsBackToInitialClass()
    {
        var identity = HyprlandForegroundAppSource.TryParseActivewindow(
            """{"class": "", "initialClass": "kitty", "pid": 5}""");

        Assert.Equal(("kitty", 5u), identity);
    }
}
