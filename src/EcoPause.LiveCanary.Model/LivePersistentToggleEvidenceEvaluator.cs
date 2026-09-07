using EcoPause.Core.Activation;
using EcoPause.Core.Recovery;
using EcoPause.HardwareHost.Protocol;
using EcoPause.HardwareHost.Transport;

namespace EcoPause.LiveCanary.Model;

public static class LivePersistentToggleEvidenceEvaluator
{
    public static bool IsVerifiedPause(
        LiveRecoveryDrillBootstrap bootstrap,
        HardwareHostWireResult result,
        LiveRecoveryDrillSummary summary,
        int helperExitCode)
    {
        if (!TryValidateProfile(bootstrap))
        {
            return false;
        }

        return bootstrap.Phase == LiveRecoveryDrillPhase.PauseAtVerifiedTarget &&
        bootstrap.Operation == HardwareHostOperation.ActivatePrepared &&
        bootstrap.OriginalLimitMilliwatts > bootstrap.TargetLimitMilliwatts &&
        result.Outcome == HardwareHostProcessOutcome.Executed &&
        result.ExecutionOutcome == nameof(ActivationOutcome.Activated) &&
        helperExitCode == 0 &&
        summary.PeerVerified &&
        summary.Phase == bootstrap.Phase &&
        summary.DeviceName == bootstrap.DeviceName &&
        summary.TargetAppliedAndVerified &&
        !summary.OriginalRestoredAndVerified &&
        summary.FinalLimitMilliwatts == bootstrap.TargetLimitMilliwatts &&
        summary.RecoveryStage == "Applied" &&
        summary.SafePhaseState &&
        !summary.RecoveryArtifactsCleaned;
    }

    public static bool IsVerifiedRecovery(
        LiveRecoveryDrillBootstrap bootstrap,
        HardwareHostWireResult result,
        LiveRecoveryDrillSummary summary,
        int helperExitCode) =>
        TryValidateProfile(bootstrap) &&
        bootstrap.Phase == LiveRecoveryDrillPhase.RecoveryOnly &&
        bootstrap.Operation == HardwareHostOperation.RestorePending &&
        result.Outcome == HardwareHostProcessOutcome.Executed &&
        result.ExecutionOutcome is nameof(ActivationOutcome.Restored) or nameof(ActivationOutcome.AlreadyRestored) &&
        helperExitCode == 0 &&
        summary.PeerVerified &&
        summary.Phase == bootstrap.Phase &&
        summary.DeviceName == bootstrap.DeviceName &&
        summary.OriginalRestoredAndVerified &&
        summary.FinalLimitMilliwatts == bootstrap.OriginalLimitMilliwatts &&
        summary.RecoveryStage == "Restored" &&
        summary.SafePhaseState &&
        summary.RecoveryArtifactsCleaned;

    private static bool TryValidateProfile(LiveRecoveryDrillBootstrap bootstrap)
    {
        try
        {
            var state = PortableLivePowerPolicy.ValidateDevice(
                bootstrap.DeviceName,
                new DeviceFingerprint(bootstrap.DeviceFingerprintSha256),
                bootstrap.TargetLimitMilliwatts,
                bootstrap.OriginalLimitMilliwatts,
                bootstrap.MinimumLimitMilliwatts,
                bootstrap.MaximumLimitMilliwatts);
            return PortableLivePowerPolicy.ResolveIdForDevice(bootstrap.ProfileId, state)
                .TargetLimitMilliwatts == bootstrap.TargetLimitMilliwatts;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}
