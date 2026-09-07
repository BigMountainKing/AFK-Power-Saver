using EcoPause.Core.Activation;
using EcoPause.Core.Recovery;

namespace EcoPause.HardwareHost.Protocol;

public sealed record HardwareHostTarget(
    string JournalPath,
    RecoverySnapshot Snapshot,
    IGpuPowerController Controller);

public interface IHardwareHostTargetRegistry
{
    Task<HardwareHostTarget?> ResolveAsync(
        Guid snapshotId,
        CancellationToken cancellationToken = default);
}

public enum HardwareHostProcessOutcome
{
    Executed,
    RejectedSession,
    RejectedProtocol,
    RejectedAuthentication,
    RejectedExpired,
    RejectedReplay,
    RejectedSequence,
    RejectedTarget,
    ExecutionFailed
}

public sealed record HardwareHostProcessResult(
    HardwareHostProcessOutcome Outcome,
    string Message,
    ActivationResult? Execution)
{
    public bool CommandAccepted =>
        Outcome is HardwareHostProcessOutcome.Executed or HardwareHostProcessOutcome.ExecutionFailed;
}

public sealed class HardwareHostCommandProcessor
{
    private readonly HardwareHostSessionState _session;
    private readonly IHardwareHostTargetRegistry _targetRegistry;
    private readonly GpuActivationEngine _activationEngine;
    private readonly TimeProvider _timeProvider;

    public HardwareHostCommandProcessor(
        HardwareHostSessionState session,
        IHardwareHostTargetRegistry targetRegistry,
        GpuActivationEngine activationEngine,
        TimeProvider? timeProvider = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _targetRegistry = targetRegistry ?? throw new ArgumentNullException(nameof(targetRegistry));
        _activationEngine = activationEngine ?? throw new ArgumentNullException(nameof(activationEngine));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<HardwareHostProcessResult> ProcessAsync(
        HardwareHostCommandEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        var validation = _session.ValidateAndConsume(envelope, _timeProvider.GetUtcNow());
        if (!validation.Accepted)
        {
            return new HardwareHostProcessResult(
                MapValidationOutcome(validation.Outcome),
                validation.Message,
                Execution: null);
        }

        HardwareHostTarget? target;
        try
        {
            target = await _targetRegistry.ResolveAsync(
                envelope.Command.SnapshotId,
                cancellationToken);
        }
#pragma warning disable CA1031 // A consumed command must return a bounded host error.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            return new HardwareHostProcessResult(
                HardwareHostProcessOutcome.ExecutionFailed,
                $"The host target registry failed: {exception.Message}",
                Execution: null);
        }

        if (target is null ||
            target.Snapshot.SnapshotId != envelope.Command.SnapshotId ||
            target.Controller.DeviceFingerprint != target.Snapshot.DeviceFingerprint ||
            target.Snapshot.Purpose != RecoveryPurpose.LiveRecovery)
        {
            return new HardwareHostProcessResult(
                HardwareHostProcessOutcome.RejectedTarget,
                "The host could not resolve an exact trusted target for the command.",
                Execution: null);
        }

        try
        {
            var execution = envelope.Command.Operation switch
            {
                HardwareHostOperation.ActivatePrepared
                    when target.Snapshot.Stage == RecoveryStage.Prepared =>
                    await _activationEngine.ActivateAsync(
                        target.Snapshot,
                        target.JournalPath,
                        target.Controller,
                        cancellationToken),
                HardwareHostOperation.RestorePending
                    when target.Snapshot.IsRecoveryPending =>
                    await _activationEngine.RestoreExpectedAsync(
                        target.JournalPath,
                        target.Snapshot,
                        target.Controller,
                        cancellationToken),
                _ => null
            };

            if (execution is null)
            {
                return new HardwareHostProcessResult(
                    HardwareHostProcessOutcome.RejectedTarget,
                    "The requested operation is not valid for the host-resolved recovery stage.",
                    Execution: null);
            }

            return new HardwareHostProcessResult(
                HardwareHostProcessOutcome.Executed,
                execution.Message,
                execution);
        }
#pragma warning disable CA1031 // The helper boundary returns bounded failures instead of crashing.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            return new HardwareHostProcessResult(
                HardwareHostProcessOutcome.ExecutionFailed,
                $"The accepted command failed safely: {exception.Message}",
                Execution: null);
        }
    }

    private static HardwareHostProcessOutcome MapValidationOutcome(
        HardwareHostValidationOutcome outcome) => outcome switch
        {
            HardwareHostValidationOutcome.SessionUnavailable => HardwareHostProcessOutcome.RejectedSession,
            HardwareHostValidationOutcome.ProtocolRejected => HardwareHostProcessOutcome.RejectedProtocol,
            HardwareHostValidationOutcome.AuthenticationFailed => HardwareHostProcessOutcome.RejectedAuthentication,
            HardwareHostValidationOutcome.CommandExpired => HardwareHostProcessOutcome.RejectedExpired,
            HardwareHostValidationOutcome.ReplayRejected => HardwareHostProcessOutcome.RejectedReplay,
            HardwareHostValidationOutcome.SequenceRejected => HardwareHostProcessOutcome.RejectedSequence,
            _ => HardwareHostProcessOutcome.RejectedProtocol
        };
}
