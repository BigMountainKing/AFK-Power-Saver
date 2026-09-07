using EcoPause.Hardware.Abstractions;

namespace EcoPause.Desktop.Safety;

public sealed record DesktopLiveTargetOption(
    int Percentage,
    decimal Watts,
    GpuPowerLimitKind LimitKind = GpuPowerLimitKind.AbsoluteWatts)
{
    public string ProfileId => $"percent-{Percentage}";

    public string DisplayName => LimitKind == GpuPowerLimitKind.DefaultRelativePercentage
        ? $"{Percentage}% of factory"
        : $"{Percentage}% ({Watts:0.##} W)";

    public string FormattedLimit => LimitKind == GpuPowerLimitKind.DefaultRelativePercentage
        ? $"{Watts:0.##}% of factory"
        : $"{Watts:0.##} W";
}

public sealed record DesktopLiveTargetValidationResult(bool Accepted, string Message);

public static class DesktopLiveTargetPolicy
{
    public const int DefaultPercentage = 89;
    public const int MaximumPercentage = 99;

    public static int GetMinimumPercentage(ReadOnlyGpuSnapshot gpu)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        if (gpu.DefaultLimitWatts <= 0)
        {
            return 1;
        }

        return Math.Clamp(
            decimal.ToInt32(decimal.Floor(gpu.MinimumLimitWatts * 100m / gpu.DefaultLimitWatts)),
            1,
            MaximumPercentage);
    }

    public static DesktopLiveTargetOption ResolvePercentage(
        int percentage,
        ReadOnlyGpuSnapshot gpu)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        if (percentage < GetMinimumPercentage(gpu) || percentage > MaximumPercentage)
        {
            throw new ArgumentOutOfRangeException(nameof(percentage));
        }

        var proportionalWatts = decimal.Floor(gpu.DefaultLimitWatts * percentage / 100m);
        var watts = Math.Max(gpu.MinimumLimitWatts, proportionalWatts);
        return new DesktopLiveTargetOption(percentage, watts, gpu.LimitKind);
    }

    public static DesktopLiveTargetOption? ResolveProfile(
        string? profileId,
        ReadOnlyGpuSnapshot gpu)
    {
        const string prefix = "percent-";
        if (string.IsNullOrWhiteSpace(profileId) ||
            !profileId.StartsWith(prefix, StringComparison.Ordinal) ||
            !int.TryParse(profileId.AsSpan(prefix.Length), out var percentage) ||
            profileId != $"{prefix}{percentage}")
        {
            return null;
        }

        try
        {
            return ResolvePercentage(percentage, gpu);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    public static DesktopLiveTargetOption? ResolveWatts(decimal watts, ReadOnlyGpuSnapshot gpu)
    {
        for (var percentage = GetMinimumPercentage(gpu);
             percentage <= MaximumPercentage;
             percentage++)
        {
            var option = ResolvePercentage(percentage, gpu);
            if (option.Watts == watts)
            {
                return option;
            }
        }

        return null;
    }

    public static DesktopLiveTargetValidationResult Validate(
        DesktopLiveTargetOption option,
        ReadOnlyGpuSnapshot gpu)
    {
        ArgumentNullException.ThrowIfNull(option);
        ArgumentNullException.ThrowIfNull(gpu);
        var known = ResolveProfile(option.ProfileId, gpu);
        if (known != option)
        {
            return new(false, "The selected power percentage is not canonical for this GPU.");
        }

        if (option.Watts < gpu.MinimumLimitWatts || option.Watts > gpu.MaximumLimitWatts)
        {
            return new(false, "The selected percentage is outside this GPU's driver limits.");
        }

        if (gpu.LimitKind == GpuPowerLimitKind.DefaultRelativePercentage &&
            (decimal.ToInt32(option.Watts - gpu.MinimumLimitWatts) % gpu.RelativePercentageStep) != 0)
        {
            return new(false, "The selected percentage does not match this AMD GPU's driver step.");
        }

        if (option.Watts >= gpu.DefaultLimitWatts)
        {
            return new(false, "A live pause target must be below the verified default limit.");
        }

        return gpu.LimitKind == GpuPowerLimitKind.DefaultRelativePercentage
            ? new(true, $"Ready for {option.Percentage}% of factory power; restore returns to the exact original AMD setting.")
            : new(
                true,
                $"Ready for {option.Percentage}% of default ({option.Watts:0.##} W); restore remains exactly {gpu.DefaultLimitWatts:0.##} W.");
    }
}
