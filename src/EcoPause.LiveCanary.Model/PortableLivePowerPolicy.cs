using EcoPause.Core.Recovery;
using EcoPause.Hardware.Abstractions;

namespace EcoPause.LiveCanary.Model;

public sealed record PortableLivePowerRequest(int Percentage)
{
    public string Id => $"percent-{Percentage}";
}

public static class PortableLivePowerPolicy
{
    public const int LowestPercentage = 1;
    public const int HighestPercentage = 99;
    public const int DefaultPercentage = 89;

    public static PortableLivePowerRequest Default { get; } = new(DefaultPercentage);

    public static PortableLivePowerRequest ParseRequest(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        const string prefix = "percent-";
        if (!id.StartsWith(prefix, StringComparison.Ordinal) ||
            !int.TryParse(id.AsSpan(prefix.Length), out var percentage) ||
            percentage is < LowestPercentage or > HighestPercentage ||
            id != $"{prefix}{percentage}")
        {
            throw new ArgumentException("The portable power percentage is not allow-listed.", nameof(id));
        }

        return new PortableLivePowerRequest(percentage);
    }

    public static int GetMinimumPercentage(LiveCanaryDeviceState device)
    {
        ArgumentNullException.ThrowIfNull(device);
        var percentage = (int)((ulong)device.MinimumLimitMilliwatts * 100 /
            device.DefaultLimitMilliwatts);
        return Math.Clamp(percentage, LowestPercentage, HighestPercentage);
    }

    public static LivePowerProfile ResolveForDevice(
        PortableLivePowerRequest request,
        LiveCanaryDeviceState device)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(device);
        if (request.Percentage < GetMinimumPercentage(device) ||
            request.Percentage > HighestPercentage)
        {
            throw new InvalidOperationException("The requested percentage is outside this GPU's lower-limit range.");
        }

        var proportional = (uint)Math.Min(
            uint.MaxValue,
            (ulong)device.DefaultLimitMilliwatts * (uint)request.Percentage / 100);
        var wholeWatts = proportional / 1000 * 1000;
        var target = Math.Max(device.MinimumLimitMilliwatts, wholeWatts);
        if (target < device.MinimumLimitMilliwatts ||
            target > device.MaximumLimitMilliwatts ||
            target >= device.DefaultLimitMilliwatts ||
            (target - device.MinimumLimitMilliwatts) % device.LimitStepMilliwatts != 0)
        {
            throw new InvalidOperationException("The requested percentage did not resolve to a safe lower power limit.");
        }

        return new LivePowerProfile(request.Id, target);
    }

    public static LivePowerProfile ResolveIdForDevice(string id, LiveCanaryDeviceState device) =>
        ResolveForDevice(ParseRequest(id), device);

    public static LivePowerProfile ResolveTargetForDevice(
        uint targetLimitMilliwatts,
        LiveCanaryDeviceState device)
    {
        ArgumentNullException.ThrowIfNull(device);
        for (var percentage = GetMinimumPercentage(device);
             percentage <= HighestPercentage;
             percentage++)
        {
            LivePowerProfile profile;
            try
            {
                profile = ResolveForDevice(new PortableLivePowerRequest(percentage), device);
            }
            catch (InvalidOperationException)
            {
                continue;
            }
            if (profile.TargetLimitMilliwatts == targetLimitMilliwatts)
            {
                return profile;
            }
        }

        throw new ArgumentException(
            "The live power target is not a valid percentage for this GPU.",
            nameof(targetLimitMilliwatts));
    }

    public static bool IsRecoverableTarget(uint targetLimitMilliwatts, LiveCanaryDeviceState device)
    {
        try
        {
            _ = ResolveTargetForDevice(targetLimitMilliwatts, device);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static LiveCanaryDeviceState ValidateProbeReport(GpuProbeReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.Status != GpuProbeStatus.Ready || report.Devices.Count != 1)
        {
            throw new InvalidOperationException("Portable live control requires exactly one ready supported GPU.");
        }

        var device = report.Devices[0];
        if (!device.Power.IsSupported || device.DeviceFingerprintSha256 is null)
        {
            throw new InvalidOperationException("The GPU does not expose every required power-limit value.");
        }

        if (string.Equals(report.Provider, LiveCanaryPolicy.Provider, StringComparison.Ordinal) &&
            string.Equals(report.Backend, "NVML", StringComparison.Ordinal) &&
            (device.Power.CurrentLimit is null ||
             device.Power.DefaultLimit is null ||
             device.Power.MinimumLimit is null ||
             device.Power.MaximumLimit is null))
        {
            throw new InvalidOperationException("The GPU does not expose every required power-limit value.");
        }

        if (string.Equals(report.Provider, LiveCanaryPolicy.Provider, StringComparison.Ordinal) &&
            string.Equals(report.Backend, "NVML", StringComparison.Ordinal))
        {
            return ValidateDevice(
                device.Name,
                new DeviceFingerprint(device.DeviceFingerprintSha256),
                ToMilliwatts(device.Power.CurrentLimit!.Value),
                ToMilliwatts(device.Power.DefaultLimit!.Value),
                ToMilliwatts(device.Power.MinimumLimit!.Value),
                ToMilliwatts(device.Power.MaximumLimit!.Value));
        }

        if (string.Equals(report.Provider, "AMD", StringComparison.Ordinal) &&
            string.Equals(report.Backend, "ADLX", StringComparison.Ordinal) &&
            device.Power.LimitKind == GpuPowerLimitKind.DefaultRelativePercentage &&
            device.Power.CurrentRelativePercentage is int current &&
            device.Power.DefaultRelativePercentage is 100 &&
            device.Power.MinimumRelativePercentage is int minimum &&
            device.Power.MaximumRelativePercentage is int maximum &&
            device.Power.RelativePercentageStep is int step)
        {
            var state = ValidateDevice(
                device.Name,
                new DeviceFingerprint(device.DeviceFingerprintSha256),
                EncodePercentage(current),
                EncodePercentage(100),
                EncodePercentage(minimum),
                EncodePercentage(maximum));
            return state with
            {
                Provider = "AMD",
                Backend = "ADLX-ManualPowerTuning",
                LimitKind = GpuPowerLimitKind.DefaultRelativePercentage,
                LimitStepMilliwatts = EncodePercentage(step),
                StateDirectoryName = "AFKPowerSaverAmdAdlx"
            };
        }

        throw new InvalidOperationException("The detected GPU backend is not supported for portable live control.");
    }

    public static LiveCanaryDeviceState ValidateDevice(
        string name,
        DeviceFingerprint deviceFingerprint,
        uint currentLimitMilliwatts,
        uint defaultLimitMilliwatts,
        uint minimumLimitMilliwatts,
        uint maximumLimitMilliwatts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(deviceFingerprint);
        if (name.Length > 96 || name.Any(char.IsControl) ||
            minimumLimitMilliwatts == 0 ||
            minimumLimitMilliwatts >= defaultLimitMilliwatts ||
            defaultLimitMilliwatts > maximumLimitMilliwatts)
        {
            throw new InvalidOperationException("The GPU power-limit constraints are not suitable for portable control.");
        }

        var state = new LiveCanaryDeviceState(
            name,
            deviceFingerprint,
            currentLimitMilliwatts,
            defaultLimitMilliwatts,
            minimumLimitMilliwatts,
            maximumLimitMilliwatts);
        if (currentLimitMilliwatts != defaultLimitMilliwatts &&
            !IsRecoverableTarget(currentLimitMilliwatts, state))
        {
            throw new InvalidOperationException(
                "The current GPU limit is neither its default nor a recoverable percentage target.");
        }

        return state;
    }

    public static RecoverySnapshot CreateSnapshot(
        LiveCanaryDeviceState device,
        LivePowerProfile profile,
        DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(profile);
        var canonical = ResolveIdForDevice(profile.Id, device);
        if (canonical != profile)
        {
            throw new InvalidOperationException("The resolved percentage profile is not canonical for this GPU.");
        }

        return RecoverySnapshot.Prepare(
            createdAtUtc,
            device.Provider,
            device.Backend,
            device.DeviceFingerprint,
            device.DefaultLimitMilliwatts,
            profile.TargetLimitMilliwatts,
            device.MinimumLimitMilliwatts,
            device.MaximumLimitMilliwatts);
    }

    public static LivePowerProfile ValidateRecoverySnapshot(
        RecoverySnapshot snapshot,
        LiveCanaryDeviceState device,
        bool allowClosed = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(device);
        LivePowerProfile profile;
        try
        {
            profile = ResolveTargetForDevice(snapshot.TargetPowerLimitMilliwatts, device);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException(
                "The protected live journal contains a non-percentage target.", exception);
        }

        if (snapshot.Purpose != RecoveryPurpose.LiveRecovery ||
            (!allowClosed && snapshot.Stage == RecoveryStage.Restored) ||
            !string.Equals(snapshot.Provider, device.Provider, StringComparison.Ordinal) ||
            !string.Equals(snapshot.Backend, device.Backend, StringComparison.Ordinal) ||
            snapshot.DeviceFingerprint != device.DeviceFingerprint ||
            snapshot.OriginalPowerLimitMilliwatts != device.DefaultLimitMilliwatts ||
            snapshot.MinimumPowerLimitMilliwatts != device.MinimumLimitMilliwatts ||
            snapshot.MaximumPowerLimitMilliwatts != device.MaximumLimitMilliwatts ||
            device.CurrentLimitMilliwatts != device.DefaultLimitMilliwatts &&
                device.CurrentLimitMilliwatts != profile.TargetLimitMilliwatts)
        {
            throw new InvalidOperationException("The protected percentage journal is not an exact recoverable snapshot.");
        }

        return profile;
    }

    private static uint ToMilliwatts(Watts watts)
    {
        var milliwatts = watts.Value * 1000m;
        if (milliwatts != decimal.Truncate(milliwatts) ||
            milliwatts <= 0 ||
            milliwatts > uint.MaxValue)
        {
            throw new InvalidOperationException("A power-limit value cannot be represented exactly in milliwatts.");
        }

        return decimal.ToUInt32(milliwatts);
    }

    private static uint EncodePercentage(int percentage)
    {
        if (percentage is <= 0 or > 1_000)
        {
            throw new InvalidOperationException("A default-relative power percentage is outside the supported range.");
        }

        return checked((uint)percentage * 1_000);
    }
}
