using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace Battuta.Linux.Platform.ForegroundApps;

/// <summary>
/// Encoder/decoder for the i3 IPC wire protocol, which sway also speaks. Kept
/// static and allocation-light so the socket layer is thin and the parsing is
/// unit-testable without a compositor.
/// </summary>
public static class SwayIpc
{
    public const string Magic = "i3-ipc";

    public enum MessageType : uint
    {
        RunCommand = 0,
        GetWorkspaces = 1,
        Subscribe = 2,
        GetOutputs = 3,
        GetTree = 4,
        GetVersion = 7,
    }

    // "i3-ipc" magic (6) + payload length (u32) + message type (u32).
    public const int HeaderLength = 14;

    /// <summary>Encodes one request frame.</summary>
    public static byte[] FormatMessage(MessageType type, string payload = "")
    {
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var frame = new byte[HeaderLength + payloadBytes.Length];
        Encoding.ASCII.GetBytes(Magic).CopyTo(frame, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(
            frame.AsSpan(Magic.Length, sizeof(uint)),
            (uint)payloadBytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(
            frame.AsSpan(Magic.Length + sizeof(uint), sizeof(uint)),
            (uint)type);
        payloadBytes.CopyTo(frame, HeaderLength);
        return frame;
    }

    /// <summary>Parses one reply frame from a buffer that holds at least one
    /// complete frame. Returns the reply type and payload, and reports the
    /// total consumed length so callers can drain exactly one frame.</summary>
    public static bool TryParseMessage(
        ReadOnlySpan<byte> buffer,
        out MessageType type,
        out string payload,
        out int consumedLength)
    {
        type = default;
        payload = string.Empty;
        consumedLength = 0;
        if (buffer.Length < HeaderLength)
        {
            return false;
        }

        if (!Encoding.ASCII.GetString(buffer[..Magic.Length]).Equals(Magic, StringComparison.Ordinal))
        {
            return false;
        }

        var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(
            buffer[Magic.Length..(Magic.Length + sizeof(uint))]);
        type = (MessageType)BinaryPrimitives.ReadUInt32LittleEndian(
            buffer[(Magic.Length + sizeof(uint))..HeaderLength]);
        var length = checked((int)payloadLength);
        if (buffer.Length < HeaderLength + length)
        {
            return false;
        }

        payload = Encoding.UTF8.GetString(buffer.Slice(HeaderLength, length));
        consumedLength = HeaderLength + length;
        return true;
    }

    /// <summary>
    /// Walks a sway/i3 layout tree and extracts the focused leaf's
    /// application identity: <c>app_id</c> (native Wayland) or
    /// <c>window_properties.class</c> (XWayland), plus the PID. Returns null
    /// when nothing focused has an identity (empty workspace, desktop shell).
    /// Never reads window names.
    /// </summary>
    public static (string ApplicationClass, uint ProcessId)? TryFindFocusedApplication(
        JsonElement tree)
    {
        if (tree.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (tree.TryGetProperty("focused", out var focused) &&
            focused.ValueKind == JsonValueKind.True)
        {
            var candidate = IdentityFromNode(tree);
            if (candidate is not null)
            {
                return candidate;
            }
        }

        if (tree.TryGetProperty("nodes", out var nodes) &&
            nodes.ValueKind == JsonValueKind.Array)
        {
            foreach (var node in nodes.EnumerateArray())
            {
                var result = TryFindFocusedApplication(node);
                if (result is not null)
                {
                    return result;
                }
            }
        }

        // floating_nodes holds floating windows, which can be focused too.
        if (tree.TryGetProperty("floating_nodes", out var floating) &&
            floating.ValueKind == JsonValueKind.Array)
        {
            foreach (var node in floating.EnumerateArray())
            {
                var result = TryFindFocusedApplication(node);
                if (result is not null)
                {
                    return result;
                }
            }
        }

        return null;
    }

    private static (string, uint)? IdentityFromNode(JsonElement node)
    {
        string? applicationClass = null;
        if (node.TryGetProperty("app_id", out var appId) &&
            appId.ValueKind == JsonValueKind.String)
        {
            applicationClass = appId.GetString();
        }
        else if (node.TryGetProperty("window_properties", out var properties) &&
                 properties.ValueKind == JsonValueKind.Object &&
                 properties.TryGetProperty("class", out var windowClass) &&
                 windowClass.ValueKind == JsonValueKind.String)
        {
            applicationClass = windowClass.GetString();
        }

        if (string.IsNullOrWhiteSpace(applicationClass))
        {
            return null;
        }

        uint processId = 0;
        if (node.TryGetProperty("pid", out var pid) &&
            pid.ValueKind == JsonValueKind.Number &&
            pid.TryGetUInt32(out var parsedPid))
        {
            processId = parsedPid;
        }

        return (applicationClass, processId);
    }
}
