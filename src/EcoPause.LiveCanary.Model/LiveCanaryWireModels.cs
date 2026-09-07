using EcoPause.HardwareHost.Protocol;

namespace EcoPause.LiveCanary.Model;

public enum LiveCanaryMode
{
    Canary,
    RecoveryOnly
}

public sealed record LiveCanaryBootstrap(
    HardwareHostSessionBootstrap Session,
    Guid SnapshotId,
    HardwareHostOperation Operation,
    LiveCanaryMode Mode,
    string ProfileId,
    string DeviceName,
    string DeviceFingerprintSha256,
    uint OriginalLimitMilliwatts,
    uint TargetLimitMilliwatts,
    uint MinimumLimitMilliwatts,
    uint MaximumLimitMilliwatts,
    int HoldDurationMilliseconds);

public sealed record LiveCanarySummary(
    bool PeerVerified,
    LiveCanaryMode Mode,
    string DeviceName,
    bool TargetAppliedAndVerified,
    bool OriginalRestoredAndVerified,
    uint FinalLimitMilliwatts,
    string RecoveryStage,
    string? ActivationOutcome,
    string? RestorationOutcome,
    bool SafeFinalState,
    bool RecoveryArtifactsCleaned);

public enum LiveRecoveryDrillPhase
{
    CrashAfterVerifiedTarget,
    PauseAtVerifiedTarget,
    RecoveryOnly
}

public sealed record LiveRecoveryDrillBootstrap(
    HardwareHostSessionBootstrap Session,
    Guid SnapshotId,
    HardwareHostOperation Operation,
    LiveRecoveryDrillPhase Phase,
    string ProfileId,
    string DeviceName,
    string DeviceFingerprintSha256,
    uint OriginalLimitMilliwatts,
    uint TargetLimitMilliwatts,
    uint MinimumLimitMilliwatts,
    uint MaximumLimitMilliwatts);

public sealed record LiveRecoveryDrillSummary(
    bool PeerVerified,
    LiveRecoveryDrillPhase Phase,
    string DeviceName,
    bool TargetAppliedAndVerified,
    bool OriginalRestoredAndVerified,
    uint FinalLimitMilliwatts,
    string RecoveryStage,
    bool SafePhaseState,
    bool RecoveryArtifactsCleaned);
