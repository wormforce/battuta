namespace Battuta.Linux.Platform.ForegroundApps;

/// <summary>Lifecycle of the active foreground-application source.</summary>
public enum ForegroundAppSourceState
{
    /// <summary>The monitor has not been started.</summary>
    Disabled,

    /// <summary>No compositor adapter matches the session (or no session).</summary>
    Unavailable,

    /// <summary>The adapter exists but its peer is not ready yet (for example
    /// the KWin script is not loaded because KWin is restarting).</summary>
    Waiting,

    /// <summary>The adapter is publishing identities.</summary>
    Running,

    /// <summary>The adapter could work, but its optional companion (the GNOME
    /// Shell extension) is not installed. Statistics stay functional with the
    /// application dimension reported as unknown (plan §8.3).</summary>
    NeedsExtension,

    /// <summary>The adapter failed; it retries in the background.</summary>
    Failed,
}

public readonly record struct ForegroundAppSourceStatus(
    ForegroundAppSourceState State,
    string? Detail)
{
    public static ForegroundAppSourceStatus Disabled { get; } =
        new(ForegroundAppSourceState.Disabled, null);

    public override string ToString() => State switch
    {
        ForegroundAppSourceState.Disabled => "未启动",
        ForegroundAppSourceState.Unavailable => Detail is null ? "不可用" : $"不可用:{Detail}",
        ForegroundAppSourceState.Waiting => Detail is null ? "等待中" : $"等待中:{Detail}",
        ForegroundAppSourceState.Running => Detail is null ? "运行中" : $"运行中:{Detail}",
        ForegroundAppSourceState.NeedsExtension => Detail is null ? "缺少可选组件" : $"缺少可选组件:{Detail}",
        ForegroundAppSourceState.Failed => Detail is null ? "失败" : $"失败:{Detail}",
        _ => State.ToString(),
    };
}

/// <summary>
/// One compositor-specific foreground-application adapter. Sources publish
/// into the shared <see cref="ForegroundApplicationCache"/> (generation-aware)
/// and report their lifecycle through <see cref="Status"/>; they never throw
/// into the input pipeline.
/// </summary>
public interface IForegroundAppSource : IAsyncDisposable
{
    ForegroundAppSourceStatus Status { get; }

    event EventHandler? StatusChanged;

    Task StartAsync(CancellationToken cancellationToken = default);
}
