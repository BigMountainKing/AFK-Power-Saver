using EcoPause.Core.Recovery;

namespace EcoPause.Core.Activation;

public interface IGpuPowerController
{
    DeviceFingerprint DeviceFingerprint { get; }

    Task<uint> ReadPowerLimitMilliwattsAsync(CancellationToken cancellationToken = default);

    Task SetPowerLimitMilliwattsAsync(
        uint powerLimitMilliwatts,
        CancellationToken cancellationToken = default);
}

public sealed class PowerControlException : Exception
{
    public PowerControlException(string message)
        : base(message)
    {
    }
}

public enum ActivationOutcome
{
    Activated,
    Restored,
    AlreadyRestored,
    FailedBeforeWrite,
    FailedRolledBack,
    FailedRollbackIncomplete
}

public sealed record ActivationResult(
    ActivationOutcome Outcome,
    RecoverySnapshot Snapshot,
    bool HardwareWriteAttempted,
    bool OriginalLimitVerified,
    bool JournalVerified,
    string Message);

public sealed class GpuActivationEngine
{
    private readonly IRecoveryJournalStore _journalStore;
    private readonly TimeProvider _timeProvider;

    public GpuActivationEngine(
        IRecoveryJournalStore? journalStore = null,
        TimeProvider? timeProvider = null)
    {
        _journalStore = journalStore ?? new FileRecoveryJournalStore();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ActivationResult> ActivateAsync(
        RecoverySnapshot preparedSnapshot,
        string journalPath,
        IGpuPowerController controller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preparedSnapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(journalPath);
        ArgumentNullException.ThrowIfNull(controller);

        if (preparedSnapshot.Purpose != RecoveryPurpose.LiveRecovery ||
            preparedSnapshot.Stage != RecoveryStage.Prepared)
        {
            throw new ArgumentException(
                "Activation requires a prepared live-recovery snapshot.",
                nameof(preparedSnapshot));
        }

        if (controller.DeviceFingerprint != preparedSnapshot.DeviceFingerprint)
        {
            return FailureBeforeWrite(preparedSnapshot, "The controller identity does not match the recovery snapshot.");
        }

        var fullJournalPath = Path.GetFullPath(journalPath);
        var workingSnapshot = preparedSnapshot;
        var hardwareWriteAttempted = false;

        try
        {
            await _journalStore.SaveAsync(fullJournalPath, workingSnapshot, cancellationToken);

            var observedBeforeApply = await controller.ReadPowerLimitMilliwattsAsync(cancellationToken);
            if (observedBeforeApply != workingSnapshot.OriginalPowerLimitMilliwatts)
            {
                return FailureBeforeWrite(
                    workingSnapshot,
                    $"Pre-apply validation failed. Expected {workingSnapshot.OriginalPowerLimitMilliwatts} mW, " +
                    $"but observed {observedBeforeApply} mW.",
                    journalVerified: true);
            }

            hardwareWriteAttempted = true;
            await controller.SetPowerLimitMilliwattsAsync(
                workingSnapshot.TargetPowerLimitMilliwatts,
                cancellationToken);

            var observedAfterApply = await controller.ReadPowerLimitMilliwattsAsync(cancellationToken);
            workingSnapshot = workingSnapshot.MarkApplied(
                observedAfterApply,
                _timeProvider.GetUtcNow());

            await _journalStore.SaveAsync(fullJournalPath, workingSnapshot, cancellationToken);

            return new ActivationResult(
                ActivationOutcome.Activated,
                workingSnapshot,
                HardwareWriteAttempted: true,
                OriginalLimitVerified: false,
                JournalVerified: true,
                "The target was applied, read back exactly, and recorded as recovery-pending.");
        }
#pragma warning disable CA1031 // Every non-fatal failure after a possible write must enter rollback.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            if (!hardwareWriteAttempted)
            {
                return FailureBeforeWrite(workingSnapshot, $"Activation stopped before a hardware write: {exception.Message}");
            }

            return await RollBackAfterFailureAsync(
                workingSnapshot,
                fullJournalPath,
                controller,
                exception);
        }
    }

    public Task<ActivationResult> RestoreAsync(
        string journalPath,
        IGpuPowerController controller,
        CancellationToken cancellationToken = default) =>
        RestoreInternalAsync(journalPath, controller, expectedSnapshot: null, cancellationToken);

    public Task<ActivationResult> RestoreExpectedAsync(
        string journalPath,
        RecoverySnapshot expectedSnapshot,
        IGpuPowerController controller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedSnapshot);
        return RestoreInternalAsync(journalPath, controller, expectedSnapshot, cancellationToken);
    }

    private async Task<ActivationResult> RestoreInternalAsync(
        string journalPath,
        IGpuPowerController controller,
        RecoverySnapshot? expectedSnapshot,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journalPath);
        ArgumentNullException.ThrowIfNull(controller);

        var fullJournalPath = Path.GetFullPath(journalPath);
        var snapshot = await _journalStore.LoadAsync(fullJournalPath, cancellationToken);

        if (expectedSnapshot is not null && snapshot != expectedSnapshot)
        {
            return FailureBeforeWrite(
                snapshot,
                "The recovery journal changed after approval and will not be restored.",
                journalVerified: true);
        }

        if (snapshot.Purpose != RecoveryPurpose.LiveRecovery)
        {
            throw new InvalidOperationException("A dry-run plan cannot be restored as live hardware state.");
        }

        if (controller.DeviceFingerprint != snapshot.DeviceFingerprint)
        {
            return FailureBeforeWrite(snapshot, "The controller identity does not match the recovery snapshot.", journalVerified: true);
        }

        if (snapshot.Stage == RecoveryStage.Restored)
        {
            return new ActivationResult(
                ActivationOutcome.AlreadyRestored,
                snapshot,
                HardwareWriteAttempted: false,
                OriginalLimitVerified: true,
                JournalVerified: true,
                "The recovery journal is already closed.");
        }

        var hardwareWriteAttempted = false;
        var originalLimitVerified = false;
        var resultSnapshot = snapshot;

        try
        {
            var observed = await controller.ReadPowerLimitMilliwattsAsync(cancellationToken);
            if (observed != snapshot.OriginalPowerLimitMilliwatts)
            {
                hardwareWriteAttempted = true;
                await controller.SetPowerLimitMilliwattsAsync(
                    snapshot.OriginalPowerLimitMilliwatts,
                    cancellationToken);
                observed = await controller.ReadPowerLimitMilliwattsAsync(cancellationToken);
            }

            resultSnapshot = snapshot.MarkRestored(observed, _timeProvider.GetUtcNow());
            originalLimitVerified = true;
            await _journalStore.SaveAsync(fullJournalPath, resultSnapshot, cancellationToken);

            return new ActivationResult(
                ActivationOutcome.Restored,
                resultSnapshot,
                hardwareWriteAttempted,
                OriginalLimitVerified: true,
                JournalVerified: true,
                "The exact original limit was verified and the recovery journal was closed.");
        }
#pragma warning disable CA1031 // Restoration failures must return a recovery-pending result.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            return new ActivationResult(
                ActivationOutcome.FailedRollbackIncomplete,
                resultSnapshot,
                hardwareWriteAttempted,
                originalLimitVerified,
                JournalVerified: !originalLimitVerified,
                $"Restoration remains incomplete: {exception.Message}");
        }
    }

    private async Task<ActivationResult> RollBackAfterFailureAsync(
        RecoverySnapshot snapshot,
        string journalPath,
        IGpuPowerController controller,
        Exception activationException)
    {
        try
        {
            await controller.SetPowerLimitMilliwattsAsync(
                snapshot.OriginalPowerLimitMilliwatts,
                CancellationToken.None);
            var observed = await controller.ReadPowerLimitMilliwattsAsync(CancellationToken.None);
            var restored = snapshot.MarkRestored(observed, _timeProvider.GetUtcNow());

            try
            {
                await _journalStore.SaveAsync(journalPath, restored, CancellationToken.None);
            }
#pragma warning disable CA1031 // Report verified hardware separately from failed journal persistence.
            catch (Exception persistenceException)
#pragma warning restore CA1031
            {
                return new ActivationResult(
                    ActivationOutcome.FailedRollbackIncomplete,
                    restored,
                    HardwareWriteAttempted: true,
                    OriginalLimitVerified: true,
                    JournalVerified: false,
                    $"Activation failed ({activationException.Message}); the original limit was restored, " +
                    $"but the journal could not be closed ({persistenceException.Message}).");
            }

            return new ActivationResult(
                ActivationOutcome.FailedRolledBack,
                restored,
                HardwareWriteAttempted: true,
                OriginalLimitVerified: true,
                JournalVerified: true,
                $"Activation failed and the exact original limit was restored: {activationException.Message}");
        }
#pragma warning disable CA1031 // A failed rollback must be surfaced without masking the activation failure.
        catch (Exception rollbackException)
#pragma warning restore CA1031
        {
            return new ActivationResult(
                ActivationOutcome.FailedRollbackIncomplete,
                snapshot,
                HardwareWriteAttempted: true,
                OriginalLimitVerified: false,
                JournalVerified: false,
                $"Activation failed ({activationException.Message}) and rollback did not verify " +
                $"({rollbackException.Message}). Recovery remains pending.");
        }
    }

    private static ActivationResult FailureBeforeWrite(
        RecoverySnapshot snapshot,
        string message,
        bool journalVerified = false) =>
        new(
            ActivationOutcome.FailedBeforeWrite,
            snapshot,
            HardwareWriteAttempted: false,
            OriginalLimitVerified: false,
            journalVerified,
            message);
}
