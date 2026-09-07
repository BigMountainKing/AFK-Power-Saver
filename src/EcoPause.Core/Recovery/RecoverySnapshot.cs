using System.Text.Json.Serialization;

namespace EcoPause.Core.Recovery;

public enum RecoveryStage
{
    Prepared,
    Applied,
    Restored
}

public enum RecoveryPurpose
{
    LiveRecovery,
    DryRunPlan
}

public sealed record RecoverySnapshot
{
    public const int CurrentSchemaVersion = 1;

    [JsonConstructor]
    public RecoverySnapshot(
        int schemaVersion,
        Guid snapshotId,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc,
        string provider,
        string backend,
        DeviceFingerprint deviceFingerprint,
        uint originalPowerLimitMilliwatts,
        uint targetPowerLimitMilliwatts,
        uint minimumPowerLimitMilliwatts,
        uint maximumPowerLimitMilliwatts,
        uint lastVerifiedPowerLimitMilliwatts,
        RecoveryPurpose purpose,
        RecoveryStage stage)
    {
        SchemaVersion = schemaVersion;
        SnapshotId = snapshotId;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = updatedAtUtc;
        Provider = provider;
        Backend = backend;
        DeviceFingerprint = deviceFingerprint;
        OriginalPowerLimitMilliwatts = originalPowerLimitMilliwatts;
        TargetPowerLimitMilliwatts = targetPowerLimitMilliwatts;
        MinimumPowerLimitMilliwatts = minimumPowerLimitMilliwatts;
        MaximumPowerLimitMilliwatts = maximumPowerLimitMilliwatts;
        LastVerifiedPowerLimitMilliwatts = lastVerifiedPowerLimitMilliwatts;
        Purpose = purpose;
        Stage = stage;

        Validate();
    }

    public int SchemaVersion { get; }

    public Guid SnapshotId { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset UpdatedAtUtc { get; }

    public string Provider { get; }

    public string Backend { get; }

    public DeviceFingerprint DeviceFingerprint { get; }

    public uint OriginalPowerLimitMilliwatts { get; }

    public uint TargetPowerLimitMilliwatts { get; }

    public uint MinimumPowerLimitMilliwatts { get; }

    public uint MaximumPowerLimitMilliwatts { get; }

    public uint LastVerifiedPowerLimitMilliwatts { get; }

    public RecoveryPurpose Purpose { get; }

    public RecoveryStage Stage { get; }

    [JsonIgnore]
    public bool IsRecoveryPending =>
        Purpose == RecoveryPurpose.LiveRecovery && Stage != RecoveryStage.Restored;

    public static RecoverySnapshot Prepare(
        DateTimeOffset createdAtUtc,
        string provider,
        string backend,
        DeviceFingerprint deviceFingerprint,
        uint originalPowerLimitMilliwatts,
        uint targetPowerLimitMilliwatts,
        uint minimumPowerLimitMilliwatts,
        uint maximumPowerLimitMilliwatts,
        Guid? snapshotId = null,
        RecoveryPurpose purpose = RecoveryPurpose.LiveRecovery) =>
        new(
            CurrentSchemaVersion,
            snapshotId ?? Guid.NewGuid(),
            createdAtUtc,
            createdAtUtc,
            provider,
            backend,
            deviceFingerprint,
            originalPowerLimitMilliwatts,
            targetPowerLimitMilliwatts,
            minimumPowerLimitMilliwatts,
            maximumPowerLimitMilliwatts,
            originalPowerLimitMilliwatts,
            purpose,
            RecoveryStage.Prepared);

    public RecoverySnapshot MarkApplied(uint observedPowerLimitMilliwatts, DateTimeOffset verifiedAtUtc)
    {
        RequireLiveRecoveryTransition();

        if (Stage != RecoveryStage.Prepared)
        {
            throw new InvalidOperationException($"A snapshot in stage '{Stage}' cannot be marked applied.");
        }

        if (observedPowerLimitMilliwatts != TargetPowerLimitMilliwatts)
        {
            throw new InvalidOperationException(
                $"Apply verification failed. Expected {TargetPowerLimitMilliwatts} mW, " +
                $"but read back {observedPowerLimitMilliwatts} mW.");
        }

        return WithStage(RecoveryStage.Applied, observedPowerLimitMilliwatts, verifiedAtUtc);
    }

    public RecoverySnapshot MarkRestored(uint observedPowerLimitMilliwatts, DateTimeOffset verifiedAtUtc)
    {
        RequireLiveRecoveryTransition();

        if (Stage == RecoveryStage.Restored)
        {
            throw new InvalidOperationException("The snapshot is already marked restored.");
        }

        if (observedPowerLimitMilliwatts != OriginalPowerLimitMilliwatts)
        {
            throw new InvalidOperationException(
                $"Restore verification failed. Expected {OriginalPowerLimitMilliwatts} mW, " +
                $"but read back {observedPowerLimitMilliwatts} mW.");
        }

        return WithStage(RecoveryStage.Restored, observedPowerLimitMilliwatts, verifiedAtUtc);
    }

    private RecoverySnapshot WithStage(
        RecoveryStage stage,
        uint lastVerifiedPowerLimitMilliwatts,
        DateTimeOffset verifiedAtUtc) =>
        new(
            SchemaVersion,
            SnapshotId,
            CreatedAtUtc,
            verifiedAtUtc,
            Provider,
            Backend,
            DeviceFingerprint,
            OriginalPowerLimitMilliwatts,
            TargetPowerLimitMilliwatts,
            MinimumPowerLimitMilliwatts,
            MaximumPowerLimitMilliwatts,
            lastVerifiedPowerLimitMilliwatts,
            Purpose,
            stage);

    private void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(SchemaVersion), "Unsupported recovery schema version.");
        }

        if (SnapshotId == Guid.Empty)
        {
            throw new ArgumentException("A recovery snapshot requires a non-empty ID.", nameof(SnapshotId));
        }

        RequireUtc(CreatedAtUtc, nameof(CreatedAtUtc));
        RequireUtc(UpdatedAtUtc, nameof(UpdatedAtUtc));

        if (UpdatedAtUtc < CreatedAtUtc)
        {
            throw new ArgumentException("The recovery update time cannot precede its creation time.", nameof(UpdatedAtUtc));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(Provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(Backend);
        ArgumentNullException.ThrowIfNull(DeviceFingerprint);

        if (!Enum.IsDefined(Purpose))
        {
            throw new ArgumentOutOfRangeException(nameof(Purpose), "Unknown recovery purpose.");
        }

        if (Purpose == RecoveryPurpose.DryRunPlan && Stage != RecoveryStage.Prepared)
        {
            throw new ArgumentException("A dry-run plan must remain in the prepared stage.");
        }

        if (MinimumPowerLimitMilliwatts > MaximumPowerLimitMilliwatts)
        {
            throw new ArgumentException("The minimum power limit cannot exceed the maximum power limit.");
        }

        RequireWithinRange(OriginalPowerLimitMilliwatts, nameof(OriginalPowerLimitMilliwatts));
        RequireWithinRange(TargetPowerLimitMilliwatts, nameof(TargetPowerLimitMilliwatts));

        if (TargetPowerLimitMilliwatts > OriginalPowerLimitMilliwatts)
        {
            throw new ArgumentException("AFK Power Saver targets may reduce or preserve a limit, but must never raise it.");
        }

        var expectedVerifiedValue = Stage switch
        {
            RecoveryStage.Prepared => OriginalPowerLimitMilliwatts,
            RecoveryStage.Applied => TargetPowerLimitMilliwatts,
            RecoveryStage.Restored => OriginalPowerLimitMilliwatts,
            _ => throw new ArgumentOutOfRangeException(nameof(Stage), "Unknown recovery stage.")
        };

        if (LastVerifiedPowerLimitMilliwatts != expectedVerifiedValue)
        {
            throw new ArgumentException("The last verified limit does not match the recovery stage.");
        }
    }

    private void RequireWithinRange(uint value, string parameterName)
    {
        if (value < MinimumPowerLimitMilliwatts || value > MaximumPowerLimitMilliwatts)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"The value must be between {MinimumPowerLimitMilliwatts} and {MaximumPowerLimitMilliwatts} mW.");
        }
    }

    private void RequireLiveRecoveryTransition()
    {
        if (Purpose != RecoveryPurpose.LiveRecovery)
        {
            throw new InvalidOperationException("A dry-run plan cannot transition hardware recovery state.");
        }
    }

    private static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Recovery timestamps must be non-default UTC values.", parameterName);
        }
    }
}
