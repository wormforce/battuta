using Battuta.Engine.Input;

namespace Battuta.Linux.Platform.ForegroundApps;

/// <summary>
/// Maps compositor-provided application identity (WM_CLASS, Wayland app_id,
/// Hyprland window class) onto the neutral snapshot. Process keys are stable
/// per application identifier, so statistics do not fragment across relaunches.
/// Window titles are never accepted.
/// </summary>
public static class ForegroundAppSnapshotFactory
{
    /// <summary>Creates a snapshot for a desktop application identifier.</summary>
    public static ForegroundApplicationSnapshot FromDesktopApplication(
        string applicationClass,
        uint processId)
    {
        if (string.IsNullOrWhiteSpace(applicationClass))
        {
            return ForegroundApplicationSnapshot.Unknown;
        }

        var identifier = applicationClass.Trim();
        return new ForegroundApplicationSnapshot(
            (int)processId,
            $"app:{identifier.ToLowerInvariant()}",
            identifier,
            identifier);
    }
}
