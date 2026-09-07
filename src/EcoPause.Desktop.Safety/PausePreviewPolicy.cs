namespace EcoPause.Desktop.Safety;

public enum PausePreviewReason
{
    ActiveInput,
    IdleThresholdReached,
    SessionLocked
}

public sealed record PausePreviewDecision(
    bool WouldPause,
    PausePreviewReason Reason,
    TimeSpan IdleDuration,
    TimeSpan RemainingUntilPause);

public static class PausePreviewPolicy
{
    public static PausePreviewDecision Evaluate(
        TimeSpan idleDuration,
        bool sessionLocked,
        TimeSpan idleThreshold)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(idleDuration, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(idleThreshold, TimeSpan.Zero);

        if (sessionLocked)
        {
            return new PausePreviewDecision(
                WouldPause: true,
                PausePreviewReason.SessionLocked,
                idleDuration,
                TimeSpan.Zero);
        }

        if (idleDuration >= idleThreshold)
        {
            return new PausePreviewDecision(
                WouldPause: true,
                PausePreviewReason.IdleThresholdReached,
                idleDuration,
                TimeSpan.Zero);
        }

        return new PausePreviewDecision(
            WouldPause: false,
            PausePreviewReason.ActiveInput,
            idleDuration,
            idleThreshold - idleDuration);
    }
}
