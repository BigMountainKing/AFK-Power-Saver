using System.Text.Json.Serialization;

namespace EcoPause.Hardware.Abstractions;

public enum GpuProbeStatus
{
    Ready,
    BackendUnavailable,
    InitializationFailed,
    QueryFailed
}

public enum GpuPowerLimitKind
{
    AbsoluteWatts,
    DefaultRelativePercentage
}

public readonly record struct Watts
{
    public Watts(decimal value)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Power cannot be negative.");
        }

        Value = value;
    }

    public decimal Value { get; }

    public static Watts FromMilliwatts(uint milliwatts) => new(milliwatts / 1000m);

    public override string ToString() => $"{Value:0.##} W";
}

public sealed record GpuPowerCapabilities
{
    private GpuPowerCapabilities(
        bool isSupported,
        GpuPowerLimitKind limitKind,
        Watts? currentLimit,
        Watts? defaultLimit,
        Watts? minimumLimit,
        Watts? maximumLimit,
        int? currentRelativePercentage,
        int? defaultRelativePercentage,
        int? minimumRelativePercentage,
        int? maximumRelativePercentage,
        int? relativePercentageStep,
        Watts? currentUsage,
        string? unavailableReason)
    {
        IsSupported = isSupported;
        LimitKind = limitKind;
        CurrentLimit = currentLimit;
        DefaultLimit = defaultLimit;
        MinimumLimit = minimumLimit;
        MaximumLimit = maximumLimit;
        CurrentRelativePercentage = currentRelativePercentage;
        DefaultRelativePercentage = defaultRelativePercentage;
        MinimumRelativePercentage = minimumRelativePercentage;
        MaximumRelativePercentage = maximumRelativePercentage;
        RelativePercentageStep = relativePercentageStep;
        CurrentUsage = currentUsage;
        UnavailableReason = unavailableReason;
    }

    public bool IsSupported { get; }

    public GpuPowerLimitKind LimitKind { get; }

    public Watts? CurrentLimit { get; }

    public Watts? DefaultLimit { get; }

    public Watts? MinimumLimit { get; }

    public Watts? MaximumLimit { get; }

    public int? CurrentRelativePercentage { get; }

    public int? DefaultRelativePercentage { get; }

    public int? MinimumRelativePercentage { get; }

    public int? MaximumRelativePercentage { get; }

    public int? RelativePercentageStep { get; }

    public Watts? CurrentUsage { get; }

    public string? UnavailableReason { get; }

    public static GpuPowerCapabilities Supported(
        Watts currentLimit,
        Watts defaultLimit,
        Watts minimumLimit,
        Watts maximumLimit,
        Watts? currentUsage = null)
    {
        if (minimumLimit.Value > maximumLimit.Value)
        {
            throw new ArgumentException("The minimum power limit cannot exceed the maximum power limit.");
        }

        return new(
            true,
            GpuPowerLimitKind.AbsoluteWatts,
            currentLimit,
            defaultLimit,
            minimumLimit,
            maximumLimit,
            null,
            null,
            null,
            null,
            null,
            currentUsage,
            null);
    }

    public static GpuPowerCapabilities SupportedDefaultRelativePercentage(
        int currentPercentage,
        int minimumPercentage,
        int maximumPercentage,
        int percentageStep,
        Watts? currentUsage = null)
    {
        const int defaultPercentage = 100;
        if (minimumPercentage <= 0 ||
            minimumPercentage >= defaultPercentage ||
            maximumPercentage < defaultPercentage ||
            currentPercentage < minimumPercentage ||
            currentPercentage > maximumPercentage ||
            percentageStep <= 0)
        {
            throw new ArgumentException("The default-relative power-limit range is invalid.");
        }

        return new(
            true,
            GpuPowerLimitKind.DefaultRelativePercentage,
            null,
            null,
            null,
            null,
            currentPercentage,
            defaultPercentage,
            minimumPercentage,
            maximumPercentage,
            percentageStep,
            currentUsage,
            null);
    }

    public static GpuPowerCapabilities Unsupported(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new(
            false,
            GpuPowerLimitKind.AbsoluteWatts,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            reason);
    }
}

public sealed record GpuDeviceInfo(
    uint Index,
    string Name,
    GpuPowerCapabilities Power,
    [property: JsonIgnore] string? DeviceFingerprintSha256 = null);

public sealed record GpuProbeReport(
    string Provider,
    string Backend,
    GpuProbeStatus Status,
    string Message,
    IReadOnlyList<GpuDeviceInfo> Devices);

public interface IGpuHardwareProbe
{
    string ProviderName { get; }

    Task<GpuProbeReport> ProbeAsync(CancellationToken cancellationToken = default);
}
