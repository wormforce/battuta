using System.Runtime.InteropServices;
using System.Text;
using Battuta.Engine.Input;

namespace Battuta.Linux.Platform.ForegroundApps;

/// <summary>
/// X11 fallback (plan §8.3 priority 5): reads <c>_NET_ACTIVE_WINDOW</c> from
/// the root window and resolves <c>WM_CLASS</c> plus <c>_NET_WM_PID</c>. Used
/// for X11 sessions; on Wayland sessions a compositor-specific adapter always
/// wins, so this source never re-labels native Wayland windows. Window titles
/// are never read.
/// </summary>
public sealed class X11ForegroundAppSource : PollingForegroundAppSource
{
    private readonly Func<bool> displayResolver;

    public X11ForegroundAppSource(
        ForegroundApplicationCache cache,
        TimeProvider? timeProvider = null,
        TimeSpan? interval = null,
        Func<bool>? displayResolver = null)
        : base(cache, timeProvider, interval)
    {
        this.displayResolver = displayResolver ?? X11Native.TryOpenDisplay;
    }

    protected override async ValueTask<ForegroundApplicationSnapshot?> PollAsync(
        CancellationToken cancellationToken)
    {
        // The Xlib round trip is synchronous; hop off the timer thread.
        var snapshot = await Task.Run(
            () => PollOnce(),
            cancellationToken).ConfigureAwait(false);
        return snapshot;
    }

    private ForegroundApplicationSnapshot? PollOnce()
    {
        if (!displayResolver())
        {
            SetStatus(new ForegroundAppSourceStatus(
                ForegroundAppSourceState.Unavailable,
                "DISPLAY 未设置或无法连接"));
            return null;
        }

        var identity = X11Native.ReadActiveWindowIdentity();
        if (identity is not (var applicationClass, var processId))
        {
            SetStatus(new ForegroundAppSourceStatus(
                ForegroundAppSourceState.Running,
                "当前没有可识别的活动窗口"));
            return null;
        }

        SetStatus(new ForegroundAppSourceStatus(
            ForegroundAppSourceState.Running,
            $"X11 _NET_ACTIVE_WINDOW({applicationClass})"));
        return ForegroundAppSnapshotFactory.FromDesktopApplication(applicationClass, processId);
    }
}

/// <summary>Minimal Xlib interop. Every call is guarded: X11 errors are
/// swallowed by the installed error handler and surface as null results.</summary>
internal static class X11Native
{
    private const int AnyPropertyType = 0;
    private const int SuccessStatus = 0;

    private static int? libX11Available;
    private static IntPtr display = IntPtr.Zero;
    private static IntPtr activeWindowAtom;
    private static IntPtr wmClassAtom;
    private static IntPtr wmPidAtom;
    private static readonly object DisplayLock = new();

    // The delegate must outlive the native registration; Xlib may invoke it
    // from any thread. It always returns "handled" so the default handler
    // (which terminates the process) never runs.
    private static readonly X11Native.XErrorHandler SwallowingErrorHandler =
        (_, _) => 0;

    private delegate int XErrorHandler(IntPtr display, IntPtr errorEvent);

    [DllImport("libX11.so.6", SetLastError = false)]
    private static extern IntPtr XOpenDisplay(IntPtr displayName);

    [DllImport("libX11.so.6", SetLastError = false)]
    private static extern int XCloseDisplay(IntPtr display);

    [DllImport("libX11.so.6", SetLastError = false)]
    private static extern IntPtr XDefaultRootWindow(IntPtr display);

    [DllImport("libX11.so.6", SetLastError = false)]
    private static extern IntPtr XInternAtom(
        IntPtr display,
        string atomName,
        bool onlyIfExists);

    [DllImport("libX11.so.6", SetLastError = false)]
    private static extern int XGetWindowProperty(
        IntPtr display,
        IntPtr window,
        IntPtr property,
        IntPtr longOffset,
        IntPtr longLength,
        bool delete,
        IntPtr requestType,
        out IntPtr actualType,
        out int actualFormat,
        out IntPtr itemCount,
        out IntPtr bytesAfter,
        out IntPtr propertyData);

    [DllImport("libX11.so.6", SetLastError = false)]
    private static extern int XFree(IntPtr data);

    [DllImport("libX11.so.6", SetLastError = false)]
    private static extern IntPtr XSetErrorHandler(XErrorHandler handler);

    public static bool TryOpenDisplay()
    {
        lock (DisplayLock)
        {
            if (libX11Available is null)
            {
                libX11Available = NativeLibrary.TryLoad("libX11.so.6", out _) ? 1 : 0;
                if (libX11Available == 1)
                {
                    _ = XSetErrorHandler(SwallowingErrorHandler);
                }
            }

            if (libX11Available == 0)
            {
                return false;
            }

            if (display != IntPtr.Zero)
            {
                return true;
            }

            display = XOpenDisplay(IntPtr.Zero);
            return display != IntPtr.Zero;
        }
    }

    /// <summary>Returns (WM_CLASS res_class, pid) for the active window, or
    /// null when no active window or identity is resolvable.</summary>
    public static (string ApplicationClass, uint ProcessId)? ReadActiveWindowIdentity()
    {
        lock (DisplayLock)
        {
            if (display == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var root = XDefaultRootWindow(display);
                var activeWindow = ReadWindowProperty(
                    root,
                    EnsureAtom(ref activeWindowAtom, "_NET_ACTIVE_WINDOW"),
                    isLongItem: true,
                    out var propertyData);
                if (activeWindow == IntPtr.Zero)
                {
                    return null;
                }

                _ = XFree(propertyData);
                propertyData = IntPtr.Zero;

                var applicationClass = ReadWindowProperty(
                    activeWindow,
                    EnsureAtom(ref wmClassAtom, "WM_CLASS"),
                    isLongItem: false,
                    out var classData);
                var pid = ReadWindowProperty(
                    activeWindow,
                    EnsureAtom(ref wmPidAtom, "_NET_WM_PID"),
                    isLongItem: true,
                    out var pidData);

                // WM_CLASS data is "instance\0class\0"; res_class is the second string.
                var resolvedClass = ReadSecondNullTerminatedString(classData);
                if (classData != IntPtr.Zero)
                {
                    _ = XFree(classData);
                }

                uint processId = 0;
                if (pidData != IntPtr.Zero)
                {
                    processId = (uint)Math.Max(0, Marshal.ReadIntPtr(pidData).ToInt64());
                    _ = XFree(pidData);
                }

                return string.IsNullOrWhiteSpace(resolvedClass)
                    ? null
                    : (resolvedClass, processId);
            }
            catch
            {
                // A dead or wedged display connection: reset so the next poll
                // reopens it.
                _ = XCloseDisplay(display);
                display = IntPtr.Zero;
                return null;
            }
        }
    }

    private static IntPtr EnsureAtom(ref IntPtr cached, string name) =>
        cached != IntPtr.Zero ? cached : cached = XInternAtom(display, name, onlyIfExists: true);

    private static IntPtr ReadWindowProperty(
        IntPtr window,
        IntPtr propertyAtom,
        bool isLongItem,
        out IntPtr propertyData)
    {
        propertyData = IntPtr.Zero;
        var status = XGetWindowProperty(
            display,
            window,
            propertyAtom,
            IntPtr.Zero,
            new IntPtr(2048),
            delete: false,
            AnyPropertyType,
            out _,
            out var actualFormat,
            out var itemCount,
            out _,
            out propertyData);
        if (status != SuccessStatus ||
            propertyData == IntPtr.Zero ||
            itemCount == IntPtr.Zero ||
            actualFormat != (isLongItem ? 32 : 8))
        {
            if (propertyData != IntPtr.Zero)
            {
                _ = XFree(propertyData);
                propertyData = IntPtr.Zero;
            }

            return IntPtr.Zero;
        }

        return isLongItem ? Marshal.ReadIntPtr(propertyData) : propertyData;
    }

    private static string? ReadSecondNullTerminatedString(IntPtr data)
    {
        if (data == IntPtr.Zero)
        {
            return null;
        }

        // Walk instance\0class\0 within a bounded buffer.
        const int MaxBytes = 512;
        var strings = new List<string>(2);
        var current = new List<byte>(64);
        for (var offset = 0; offset < MaxBytes; offset++)
        {
            var b = Marshal.ReadByte(data, offset);
            if (b == 0)
            {
                strings.Add(Encoding.ASCII.GetString([.. current]));
                current.Clear();
                if (strings.Count == 2)
                {
                    break;
                }

                // Two terminators in a row means only one string exists.
                if (Marshal.ReadByte(data, offset + 1) == 0)
                {
                    break;
                }
            }
            else
            {
                current.Add(b);
            }
        }

        if (strings.Count == 0)
        {
            return null;
        }

        return strings.Count >= 2 ? strings[1] : strings[0];
    }
}
