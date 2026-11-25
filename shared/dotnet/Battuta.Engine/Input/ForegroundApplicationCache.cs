namespace Battuta.Engine.Input;

public sealed record ForegroundApplicationSnapshot(
    int ProcessId,
    string ProcessKey,
    string DisplayName,
    string ProcessName)
{
    public static ForegroundApplicationSnapshot Unknown { get; } = new(
        0,
        "process:unknown",
        "未知应用",
        "unknown");
}

public interface IForegroundApplicationSnapshotProvider
{
    ForegroundApplicationSnapshot Current { get; }
}

/// <summary>
/// An atomic cache used by the input consumer. Publishing is generation-aware so a slow
/// process lookup from an older foreground notification cannot overwrite a newer result.
/// </summary>
public sealed class ForegroundApplicationCache : IForegroundApplicationSnapshotProvider
{
    private sealed record Entry(long Generation, ForegroundApplicationSnapshot Snapshot);

    private Entry _entry = new(0, ForegroundApplicationSnapshot.Unknown);

    public ForegroundApplicationSnapshot Current => Volatile.Read(ref _entry).Snapshot;

    public long Generation => Volatile.Read(ref _entry).Generation;

    public bool TryPublish(long generation, ForegroundApplicationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        while (true)
        {
            var current = Volatile.Read(ref _entry);
            if (generation < current.Generation)
            {
                return false;
            }

            var replacement = new Entry(generation, snapshot);
            if (ReferenceEquals(
                Interlocked.CompareExchange(ref _entry, replacement, current),
                current))
            {
                return true;
            }
        }
    }
}
