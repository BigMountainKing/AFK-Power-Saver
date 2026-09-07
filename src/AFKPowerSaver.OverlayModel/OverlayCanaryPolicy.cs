namespace AFKPowerSaver.OverlayModel;

public sealed record OverlayCanarySettings(int OpacityPercent, TimeSpan HoldDuration)
{
    public double OpacityFraction => OpacityPercent / 100d;

    public void Validate()
    {
        if (OpacityPercent is < OverlayCanaryPolicy.MinimumOpacityPercent or > OverlayCanaryPolicy.MaximumOpacityPercent)
        {
            throw new ArgumentOutOfRangeException(
                nameof(OpacityPercent),
                OpacityPercent,
                $"Overlay opacity must be between {OverlayCanaryPolicy.MinimumOpacityPercent}% and {OverlayCanaryPolicy.MaximumOpacityPercent}%.");
        }

        if (HoldDuration < OverlayCanaryPolicy.MinimumHoldDuration ||
            HoldDuration > OverlayCanaryPolicy.MaximumHoldDuration)
        {
            throw new ArgumentOutOfRangeException(
                nameof(HoldDuration),
                HoldDuration,
                "The overlay test duration is outside the qualified range.");
        }
    }
}

public static class OverlayCanaryPolicy
{
    public const int MinimumOpacityPercent = 10;
    public const int MaximumOpacityPercent = 95;
    public const int DefaultOpacityPercent = 70;
    public static TimeSpan MinimumHoldDuration { get; } = TimeSpan.FromSeconds(5);
    public static TimeSpan MaximumHoldDuration { get; } = TimeSpan.FromSeconds(30);
    public static TimeSpan QualifiedHoldDuration { get; } = TimeSpan.FromSeconds(15);

    public static OverlayCanarySettings Create(int opacityPercent)
    {
        var settings = new OverlayCanarySettings(opacityPercent, QualifiedHoldDuration);
        settings.Validate();
        return settings;
    }
}

public static class DisplayTargetPolicy
{
    public const string AllDisplaysTargetId = "all";

    public static string NormalizeSavedTarget(string? savedTargetId, IEnumerable<string> connectedTargetIds)
    {
        ArgumentNullException.ThrowIfNull(connectedTargetIds);
        if (string.IsNullOrWhiteSpace(savedTargetId) ||
            string.Equals(savedTargetId, AllDisplaysTargetId, StringComparison.OrdinalIgnoreCase))
        {
            return AllDisplaysTargetId;
        }

        return connectedTargetIds.Any(targetId =>
            string.Equals(targetId, savedTargetId, StringComparison.OrdinalIgnoreCase))
                ? savedTargetId
                : AllDisplaysTargetId;
    }

    public static string[] NormalizeSavedTargets(
        IEnumerable<string>? savedTargetIds,
        IEnumerable<string> connectedTargetIds)
    {
        ArgumentNullException.ThrowIfNull(connectedTargetIds);
        var connected = connectedTargetIds
            .Where(target => !string.IsNullOrWhiteSpace(target))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var saved = (savedTargetIds ?? [])
            .Where(target => !string.IsNullOrWhiteSpace(target))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (saved.Any(target => string.Equals(target, AllDisplaysTargetId, StringComparison.OrdinalIgnoreCase)))
        {
            return [AllDisplaysTargetId];
        }

        return saved.Where(connected.Contains).ToArray();
    }
}
