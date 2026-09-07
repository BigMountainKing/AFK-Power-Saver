namespace EcoPause.LiveCanary.Model;

public sealed record LivePowerProfile(string Id, uint TargetLimitMilliwatts)
{
    public decimal TargetWatts => TargetLimitMilliwatts / 1000m;
}

public static class LivePowerProfiles
{
    private static readonly LivePowerProfile[] KnownProfiles =
    [
        new("live-150", 150_000),
        new("live-200", 200_000),
        new("live-250", 250_000),
        new("live-300", 300_000),
        new("live-350", 350_000),
        new("live-400", 400_000)
    ];

    public static IReadOnlyList<LivePowerProfile> All { get; } = Array.AsReadOnly(KnownProfiles);

    public static LivePowerProfile Default => KnownProfiles[^1];

    public static LivePowerProfile ResolveId(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return KnownProfiles.SingleOrDefault(profile =>
                string.Equals(profile.Id, id, StringComparison.Ordinal))
            ?? throw new ArgumentException("The live power profile is not allow-listed.", nameof(id));
    }

    public static LivePowerProfile ResolveTarget(uint targetLimitMilliwatts) =>
        KnownProfiles.SingleOrDefault(profile =>
                profile.TargetLimitMilliwatts == targetLimitMilliwatts)
            ?? throw new ArgumentException(
                "The live power target is not allow-listed.",
                nameof(targetLimitMilliwatts));

    public static bool IsKnownTarget(uint targetLimitMilliwatts) =>
        KnownProfiles.Any(profile => profile.TargetLimitMilliwatts == targetLimitMilliwatts);

    public static void ValidateForDevice(
        LivePowerProfile profile,
        LiveCanaryDeviceState device)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(device);
        var canonical = ResolveId(profile.Id);
        if (canonical != profile)
        {
            throw new InvalidOperationException("The live power profile does not match its fixed allow-listed value.");
        }

        if (profile.TargetLimitMilliwatts < device.MinimumLimitMilliwatts ||
            profile.TargetLimitMilliwatts > device.MaximumLimitMilliwatts ||
            profile.TargetLimitMilliwatts >= LiveCanaryPolicy.RequiredOriginalLimitMilliwatts)
        {
            throw new InvalidOperationException("The fixed live profile is outside this GPU's safe lower-limit range.");
        }
    }
}
