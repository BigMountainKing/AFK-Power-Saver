using EcoPause.Hardware.Abstractions;

namespace EcoPause.Desktop.Safety;

public enum DesktopLiveToggleIntent
{
    Unavailable,
    Pause,
    Restore
}

public sealed record DesktopLiveToggleAvailability(
    bool Available,
    DesktopLiveToggleIntent Intent,
    DesktopLiveTargetOption? ActiveProfile,
    string Message);

public static class DesktopLiveTogglePolicy
{
    public static DesktopLiveToggleAvailability Evaluate(ReadOnlyGpuProbeResult report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (!report.Ready ||
            report.Provider is not ("NVIDIA" or "AMD") ||
            report.Devices.Count != 1)
        {
            return Unavailable("Live toggle requires exactly one ready NVIDIA or AMD GPU.");
        }

        var gpu = report.Devices[0];
        if (gpu.MinimumLimitWatts <= 0 ||
            gpu.MinimumLimitWatts >= gpu.DefaultLimitWatts ||
            gpu.DefaultLimitWatts > gpu.MaximumLimitWatts)
        {
            return Unavailable("The GPU does not expose a usable lower-limit / default safety contract.");
        }

        if (gpu.CurrentLimitWatts == gpu.DefaultLimitWatts)
        {
            return new DesktopLiveToggleAvailability(
                true,
                DesktopLiveToggleIntent.Pause,
                ActiveProfile: null,
                "Choose a percentage of this GPU's default power limit.");
        }

        var activeProfile = DesktopLiveTargetPolicy.ResolveWatts(gpu.CurrentLimitWatts, gpu);
        if (activeProfile is not null &&
            DesktopLiveTargetPolicy.Validate(activeProfile, gpu).Accepted)
        {
            return new DesktopLiveToggleAvailability(
                true,
                DesktopLiveToggleIntent.Restore,
                activeProfile,
                gpu.LimitKind == GpuPowerLimitKind.DefaultRelativePercentage
                    ? $"Protected {activeProfile.Percentage}% AMD recovery is expected; the next action restores the exact original driver setting."
                    : $"Protected {activeProfile.Percentage}% / {activeProfile.Watts:0.##} W recovery is expected; the next action restores {gpu.DefaultLimitWatts:0.##} W.");
        }

        return Unavailable(
            "The current limit is neither the GPU default nor a recoverable percentage target.");
    }

    private static DesktopLiveToggleAvailability Unavailable(string message) =>
        new(false, DesktopLiveToggleIntent.Unavailable, ActiveProfile: null, message);
}
