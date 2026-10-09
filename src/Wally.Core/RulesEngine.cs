namespace Wally.Core;

/// <summary>A resolved instruction: put <see cref="ImagePath"/> on monitor <see cref="DeviceId"/>.</summary>
public sealed record ApplyAction(
    string DeviceId,
    string ImagePath,
    WallpaperFit Fit);

/// <summary>Outcome of resolving a monitor setup against the stored config.</summary>
public sealed record ResolveResult(
    IReadOnlyList<ApplyAction> Actions,
    IReadOnlyList<Monitor> Unassigned,
    bool IsNewSetup);

/// <summary>
/// Rule resolution: turns detected state + stored config into concrete actions.
/// Pure logic, no Windows calls. The platform layer consumes the ApplyAction
/// list and performs the actual COM wallpaper sets.
/// </summary>
public static class RulesEngine
{
    /// <summary>
    /// Resolve the per-monitor wallpaper actions for the current setup.
    /// Monitors with no resolvable image are reported as <c>Unassigned</c>.
    /// </summary>
    public static ResolveResult ResolveSetup(WallyConfig config, MonitorSetup setup)
    {
        var rule = config.RuleForSetup(setup.Fingerprint);
        var actions = new List<ApplyAction>();
        var unassigned = new List<Monitor>();

        foreach (var monitor in setup.Monitors)
        {
            var monitorRule = rule?.RuleFor(monitor.EdidKey);
            if (monitorRule is null)
            {
                unassigned.Add(monitor);
                continue;
            }

            actions.Add(new ApplyAction(monitor.DeviceId, monitorRule.ImagePath, monitorRule.Fit));
        }

        return new ResolveResult(actions, unassigned, IsNewSetup: rule is null);
    }

    /// <summary>
    /// Pick the next image for auto-assign, cycling through the rotation pool and
    /// preferring images not yet used by current desktop assignments.
    /// </summary>
    public static string? NextRotationImage(WallyConfig config, IReadOnlyCollection<string> alreadyUsed)
    {
        if (config.RotationPool.Count == 0)
        {
            return null;
        }

        var used = new HashSet<string>(alreadyUsed);
        foreach (var img in config.RotationPool)
        {
            if (!used.Contains(img))
            {
                return img;
            }
        }

        // All used at least once - wrap to the first.
        return config.RotationPool[0];
    }

    /// <summary>
    /// Return the assignment for a virtual desktop, or null if unassigned.
    /// When null and NewDesktopBehavior == "auto_assign", the caller should use
    /// <see cref="NextRotationImage"/> to create one.
    /// </summary>
    public static DesktopAssignment? ResolveDesktop(WallyConfig config, string desktopGuid)
        => config.AssignmentForDesktop(desktopGuid);
}
