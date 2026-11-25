using Battuta.Windows.Stats.Models;

namespace Battuta.Engine.Input;

public static class ForegroundApplicationSnapshotExtensions
{
    /// <summary>
    /// Maps the neutral foreground snapshot onto the statistics application
    /// identity. Windows and Linux platforms use the same field semantics, so
    /// aggregate statistics keep their per-platform ProcessKey stability.
    /// </summary>
    public static TypingApplicationIdentity ToTypingApplicationIdentity(
        this ForegroundApplicationSnapshot snapshot) =>
        new(snapshot.ProcessKey, snapshot.DisplayName, snapshot.ProcessName);
}
