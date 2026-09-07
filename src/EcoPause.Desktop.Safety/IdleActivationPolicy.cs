namespace EcoPause.Desktop.Safety;

public enum IdleActivationDirective
{
    None,
    ApplyLimits,
    RestoreOriginal
}

public static class IdleActivationPolicy
{
    public const int MinimumMinutes = 1;
    public const int MaximumMinutes = 120;
    public const int DefaultMinutes = 5;

    public static int ValidateMinutes(int minutes)
    {
        if (minutes is < MinimumMinutes or > MaximumMinutes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minutes),
                $"Idle activation must be between {MinimumMinutes} and {MaximumMinutes} minutes.");
        }

        return minutes;
    }

    public static IdleActivationDirective Evaluate(
        bool enabled,
        bool timerOwnsActiveLimit,
        bool transitionAttempted,
        bool unattended,
        bool canApply,
        bool canRestore)
    {
        if (transitionAttempted && !timerOwnsActiveLimit)
        {
            return IdleActivationDirective.None;
        }

        if (!enabled)
        {
            return timerOwnsActiveLimit && canRestore
                ? IdleActivationDirective.RestoreOriginal
                : IdleActivationDirective.None;
        }

        if (unattended)
        {
            return !timerOwnsActiveLimit && canApply
                ? IdleActivationDirective.ApplyLimits
                : IdleActivationDirective.None;
        }

        return timerOwnsActiveLimit && canRestore
            ? IdleActivationDirective.RestoreOriginal
            : IdleActivationDirective.None;
    }
}
