using System.Security.Cryptography;
using System.Text.Json;
using EcoPause.Core.Activation;
using EcoPause.Core.Recovery;

namespace EcoPause.CrashRecovery.SimulationModel;

public static class CrashSimulationProfile
{
    public const string Provider = "Simulation";
    public const string Backend = "CrashPersistentFake";
    public const uint OriginalLimitMilliwatts = 450_000;
    public const uint TargetLimitMilliwatts = 400_000;
    public const uint MinimumLimitMilliwatts = 150_000;
    public const uint MaximumLimitMilliwatts = 600_000;
    public const string JournalFileName = "recovery.json";
    public const string HardwareStateFileName = "fake-hardware.json";
    public static Guid ToggleRunId => PersistentToggleProfiles.Default.RunId;

    public static DeviceFingerprint CreateFingerprint(Guid runId) =>
        DeviceFingerprint.FromStableIdentifier($"ELEVATED-CRASH-SIMULATION-{runId:N}");

    public static RecoverySnapshot CreateSnapshot(Guid runId, DateTimeOffset createdAtUtc) =>
        RecoverySnapshot.Prepare(
            createdAtUtc,
            Provider,
            Backend,
            CreateFingerprint(runId),
            OriginalLimitMilliwatts,
            TargetLimitMilliwatts,
            MinimumLimitMilliwatts,
            MaximumLimitMilliwatts);

    public static RecoverySnapshot CreateToggleSnapshot(
        PersistentToggleProfile profile,
        DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return RecoverySnapshot.Prepare(
            createdAtUtc,
            Provider,
            Backend,
            CreateFingerprint(profile.RunId),
            OriginalLimitMilliwatts,
            profile.TargetLimitMilliwatts,
            MinimumLimitMilliwatts,
            MaximumLimitMilliwatts);
    }

    public static void ValidatePendingSnapshot(RecoverySnapshot snapshot, Guid runId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.Provider != Provider ||
            snapshot.Backend != Backend ||
            snapshot.DeviceFingerprint != CreateFingerprint(runId) ||
            snapshot.OriginalPowerLimitMilliwatts != OriginalLimitMilliwatts ||
            snapshot.TargetPowerLimitMilliwatts != TargetLimitMilliwatts ||
            snapshot.MinimumPowerLimitMilliwatts != MinimumLimitMilliwatts ||
            snapshot.MaximumPowerLimitMilliwatts != MaximumLimitMilliwatts ||
            snapshot.Purpose != RecoveryPurpose.LiveRecovery ||
            snapshot.Stage != RecoveryStage.Applied)
        {
            throw new InvalidOperationException("The pending simulation snapshot does not match the fixed crash-recovery profile.");
        }
    }

    public static void ValidateToggleSnapshot(
        RecoverySnapshot snapshot,
        PersistentToggleProfile? expectedProfile = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var profile = expectedProfile ?? PersistentToggleProfiles.Default;

        if (snapshot.Provider != Provider ||
            snapshot.Backend != Backend ||
            snapshot.DeviceFingerprint != CreateFingerprint(profile.RunId) ||
            snapshot.OriginalPowerLimitMilliwatts != OriginalLimitMilliwatts ||
            snapshot.TargetPowerLimitMilliwatts != profile.TargetLimitMilliwatts ||
            snapshot.MinimumPowerLimitMilliwatts != MinimumLimitMilliwatts ||
            snapshot.MaximumPowerLimitMilliwatts != MaximumLimitMilliwatts ||
            snapshot.Purpose != RecoveryPurpose.LiveRecovery ||
            snapshot.Stage is not (RecoveryStage.Prepared or RecoveryStage.Applied or RecoveryStage.Restored))
        {
            throw new InvalidOperationException("The persistent toggle snapshot does not match the fixed simulation profile.");
        }
    }
}

public sealed record PersistentToggleProfile(
    string Id,
    Guid RunId,
    uint TargetLimitMilliwatts);

public static class PersistentToggleProfiles
{
    private static readonly PersistentToggleProfile[] KnownProfiles =
    [
        new("eco-250", new Guid("ec0a0250-0000-4000-8000-000000000015"), 250_000),
        new("eco-300", new Guid("ec0a0300-0000-4000-8000-000000000015"), 300_000),
        new("eco-350", new Guid("ec0a0350-0000-4000-8000-000000000015"), 350_000),
        new("eco-400", new Guid("ec0a0000-0000-4000-8000-000000000014"), 400_000)
    ];

    public static IReadOnlyList<PersistentToggleProfile> All { get; } = Array.AsReadOnly(KnownProfiles);

    public static PersistentToggleProfile Default => KnownProfiles[^1];

    public static PersistentToggleProfile ResolveId(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return KnownProfiles.SingleOrDefault(profile => string.Equals(profile.Id, id, StringComparison.Ordinal))
            ?? throw new ArgumentException("The persistent fake target profile is not allow-listed.", nameof(id));
    }

    public static PersistentToggleProfile ResolveRunId(Guid runId)
    {
        if (runId == Guid.Empty)
        {
            throw new ArgumentException("The persistent fake target run ID cannot be empty.", nameof(runId));
        }

        return KnownProfiles.SingleOrDefault(profile => profile.RunId == runId)
            ?? throw new ArgumentException("The persistent fake target run ID is not allow-listed.", nameof(runId));
    }
}

public static class CrashSimulationPaths
{
    public static Guid ParseRunId(string value)
    {
        if (!Guid.TryParseExact(value, "N", out var runId) || runId == Guid.Empty)
        {
            throw new ArgumentException("A crash-simulation run ID must contain exactly 32 hexadecimal characters.", nameof(value));
        }

        return runId;
    }

    public static string ResolveRunDirectory(string trustedRoot, Guid runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trustedRoot);
        if (runId == Guid.Empty)
        {
            throw new ArgumentException("A crash-simulation run ID cannot be empty.", nameof(runId));
        }

        var root = Path.GetFullPath(trustedRoot);
        var candidate = Path.GetFullPath(Path.Combine(root, runId.ToString("N")));
        var relative = Path.GetRelativePath(root, candidate);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            throw new InvalidOperationException("The crash-simulation path escaped its trusted root.");
        }

        return candidate;
    }
}

public sealed class PersistentFakeHardwareException : Exception
{
    public PersistentFakeHardwareException(string message)
        : base(message)
    {
    }

    public PersistentFakeHardwareException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class PersistentFakePowerController : IGpuPowerController
{
    private const int CurrentSchemaVersion = 1;
    private const long MaximumStateBytes = 4 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        MaxDepth = 8
    };

    private readonly string _statePath;

    public PersistentFakePowerController(
        string statePath,
        DeviceFingerprint deviceFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statePath);
        DeviceFingerprint = deviceFingerprint ?? throw new ArgumentNullException(nameof(deviceFingerprint));
        _statePath = Path.GetFullPath(statePath);
    }

    public DeviceFingerprint DeviceFingerprint { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (File.Exists(_statePath))
        {
            throw new PersistentFakeHardwareException("Persistent fake hardware state already exists.");
        }

        await SaveStateAsync(
            new PersistentFakeHardwareState(
                CurrentSchemaVersion,
                DeviceFingerprint,
                CrashSimulationProfile.OriginalLimitMilliwatts,
                CrashSimulationProfile.MinimumLimitMilliwatts,
                CrashSimulationProfile.MaximumLimitMilliwatts),
            overwrite: false,
            cancellationToken);
    }

    public async Task<uint> ReadPowerLimitMilliwattsAsync(
        CancellationToken cancellationToken = default)
    {
        var state = await LoadStateAsync(cancellationToken);
        return state.CurrentLimitMilliwatts;
    }

    public async Task SetPowerLimitMilliwattsAsync(
        uint powerLimitMilliwatts,
        CancellationToken cancellationToken = default)
    {
        var state = await LoadStateAsync(cancellationToken);
        if (powerLimitMilliwatts < state.MinimumLimitMilliwatts ||
            powerLimitMilliwatts > state.MaximumLimitMilliwatts)
        {
            throw new PersistentFakeHardwareException("The simulated power limit is outside the fixed permitted range.");
        }

        await SaveStateAsync(
            state with { CurrentLimitMilliwatts = powerLimitMilliwatts },
            overwrite: true,
            cancellationToken);
    }

    private async Task<PersistentFakeHardwareState> LoadStateAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var file = new FileInfo(_statePath);
            if (!file.Exists || file.Length is <= 0 or > MaximumStateBytes)
            {
                throw new PersistentFakeHardwareException("Persistent fake hardware state has an invalid size.");
            }

            var bytes = await File.ReadAllBytesAsync(_statePath, cancellationToken);
            var envelope = JsonSerializer.Deserialize<PersistentFakeHardwareEnvelope>(bytes, SerializerOptions)
                ?? throw new PersistentFakeHardwareException("Persistent fake hardware state is malformed.");
            return VerifyEnvelope(envelope);
        }
        catch (PersistentFakeHardwareException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            throw new PersistentFakeHardwareException("Persistent fake hardware state could not be loaded safely.", exception);
        }
    }

    private async Task SaveStateAsync(
        PersistentFakeHardwareState state,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        ValidateState(state);
        var directory = Path.GetDirectoryName(_statePath)
            ?? throw new PersistentFakeHardwareException("Persistent fake hardware state requires a directory.");
        Directory.CreateDirectory(directory);

        var stateBytes = JsonSerializer.SerializeToUtf8Bytes(state, SerializerOptions);
        var envelope = new PersistentFakeHardwareEnvelope(
            Convert.ToHexString(SHA256.HashData(stateBytes)),
            state);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_statePath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, envelope, SerializerOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _statePath, overwrite);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private PersistentFakeHardwareState VerifyEnvelope(PersistentFakeHardwareEnvelope envelope)
    {
        if (string.IsNullOrWhiteSpace(envelope.IntegritySha256) || envelope.State is null)
        {
            throw new PersistentFakeHardwareException("Persistent fake hardware state is incomplete.");
        }

        byte[] recordedDigest;
        try
        {
            recordedDigest = Convert.FromHexString(envelope.IntegritySha256);
        }
        catch (FormatException exception)
        {
            throw new PersistentFakeHardwareException("Persistent fake hardware integrity data is malformed.", exception);
        }

        var stateBytes = JsonSerializer.SerializeToUtf8Bytes(envelope.State, SerializerOptions);
        var computedDigest = SHA256.HashData(stateBytes);
        if (!CryptographicOperations.FixedTimeEquals(recordedDigest, computedDigest))
        {
            throw new PersistentFakeHardwareException("Persistent fake hardware state failed its integrity check.");
        }

        ValidateState(envelope.State);
        return envelope.State;
    }

    private void ValidateState(PersistentFakeHardwareState state)
    {
        if (state.SchemaVersion != CurrentSchemaVersion ||
            state.DeviceFingerprint != DeviceFingerprint ||
            state.MinimumLimitMilliwatts != CrashSimulationProfile.MinimumLimitMilliwatts ||
            state.MaximumLimitMilliwatts != CrashSimulationProfile.MaximumLimitMilliwatts ||
            state.CurrentLimitMilliwatts < state.MinimumLimitMilliwatts ||
            state.CurrentLimitMilliwatts > state.MaximumLimitMilliwatts)
        {
            throw new PersistentFakeHardwareException("Persistent fake hardware state does not match the fixed simulation profile.");
        }
    }

    private sealed record PersistentFakeHardwareEnvelope(
        string? IntegritySha256,
        PersistentFakeHardwareState? State);

    private sealed record PersistentFakeHardwareState(
        int SchemaVersion,
        DeviceFingerprint DeviceFingerprint,
        uint CurrentLimitMilliwatts,
        uint MinimumLimitMilliwatts,
        uint MaximumLimitMilliwatts);
}
