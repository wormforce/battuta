using Battuta.Windows.Audio;

namespace Battuta.Linux.Runtime;

/// <summary>
/// Keeps the output stream alive (plan §6.3): a stream failure triggers a
/// rebuild after a bounded exponential backoff, a default-device change
/// triggers an immediate rebuild (debounced), and reaching the streaming
/// state resets the backoff. Banks and scheduling semantics are untouched —
/// only the stream shell is recycled. Mirrors the bounded recovery idea of
/// the Windows <c>AudioOutputService</c>.
/// </summary>
public sealed class AudioStreamSupervisor : IDisposable
{
    public static readonly TimeSpan MinimumRebuildInterval = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(8);
    public static readonly TimeSpan StabilityResetWindow = TimeSpan.FromSeconds(10);

    private readonly IAudioStreamController controller;
    private readonly TimeProvider timeProvider;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Lock gate = new();
    private Task recoveryTask = Task.CompletedTask;
    private TimeSpan retryDelay = InitialRetryDelay;
    private long lastRebuildTimestamp;
    private long lastReadyTimestamp;
    private bool disposed;

    public AudioStreamSupervisor(IAudioStreamController controller, TimeProvider? timeProvider = null)
    {
        this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
        this.timeProvider = timeProvider ?? TimeProvider.System;

        this.controller.StreamReady += OnStreamReady;
        this.controller.StreamFailed += OnStreamFailed;
        this.controller.StreamDeviceChanged += OnStreamDeviceChanged;
    }

    /// <summary>Current stream state for the UI (mirrors the controller).</summary>
    public bool IsStreamReady => controller.IsStreamReady;

    /// <summary>Starts the stream and reports whether the request was
    /// accepted (readiness is asynchronous).</summary>
    public bool Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return controller.StartStream() == 0;
    }

    private void OnStreamReady(object? sender, EventArgs eventArgs)
    {
        lock (gate)
        {
            retryDelay = InitialRetryDelay;
            lastReadyTimestamp = timeProvider.GetTimestamp();
        }
    }

    private void OnStreamFailed(object? sender, EventArgs eventArgs) =>
        ScheduleRecovery(InitialRetryDelay);

    private void OnStreamDeviceChanged(object? sender, EventArgs eventArgs)
    {
        // Debounce bursts (metadata can change a few times during one
        // switch): skip the rebuild when one just happened.
        var now = timeProvider.GetTimestamp();
        lock (gate)
        {
            var sinceLast = lastRebuildTimestamp == 0
                ? TimeSpan.MaxValue
                : timeProvider.GetElapsedTime(lastRebuildTimestamp, now);
            if (sinceLast < MinimumRebuildInterval)
            {
                return;
            }

            lastRebuildTimestamp = now;
        }

        ScheduleRecovery(TimeSpan.Zero);
    }

    private void ScheduleRecovery(TimeSpan delay)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Task recovery;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            recovery = recoveryTask;
        }

        // Serialize recovery attempts on one continuation chain.
        recoveryTask = recovery.ContinueWith(
            async _ =>
            {
                if (delay > TimeSpan.Zero)
                {
                    try
                    {
                        await Task.Delay(delay, lifetime.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }

                var result = controller.RebuildStream();
                if (result != 0)
                {
                    TimeSpan next;
                    lock (gate)
                    {
                        retryDelay = retryDelay * 2 > MaximumRetryDelay
                            ? MaximumRetryDelay
                            : retryDelay * 2;
                        next = retryDelay;
                    }

                    ScheduleRecovery(next);
                }
                else
                {
                    lock (gate)
                    {
                        lastRebuildTimestamp = timeProvider.GetTimestamp();
                    }
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default)
            .Unwrap();
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
        }

        lifetime.Cancel();
        lifetime.Dispose();
        controller.StreamReady -= OnStreamReady;
        controller.StreamFailed -= OnStreamFailed;
        controller.StreamDeviceChanged -= OnStreamDeviceChanged;
    }
}
