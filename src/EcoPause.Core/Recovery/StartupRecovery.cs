using EcoPause.Core.Activation;

namespace EcoPause.Core.Recovery;

public enum RecoveryDiscoveryKind
{
    PendingLiveRecovery,
    RestoredLiveRecovery,
    DryRunPlan,
    InvalidJournal
}

public sealed record RecoveryDiscoveryItem(
    string JournalPath,
    RecoveryDiscoveryKind Kind,
    RecoverySnapshot? Snapshot,
    string Message);

public sealed record RecoveryDiscoveryReport(
    string DirectoryPath,
    IReadOnlyList<RecoveryDiscoveryItem> Items)
{
    public bool HasPendingRecovery =>
        Items.Any(item => item.Kind == RecoveryDiscoveryKind.PendingLiveRecovery);
}

public sealed class StartupRecoveryScanner
{
    private const int MaximumJournalCount = 128;
    private readonly IRecoveryJournalStore _journalStore;

    public StartupRecoveryScanner(IRecoveryJournalStore? journalStore = null)
    {
        _journalStore = journalStore ?? new FileRecoveryJournalStore();
    }

    public async Task<RecoveryDiscoveryReport> DiscoverAsync(
        string directoryPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        var fullDirectoryPath = Path.GetFullPath(directoryPath);

        if (!Directory.Exists(fullDirectoryPath))
        {
            return new RecoveryDiscoveryReport(fullDirectoryPath, Array.Empty<RecoveryDiscoveryItem>());
        }

        var journalPaths = Directory
            .EnumerateFiles(fullDirectoryPath, "*.json", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Take(MaximumJournalCount + 1)
            .ToArray();

        if (journalPaths.Length > MaximumJournalCount)
        {
            throw new RecoveryJournalException(
                $"The recovery directory contains more than {MaximumJournalCount} journal files.");
        }

        var items = new List<RecoveryDiscoveryItem>(journalPaths.Length);
        foreach (var journalPath in journalPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var snapshot = await _journalStore.LoadAsync(journalPath, cancellationToken);
                items.Add(Classify(journalPath, snapshot));
            }
            catch (RecoveryJournalException exception)
            {
                items.Add(new RecoveryDiscoveryItem(
                    journalPath,
                    RecoveryDiscoveryKind.InvalidJournal,
                    Snapshot: null,
                    $"The journal is invalid and will not be acted upon: {exception.Message}"));
            }
        }

        return new RecoveryDiscoveryReport(fullDirectoryPath, items);
    }

    private static RecoveryDiscoveryItem Classify(string path, RecoverySnapshot snapshot)
    {
        if (snapshot.Purpose == RecoveryPurpose.DryRunPlan)
        {
            return new RecoveryDiscoveryItem(
                path,
                RecoveryDiscoveryKind.DryRunPlan,
                snapshot,
                "Dry-run plan; no startup recovery action is allowed.");
        }

        return snapshot.IsRecoveryPending
            ? new RecoveryDiscoveryItem(
                path,
                RecoveryDiscoveryKind.PendingLiveRecovery,
                snapshot,
                "Live recovery is pending and requires explicit approval.")
            : new RecoveryDiscoveryItem(
                path,
                RecoveryDiscoveryKind.RestoredLiveRecovery,
                snapshot,
                "Live recovery was already restored and verified.");
    }
}

public sealed record RecoveryApprovalRequest
{
    internal RecoveryApprovalRequest(
        Guid requestId,
        Guid snapshotId,
        string journalPath,
        string provider,
        RecoveryStage stage,
        uint originalPowerLimitMilliwatts,
        DateTimeOffset createdAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        RequestId = requestId;
        SnapshotId = snapshotId;
        JournalPath = journalPath;
        Provider = provider;
        Stage = stage;
        OriginalPowerLimitMilliwatts = originalPowerLimitMilliwatts;
        CreatedAtUtc = createdAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    public Guid RequestId { get; }

    public Guid SnapshotId { get; }

    public string JournalPath { get; }

    public string Provider { get; }

    public RecoveryStage Stage { get; }

    public uint OriginalPowerLimitMilliwatts { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset ExpiresAtUtc { get; }

    public string ConfirmationSummary =>
        $"Restore the {Provider} GPU from recovery stage {Stage} to " +
        $"{OriginalPowerLimitMilliwatts / 1000m:0.###} W?";
}

public sealed record RecoveryApprovalDecision
{
    private RecoveryApprovalDecision(
        Guid requestId,
        Guid snapshotId,
        bool approved,
        DateTimeOffset decidedAtUtc)
    {
        RequestId = requestId;
        SnapshotId = snapshotId;
        Approved = approved;
        DecidedAtUtc = decidedAtUtc;
    }

    public Guid RequestId { get; }

    public Guid SnapshotId { get; }

    public bool Approved { get; }

    public DateTimeOffset DecidedAtUtc { get; }

    public static RecoveryApprovalDecision Approve(
        RecoveryApprovalRequest request,
        DateTimeOffset decidedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(request.RequestId, request.SnapshotId, approved: true, decidedAtUtc);
    }

    public static RecoveryApprovalDecision Reject(
        RecoveryApprovalRequest request,
        DateTimeOffset decidedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(request.RequestId, request.SnapshotId, approved: false, decidedAtUtc);
    }
}

public enum StartupRecoveryOutcome
{
    Restored,
    Declined,
    InvalidApproval,
    StaleJournal,
    RecoveryFailed
}

public sealed record StartupRecoveryResult(
    StartupRecoveryOutcome Outcome,
    string Message,
    ActivationResult? Restoration);

public sealed class StartupRecoveryCoordinator
{
    private static readonly TimeSpan DefaultApprovalLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaximumApprovalLifetime = TimeSpan.FromMinutes(30);

    private readonly GpuActivationEngine _activationEngine;
    private readonly TimeProvider _timeProvider;
    private readonly HashSet<Guid> _consumedRequests = [];
    private readonly object _consumedRequestsLock = new();

    public StartupRecoveryCoordinator(
        GpuActivationEngine activationEngine,
        TimeProvider? timeProvider = null)
    {
        _activationEngine = activationEngine ?? throw new ArgumentNullException(nameof(activationEngine));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public RecoveryApprovalRequest CreateApprovalRequest(
        RecoveryDiscoveryItem item,
        TimeSpan? lifetime = null)
    {
        var snapshot = RequirePendingSnapshot(item);
        var effectiveLifetime = lifetime ?? DefaultApprovalLifetime;

        if (effectiveLifetime <= TimeSpan.Zero || effectiveLifetime > MaximumApprovalLifetime)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lifetime),
                $"Approval lifetime must be greater than zero and no more than {MaximumApprovalLifetime.TotalMinutes} minutes.");
        }

        var createdAtUtc = _timeProvider.GetUtcNow();
        return new RecoveryApprovalRequest(
            Guid.NewGuid(),
            snapshot.SnapshotId,
            Path.GetFullPath(item.JournalPath),
            snapshot.Provider,
            snapshot.Stage,
            snapshot.OriginalPowerLimitMilliwatts,
            createdAtUtc,
            createdAtUtc.Add(effectiveLifetime));
    }

    public async Task<StartupRecoveryResult> ResolveAsync(
        RecoveryDiscoveryItem item,
        RecoveryApprovalRequest request,
        RecoveryApprovalDecision decision,
        IGpuPowerController controller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(controller);

        var snapshot = RequirePendingSnapshot(item);
        if (!RequestMatchesItem(request, item, snapshot) ||
            decision.RequestId != request.RequestId ||
            decision.SnapshotId != request.SnapshotId ||
            decision.DecidedAtUtc.Offset != TimeSpan.Zero ||
            decision.DecidedAtUtc < request.CreatedAtUtc ||
            decision.DecidedAtUtc > request.ExpiresAtUtc)
        {
            return new StartupRecoveryResult(
                StartupRecoveryOutcome.InvalidApproval,
                "The recovery approval is expired or does not match the exact discovered snapshot.",
                Restoration: null);
        }

        lock (_consumedRequestsLock)
        {
            if (!_consumedRequests.Add(request.RequestId))
            {
                return new StartupRecoveryResult(
                    StartupRecoveryOutcome.InvalidApproval,
                    "The recovery approval request has already been used.",
                    Restoration: null);
            }
        }

        if (!decision.Approved)
        {
            return new StartupRecoveryResult(
                StartupRecoveryOutcome.Declined,
                "Recovery was declined; no controller action was taken.",
                Restoration: null);
        }

        var restoration = await _activationEngine.RestoreExpectedAsync(
            request.JournalPath,
            snapshot,
            controller,
            cancellationToken);

        return restoration.Outcome switch
        {
            ActivationOutcome.Restored or ActivationOutcome.AlreadyRestored =>
                new StartupRecoveryResult(
                    StartupRecoveryOutcome.Restored,
                    restoration.Message,
                    restoration),
            ActivationOutcome.FailedBeforeWrite =>
                new StartupRecoveryResult(
                    StartupRecoveryOutcome.StaleJournal,
                    restoration.Message,
                    restoration),
            _ => new StartupRecoveryResult(
                StartupRecoveryOutcome.RecoveryFailed,
                restoration.Message,
                restoration)
        };
    }

    private static RecoverySnapshot RequirePendingSnapshot(RecoveryDiscoveryItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item.Kind != RecoveryDiscoveryKind.PendingLiveRecovery ||
            item.Snapshot is null ||
            !item.Snapshot.IsRecoveryPending)
        {
            throw new ArgumentException("The discovery item is not a pending live-recovery journal.", nameof(item));
        }

        return item.Snapshot;
    }

    private static bool RequestMatchesItem(
        RecoveryApprovalRequest request,
        RecoveryDiscoveryItem item,
        RecoverySnapshot snapshot) =>
        request.SnapshotId == snapshot.SnapshotId &&
        PathsEqual(request.JournalPath, item.JournalPath) &&
        request.Provider == snapshot.Provider &&
        request.Stage == snapshot.Stage &&
        request.OriginalPowerLimitMilliwatts == snapshot.OriginalPowerLimitMilliwatts;

    private static bool PathsEqual(string first, string second)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), comparison);
    }
}
