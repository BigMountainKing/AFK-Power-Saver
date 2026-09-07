using EcoPause.Core.Activation;
using EcoPause.Core.Recovery;
using EcoPause.Hardware.Abstractions;

namespace EcoPause.LiveCanary.Model;

public sealed record LiveCanaryDeviceState(
    string Name,
    DeviceFingerprint DeviceFingerprint,
    uint CurrentLimitMilliwatts,
    uint DefaultLimitMilliwatts,
    uint MinimumLimitMilliwatts,
    uint MaximumLimitMilliwatts)
{
    public string Provider { get; init; } = LiveCanaryPolicy.Provider;

    public string Backend { get; init; } = LiveCanaryPolicy.Backend;

    public GpuPowerLimitKind LimitKind { get; init; } = GpuPowerLimitKind.AbsoluteWatts;

    public uint LimitStepMilliwatts { get; init; } = 1_000;

    public string StateDirectoryName { get; init; } = LiveCanaryPolicy.StateDirectoryName;

    public bool RecoveryMayBePending =>
        CurrentLimitMilliwatts != DefaultLimitMilliwatts;

    public bool CanStartNewCanary =>
        CurrentLimitMilliwatts == LiveCanaryPolicy.RequiredOriginalLimitMilliwatts;
}

public interface ILiveGpuPowerController : IGpuPowerController, IDisposable
{
    LiveCanaryDeviceState State { get; }
}

public static class LiveCanaryPolicy
{
    public const string Provider = "NVIDIA";
    public const string Backend = "NVML-Live-Canary";
    public const string StateDirectoryName = "EcoPauseLiveCanary";
    public const string JournalFileName = "recovery.json";
    public const string GlobalHardwareLockName = @"Global\EcoPauseLiveCanaryHardwareLock";
    public const uint RequiredOriginalLimitMilliwatts = 450_000;
    public const uint TargetLimitMilliwatts = 400_000;
    public const int HoldDurationMilliseconds = 5_000;
    public const int ResultTimeoutMilliseconds = 15_000;
    public const string ConfirmationPhrase = "APPLY 400 W THEN RESTORE 450 W";
    public const string RecoveryDrillConfirmationPhrase = "INTERRUPT AT 400 W THEN RECOVER 450 W";
    public const string PersistentToggleConfirmationPhrase = "TOGGLE LIVE GPU BETWEEN 400 W AND 450 W";
    public const int IntentionalRecoveryDrillExitCode = 91;

    public static LiveCanaryDeviceState ValidateProbeReport(GpuProbeReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.Status != GpuProbeStatus.Ready ||
            !string.Equals(report.Provider, Provider, StringComparison.Ordinal) ||
            !string.Equals(report.Backend, "NVML", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The NVIDIA read-only preflight is not ready.");
        }

        if (report.Devices.Count != 1)
        {
            throw new InvalidOperationException("The live canary requires exactly one NVIDIA GPU.");
        }

        var device = report.Devices[0];
        if (!device.Power.IsSupported ||
            device.Power.CurrentLimit is null ||
            device.Power.DefaultLimit is null ||
            device.Power.MinimumLimit is null ||
            device.Power.MaximumLimit is null ||
            device.DeviceFingerprintSha256 is null)
        {
            throw new InvalidOperationException("The GPU does not expose every required power-limit preflight value.");
        }

        return ValidateDevice(
            device.Name,
            new DeviceFingerprint(device.DeviceFingerprintSha256),
            ToMilliwatts(device.Power.CurrentLimit.Value),
            ToMilliwatts(device.Power.DefaultLimit.Value),
            ToMilliwatts(device.Power.MinimumLimit.Value),
            ToMilliwatts(device.Power.MaximumLimit.Value));
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
        if (name.Length > 96 || name.Any(char.IsControl))
        {
            throw new InvalidOperationException("The GPU name is not safe to display.");
        }

        if (minimumLimitMilliwatts > maximumLimitMilliwatts ||
            TargetLimitMilliwatts < minimumLimitMilliwatts ||
            RequiredOriginalLimitMilliwatts > maximumLimitMilliwatts)
        {
            throw new InvalidOperationException("The fixed canary limits are outside the live driver constraints.");
        }

        if (defaultLimitMilliwatts != RequiredOriginalLimitMilliwatts)
        {
            throw new InvalidOperationException("The GPU default limit is not the canary's required 450 W baseline.");
        }

        if (currentLimitMilliwatts != RequiredOriginalLimitMilliwatts &&
            (!LivePowerProfiles.IsKnownTarget(currentLimitMilliwatts) ||
             currentLimitMilliwatts < minimumLimitMilliwatts ||
             currentLimitMilliwatts > maximumLimitMilliwatts))
        {
            throw new InvalidOperationException("The current GPU limit is neither the 450 W baseline nor an allow-listed recoverable target.");
        }

        return new LiveCanaryDeviceState(
            name,
            deviceFingerprint,
            currentLimitMilliwatts,
            defaultLimitMilliwatts,
            minimumLimitMilliwatts,
            maximumLimitMilliwatts);
    }

    public static RecoverySnapshot CreateSnapshot(
        LiveCanaryDeviceState device,
        DateTimeOffset createdAtUtc) =>
        CreateSnapshot(device, LivePowerProfiles.Default, createdAtUtc);

    public static RecoverySnapshot CreateSnapshot(
        LiveCanaryDeviceState device,
        LivePowerProfile profile,
        DateTimeOffset createdAtUtc)
    {
        LivePowerProfiles.ValidateForDevice(profile, device);
        return RecoverySnapshot.Prepare(
            createdAtUtc,
            Provider,
            Backend,
            device.DeviceFingerprint,
            RequiredOriginalLimitMilliwatts,
            profile.TargetLimitMilliwatts,
            device.MinimumLimitMilliwatts,
            device.MaximumLimitMilliwatts);
    }

    public static void ValidateRecoverySnapshot(
        RecoverySnapshot snapshot,
        LiveCanaryDeviceState device)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(device);
        LivePowerProfile profile;
        try
        {
            profile = LivePowerProfiles.ResolveTarget(snapshot.TargetPowerLimitMilliwatts);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException("The protected live journal contains a non-allow-listed target.", exception);
        }

        if (snapshot.Purpose != RecoveryPurpose.LiveRecovery ||
            snapshot.Stage == RecoveryStage.Restored ||
            !string.Equals(snapshot.Provider, Provider, StringComparison.Ordinal) ||
            !string.Equals(snapshot.Backend, Backend, StringComparison.Ordinal) ||
            snapshot.DeviceFingerprint != device.DeviceFingerprint ||
            snapshot.OriginalPowerLimitMilliwatts != RequiredOriginalLimitMilliwatts ||
            snapshot.MinimumPowerLimitMilliwatts > profile.TargetLimitMilliwatts ||
            snapshot.MaximumPowerLimitMilliwatts < RequiredOriginalLimitMilliwatts)
        {
            throw new InvalidOperationException("The protected live-canary journal is not an exact recoverable snapshot.");
        }

        if (device.CurrentLimitMilliwatts != RequiredOriginalLimitMilliwatts &&
            device.CurrentLimitMilliwatts != profile.TargetLimitMilliwatts)
        {
            throw new InvalidOperationException("The live GPU is not at a recoverable canary limit.");
        }
    }

    private static uint ToMilliwatts(Watts watts)
    {
        var milliwatts = watts.Value * 1000m;
        if (milliwatts != decimal.Truncate(milliwatts) ||
            milliwatts < 0 ||
            milliwatts > uint.MaxValue)
        {
            throw new InvalidOperationException("A power-limit value cannot be represented exactly in milliwatts.");
        }

        return decimal.ToUInt32(milliwatts);
    }
}
