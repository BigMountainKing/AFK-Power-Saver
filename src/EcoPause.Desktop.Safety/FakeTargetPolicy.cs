namespace EcoPause.Desktop.Safety;

public sealed record FakeTargetOption(string ProfileId, decimal Watts)
{
    public string DisplayName => $"{Watts:0} W fake target";
}

public sealed record FakeTargetValidationResult(bool Accepted, string Message);

public static class FakeTargetPolicy
{
    private static readonly FakeTargetOption[] KnownOptions =
    [
        new("eco-250", 250m),
        new("eco-300", 300m),
        new("eco-350", 350m),
        new("eco-400", 400m)
    ];

    public static IReadOnlyList<FakeTargetOption> Options { get; } = Array.AsReadOnly(KnownOptions);

    public static FakeTargetOption Default => KnownOptions[^1];

    public static FakeTargetOption ResolveProfile(string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        return KnownOptions.SingleOrDefault(option =>
            string.Equals(option.ProfileId, profileId, StringComparison.Ordinal))
            ?? throw new ArgumentException("The fake target profile is not allow-listed.", nameof(profileId));
    }

    public static FakeTargetValidationResult Validate(
        FakeTargetOption option,
        decimal currentLimitWatts,
        decimal minimumLimitWatts,
        decimal maximumLimitWatts)
    {
        ArgumentNullException.ThrowIfNull(option);
        if (!KnownOptions.Contains(option) ||
            minimumLimitWatts < 0 ||
            maximumLimitWatts < minimumLimitWatts ||
            currentLimitWatts < minimumLimitWatts ||
            currentLimitWatts > maximumLimitWatts)
        {
            return new FakeTargetValidationResult(false, "Live GPU constraints are unavailable or inconsistent.");
        }

        if (option.Watts < minimumLimitWatts || option.Watts > maximumLimitWatts)
        {
            return new FakeTargetValidationResult(false, "The fake target is outside the live driver range.");
        }

        if (option.Watts >= currentLimitWatts)
        {
            return new FakeTargetValidationResult(false, "The fake target must be lower than the current driver limit.");
        }

        return new FakeTargetValidationResult(
            true,
            $"{option.Watts:0} W is inside the live range and below the current limit.");
    }
}
