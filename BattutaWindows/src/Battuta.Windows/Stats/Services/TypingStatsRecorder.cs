using Battuta.Core.Input;
using Battuta.Windows.Stats.Models;
using Battuta.Windows.Stats.Persistence;

namespace Battuta.Windows.Stats.Services;

/// <summary>
/// Batches the input hot path in memory and serializes persistence writes.
/// No database work is performed by <see cref="RecordKeyDown"/> or <see cref="RecordMouseDown"/>.
/// </summary>
public sealed class TypingStatsRecorder : IAsyncDisposable
{
    private readonly record struct PendingCharacterKey(
        long SecondStartUtc,
        DateOnly LocalDate,
        int LocalHour,
        TypingApplicationIdentity Application);

    private readonly record struct PendingKeyPressKey(
        DateOnly LocalDate,
        PhysicalKeyId PhysicalKeyId);

    private readonly object _sync = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly Dictionary<PendingCharacterKey, long> _pendingCharacters = [];
    private readonly Dictionary<PendingKeyPressKey, long> _pendingKeyPresses = [];
    private readonly Dictionary<DateOnly, long> _pendingMouseClicks = [];
    private readonly TimeZoneInfo _localTimeZone;
    private readonly TimeSpan _defaultFlushDelay;
    private CancellationTokenSource? _scheduledFlushCancellation;
    private TypingStatsWriteBatch? _retryBatch;
    private string? _lastWriteError;
    private int _consecutiveWriteFailures;
    private bool _isRecordingSuspended;
    private bool _isClearing;
    private bool _disposed;

    public TypingStatsRecorder(
        ITypingStatsPersistence persistence,
        TimeZoneInfo? localTimeZone = null,
        TimeSpan? flushDelay = null)
    {
        Persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
        _localTimeZone = localTimeZone ?? TimeZoneInfo.Local;
        _defaultFlushDelay = flushDelay ?? TimeSpan.FromMilliseconds(750);
    }

    public event EventHandler? StateChanged;

    public ITypingStatsPersistence Persistence { get; }

    public string? LastWriteError
    {
        get
        {
            lock (_sync)
            {
                return _lastWriteError;
            }
        }
    }

    public int ConsecutiveWriteFailures
    {
        get
        {
            lock (_sync)
            {
                return _consecutiveWriteFailures;
            }
        }
    }

    public bool IsRecordingSuspended
    {
        get
        {
            lock (_sync)
            {
                return _isRecordingSuspended;
            }
        }
    }

    public bool IsClearing
    {
        get
        {
            lock (_sync)
            {
                return _isClearing;
            }
        }
    }

    public void RecordKeyDown(
        PhysicalKeyId key,
        bool isRepeat,
        bool isShortcutModified,
        TypingApplicationIdentity application,
        DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (!key.IsValid)
        {
            return;
        }

        var local = TimeZoneInfo.ConvertTime(occurredAt, _localTimeZone);
        var localDate = DateOnly.FromDateTime(local.DateTime);
        var localHour = local.Hour;
        var didRecord = false;
        lock (_sync)
        {
            if (_disposed || _isClearing || _isRecordingSuspended)
            {
                return;
            }

            if (!isRepeat)
            {
                var physicalKey = new PendingKeyPressKey(localDate, key);
                _pendingKeyPresses[physicalKey] =
                    _pendingKeyPresses.GetValueOrDefault(physicalKey) + 1;
                didRecord = true;
            }

            if (TypingCharacterKeyFilter.CountsAsCharacter(key, isShortcutModified))
            {
                var characterKey = new PendingCharacterKey(
                    occurredAt.ToUnixTimeSeconds(),
                    localDate,
                    localHour,
                    application);
                _pendingCharacters[characterKey] =
                    _pendingCharacters.GetValueOrDefault(characterKey) + 1;
                didRecord = true;
            }

            if (didRecord)
            {
                ScheduleFlushLocked(_defaultFlushDelay);
            }
        }
    }

    public void RecordMouseDown(DateTimeOffset occurredAt)
    {
        var local = TimeZoneInfo.ConvertTime(occurredAt, _localTimeZone);
        var localDate = DateOnly.FromDateTime(local.DateTime);
        lock (_sync)
        {
            if (_disposed || _isClearing || _isRecordingSuspended)
            {
                return;
            }

            _pendingMouseClicks[localDate] = _pendingMouseClicks.GetValueOrDefault(localDate) + 1;
            ScheduleFlushLocked(_defaultFlushDelay);
        }
    }

    public async Task<bool> FlushPendingAsync(CancellationToken cancellationToken = default)
    {
        CancelScheduledFlush();
        await _flushGate.WaitAsync(cancellationToken);
        var succeeded = true;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = TakeNextBatch();
                if (batch.IsEmpty)
                {
                    break;
                }

                try
                {
                    await Persistence.RecordAsync(batch, cancellationToken);
                    lock (_sync)
                    {
                        _lastWriteError = null;
                        _consecutiveWriteFailures = 0;
                        _isRecordingSuspended = false;
                    }

                    OnStateChanged();
                }
                catch (OperationCanceledException)
                {
                    lock (_sync)
                    {
                        _retryBatch = batch;
                        if (!_disposed && !_isClearing)
                        {
                            ScheduleFlushLocked(_defaultFlushDelay);
                        }
                    }

                    succeeded = false;
                    break;
                }
                catch (Exception exception)
                {
                    lock (_sync)
                    {
                        _retryBatch = batch;
                        _consecutiveWriteFailures = Math.Min(_consecutiveWriteFailures + 1, 6);
                        if (_consecutiveWriteFailures >= 6)
                        {
                            _isRecordingSuspended = true;
                            _lastWriteError =
                                $"{exception.Message} 连续写入失败，输入统计已暂停；刷新统计或清除数据后可重试。";
                        }
                        else
                        {
                            _lastWriteError = exception.Message;
                            var retrySeconds = Math.Min(60, 1 << _consecutiveWriteFailures);
                            ScheduleFlushLocked(TimeSpan.FromSeconds(retrySeconds));
                        }
                    }

                    OnStateChanged();
                    succeeded = false;
                    break;
                }
            }
        }
        finally
        {
            _flushGate.Release();
        }

        return succeeded;
    }

    public async Task ClearAllAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_isClearing)
            {
                throw new InvalidOperationException("Typing statistics are already being cleared.");
            }

            _isClearing = true;
        }

        OnStateChanged();
        try
        {
            _ = await FlushPendingAsync(cancellationToken);
            CancelScheduledFlush();
            lock (_sync)
            {
                _pendingCharacters.Clear();
                _pendingKeyPresses.Clear();
                _pendingMouseClicks.Clear();
                _retryBatch = null;
            }

            await Persistence.ClearAllAsync(cancellationToken);
            lock (_sync)
            {
                _consecutiveWriteFailures = 0;
                _lastWriteError = null;
                _isRecordingSuspended = false;
            }
        }
        finally
        {
            lock (_sync)
            {
                _isClearing = false;
            }

            OnStateChanged();
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        CancelScheduledFlush();
        try
        {
            await FlushPendingAsync();
        }
        catch
        {
            // Shutdown is best effort; the last error remains available to diagnostics.
        }

        // Do not dispose the semaphore here: a debounce task may have passed its
        // cancellation check immediately before shutdown and still be about to wait.
        // The semaphore never exposes its WaitHandle, so retaining it has no OS handle cost.
    }

    private TypingStatsWriteBatch TakeNextBatch()
    {
        lock (_sync)
        {
            if (_retryBatch is not null)
            {
                var retry = _retryBatch;
                _retryBatch = null;
                return retry;
            }

            if (_pendingCharacters.Count == 0 && _pendingKeyPresses.Count == 0
                && _pendingMouseClicks.Count == 0)
            {
                return TypingStatsWriteBatch.Empty;
            }

            var characters = _pendingCharacters
                .Select(pair => new TypingCharacterAggregate(
                    pair.Key.SecondStartUtc,
                    pair.Key.LocalDate,
                    pair.Key.LocalHour,
                    pair.Key.Application,
                    pair.Value))
                .OrderBy(item => item.SecondStartUtc)
                .ThenBy(item => item.Application.ProcessKey, StringComparer.Ordinal)
                .ToArray();
            var keys = _pendingKeyPresses
                .Select(pair => new TypingKeyAggregate(
                    pair.Key.LocalDate,
                    pair.Key.PhysicalKeyId,
                    pair.Value))
                .OrderBy(item => item.LocalDate)
                .ThenBy(item => item.PhysicalKeyId.Value, StringComparer.Ordinal)
                .ToArray();
            var mouseClicks = _pendingMouseClicks
                .Select(pair => new MouseClickAggregate(pair.Key, pair.Value))
                .OrderBy(item => item.LocalDate)
                .ToArray();
            _pendingCharacters.Clear();
            _pendingKeyPresses.Clear();
            _pendingMouseClicks.Clear();
            return new TypingStatsWriteBatch(characters, keys) { MouseAggregates = mouseClicks };
        }
    }

    private void CancelScheduledFlush()
    {
        CancellationTokenSource? cancellation;
        lock (_sync)
        {
            cancellation = _scheduledFlushCancellation;
            _scheduledFlushCancellation = null;
        }

        if (cancellation is not null)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
    }

    private void ScheduleFlushLocked(TimeSpan delay)
    {
        if (_disposed || _scheduledFlushCancellation is not null)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _scheduledFlushCancellation = cancellation;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            lock (_sync)
            {
                if (!ReferenceEquals(_scheduledFlushCancellation, cancellation))
                {
                    return;
                }

                _scheduledFlushCancellation = null;
            }

            cancellation.Dispose();
            await FlushPendingAsync();
        });
    }

    private void OnStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
}
