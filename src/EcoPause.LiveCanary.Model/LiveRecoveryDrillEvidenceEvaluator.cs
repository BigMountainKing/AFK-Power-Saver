using EcoPause.Core.Activation;
using EcoPause.HardwareHost.Protocol;
using EcoPause.HardwareHost.Transport;

namespace EcoPause.LiveCanary.Model;

public static class LiveRecoveryDrillEvidenceEvaluator
{
    public static bool IsVerifiedInterruption(
        LiveRecoveryDrillBootstrap bootstrap,
        HardwareHostWireResult result,
        LiveRecoveryDrillSummary summary,
        int helperExitCode) =>
        bootstrap.Phase == LiveRecoveryDrillPhase.CrashAfterVerifiedTarget &&
        bootstrap.Operation == HardwareHostOperation.ActivatePrepared &&
        bootstrap.TargetLimitMilliwatts == LiveCanaryPolicy.TargetLimitMilliwatts &&
        bootstrap.OriginalLimitMilliwatts == LiveCanaryPolicy.RequiredOriginalLimitMilliwatts &&
        result.Outcome == HardwareHostProcessOutcome.Executed &&
        result.ExecutionOutcome == nameof(ActivationOutcome.Activated) &&
        bootstrap.ProfileId == LivePowerProfiles.Default.Id &&
        helperExitCode == LiveCanaryPolicy.IntentionalRecoveryDrillExitCode &&
        summary.PeerVerified &&
        summary.Phase == bootstrap.Phase &&
        summary.DeviceName == bootstrap.DeviceName &&
        summary.TargetAppliedAndVerified &&
        !summary.OriginalRestoredAndVerified &&
        summary.FinalLimitMilliwatts == LiveCanaryPolicy.TargetLimitMilliwatts &&
        summary.RecoveryStage == "Applied" &&
        summary.SafePhaseState &&
        !summary.RecoveryArtifactsCleaned;

    public static bool IsVerifiedRecovery(
        LiveRecoveryDrillBootstrap bootstrap,
        HardwareHostWireResult result,
        LiveRecoveryDrillSummary summary,
        int helperExitCode)
    {
        LivePowerProfile profile;
        try
        {
            profile = LivePowerProfiles.ResolveId(bootstrap.ProfileId);
        }
        catch (ArgumentException)
        {
            return false;
        }

        return bootstrap.Phase == LiveRecoveryDrillPhase.RecoveryOnly &&
        bootstrap.Operation == HardwareHostOperation.RestorePending &&
        bootstrap.TargetLimitMilliwatts == profile.TargetLimitMilliwatts &&
        bootstrap.OriginalLimitMilliwatts == LiveCanaryPolicy.RequiredOriginalLimitMilliwatts &&
        result.Outcome == HardwareHostProcessOutcome.Executed &&
        result.ExecutionOutcome is nameof(ActivationOutcome.Restored) or nameof(ActivationOutcome.AlreadyRestored) &&
        helperExitCode == 0 &&
        summary.PeerVerified &&
        summary.Phase == bootstrap.Phase &&
        summary.DeviceName == bootstrap.DeviceName &&
        summary.OriginalRestoredAndVerified &&
        summary.FinalLimitMilliwatts == LiveCanaryPolicy.RequiredOriginalLimitMilliwatts &&
        summary.RecoveryStage == "Restored" &&
        summary.SafePhaseState &&
        summary.RecoveryArtifactsCleaned;
    }
}
