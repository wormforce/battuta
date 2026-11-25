using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace Battuta.Windows.Audio.Linux;

/// <summary>Event codes the native shim delivers through its callback.</summary>
internal static class NativeAudioEvents
{
    /// <summary>The stream reached the streaming state.</summary>
    public const int StreamReady = 1;

    /// <summary>The stream reported an error; the host must rebuild it.</summary>
    public const int StreamError = 2;

    /// <summary>The default output device changed (plan §6.3); the host
    /// answers with a stream rebuild — the PCM banks are never re-decoded.
    /// Fires from the shim's device monitor, not the realtime thread.</summary>
    public const int DeviceChanged = 3;
}

/// <summary>Native voice priorities (must match <c>battuta_audio_schedule</c>).</summary>
internal static class NativeAudioPriorities
{
    public const int Keyboard = 0;
    public const int Pointer = 1;
}

/// <summary>
/// Mirrors <c>AudioStatsSnapshot</c> from the native shim (<c>ffi.rs</c>).
/// Field order and types are part of the ABI.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeAudioStats
{
    public long DroppedCommands;
    public long VoiceSteals;
    public long Underruns;
    public long StreamRecoveries;
    public uint ActualQuantum;
    public ulong RenderedFrames;
}

/// <summary>
/// C# binding for the `battuta_audio` C ABI (plan §6.1). All methods are
/// thin P/Invoke wrappers; realtime safety rules live on the Rust side.
/// </summary>
internal static unsafe class NativeAudioShim
{
    private const string LibraryName = "battuta_audio";
    private static int resolverInitialized;

    internal delegate void AudioEventCallback(int eventId, void* userData);

    /// <summary>
    /// True when the native library is loadable. This machine probe also
    /// installs the fallback resolution paths used by tests and dev builds.
    /// </summary>
    internal static bool IsAvailable()
    {
        try
        {
            EnsureResolver();
            if (NativeLibrary.TryLoad(LibraryName, out _))
            {
                return true;
            }

            foreach (var candidate in CandidatePaths())
            {
                if (NativeLibrary.TryLoad(candidate, out _))
                {
                    return true;
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int battuta_audio_engine_create(
        AudioEventCallback? eventCallback,
        void* eventUserData,
        out IntPtr engine);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int battuta_audio_engine_destroy(IntPtr engine);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern uint battuta_audio_sample_register(
        IntPtr engine, float* data, uint frameCount);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int battuta_audio_sample_release(IntPtr engine, uint handle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int battuta_audio_schedule(
        IntPtr engine, uint sample, float gain, float rate, int priority);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int battuta_audio_reset(IntPtr engine);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int battuta_audio_stream_start(IntPtr engine);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int battuta_audio_stream_stop(IntPtr engine);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int battuta_audio_stream_rebuild(IntPtr engine);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int battuta_audio_set_stream_property(
        IntPtr engine, byte* key, byte* value);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int battuta_audio_get_stats(IntPtr engine, out NativeAudioStats stats);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int battuta_audio_get_format(out uint sampleRate, out uint channels);

    /// <summary>
    /// Registers a fallback resolver so dev/test runs find the cdylib built
    /// under <c>BattutaLinux/native/target</c> without installation.
    /// </summary>
    private static void EnsureResolver()
    {
        if (Interlocked.Exchange(ref resolverInitialized, 1) != 0)
        {
            return;
        }

        NativeLibrary.SetDllImportResolver(
            typeof(NativeAudioShim).Assembly,
            static (name, assembly, searchPath) =>
            {
                if (name != LibraryName)
                {
                    return IntPtr.Zero;
                }

                foreach (var candidate in CandidatePaths())
                {
                    if (NativeLibrary.TryLoad(candidate, out var handle))
                    {
                        return handle;
                    }
                }

                return IntPtr.Zero;
            });
    }

    private static IEnumerable<string> CandidatePaths()
    {
        // Installed layout: lib sits next to the executable (packaged FHS:
        // /usr/lib/battuta/bin/libbattuta_audio.so). Check it first so the
        // production install does not depend on LD_LIBRARY_PATH.
        yield return Path.Combine(AppContext.BaseDirectory, "libbattuta_audio.so");
        yield return "/usr/lib/battuta/bin/libbattuta_audio.so";

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 8 && current is not null; depth++, current = current.Parent)
        {
            var nativeRoot = Path.Combine(current.FullName, "BattutaLinux", "native", "target");
            if (!Directory.Exists(nativeRoot))
            {
                continue;
            }

            foreach (var configuration in new[] { "debug", "release" })
            {
                yield return Path.Combine(nativeRoot, configuration, "libbattuta_audio.so");
            }
        }
    }
}

/// <summary>
/// Owns a native engine handle; destruction runs on finalization too, so a
/// leaking host cannot strand the native thread loop.
/// </summary>
internal sealed class NativeAudioEngineHandle : SafeHandle
{
    public NativeAudioEngineHandle(IntPtr engine)
        : base(engine, ownsHandle: true)
    {
    }

    public NativeAudioEngineHandle()
        : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        NativeAudioShim.battuta_audio_engine_destroy(handle);
        return true;
    }
}

/// <summary>
/// Owns one registered PCM slot in the native sample bank. The native side
/// keeps its own copy of the data, so release only retires the slot
/// (deferred while a voice still references it — plan §6.1). The handle
/// references the owning engine handle so a late finalization cannot touch
/// a destroyed engine.
/// </summary>
internal sealed class NativeSampleHandle : SafeHandle
{
    private readonly NativeAudioEngineHandle engine;

    public NativeSampleHandle(NativeAudioEngineHandle engine, uint sampleHandle)
        : base(IntPtr.Zero, ownsHandle: true)
    {
        this.engine = engine;
        handle = new IntPtr(sampleHandle);
    }

    public uint Sample => unchecked((uint)handle);

    public override bool IsInvalid => handle == IntPtr.Zero || engine.IsClosed;

    protected override bool ReleaseHandle()
    {
        var success = false;
        try
        {
            engine.DangerousAddRef(ref success);
        }
        catch (ObjectDisposedException)
        {
            // The engine is already gone; the native side freed the bank
            // with it, so there is nothing to release.
            return true;
        }

        if (!success)
        {
            return true;
        }

        try
        {
            NativeAudioShim.battuta_audio_sample_release(
                engine.DangerousGetHandle(), unchecked((uint)handle));
            return true;
        }
        finally
        {
            engine.DangerousRelease();
        }
    }
}
