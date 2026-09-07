using System.Text.Json;
using System.Text.Json.Serialization;
using EcoPause.Hardware.Abstractions;

namespace EcoPause.Desktop.Safety;

public sealed record ReadOnlyGpuSnapshot(
    uint Index,
    string Model,
    decimal CurrentLimitWatts,
    decimal DefaultLimitWatts,
    decimal MinimumLimitWatts,
    decimal MaximumLimitWatts,
    decimal? CurrentUsageWatts,
    DateTimeOffset ObservedAtUtc,
    GpuPowerLimitKind LimitKind = GpuPowerLimitKind.AbsoluteWatts,
    int RelativePercentageStep = 1);

public sealed record ReadOnlyGpuProbeResult(
    bool Ready,
    string Provider,
    string Message,
    IReadOnlyList<ReadOnlyGpuSnapshot> Devices);

public sealed class ReadOnlyGpuProbeDataException : Exception
{
    public ReadOnlyGpuProbeDataException(string message)
        : base(message)
    {
    }

    public ReadOnlyGpuProbeDataException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public static class ReadOnlyGpuProbeParser
{
    public const int MaximumJsonCharacters = 64 * 1024;

    private const int MaximumDevices = 16;
    private const decimal MaximumPlausibleWatts = 2_000m;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        MaxDepth = 8,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static ReadOnlyGpuProbeResult Parse(
        string json,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length is <= 0 or > MaximumJsonCharacters)
        {
            throw new ReadOnlyGpuProbeDataException("The read-only GPU report has an invalid size.");
        }

        if (observedAtUtc == default || observedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The GPU observation time must be non-default UTC.", nameof(observedAtUtc));
        }

        ProbeReportDto report;
        try
        {
            report = JsonSerializer.Deserialize<ProbeReportDto>(json, SerializerOptions)
                ?? throw new ReadOnlyGpuProbeDataException("The read-only GPU report is empty.");
        }
        catch (JsonException exception)
        {
            throw new ReadOnlyGpuProbeDataException(
                "The read-only GPU report did not match the strict sanitized contract.",
                exception);
        }

        var provider = RequireBoundedText(report.Provider, nameof(report.Provider), 32);
        var message = RequireBoundedText(report.Message, nameof(report.Message), 256);
        _ = RequireBoundedText(report.Backend, nameof(report.Backend), 32);

        if (!string.Equals(report.Status, "Ready", StringComparison.Ordinal))
        {
            if (report.Devices is { Count: > MaximumDevices })
            {
                throw new ReadOnlyGpuProbeDataException("The read-only GPU report contains too many devices.");
            }

            return new ReadOnlyGpuProbeResult(
                Ready: false,
                provider,
                message,
                Devices: []);
        }

        if (report.Devices is null || report.Devices.Count is <= 0 or > MaximumDevices)
        {
            throw new ReadOnlyGpuProbeDataException("A ready GPU report requires a bounded device list.");
        }

        var devices = new List<ReadOnlyGpuSnapshot>(report.Devices.Count);
        var indexes = new HashSet<uint>();
        foreach (var device in report.Devices)
        {
            if (!indexes.Add(device.Index))
            {
                throw new ReadOnlyGpuProbeDataException("The read-only GPU report contains a duplicate device index.");
            }

            var model = RequireBoundedText(device.Name, nameof(device.Name), 128);
            if (device.Power is null || !device.Power.IsSupported)
            {
                continue;
            }

            var limitKind = ParseLimitKind(device.Power.LimitKind);
            var currentLimit = limitKind == GpuPowerLimitKind.AbsoluteWatts
                ? RequireWatts(device.Power.CurrentLimit, "current limit")
                : RequirePercentage(device.Power.CurrentRelativePercentage, "current relative limit");
            var defaultLimit = limitKind == GpuPowerLimitKind.AbsoluteWatts
                ? RequireWatts(device.Power.DefaultLimit, "default limit")
                : RequirePercentage(device.Power.DefaultRelativePercentage, "default relative limit");
            var minimumLimit = limitKind == GpuPowerLimitKind.AbsoluteWatts
                ? RequireWatts(device.Power.MinimumLimit, "minimum limit")
                : RequirePercentage(device.Power.MinimumRelativePercentage, "minimum relative limit");
            var maximumLimit = limitKind == GpuPowerLimitKind.AbsoluteWatts
                ? RequireWatts(device.Power.MaximumLimit, "maximum limit")
                : RequirePercentage(device.Power.MaximumRelativePercentage, "maximum relative limit");
            var relativeStep = limitKind == GpuPowerLimitKind.AbsoluteWatts
                ? 1
                : RequirePercentageStep(device.Power.RelativePercentageStep);
            decimal? usage = device.Power.CurrentUsage is null
                ? null
                : RequireWatts(device.Power.CurrentUsage, "current usage");

            if (minimumLimit > maximumLimit ||
                currentLimit < minimumLimit || currentLimit > maximumLimit ||
                defaultLimit < minimumLimit || defaultLimit > maximumLimit)
            {
                throw new ReadOnlyGpuProbeDataException("The read-only GPU report contains inconsistent power constraints.");
            }

            devices.Add(new ReadOnlyGpuSnapshot(
                device.Index,
                model,
                currentLimit,
                defaultLimit,
                minimumLimit,
                maximumLimit,
                usage,
                observedAtUtc,
                limitKind,
                relativeStep));
        }

        if (devices.Count == 0)
        {
            return new ReadOnlyGpuProbeResult(
                Ready: false,
                provider,
                "GPU power information is unavailable.",
                Devices: []);
        }

        return new ReadOnlyGpuProbeResult(
            Ready: true,
            provider,
            message,
            devices.OrderBy(device => device.Index).ToArray());
    }

    private static string RequireBoundedText(string? value, string field, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
        {
            throw new ReadOnlyGpuProbeDataException($"The read-only GPU {field} field is invalid.");
        }

        return value;
    }

    private static decimal RequireWatts(WattValueDto? value, string field)
    {
        if (value is null || value.Value < 0 || value.Value > MaximumPlausibleWatts)
        {
            throw new ReadOnlyGpuProbeDataException($"The read-only GPU {field} is invalid.");
        }

        return value.Value;
    }

    private static GpuPowerLimitKind ParseLimitKind(string? value) => value switch
    {
        null or "AbsoluteWatts" => GpuPowerLimitKind.AbsoluteWatts,
        "DefaultRelativePercentage" => GpuPowerLimitKind.DefaultRelativePercentage,
        _ => throw new ReadOnlyGpuProbeDataException("The read-only GPU power-limit kind is invalid.")
    };

    private static decimal RequirePercentage(int? value, string field)
    {
        if (value is null or <= 0 or > 1_000)
        {
            throw new ReadOnlyGpuProbeDataException($"The read-only GPU {field} is invalid.");
        }
        return value.Value;
    }

    private static int RequirePercentageStep(int? value)
    {
        if (value is null or <= 0 or > 100)
        {
            throw new ReadOnlyGpuProbeDataException("The read-only GPU percentage step is invalid.");
        }
        return value.Value;
    }

    private sealed record ProbeReportDto(
        string? Provider,
        string? Backend,
        string? Status,
        string? Message,
        IReadOnlyList<GpuDeviceDto>? Devices);

    private sealed record GpuDeviceDto(
        uint Index,
        string? Name,
        GpuPowerDto? Power);

    private sealed record GpuPowerDto(
        bool IsSupported,
        string? LimitKind,
        WattValueDto? CurrentLimit,
        WattValueDto? DefaultLimit,
        WattValueDto? MinimumLimit,
        WattValueDto? MaximumLimit,
        int? CurrentRelativePercentage,
        int? DefaultRelativePercentage,
        int? MinimumRelativePercentage,
        int? MaximumRelativePercentage,
        int? RelativePercentageStep,
        WattValueDto? CurrentUsage,
        string? UnavailableReason);

    private sealed record WattValueDto(decimal Value);
}
