using EcoPause.Core.Activation;
using EcoPause.Core.Recovery;
using EcoPause.HardwareHost.Protocol;

var requestedScenario = args.Length switch
{
    0 => "all",
    2 when args[0].Equals("--scenario", StringComparison.OrdinalIgnoreCase) => args[1].ToLowerInvariant(),
    _ => string.Empty
};

var knownScenarios = new[]
{
    "success",
    "verification-failure",
    "interruption",
    "startup-recovery",
    "helper-protocol"
};
if (requestedScenario != "all" && !knownScenarios.Contains(requestedScenario, StringComparer.Ordinal))
{
    Console.Error.WriteLine(
        "Usage: EcoPause.Simulation [--scenario success|verification-failure|interruption|startup-recovery|helper-protocol]");
    return 2;
}

Console.WriteLine("EcoPause Safety Simulation Suite 0.6");
Console.WriteLine("Hardware backend: IN-MEMORY ONLY");
Console.WriteLine("No NVIDIA or AMD setting library is referenced by this executable.");

var scenarios = requestedScenario == "all" ? knownScenarios : new[] { requestedScenario };
var failures = 0;

foreach (var scenario in scenarios)
{
    Console.WriteLine();
    Console.WriteLine($"=== {scenario} ===");

    var scenarioResult = scenario switch
    {
        "startup-recovery" => await RunStartupRecoveryScenarioAsync(),
        "helper-protocol" => await RunHelperProtocolScenarioAsync(),
        _ => await RunScenarioAsync(scenario)
    };
    if (!scenarioResult)
    {
        failures++;
    }
}

Console.WriteLine();
Console.WriteLine(failures == 0
    ? "All requested simulations reached their expected safe outcome."
    : $"{failures} simulation(s) did not reach the expected safe outcome.");

return failures == 0 ? 0 : 1;

static async Task<bool> RunScenarioAsync(string scenario)
{
    const uint originalLimit = 450_000;
    const uint targetLimit = 400_000;

    var fingerprint = DeviceFingerprint.FromStableIdentifier("SIMULATED-GPU-0");
    var snapshot = RecoverySnapshot.Prepare(
        DateTimeOffset.UtcNow,
        "Simulation",
        "InMemory",
        fingerprint,
        originalLimit,
        targetLimit,
        minimumPowerLimitMilliwatts: 150_000,
        maximumPowerLimitMilliwatts: 600_000);

    var failureMode = scenario switch
    {
        "verification-failure" => SimulatedFailureMode.TargetReadBackMismatch,
        "interruption" => SimulatedFailureMode.InterruptAfterTargetWrite,
        _ => SimulatedFailureMode.None
    };

    var controller = new SimulatedGpuPowerController(fingerprint, originalLimit, targetLimit, failureMode);
    var engine = new GpuActivationEngine();
    var journalPath = Path.Combine(
        Environment.CurrentDirectory,
        "artifacts",
        "simulation",
        $"{scenario}.json");

    var activation = await engine.ActivateAsync(snapshot, journalPath, controller);
    PrintResult("Activation", activation, controller.CurrentLimitMilliwatts);

    ActivationResult finalResult = activation;
    if (activation.Outcome == ActivationOutcome.Activated)
    {
        finalResult = await engine.RestoreAsync(journalPath, controller);
        PrintResult("Restoration", finalResult, controller.CurrentLimitMilliwatts);
    }

    Console.WriteLine("Event trace:");
    foreach (var entry in controller.Events)
    {
        Console.WriteLine($"  {entry}");
    }

    Console.WriteLine($"Journal: {Path.GetFullPath(journalPath)}");

    var expectedOutcome = scenario == "success"
        ? ActivationOutcome.Restored
        : ActivationOutcome.FailedRolledBack;

    var safe = finalResult.Outcome == expectedOutcome &&
               controller.CurrentLimitMilliwatts == originalLimit &&
               finalResult.OriginalLimitVerified &&
               finalResult.JournalVerified &&
               finalResult.Snapshot.Stage == RecoveryStage.Restored;

    Console.WriteLine($"Safe final state: {safe}");
    return safe;
}

static async Task<bool> RunStartupRecoveryScenarioAsync()
{
    const uint originalLimit = 450_000;
    const uint targetLimit = 400_000;

    var directory = Path.Combine(
        Environment.CurrentDirectory,
        "artifacts",
        "startup-simulation");
    var fingerprint = DeviceFingerprint.FromStableIdentifier("SIMULATED-STARTUP-GPU");
    var createdAtUtc = DateTimeOffset.UtcNow;

    var pending = RecoverySnapshot.Prepare(
        createdAtUtc,
        "Simulation",
        "InMemory",
        fingerprint,
        originalLimit,
        targetLimit,
        150_000,
        600_000).MarkApplied(targetLimit, createdAtUtc.AddSeconds(1));

    var dryRun = RecoverySnapshot.Prepare(
        createdAtUtc,
        "Simulation",
        "InMemory",
        fingerprint,
        originalLimit,
        targetLimit,
        150_000,
        600_000,
        purpose: RecoveryPurpose.DryRunPlan);

    var restored = RecoverySnapshot.Prepare(
        createdAtUtc,
        "Simulation",
        "InMemory",
        fingerprint,
        originalLimit,
        targetLimit,
        150_000,
        600_000).MarkRestored(originalLimit, createdAtUtc.AddSeconds(1));

    var pendingPath = Path.Combine(directory, "pending.json");
    await RecoveryJournalStore.SaveAsync(pendingPath, pending);
    await RecoveryJournalStore.SaveAsync(Path.Combine(directory, "dry-run.json"), dryRun);
    await RecoveryJournalStore.SaveAsync(Path.Combine(directory, "restored.json"), restored);
    await File.WriteAllTextAsync(Path.Combine(directory, "corrupt.json"), "{ not valid recovery json }");

    var discovery = await new StartupRecoveryScanner().DiscoverAsync(directory);
    foreach (var item in discovery.Items)
    {
        Console.WriteLine($"Discovered {Path.GetFileName(item.JournalPath),-14} {item.Kind}");
    }

    var pendingItem = discovery.Items.Single(item => item.Kind == RecoveryDiscoveryKind.PendingLiveRecovery);
    var controller = new SimulatedGpuPowerController(
        fingerprint,
        targetLimit,
        targetLimit,
        SimulatedFailureMode.None);
    var recoveryCoordinator = new StartupRecoveryCoordinator(new GpuActivationEngine());

    var rejectionRequest = recoveryCoordinator.CreateApprovalRequest(pendingItem);
    Console.WriteLine($"Approval prompt: {rejectionRequest.ConfirmationSummary}");
    var rejection = await recoveryCoordinator.ResolveAsync(
        pendingItem,
        rejectionRequest,
        RecoveryApprovalDecision.Reject(rejectionRequest, rejectionRequest.CreatedAtUtc.AddSeconds(1)),
        controller);
    var writesAfterRejection = controller.Events.Count(entry => entry.StartsWith("SET", StringComparison.Ordinal));
    Console.WriteLine($"Rejected decision: {rejection.Outcome}; writes: {writesAfterRejection}");

    var approvalRequest = recoveryCoordinator.CreateApprovalRequest(pendingItem);
    var approval = await recoveryCoordinator.ResolveAsync(
        pendingItem,
        approvalRequest,
        RecoveryApprovalDecision.Approve(approvalRequest, approvalRequest.CreatedAtUtc.AddSeconds(1)),
        controller);

    Console.WriteLine($"Approved decision: {approval.Outcome}");
    Console.WriteLine($"Final simulated limit: {controller.CurrentLimitMilliwatts / 1000m:0.###} W");
    Console.WriteLine($"Journal: {Path.GetFullPath(pendingPath)}");

    var classificationSafe = discovery.Items.Count(item => item.Kind == RecoveryDiscoveryKind.PendingLiveRecovery) == 1 &&
                             discovery.Items.Count(item => item.Kind == RecoveryDiscoveryKind.DryRunPlan) == 1 &&
                             discovery.Items.Count(item => item.Kind == RecoveryDiscoveryKind.RestoredLiveRecovery) == 1 &&
                             discovery.Items.Count(item => item.Kind == RecoveryDiscoveryKind.InvalidJournal) == 1;
    var safe = classificationSafe &&
               rejection.Outcome == StartupRecoveryOutcome.Declined &&
               writesAfterRejection == 0 &&
               approval.Outcome == StartupRecoveryOutcome.Restored &&
               controller.CurrentLimitMilliwatts == originalLimit;

    Console.WriteLine($"Safe final state: {safe}");
    return safe;
}

static async Task<bool> RunHelperProtocolScenarioAsync()
{
    const uint originalLimit = 450_000;
    const uint targetLimit = 400_000;

    var fingerprint = DeviceFingerprint.FromStableIdentifier("SIMULATED-HOST-GPU");
    var snapshot = RecoverySnapshot.Prepare(
        DateTimeOffset.UtcNow,
        "Simulation",
        "InMemory",
        fingerprint,
        originalLimit,
        targetLimit,
        150_000,
        600_000);
    var journalPath = Path.Combine(
        Environment.CurrentDirectory,
        "artifacts",
        "helper-protocol",
        "recovery.json");
    await RecoveryJournalStore.SaveAsync(journalPath, snapshot);

    var controller = new SimulatedGpuPowerController(
        fingerprint,
        originalLimit,
        targetLimit,
        SimulatedFailureMode.None);
    var registry = new SimulationHardwareHostRegistry(journalPath, controller);

    using var session = HardwareHostSessionFactory.Create();
    var processor = new HardwareHostCommandProcessor(
        session.Host,
        registry,
        new GpuActivationEngine());

    var activate = session.Client.CreateCommand(
        HardwareHostOperation.ActivatePrepared,
        snapshot.SnapshotId);
    var activation = await processor.ProcessAsync(activate);
    Console.WriteLine($"Authenticated activation: {activation.Outcome} / {activation.Execution?.Outcome}");
    Console.WriteLine($"Simulated limit: {controller.CurrentLimitMilliwatts / 1000m:0.###} W");

    var replay = await processor.ProcessAsync(activate);
    Console.WriteLine($"Replay attempt: {replay.Outcome}");

    var restore = session.Client.CreateCommand(
        HardwareHostOperation.RestorePending,
        snapshot.SnapshotId);
    var tampered = restore with
    {
        Command = restore.Command with { SnapshotId = Guid.NewGuid() }
    };
    var tamperResult = await processor.ProcessAsync(tampered);
    Console.WriteLine($"Tampered command: {tamperResult.Outcome}");

    var restoration = await processor.ProcessAsync(restore);
    Console.WriteLine($"Authenticated restoration: {restoration.Outcome} / {restoration.Execution?.Outcome}");
    Console.WriteLine($"Final simulated limit: {controller.CurrentLimitMilliwatts / 1000m:0.###} W");
    Console.WriteLine($"Journal: {Path.GetFullPath(journalPath)}");

    var safe = activation.Outcome == HardwareHostProcessOutcome.Executed &&
               activation.Execution?.Outcome == ActivationOutcome.Activated &&
               replay.Outcome == HardwareHostProcessOutcome.RejectedReplay &&
               tamperResult.Outcome == HardwareHostProcessOutcome.RejectedAuthentication &&
               restoration.Outcome == HardwareHostProcessOutcome.Executed &&
               restoration.Execution?.Outcome == ActivationOutcome.Restored &&
               controller.CurrentLimitMilliwatts == originalLimit;

    Console.WriteLine($"Safe final state: {safe}");
    return safe;
}

static void PrintResult(string label, ActivationResult result, uint currentLimitMilliwatts)
{
    Console.WriteLine($"{label} outcome:       {result.Outcome}");
    Console.WriteLine($"Hardware write tried: {result.HardwareWriteAttempted}");
    Console.WriteLine($"Original verified:    {result.OriginalLimitVerified}");
    Console.WriteLine($"Journal verified:     {result.JournalVerified}");
    Console.WriteLine($"Journal stage:        {result.Snapshot.Stage}");
    Console.WriteLine($"Simulated limit:      {currentLimitMilliwatts / 1000m:0.###} W");
    Console.WriteLine($"Details:              {result.Message}");
}

internal enum SimulatedFailureMode
{
    None,
    TargetReadBackMismatch,
    InterruptAfterTargetWrite
}

internal sealed class SimulatedGpuPowerController(
    DeviceFingerprint deviceFingerprint,
    uint initialLimitMilliwatts,
    uint targetLimitMilliwatts,
    SimulatedFailureMode failureMode) : IGpuPowerController
{
    private bool _targetFailureTriggered;

    public DeviceFingerprint DeviceFingerprint { get; } = deviceFingerprint;

    public uint CurrentLimitMilliwatts { get; private set; } = initialLimitMilliwatts;

    public List<string> Events { get; } = [];

    public Task<uint> ReadPowerLimitMilliwattsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Events.Add($"READ {CurrentLimitMilliwatts} mW");
        return Task.FromResult(CurrentLimitMilliwatts);
    }

    public Task SetPowerLimitMilliwattsAsync(
        uint powerLimitMilliwatts,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Events.Add($"SET {powerLimitMilliwatts} mW (in memory)");

        if (!_targetFailureTriggered && powerLimitMilliwatts == targetLimitMilliwatts)
        {
            _targetFailureTriggered = true;

            if (failureMode == SimulatedFailureMode.TargetReadBackMismatch)
            {
                CurrentLimitMilliwatts = powerLimitMilliwatts + 1_000;
                return Task.CompletedTask;
            }

            if (failureMode == SimulatedFailureMode.InterruptAfterTargetWrite)
            {
                CurrentLimitMilliwatts = powerLimitMilliwatts;
                throw new OperationCanceledException("Simulated interruption after the target write.");
            }
        }

        CurrentLimitMilliwatts = powerLimitMilliwatts;
        return Task.CompletedTask;
    }
}

internal sealed class SimulationHardwareHostRegistry(
    string journalPath,
    SimulatedGpuPowerController controller) : IHardwareHostTargetRegistry
{
    public async Task<HardwareHostTarget?> ResolveAsync(
        Guid snapshotId,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await RecoveryJournalStore.LoadAsync(journalPath, cancellationToken);
        return snapshot.SnapshotId == snapshotId
            ? new HardwareHostTarget(journalPath, snapshot, controller)
            : null;
    }
}
