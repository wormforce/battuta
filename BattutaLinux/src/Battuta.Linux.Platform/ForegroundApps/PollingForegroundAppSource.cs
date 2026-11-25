using Battuta.Engine.Input;

namespace Battuta.Linux.Platform.ForegroundApps;

/// <summary>
/// Shared engine for adapters that resolve the foreground application by
/// polling (Sway/i3, Hyprland, X11). A poll never queries on the key path:
/// events always read the shared cache, and this loop refreshes it on a slow
/// fixed interval. Poll ticks are skipped while a previous poll is still in
/// flight, and exceptions degrade to <see cref="ForegroundAppSourceState.Failed"/>
/// with a detail while polling continues.
/// </summary>
public abstract class PollingForegroundAppSource : IForegroundAppSource
{
    private readonly ForegroundApplicationCache cache;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan interval;
    private ITimer? timer;
    private int pollInFlight;
    private long generation;
    private int started;
    private int disposed;

    protected PollingForegroundAppSource(
        ForegroundApplicationCache cache,
        TimeProvider? timeProvider = null,
        TimeSpan? interval = null)
    {
        this.cache = cache ?? throw new ArgumentNullException(nameof(cache));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.interval = interval ?? TimeSpan.FromMilliseconds(500);
    }

    public ForegroundAppSourceStatus Status { get; private set; } =
        new(ForegroundAppSourceState.Waiting, "未轮询");

    public event EventHandler? StatusChanged;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (Interlocked.Exchange(ref started, 1) != 0)
        {
            return Task.CompletedTask;
        }

        // DueTime zero: resolve the initial foreground application immediately.
        timer = timeProvider.CreateTimer(OnTick, null, TimeSpan.Zero, interval);
        return Task.CompletedTask;
    }

    public virtual ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        timer?.Dispose();
        timer = null;
        return ValueTask.CompletedTask;
    }

    /// <summary>Resolves one foreground identity; return null to keep the
    /// last published value (for example when nothing has focus).</summary>
    protected abstract ValueTask<ForegroundApplicationSnapshot?> PollAsync(
        CancellationToken cancellationToken);

    protected void SetStatus(ForegroundAppSourceStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private async void OnTick(object? state)
    {
        if (Volatile.Read(ref disposed) != 0 ||
            Interlocked.Exchange(ref pollInFlight, 1) != 0)
        {
            return;
        }

        try
        {
            var snapshot = await PollAsync(CancellationToken.None).ConfigureAwait(false);
            if (snapshot is not null)
            {
                cache.TryPublish(Interlocked.Increment(ref generation), snapshot);
            }
        }
        catch (Exception error)
        {
            SetStatus(new ForegroundAppSourceStatus(
                ForegroundAppSourceState.Failed,
                $"轮询失败:{error.Message}"));
        }
        finally
        {
            Volatile.Write(ref pollInFlight, 0);
        }
    }
}
