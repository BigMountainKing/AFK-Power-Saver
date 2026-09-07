using EcoPause.Core.Recovery;
using EcoPause.Hardware.Abstractions;

namespace EcoPause.Core.Activation;

public sealed class ActivationPlanningException : Exception
{
    public ActivationPlanningException(string message)
        : base(message)
    {
    }

    public ActivationPlanningException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed record DryRunActivationPlan(
    string JournalPath,
    string DeviceName,
    RecoverySnapshot Snapshot,
    bool HardwareWriteAttempted);

public sealed class DryRunActivationCoordinator
{
    private readonly IGpuHardwareProbe _gpuProbe;
    private readonly IRecoveryJournalStore _journalStore;
    private readonly TimeProvider _timeProvider;

    public DryRunActivationCoordinator(
        IGpuHardwareProbe gpuProbe,
        IRecoveryJournalStore? journalStore = null,
        TimeProvider? timeProvider = null)
    {
        _gpuProbe = gpuProbe ?? throw new ArgumentNullException(nameof(gpuProbe));
        _journalStore = journalStore ?? new FileRecoveryJournalStore();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<DryRunActivationPlan> CreatePlanAsync(
        uint gpuIndex,
        uint targetPowerLimitMilliwatts,
        string journalPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journalPath);

        var report = await _gpuProbe.ProbeAsync(cancellationToken);
        if (report.Status != GpuProbeStatus.Ready)
        {
            throw new ActivationPlanningException(
                $"The {report.Provider} backend is not ready: {report.Message}");
        }

        var device = report.Devices.SingleOrDefault(candidate => candidate.Index == gpuIndex)
            ?? throw new ActivationPlanningException($"GPU {gpuIndex} was not found.");

        if (!device.Power.IsSupported ||
            device.Power.CurrentLimit is null ||
            device.Power.MinimumLimit is null ||
            device.Power.MaximumLimit is null)
        {
            throw new ActivationPlanningException(
                device.Power.UnavailableReason ?? "Power-limit information is incomplete.");
        }

        if (string.IsNullOrWhiteSpace(device.DeviceFingerprintSha256))
        {
            throw new ActivationPlanningException(
                "The GPU does not expose the stable identity needed for safe recovery matching.");
        }

        RecoverySnapshot snapshot;
        try
        {
            snapshot = RecoverySnapshot.Prepare(
                _timeProvider.GetUtcNow(),
                report.Provider,
                report.Backend,
                new DeviceFingerprint(device.DeviceFingerprintSha256),
                ToMilliwatts(device.Power.CurrentLimit.Value),
                targetPowerLimitMilliwatts,
                ToMilliwatts(device.Power.MinimumLimit.Value),
                ToMilliwatts(device.Power.MaximumLimit.Value),
                purpose: RecoveryPurpose.DryRunPlan);
        }
        catch (ArgumentException exception)
        {
            throw new ActivationPlanningException("The requested dry-run plan failed safety validation.", exception);
        }

        var fullJournalPath = Path.GetFullPath(journalPath);
        try
        {
            await _journalStore.SaveAsync(fullJournalPath, snapshot, cancellationToken);
            var verifiedSnapshot = await _journalStore.LoadAsync(fullJournalPath, cancellationToken);

            if (verifiedSnapshot != snapshot)
            {
                throw new ActivationPlanningException("The persisted dry-run plan did not verify exactly.");
            }
        }
        catch (RecoveryJournalException exception)
        {
            throw new ActivationPlanningException("The dry-run recovery plan could not be verified.", exception);
        }
        catch (IOException exception)
        {
            throw new ActivationPlanningException("The dry-run recovery plan could not be persisted.", exception);
        }

        return new DryRunActivationPlan(fullJournalPath, device.Name, snapshot, HardwareWriteAttempted: false);
    }

    private static uint ToMilliwatts(Watts watts)
    {
        var milliwatts = watts.Value * 1000m;
        if (milliwatts != decimal.Truncate(milliwatts) ||
            milliwatts < 0 ||
            milliwatts > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(watts), "The reported power value cannot be represented exactly in milliwatts.");
        }

        return (uint)milliwatts;
    }
}
