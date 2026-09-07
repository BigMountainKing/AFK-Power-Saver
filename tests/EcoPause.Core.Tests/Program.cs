using EcoPause.Core.Activation;
using EcoPause.Core.Recovery;
using EcoPause.CrashRecovery.SimulationModel;
using EcoPause.Desktop.Safety;
using EcoPause.Hardware.Abstractions;
using EcoPause.HardwareHost.Protocol;
using EcoPause.HardwareHost.Transport;
using EcoPause.LiveCanary.Model;

var tests = new (string Name, Func<Task> Run)[]
{
    ("prepared snapshots require recovery", PreparedSnapshotsRequireRecovery),
    ("targets cannot raise the original limit", TargetsCannotRaiseOriginalLimit),
    ("targets must remain inside driver constraints", TargetsMustRemainInsideDriverConstraints),
    ("apply requires an exact read-back", ApplyRequiresExactReadBack),
    ("restore requires an exact read-back", RestoreRequiresExactReadBack),
    ("journal round-trips and replaces atomically", JournalRoundTripsAndReplacesAtomically),
    ("journal detects content corruption", JournalDetectsContentCorruption),
    ("fingerprints do not expose the stable identifier", FingerprintsDoNotExposeStableIdentifier),
    ("dry-run coordinator persists an inactive plan", DryRunCoordinatorPersistsInactivePlan),
    ("dry-run coordinator rejects an increase", DryRunCoordinatorRejectsIncrease),
    ("dry-run coordinator requires stable identity", DryRunCoordinatorRequiresStableIdentity),
    ("dry-run plans cannot transition recovery state", DryRunPlansCannotTransitionRecoveryState),
    ("activation persists before writing and restores exactly", ActivationPersistsBeforeWritingAndRestoresExactly),
    ("failed apply verification rolls back", FailedApplyVerificationRollsBack),
    ("interruption after a write rolls back", InterruptionAfterWriteRollsBack),
    ("applied-journal failure rolls back", AppliedJournalFailureRollsBack),
    ("device mismatch fails before writing", DeviceMismatchFailsBeforeWriting),
    ("failed rollback remains recovery-pending", FailedRollbackRemainsRecoveryPending),
    ("startup scanner classifies journals without recursion", StartupScannerClassifiesJournalsWithoutRecursion),
    ("rejected startup approval performs no write", RejectedStartupApprovalPerformsNoWrite),
    ("expired startup approval performs no write", ExpiredStartupApprovalPerformsNoWrite),
    ("approved startup recovery is exact and one-use", ApprovedStartupRecoveryIsExactAndOneUse),
    ("changed journal after approval performs no write", ChangedJournalAfterApprovalPerformsNoWrite),
    ("authenticated host commands activate and restore", AuthenticatedHostCommandsActivateAndRestore),
    ("replayed host command is rejected", ReplayedHostCommandIsRejected),
    ("tampered host command performs no write", TamperedHostCommandPerformsNoWrite),
    ("expired host command performs no write", ExpiredHostCommandPerformsNoWrite),
    ("out-of-order host command performs no write", OutOfOrderHostCommandPerformsNoWrite),
    ("unknown host target consumes the command", UnknownHostTargetConsumesCommand),
    ("bounded transport frames round-trip", BoundedTransportFramesRoundTrip),
    ("transport rejects oversized writes before output", TransportRejectsOversizedWritesBeforeOutput),
    ("transport rejects oversized declared frames", TransportRejectsOversizedDeclaredFrames),
    ("transport rejects truncated frames", TransportRejectsTruncatedFrames),
    ("transport validates generated pipe names", TransportValidatesGeneratedPipeNames),
    ("transport bootstrap round-trips and imports", TransportBootstrapRoundTripsAndImports),
    ("transport rejects invalid expected peer IDs", TransportRejectsInvalidExpectedPeerIds),
    ("Windows pipe peers report their process ID", WindowsPipePeersReportTheirProcessId),
    ("persistent fake hardware survives controller restart", PersistentFakeHardwareSurvivesControllerRestart),
    ("persistent fake hardware rejects corruption", PersistentFakeHardwareRejectsCorruption),
    ("crash simulation paths and snapshots are fixed", CrashSimulationPathsAndSnapshotsAreFixed),
    ("desktop accepts complete safety evidence", DesktopAcceptsCompleteSafetyEvidence),
    ("desktop rejects incomplete safety evidence", DesktopRejectsIncompleteSafetyEvidence),
    ("desktop reports declined UAC safely", DesktopReportsDeclinedUacSafely),
    ("desktop parses sanitized read-only GPU data", DesktopParsesSanitizedReadOnlyGpuData),
    ("desktop parses AMD factory-relative GPU data", DesktopParsesAmdRelativeGpuData),
    ("desktop rejects identifying GPU fields", DesktopRejectsIdentifyingGpuFields),
    ("desktop rejects oversized GPU reports", DesktopRejectsOversizedGpuReports),
    ("desktop rejects inconsistent GPU constraints", DesktopRejectsInconsistentGpuConstraints),
    ("desktop live toggle accepts baseline and allow-listed recovery states", DesktopLiveToggleAcceptsAllowListedReadyStates),
    ("desktop live toggle rejects ambiguous hardware", DesktopLiveToggleRejectsAmbiguousHardware),
    ("desktop accepts complete live toggle evidence", DesktopAcceptsCompleteLiveToggleEvidence),
    ("desktop accepts complete AMD live toggle evidence", DesktopAcceptsCompleteAmdLiveToggleEvidence),
    ("desktop rejects conflicting live toggle evidence", DesktopRejectsConflictingLiveToggleEvidence),
    ("desktop routes authorized controls to the live toggle", DesktopRoutesAuthorizedControlsToLiveToggle),
    ("disabled idle activation performs no action", DisabledIdleActivationPerformsNoAction),
    ("idle activation applies only a restored profile", IdleActivationAppliesOnlyRestoredProfile),
    ("idle activation restores only its owned profile", IdleActivationRestoresOnlyOwnedProfile),
    ("idle activation validates the configured delay", IdleActivationValidatesConfiguredDelay),
    ("startup launch command quotes the executable path", StartupLaunchCommandQuotesExecutablePath),
    ("startup launch policy recognizes only the current executable", StartupLaunchPolicyRecognizesOnlyCurrentExecutable),
    ("tip configuration accepts allow-listed destinations", TipConfigurationAcceptsAllowListedDestinations),
    ("tip configuration rejects unsafe payment links", TipConfigurationRejectsUnsafePaymentLinks),
    ("tip configuration rejects malformed crypto addresses", TipConfigurationRejectsMalformedCryptoAddresses),
    ("activity preview counts down during active use", ActivityPreviewCountsDownDuringActiveUse),
    ("activity preview recommends pause at the idle threshold", ActivityPreviewRecommendsPauseAtIdleThreshold),
    ("activity preview recommends pause while locked", ActivityPreviewRecommendsPauseWhileLocked),
    ("activity preview rejects invalid durations", ActivityPreviewRejectsInvalidDurations),
    ("persistent toggle profile accepts recoverable stages", PersistentToggleProfileAcceptsRecoverableStages),
    ("desktop accepts persistent fake pause evidence", DesktopAcceptsPersistentFakePauseEvidence),
    ("desktop accepts persistent fake restore evidence", DesktopAcceptsPersistentFakeRestoreEvidence),
    ("desktop rejects conflicting persistent toggle evidence", DesktopRejectsConflictingPersistentToggleEvidence),
    ("desktop parses display-only toggle receipts", DesktopParsesDisplayOnlyToggleReceipts),
    ("desktop rejects tampered toggle receipts", DesktopRejectsTamperedToggleReceipts),
    ("persistent toggle profiles are fixed and unique", PersistentToggleProfilesAreFixedAndUnique),
    ("fake targets validate against live constraints", FakeTargetsValidateAgainstLiveConstraints),
    ("fake targets reject unsafe live constraints", FakeTargetsRejectUnsafeLiveConstraints),
    ("live power profiles are fixed and match the desktop", LivePowerProfilesAreFixedAndMatchDesktop),
    ("portable live percentages adapt to another GPU default", PortableLivePercentagesAdaptToAnotherGpu),
    ("portable live percentages preserve AMD driver steps", PortableLivePercentagesPreserveAmdDriverSteps),
    ("live canary accepts only the exact baseline", LiveCanaryAcceptsOnlyExactBaseline),
    ("live canary recognizes the fixed recovery state", LiveCanaryRecognizesFixedRecoveryState),
    ("live canary rejects ambiguous devices and limits", LiveCanaryRejectsAmbiguousDevicesAndLimits),
    ("live canary snapshots are fixed and recoverable", LiveCanarySnapshotsAreFixedAndRecoverable),
    ("live recovery snapshots preserve the selected profile", LiveRecoverySnapshotsPreserveSelectedProfile),
    ("live canary response timeout exceeds its hold", LiveCanaryResponseTimeoutExceedsHold),
    ("live recovery drill accepts exact two-phase evidence", LiveRecoveryDrillAcceptsExactEvidence),
    ("live recovery drill rejects conflicting evidence", LiveRecoveryDrillRejectsConflictingEvidence),
    ("live persistent toggle accepts exact pause evidence", LivePersistentToggleAcceptsExactPauseEvidence),
    ("live persistent toggle rejects crash-style evidence", LivePersistentToggleRejectsCrashStyleEvidence)
};

var failures = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS: {test.Name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL: {test.Name} - {exception.Message}");
    }
}

Console.WriteLine();
Console.WriteLine($"{tests.Length - failures}/{tests.Length} recovery tests passed.");
return failures == 0 ? 0 : 1;

static Task PreparedSnapshotsRequireRecovery()
{
    var snapshot = CreateSnapshot();
    AssertEqual(RecoveryStage.Prepared, snapshot.Stage);
    AssertTrue(snapshot.IsRecoveryPending, "A prepared journal must be treated as pending after a crash.");
    AssertEqual(snapshot.OriginalPowerLimitMilliwatts, snapshot.LastVerifiedPowerLimitMilliwatts);
    return Task.CompletedTask;
}

static Task TipConfigurationAcceptsAllowListedDestinations()
{
    var configuration = TipPaymentPolicy.Parse($"""
        Paypal: paypal.me/afkpowersaver
        USCD ERC20: 0x{new string('1', 40)}
        Solana: {new string('A', 44)}
        BTC: 1{new string('A', 33)}
        ETH: 0x{new string('2', 40)}
        """);
    AssertEqual("paypal.me", configuration.PayPalUri.Host);
    AssertEqual(4, configuration.CryptoOptions.Count);
    AssertEqual("USDC (ERC-20)", configuration.CryptoOptions[0].Label);
    AssertTrue(
        configuration.CryptoOptions.All(option => option.DisplayAddress.Contains('…')),
        "Long crypto destinations should be shortened in the UI.");
    return Task.CompletedTask;
}

static Task TipConfigurationRejectsUnsafePaymentLinks()
{
    AssertThrows<InvalidOperationException>(() => TipPaymentPolicy.Parse("Paypal: https://example.com/not-paypal"));
    AssertThrows<InvalidOperationException>(() => TipPaymentPolicy.Parse("Paypal: http://paypal.me/not-secure"));
    return Task.CompletedTask;
}

static Task TipConfigurationRejectsMalformedCryptoAddresses()
{
    AssertThrows<InvalidOperationException>(() => TipPaymentPolicy.Parse("""
        Paypal: paypal.me/afkpowersaver
        ETH: not-an-address
        """));
    return Task.CompletedTask;
}

static Task TargetsCannotRaiseOriginalLimit()
{
    AssertThrows<ArgumentException>(() => CreateSnapshot(target: 500_000));
    return Task.CompletedTask;
}

static Task TargetsMustRemainInsideDriverConstraints()
{
    AssertThrows<ArgumentOutOfRangeException>(() => CreateSnapshot(target: 149_000));
    return Task.CompletedTask;
}

static Task ApplyRequiresExactReadBack()
{
    var snapshot = CreateSnapshot();
    AssertThrows<InvalidOperationException>(() => snapshot.MarkApplied(399_000, Utc(1)));

    var applied = snapshot.MarkApplied(400_000, Utc(1));
    AssertEqual(RecoveryStage.Applied, applied.Stage);
    AssertTrue(applied.IsRecoveryPending, "An applied limit must remain pending until restoration is verified.");
    return Task.CompletedTask;
}

static Task RestoreRequiresExactReadBack()
{
    var applied = CreateSnapshot().MarkApplied(400_000, Utc(1));
    AssertThrows<InvalidOperationException>(() => applied.MarkRestored(449_000, Utc(2)));

    var restored = applied.MarkRestored(450_000, Utc(2));
    AssertEqual(RecoveryStage.Restored, restored.Stage);
    AssertTrue(!restored.IsRecoveryPending, "An exactly verified restore should close recovery.");
    return Task.CompletedTask;
}

static async Task JournalRoundTripsAndReplacesAtomically()
{
    await WithTemporaryJournal(async path =>
    {
        var prepared = CreateSnapshot();
        await RecoveryJournalStore.SaveAsync(path, prepared);
        AssertEqual(prepared, await RecoveryJournalStore.LoadAsync(path));

        var applied = prepared.MarkApplied(400_000, Utc(1));
        await RecoveryJournalStore.SaveAsync(path, applied);
        AssertEqual(applied, await RecoveryJournalStore.LoadAsync(path));

        var temporaryFiles = Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp");
        AssertEqual(0, temporaryFiles.Length);
    });
}

static async Task JournalDetectsContentCorruption()
{
    await WithTemporaryJournal(async path =>
    {
        await RecoveryJournalStore.SaveAsync(path, CreateSnapshot());

        var json = await File.ReadAllTextAsync(path);
        json = json.Replace("400000", "399000", StringComparison.Ordinal);
        await File.WriteAllTextAsync(path, json);

        await AssertThrowsAsync<RecoveryJournalException>(() => RecoveryJournalStore.LoadAsync(path));
    });
}

static Task FingerprintsDoNotExposeStableIdentifier()
{
    const string stableIdentifier = "GPU-01234567-89ab-cdef-0123-456789abcdef";
    var fingerprint = DeviceFingerprint.FromStableIdentifier(stableIdentifier);

    AssertTrue(!fingerprint.Value.Contains(stableIdentifier, StringComparison.OrdinalIgnoreCase),
        "The local journal fingerprint must not contain the raw GPU identifier.");
    AssertTrue(fingerprint.Value.StartsWith("sha256:", StringComparison.Ordinal),
        "The fingerprint should identify its digest format.");
    return Task.CompletedTask;
}

static async Task DryRunCoordinatorPersistsInactivePlan()
{
    await WithTemporaryJournal(async path =>
    {
        var coordinator = new DryRunActivationCoordinator(
            CreateFakeProbe(),
            timeProvider: new FixedTimeProvider(Utc(3)));

        var plan = await coordinator.CreatePlanAsync(0, 400_000, path);
        var reloaded = await RecoveryJournalStore.LoadAsync(path);

        AssertTrue(!plan.HardwareWriteAttempted, "A dry-run coordinator must never report a hardware write.");
        AssertEqual(RecoveryPurpose.DryRunPlan, plan.Snapshot.Purpose);
        AssertEqual(RecoveryStage.Prepared, plan.Snapshot.Stage);
        AssertTrue(!plan.Snapshot.IsRecoveryPending, "A dry-run file must never trigger startup recovery.");
        AssertEqual(plan.Snapshot, reloaded);
    });
}

static async Task DryRunCoordinatorRejectsIncrease()
{
    await WithTemporaryJournal(async path =>
    {
        var coordinator = new DryRunActivationCoordinator(CreateFakeProbe());
        await AssertThrowsAsync<ActivationPlanningException>(() =>
            coordinator.CreatePlanAsync(0, 500_000, path));
        AssertTrue(!File.Exists(path), "An invalid plan must not be persisted.");
    });
}

static async Task DryRunCoordinatorRequiresStableIdentity()
{
    await WithTemporaryJournal(async path =>
    {
        var coordinator = new DryRunActivationCoordinator(CreateFakeProbe(deviceFingerprint: null));
        await AssertThrowsAsync<ActivationPlanningException>(() =>
            coordinator.CreatePlanAsync(0, 400_000, path));
        AssertTrue(!File.Exists(path), "A plan without stable device identity must not be persisted.");
    });
}

static Task DryRunPlansCannotTransitionRecoveryState()
{
    var snapshot = RecoverySnapshot.Prepare(
        Utc(0),
        "NVIDIA",
        "NVML",
        DeviceFingerprint.FromStableIdentifier("GPU-test-identifier"),
        450_000,
        400_000,
        150_000,
        600_000,
        purpose: RecoveryPurpose.DryRunPlan);

    AssertThrows<InvalidOperationException>(() => snapshot.MarkApplied(400_000, Utc(1)));
    AssertThrows<InvalidOperationException>(() => snapshot.MarkRestored(450_000, Utc(1)));
    return Task.CompletedTask;
}

static async Task ActivationPersistsBeforeWritingAndRestoresExactly()
{
    var events = new List<string>();
    var store = new InMemoryJournalStore(events);
    var controller = CreatePowerController(events);
    var engine = new GpuActivationEngine(store, new FixedTimeProvider(Utc(5)));

    var activation = await engine.ActivateAsync(CreateLiveSnapshot(), "activation.json", controller);
    AssertEqual(ActivationOutcome.Activated, activation.Outcome);
    AssertEqual(400_000u, controller.CurrentLimitMilliwatts);
    AssertEqual(RecoveryStage.Applied, activation.Snapshot.Stage);
    AssertTrue(activation.Snapshot.IsRecoveryPending, "An applied simulation must require restoration.");

    var preparedSave = events.IndexOf("SAVE Prepared");
    var targetWrite = events.IndexOf("SET 400000");
    AssertTrue(preparedSave >= 0 && targetWrite > preparedSave,
        "The prepared journal must be saved before the first target write.");

    var restoration = await engine.RestoreAsync("activation.json", controller);
    AssertEqual(ActivationOutcome.Restored, restoration.Outcome);
    AssertEqual(450_000u, controller.CurrentLimitMilliwatts);
    AssertEqual(RecoveryStage.Restored, restoration.Snapshot.Stage);
    AssertTrue(!restoration.Snapshot.IsRecoveryPending, "Exact restoration should close recovery.");
}

static async Task FailedApplyVerificationRollsBack()
{
    var store = new InMemoryJournalStore();
    var controller = CreatePowerController(failureMode: TestPowerFailure.TargetReadBackMismatch);
    var engine = new GpuActivationEngine(store, new FixedTimeProvider(Utc(5)));

    var result = await engine.ActivateAsync(CreateLiveSnapshot(), "verification-failure.json", controller);
    AssertSafeRollback(result, controller);
}

static async Task InterruptionAfterWriteRollsBack()
{
    var store = new InMemoryJournalStore();
    var controller = CreatePowerController(failureMode: TestPowerFailure.InterruptAfterTargetWrite);
    var engine = new GpuActivationEngine(store, new FixedTimeProvider(Utc(5)));

    using var cancellationSource = new CancellationTokenSource();
    var result = await engine.ActivateAsync(
        CreateLiveSnapshot(),
        "interruption.json",
        controller,
        cancellationSource.Token);

    AssertSafeRollback(result, controller);
    AssertTrue(controller.RollbackReceivedUncancelledToken,
        "Rollback must use an uncancelled token after an interrupted activation.");
}

static async Task AppliedJournalFailureRollsBack()
{
    var store = new InMemoryJournalStore(failOnSaveCall: 2);
    var controller = CreatePowerController();
    var engine = new GpuActivationEngine(store, new FixedTimeProvider(Utc(5)));

    var result = await engine.ActivateAsync(CreateLiveSnapshot(), "journal-failure.json", controller);
    AssertSafeRollback(result, controller);
    AssertEqual(3, store.SaveCalls);
}

static async Task DeviceMismatchFailsBeforeWriting()
{
    var store = new InMemoryJournalStore();
    var otherFingerprint = DeviceFingerprint.FromStableIdentifier("OTHER-GPU");
    var controller = new TestPowerController(otherFingerprint, 450_000, 400_000);
    var engine = new GpuActivationEngine(store, new FixedTimeProvider(Utc(5)));

    var result = await engine.ActivateAsync(CreateLiveSnapshot(), "mismatch.json", controller);
    AssertEqual(ActivationOutcome.FailedBeforeWrite, result.Outcome);
    AssertTrue(!result.HardwareWriteAttempted, "Identity mismatch must fail without a write.");
    AssertEqual(0, controller.SetCalls);
    AssertEqual(0, store.SaveCalls);
}

static async Task FailedRollbackRemainsRecoveryPending()
{
    var store = new InMemoryJournalStore();
    var controller = CreatePowerController(failureMode: TestPowerFailure.TargetAndRollbackReadBackMismatch);
    var engine = new GpuActivationEngine(store, new FixedTimeProvider(Utc(5)));

    var result = await engine.ActivateAsync(CreateLiveSnapshot(), "rollback-failure.json", controller);
    AssertEqual(ActivationOutcome.FailedRollbackIncomplete, result.Outcome);
    AssertTrue(!result.OriginalLimitVerified, "A mismatched rollback must not claim restoration.");
    AssertTrue(result.Snapshot.IsRecoveryPending, "Failed rollback must remain recovery-pending.");
}

static async Task StartupScannerClassifiesJournalsWithoutRecursion()
{
    await WithTemporaryJournal(async path =>
    {
        var directory = Path.GetDirectoryName(path)!;
        var pending = CreateAppliedLiveSnapshot();
        var dryRun = RecoverySnapshot.Prepare(
            Utc(0),
            "Simulation",
            "InMemory",
            DeviceFingerprint.FromStableIdentifier("TEST-GPU"),
            450_000,
            400_000,
            150_000,
            600_000,
            purpose: RecoveryPurpose.DryRunPlan);
        var restored = CreateLiveSnapshot().MarkRestored(450_000, Utc(1));

        await RecoveryJournalStore.SaveAsync(Path.Combine(directory, "pending.json"), pending);
        await RecoveryJournalStore.SaveAsync(Path.Combine(directory, "dry-run.json"), dryRun);
        await RecoveryJournalStore.SaveAsync(Path.Combine(directory, "restored.json"), restored);
        await File.WriteAllTextAsync(Path.Combine(directory, "corrupt.json"), "not-json");

        var nestedDirectory = Path.Combine(directory, "nested");
        await RecoveryJournalStore.SaveAsync(
            Path.Combine(nestedDirectory, "must-not-be-scanned.json"),
            pending);

        var report = await new StartupRecoveryScanner().DiscoverAsync(directory);
        AssertEqual(4, report.Items.Count);
        AssertEqual(1, report.Items.Count(item => item.Kind == RecoveryDiscoveryKind.PendingLiveRecovery));
        AssertEqual(1, report.Items.Count(item => item.Kind == RecoveryDiscoveryKind.DryRunPlan));
        AssertEqual(1, report.Items.Count(item => item.Kind == RecoveryDiscoveryKind.RestoredLiveRecovery));
        AssertEqual(1, report.Items.Count(item => item.Kind == RecoveryDiscoveryKind.InvalidJournal));
        AssertTrue(report.HasPendingRecovery, "The pending live journal should be surfaced.");
    });
}

static async Task RejectedStartupApprovalPerformsNoWrite()
{
    var snapshot = CreateAppliedLiveSnapshot();
    var store = new InMemoryJournalStore();
    await store.SaveAsync("startup.json", snapshot);
    var controller = CreatePowerController();
    controller.SetCurrentLimitForRecovery(400_000);
    var coordinator = CreateStartupCoordinator(store);
    var item = CreatePendingItem(snapshot, "startup.json");
    var request = coordinator.CreateApprovalRequest(item);

    var result = await coordinator.ResolveAsync(
        item,
        request,
        RecoveryApprovalDecision.Reject(request, Utc(7)),
        controller);

    AssertEqual(StartupRecoveryOutcome.Declined, result.Outcome);
    AssertEqual(0, controller.SetCalls);
    AssertEqual(400_000u, controller.CurrentLimitMilliwatts);
}

static async Task ExpiredStartupApprovalPerformsNoWrite()
{
    var snapshot = CreateAppliedLiveSnapshot();
    var store = new InMemoryJournalStore();
    await store.SaveAsync("expired.json", snapshot);
    var controller = CreatePowerController();
    controller.SetCurrentLimitForRecovery(400_000);
    var coordinator = CreateStartupCoordinator(store);
    var item = CreatePendingItem(snapshot, "expired.json");
    var request = coordinator.CreateApprovalRequest(item, TimeSpan.FromMinutes(1));

    var result = await coordinator.ResolveAsync(
        item,
        request,
        RecoveryApprovalDecision.Approve(request, Utc(8)),
        controller);

    AssertEqual(StartupRecoveryOutcome.InvalidApproval, result.Outcome);
    AssertEqual(0, controller.SetCalls);
}

static async Task ApprovedStartupRecoveryIsExactAndOneUse()
{
    var snapshot = CreateAppliedLiveSnapshot();
    var store = new InMemoryJournalStore();
    await store.SaveAsync("approved.json", snapshot);
    var controller = CreatePowerController();
    controller.SetCurrentLimitForRecovery(400_000);
    var coordinator = CreateStartupCoordinator(store);
    var item = CreatePendingItem(snapshot, "approved.json");
    var request = coordinator.CreateApprovalRequest(item);
    var decision = RecoveryApprovalDecision.Approve(request, Utc(7));

    var result = await coordinator.ResolveAsync(item, request, decision, controller);
    AssertEqual(StartupRecoveryOutcome.Restored, result.Outcome);
    AssertEqual(450_000u, controller.CurrentLimitMilliwatts);
    AssertEqual(1, controller.SetCalls);

    var reused = await coordinator.ResolveAsync(item, request, decision, controller);
    AssertEqual(StartupRecoveryOutcome.InvalidApproval, reused.Outcome);
    AssertEqual(1, controller.SetCalls);
}

static async Task ChangedJournalAfterApprovalPerformsNoWrite()
{
    var approvedSnapshot = CreateAppliedLiveSnapshot();
    var changedSnapshot = CreateLiveSnapshot(Guid.NewGuid()).MarkApplied(400_000, Utc(1));
    var store = new InMemoryJournalStore();
    await store.SaveAsync("changed.json", approvedSnapshot);
    var controller = CreatePowerController();
    controller.SetCurrentLimitForRecovery(400_000);
    var coordinator = CreateStartupCoordinator(store);
    var item = CreatePendingItem(approvedSnapshot, "changed.json");
    var request = coordinator.CreateApprovalRequest(item);

    await store.SaveAsync("changed.json", changedSnapshot);
    var result = await coordinator.ResolveAsync(
        item,
        request,
        RecoveryApprovalDecision.Approve(request, Utc(7)),
        controller);

    AssertEqual(StartupRecoveryOutcome.StaleJournal, result.Outcome);
    AssertEqual(0, controller.SetCalls);
    AssertEqual(400_000u, controller.CurrentLimitMilliwatts);
}

static async Task AuthenticatedHostCommandsActivateAndRestore()
{
    var timeProvider = new MutableTimeProvider(Utc(10));
    var snapshot = CreateLiveSnapshot();
    var store = new InMemoryJournalStore();
    await store.SaveAsync("host.json", snapshot);
    var controller = CreatePowerController();
    var registry = new TestHardwareHostRegistry(store, controller, "host.json");

    using var session = HardwareHostSessionFactory.Create(timeProvider);
    var processor = new HardwareHostCommandProcessor(
        session.Host,
        registry,
        new GpuActivationEngine(store, timeProvider),
        timeProvider);

    var activate = session.Client.CreateCommand(
        HardwareHostOperation.ActivatePrepared,
        snapshot.SnapshotId);
    var activation = await processor.ProcessAsync(activate);
    AssertEqual(HardwareHostProcessOutcome.Executed, activation.Outcome);
    AssertEqual(ActivationOutcome.Activated, activation.Execution?.Outcome);
    AssertEqual(400_000u, controller.CurrentLimitMilliwatts);

    var restore = session.Client.CreateCommand(
        HardwareHostOperation.RestorePending,
        snapshot.SnapshotId);
    var restoration = await processor.ProcessAsync(restore);
    AssertEqual(HardwareHostProcessOutcome.Executed, restoration.Outcome);
    AssertEqual(ActivationOutcome.Restored, restoration.Execution?.Outcome);
    AssertEqual(450_000u, controller.CurrentLimitMilliwatts);
}

static async Task ReplayedHostCommandIsRejected()
{
    var harness = await CreateHostHarnessAsync();
    using (harness.Session)
    {
        var command = harness.Session.Client.CreateCommand(
            HardwareHostOperation.ActivatePrepared,
            harness.Snapshot.SnapshotId);
        var first = await harness.Processor.ProcessAsync(command);
        var writesAfterFirst = harness.Controller.SetCalls;
        var replay = await harness.Processor.ProcessAsync(command);

        AssertEqual(HardwareHostProcessOutcome.Executed, first.Outcome);
        AssertEqual(HardwareHostProcessOutcome.RejectedReplay, replay.Outcome);
        AssertEqual(writesAfterFirst, harness.Controller.SetCalls);
    }
}

static async Task TamperedHostCommandPerformsNoWrite()
{
    var harness = await CreateHostHarnessAsync();
    using (harness.Session)
    {
        var valid = harness.Session.Client.CreateCommand(
            HardwareHostOperation.ActivatePrepared,
            harness.Snapshot.SnapshotId);
        var tampered = valid with
        {
            Command = valid.Command with { SnapshotId = Guid.NewGuid() }
        };

        var rejected = await harness.Processor.ProcessAsync(tampered);
        AssertEqual(HardwareHostProcessOutcome.RejectedAuthentication, rejected.Outcome);
        AssertEqual(0, harness.Controller.SetCalls);

        var accepted = await harness.Processor.ProcessAsync(valid);
        AssertEqual(HardwareHostProcessOutcome.Executed, accepted.Outcome);
    }
}

static async Task ExpiredHostCommandPerformsNoWrite()
{
    var harness = await CreateHostHarnessAsync();
    using (harness.Session)
    {
        var command = harness.Session.Client.CreateCommand(
            HardwareHostOperation.ActivatePrepared,
            harness.Snapshot.SnapshotId,
            TimeSpan.FromSeconds(30));
        harness.TimeProvider.Advance(TimeSpan.FromMinutes(1));

        var result = await harness.Processor.ProcessAsync(command);
        AssertEqual(HardwareHostProcessOutcome.RejectedExpired, result.Outcome);
        AssertEqual(0, harness.Controller.SetCalls);
    }
}

static async Task OutOfOrderHostCommandPerformsNoWrite()
{
    var harness = await CreateHostHarnessAsync();
    using (harness.Session)
    {
        var first = harness.Session.Client.CreateCommand(
            HardwareHostOperation.ActivatePrepared,
            harness.Snapshot.SnapshotId);
        var second = harness.Session.Client.CreateCommand(
            HardwareHostOperation.ActivatePrepared,
            harness.Snapshot.SnapshotId);

        var outOfOrder = await harness.Processor.ProcessAsync(second);
        AssertEqual(HardwareHostProcessOutcome.RejectedSequence, outOfOrder.Outcome);
        AssertEqual(0, harness.Controller.SetCalls);

        var accepted = await harness.Processor.ProcessAsync(first);
        AssertEqual(HardwareHostProcessOutcome.Executed, accepted.Outcome);
    }
}

static async Task UnknownHostTargetConsumesCommand()
{
    var harness = await CreateHostHarnessAsync();
    using (harness.Session)
    {
        var command = harness.Session.Client.CreateCommand(
            HardwareHostOperation.ActivatePrepared,
            Guid.NewGuid());

        var missing = await harness.Processor.ProcessAsync(command);
        var replay = await harness.Processor.ProcessAsync(command);

        AssertEqual(HardwareHostProcessOutcome.RejectedTarget, missing.Outcome);
        AssertEqual(HardwareHostProcessOutcome.RejectedReplay, replay.Outcome);
        AssertEqual(0, harness.Controller.SetCalls);
    }
}

static async Task BoundedTransportFramesRoundTrip()
{
    var expected = new HardwareHostWireResult(
        HardwareHostProcessOutcome.Executed,
        nameof(ActivationOutcome.Activated),
        "Bounded result.");
    await using var stream = new MemoryStream();

    await BoundedJsonPipe.WriteAsync(stream, expected);
    stream.Position = 0;
    var actual = await BoundedJsonPipe.ReadAsync<HardwareHostWireResult>(stream);

    AssertEqual(expected, actual);
}

static async Task TransportRejectsOversizedWritesBeforeOutput()
{
    await using var stream = new MemoryStream();
    var oversized = new string('X', BoundedJsonPipe.MaximumFrameBytes + 1);

    await AssertThrowsAsync<HardwareHostTransportException>(
        () => BoundedJsonPipe.WriteAsync(stream, oversized));
    AssertEqual(0L, stream.Length);
}

static async Task TransportRejectsOversizedDeclaredFrames()
{
    var header = BitConverter.GetBytes(BoundedJsonPipe.MaximumFrameBytes + 1);
    await using var stream = new MemoryStream(header);

    await AssertThrowsAsync<HardwareHostTransportException>(
        () => BoundedJsonPipe.ReadAsync<HardwareHostWireResult>(stream));
}

static async Task TransportRejectsTruncatedFrames()
{
    var header = BitConverter.GetBytes(10);
    await using var stream = new MemoryStream();
    await stream.WriteAsync(header);
    await stream.WriteAsync("{}"u8.ToArray());
    stream.Position = 0;

    await AssertThrowsAsync<HardwareHostTransportException>(
        () => BoundedJsonPipe.ReadAsync<HardwareHostWireResult>(stream));
}

static Task TransportValidatesGeneratedPipeNames()
{
    var generated = HardwareHostPipeName.Create();
    HardwareHostPipeName.Validate(generated);
    AssertThrows<ArgumentException>(() => HardwareHostPipeName.Validate("EcoPause-user-chosen"));
    AssertThrows<ArgumentException>(() => HardwareHostPipeName.Validate($"EcoPause-{new string('Z', 32)}"));
    return Task.CompletedTask;
}

static async Task TransportBootstrapRoundTripsAndImports()
{
    var timeProvider = new MutableTimeProvider(Utc(10));
    using var transportSession = HardwareHostSessionFactory.CreateTransportSession(timeProvider);
    var expected = new HardwareHostTransportBootstrap(
        transportSession.Bootstrap,
        Guid.Parse("12345678-1234-1234-1234-123456789ABC"));
    await using var stream = new MemoryStream();

    await BoundedJsonPipe.WriteAsync(stream, expected);
    stream.Position = 0;
    var received = await BoundedJsonPipe.ReadAsync<HardwareHostTransportBootstrap>(stream);
    using (received.Session)
    using (var client = HardwareHostSessionFactory.ImportClientSession(received.Session, timeProvider))
    {
        var command = client.CreateCommand(
            HardwareHostOperation.ActivatePrepared,
            received.SnapshotId);
        var validation = transportSession.Host.ValidateAndConsume(command, timeProvider.GetUtcNow());

        AssertEqual(HardwareHostValidationOutcome.Accepted, validation.Outcome);
    }
}

static Task TransportRejectsInvalidExpectedPeerIds()
{
    if (OperatingSystem.IsWindows())
    {
        AssertThrows<ArgumentOutOfRangeException>(() => HardwareHostPipePeer.ValidateExpectedProcessId(0));
        AssertThrows<ArgumentOutOfRangeException>(() => HardwareHostPipePeer.ValidateExpectedProcessId(-1));
        HardwareHostPipePeer.ValidateExpectedProcessId(Environment.ProcessId);
    }

    return Task.CompletedTask;
}

static async Task WindowsPipePeersReportTheirProcessId()
{
    if (!OperatingSystem.IsWindows())
    {
        return;
    }

    var pipeName = HardwareHostPipeName.Create();
    await using var server = HardwareHostNamedPipe.CreateServer(pipeName);
    var connectTask = HardwareHostNamedPipe.ConnectClientAsync(pipeName);
    await HardwareHostNamedPipe.WaitForConnectionAsync(server);
    await using var client = await connectTask;

    HardwareHostPipePeer.VerifyClientProcess(server, Environment.ProcessId);
    HardwareHostPipePeer.VerifyServerProcess(client, Environment.ProcessId);
    AssertThrows<HardwareHostTransportException>(
        () => HardwareHostPipePeer.VerifyClientProcess(server, Environment.ProcessId + 1));

    var verifiedPipeName = HardwareHostPipeName.Create();
    await using var verifiedServer = HardwareHostNamedPipe.CreateServer(verifiedPipeName);
    var verifiedConnectTask = HardwareHostNamedPipe.ConnectVerifiedClientAsync(
        verifiedPipeName,
        Environment.ProcessId);
    await HardwareHostNamedPipe.WaitForConnectionAsync(verifiedServer);
    await using var verifiedClient = await verifiedConnectTask;
    HardwareHostPipePeer.VerifyClientProcess(verifiedServer, Environment.ProcessId);
}

static async Task PersistentFakeHardwareSurvivesControllerRestart()
{
    await WithTemporaryJournal(async statePath =>
    {
        var runId = Guid.NewGuid();
        var fingerprint = CrashSimulationProfile.CreateFingerprint(runId);
        var firstController = new PersistentFakePowerController(statePath, fingerprint);
        await firstController.InitializeAsync();
        await firstController.SetPowerLimitMilliwattsAsync(
            CrashSimulationProfile.TargetLimitMilliwatts);

        var restartedController = new PersistentFakePowerController(statePath, fingerprint);
        AssertEqual(
            CrashSimulationProfile.TargetLimitMilliwatts,
            await restartedController.ReadPowerLimitMilliwattsAsync());

        await restartedController.SetPowerLimitMilliwattsAsync(
            CrashSimulationProfile.OriginalLimitMilliwatts);
        AssertEqual(
            CrashSimulationProfile.OriginalLimitMilliwatts,
            await firstController.ReadPowerLimitMilliwattsAsync());
    });
}

static async Task PersistentFakeHardwareRejectsCorruption()
{
    await WithTemporaryJournal(async statePath =>
    {
        var controller = new PersistentFakePowerController(
            statePath,
            CrashSimulationProfile.CreateFingerprint(Guid.NewGuid()));
        await controller.InitializeAsync();
        await File.WriteAllTextAsync(statePath, "{}");

        await AssertThrowsAsync<PersistentFakeHardwareException>(
            () => controller.ReadPowerLimitMilliwattsAsync());
    });
}

static Task CrashSimulationPathsAndSnapshotsAreFixed()
{
    var runId = Guid.NewGuid();
    var root = Path.Combine(Path.GetTempPath(), "EcoPause.Tests", Guid.NewGuid().ToString("N"));
    var resolved = CrashSimulationPaths.ResolveRunDirectory(root, runId);
    AssertTrue(
        resolved.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase),
        "The canonical run directory must remain beneath its trusted root.");
    AssertEqual(runId, CrashSimulationPaths.ParseRunId(runId.ToString("N")));
    AssertThrows<ArgumentException>(() => CrashSimulationPaths.ParseRunId("..\\untrusted"));

    var applied = CrashSimulationProfile.CreateSnapshot(runId, Utc(0))
        .MarkApplied(CrashSimulationProfile.TargetLimitMilliwatts, Utc(1));
    CrashSimulationProfile.ValidatePendingSnapshot(applied, runId);
    AssertThrows<InvalidOperationException>(
        () => CrashSimulationProfile.ValidatePendingSnapshot(applied, Guid.NewGuid()));
    return Task.CompletedTask;
}

static Task DesktopAcceptsCompleteSafetyEvidence()
{
    const string output = """
        Mutual process-ID verification: PASS
        Elevated helper verified launcher PID: PASS
        Elevated helper initialization: PASS
        Authenticated activation: Executed / Activated
        Replay attempt: RejectedReplay
        Tampered command: RejectedAuthentication
        Authenticated restoration: Executed / Restored
        Elevated helper exit: 0
        Final simulated limit: 450 W
        Recovery journal closed: True
        Safe elevated lifecycle: PASS
        Real NVIDIA access: NONE
        """;

    var result = SimulationEvidenceEvaluator.Evaluate(0, output, string.Empty);
    AssertEqual(SimulationEvidenceOutcome.Passed, result.Outcome);
    AssertTrue(result.Passed, "Complete safety evidence must be accepted.");
    return Task.CompletedTask;
}

static Task DesktopRejectsIncompleteSafetyEvidence()
{
    var result = SimulationEvidenceEvaluator.Evaluate(
        0,
        "Safe elevated lifecycle: PASS",
        string.Empty);

    AssertEqual(SimulationEvidenceOutcome.IncompleteEvidence, result.Outcome);
    AssertTrue(result.MissingEvidence.Count > 0, "Missing evidence must be identified.");
    return Task.CompletedTask;
}

static Task DesktopReportsDeclinedUacSafely()
{
    var result = SimulationEvidenceEvaluator.Evaluate(
        4,
        "UAC was declined. No helper commands ran and no hardware was changed.",
        string.Empty);

    AssertEqual(SimulationEvidenceOutcome.PermissionDeclined, result.Outcome);
    AssertTrue(!result.Passed, "Declined permission must not be displayed as a completed run.");
    return Task.CompletedTask;
}

static Task DesktopParsesSanitizedReadOnlyGpuData()
{
    var result = ReadOnlyGpuProbeParser.Parse(CreateReadOnlyProbeJson(), Utc(5));

    AssertTrue(result.Ready, "The strict sanitized GPU report should be ready.");
    AssertEqual("NVIDIA", result.Provider);
    AssertEqual(1, result.Devices.Count);
    AssertEqual("NVIDIA GeForce RTX 4090", result.Devices[0].Model);
    AssertEqual(450m, result.Devices[0].CurrentLimitWatts);
    AssertEqual(108.451m, result.Devices[0].CurrentUsageWatts);
    return Task.CompletedTask;
}

static Task DesktopParsesAmdRelativeGpuData()
{
    var result = ReadOnlyGpuProbeParser.Parse(CreateAmdReadOnlyProbeJson(), Utc(5));

    AssertTrue(result.Ready, "The strict AMD GPU report should be ready.");
    AssertEqual("AMD", result.Provider);
    var gpu = result.Devices.Single();
    AssertEqual(GpuPowerLimitKind.DefaultRelativePercentage, gpu.LimitKind);
    AssertEqual(100m, gpu.CurrentLimitWatts);
    AssertEqual(90m, gpu.MinimumLimitWatts);
    AssertEqual(115m, gpu.MaximumLimitWatts);
    AssertEqual(1, gpu.RelativePercentageStep);

    var target = DesktopLiveTargetPolicy.ResolvePercentage(95, gpu);
    AssertEqual("95% of factory", target.FormattedLimit);
    AssertTrue(
        DesktopLiveTargetPolicy.Validate(target, gpu).Accepted,
        "An AMD target matching the reported driver step should be accepted.");
    AssertEqual(DesktopLiveToggleIntent.Pause, DesktopLiveTogglePolicy.Evaluate(result).Intent);

    var recovery = DesktopLiveTogglePolicy.Evaluate(result with
    {
        Devices = [gpu with { CurrentLimitWatts = 95m }]
    });
    AssertEqual(DesktopLiveToggleIntent.Restore, recovery.Intent);
    AssertEqual("percent-95", recovery.ActiveProfile?.ProfileId);
    return Task.CompletedTask;
}

static Task DesktopRejectsIdentifyingGpuFields()
{
    var identifyingJson = CreateReadOnlyProbeJson().Replace(
        "\"Index\": 0,",
        "\"Index\": 0, \"DeviceFingerprintSha256\": \"secret\",",
        StringComparison.Ordinal);

    AssertThrows<ReadOnlyGpuProbeDataException>(
        () => ReadOnlyGpuProbeParser.Parse(identifyingJson, Utc(5)));
    return Task.CompletedTask;
}

static Task DesktopRejectsOversizedGpuReports()
{
    var oversized = new string('X', ReadOnlyGpuProbeParser.MaximumJsonCharacters + 1);
    AssertThrows<ReadOnlyGpuProbeDataException>(
        () => ReadOnlyGpuProbeParser.Parse(oversized, Utc(5)));
    return Task.CompletedTask;
}

static Task DesktopRejectsInconsistentGpuConstraints()
{
    var inconsistentJson = CreateReadOnlyProbeJson().Replace(
        "\"Value\": 150",
        "\"Value\": 700",
        StringComparison.Ordinal);

    AssertThrows<ReadOnlyGpuProbeDataException>(
        () => ReadOnlyGpuProbeParser.Parse(inconsistentJson, Utc(5)));
    return Task.CompletedTask;
}

static Task DesktopLiveToggleAcceptsAllowListedReadyStates()
{
    var report = ReadOnlyGpuProbeParser.Parse(CreateReadOnlyProbeJson(), Utc(5));
    var pause = DesktopLiveTogglePolicy.Evaluate(report);
    AssertTrue(pause.Available, "The exact 450 W baseline should enable the manual live pause.");
    AssertEqual(DesktopLiveToggleIntent.Pause, pause.Intent);

    var recoveryReport = report with
    {
        Devices = [report.Devices[0] with { CurrentLimitWatts = 400m }]
    };
    var restore = DesktopLiveTogglePolicy.Evaluate(recoveryReport);
    AssertTrue(restore.Available, "The exact 400 W target should enable recovery.");
    AssertEqual(DesktopLiveToggleIntent.Restore, restore.Intent);
    AssertEqual("percent-89", restore.ActiveProfile?.ProfileId);

    var minimumRecovery = DesktopLiveTogglePolicy.Evaluate(report with
    {
        Devices = [report.Devices[0] with { CurrentLimitWatts = 150m }]
    });
    AssertTrue(minimumRecovery.Available, "The allow-listed 150 W target should enable recovery.");
    AssertEqual(DesktopLiveToggleIntent.Restore, minimumRecovery.Intent);
    AssertEqual("percent-33", minimumRecovery.ActiveProfile?.ProfileId);

    var unknown = DesktopLiveTogglePolicy.Evaluate(report with
    {
        Devices = [report.Devices[0] with { CurrentLimitWatts = 425m }]
    });
    AssertTrue(!unknown.Available, "An unknown current limit must disable live control.");
    return Task.CompletedTask;
}

static Task DesktopLiveToggleRejectsAmbiguousHardware()
{
    var report = ReadOnlyGpuProbeParser.Parse(CreateReadOnlyProbeJson(), Utc(5));
    var ambiguous = DesktopLiveTogglePolicy.Evaluate(report with
    {
        Devices = [report.Devices[0], report.Devices[0] with { Index = 1 }]
    });
    AssertTrue(!ambiguous.Available, "Multiple GPUs must disable the desktop live action.");

    var wrongDefault = DesktopLiveTogglePolicy.Evaluate(report with
    {
        Devices = [report.Devices[0] with { DefaultLimitWatts = 425m }]
    });
    AssertTrue(!wrongDefault.Available, "A non-450 W default must disable the desktop live action.");
    return Task.CompletedTask;
}

static Task DesktopAcceptsCompleteLiveToggleEvidence()
{
    var paused = DesktopLiveToggleEvidenceEvaluator.Evaluate(
        0,
        CreateDesktopLiveToggleTranscript(paused: true),
        string.Empty);
    AssertTrue(paused.Passed, "Complete live limit evidence should pass.");
    AssertEqual(DesktopLiveToggleState.Limited, paused.State);
    AssertEqual("percent-33", paused.Target?.ProfileId);

    var restored = DesktopLiveToggleEvidenceEvaluator.Evaluate(
        0,
        CreateDesktopLiveToggleTranscript(paused: false),
        string.Empty);
    AssertTrue(restored.Passed, "Complete live restoration evidence should pass.");
    AssertEqual(DesktopLiveToggleState.Restored, restored.State);
    AssertEqual("percent-33", restored.Target?.ProfileId);

    var preparedOnlyRecovery = CreateDesktopLiveToggleTranscript(paused: false)
        .Replace("Current limit: 150 W", "Current limit: 450 W", StringComparison.Ordinal)
        .Replace("Preflight state: RECOVERY EXPECTED", "Preflight state: READY TO LIMIT", StringComparison.Ordinal);
    var recoveredBeforeWrite = DesktopLiveToggleEvidenceEvaluator.Evaluate(
        0,
        preparedOnlyRecovery,
        string.Empty);
    AssertTrue(
        recoveredBeforeWrite.Passed,
        "A protected Prepared journal may recover safely while read-only preflight still sees 450 W.");
    return Task.CompletedTask;
}

static Task DesktopAcceptsCompleteAmdLiveToggleEvidence()
{
    const string output = """
        AFK Power Saver Live GPU Limit Session 1.22
        Launcher privilege: administrator startup broker
        Desktop GPU control: PERCENTAGE LIMIT / EXACT DEFAULT RESTORE
        Desktop activation triggers: BUTTON, ASSIGNED GLOBAL HOTKEY, OR ENABLED IDLE TIMER
        Current limit: 100% of factory
        Default limit: 100% of factory
        Requested limit profile: percent-90 / 90% / 90% of factory
        Preflight state: READY TO LIMIT
        Startup UAC session: PASS / mutually verified normal-user launcher
        Per-action confirmation: NOT REQUIRED / authorized button, hotkey, or enabled idle timer
        Startup-authorized broker launched the journal-selected helper without another UAC prompt.
        Requested live profile: percent-90 / 90% / 90% of factory
        Helper PID verified by Windows: PASS (1234)
        Helper-selected action: ApplyVerifiedLimit
        Helper-selected live profile: percent-90 / 90% / 90% of factory
        Authenticated operation: Executed / Activated
        Helper exit: 0
        Final helper limit: 90% of factory
        Recovery journal stage: Applied
        90% of factory exact persistent limit: PASS
        Protected recovery artifacts retained: PASS
        Independent final live limit: 90% of factory
        Protected exact-restoration journal retained: PASS
        Live GPU toggle: PASS / Limited
        GPU lifecycle: PERCENTAGE LIMIT / EXACT DEFAULT RESTORE
        Idle activation hardware path: STARTUP-AUTHORIZED BROKER ONLY
        """;

    var result = DesktopLiveToggleEvidenceEvaluator.Evaluate(0, output, string.Empty);
    AssertTrue(result.Passed, "Complete AMD limit evidence should pass.");
    AssertEqual(DesktopLiveToggleState.Limited, result.State);
    AssertEqual(GpuPowerLimitKind.DefaultRelativePercentage, result.Target?.LimitKind);
    AssertEqual("90% of factory", result.Target?.FormattedLimit);
    return Task.CompletedTask;
}

static Task DesktopRejectsConflictingLiveToggleEvidence()
{
    var wrongLimit = CreateDesktopLiveToggleTranscript(paused: true).Replace(
        "Independent final live limit: 150 W",
        "Independent final live limit: 425 W",
        StringComparison.Ordinal);
    var incomplete = DesktopLiveToggleEvidenceEvaluator.Evaluate(0, wrongLimit, string.Empty);
    AssertEqual(SimulationEvidenceOutcome.IncompleteEvidence, incomplete.Outcome);

    var oldInteractiveContract = CreateDesktopLiveToggleTranscript(paused: true).Replace(
        "Per-action confirmation: NOT REQUIRED / authorized button, hotkey, or enabled idle timer",
        "Desktop confirmation: PASS / explicit allow-listed live-toggle dialog accepted",
        StringComparison.Ordinal);
    AssertEqual(
        SimulationEvidenceOutcome.IncompleteEvidence,
        DesktopLiveToggleEvidenceEvaluator.Evaluate(0, oldInteractiveContract, string.Empty).Outcome);

    var crashExit = DesktopLiveToggleEvidenceEvaluator.Evaluate(
        LiveCanaryPolicy.IntentionalRecoveryDrillExitCode,
        CreateDesktopLiveToggleTranscript(paused: true),
        string.Empty);
    AssertEqual(SimulationEvidenceOutcome.ProcessFailed, crashExit.Outcome);
    return Task.CompletedTask;
}

static Task DesktopRoutesAuthorizedControlsToLiveToggle()
{
    AssertEqual(
        DesktopActivationRoute.PersistentFakeToggle,
        DesktopActivationRoutingPolicy.Route(DesktopActivationSource.GreenFakeButton));
    AssertEqual(
        DesktopActivationRoute.ConfirmedLiveToggle,
        DesktopActivationRoutingPolicy.Route(DesktopActivationSource.AmberLiveButton));
    AssertEqual(
        DesktopActivationRoute.ConfirmedLiveToggle,
        DesktopActivationRoutingPolicy.Route(DesktopActivationSource.GlobalHotKey));
    AssertEqual(
        DesktopActivationRoute.ConfirmedLiveToggle,
        DesktopActivationRoutingPolicy.Route(DesktopActivationSource.IdleObserver));
    AssertEqual(
        DesktopActivationRoute.ConfirmedLiveToggle,
        DesktopActivationRoutingPolicy.Route(DesktopActivationSource.TrayMenu));
    AssertThrows<ArgumentOutOfRangeException>(() =>
        DesktopActivationRoutingPolicy.Route((DesktopActivationSource)int.MaxValue));
    return Task.CompletedTask;
}

static Task DisabledIdleActivationPerformsNoAction()
{
    AssertEqual(
        IdleActivationDirective.None,
        IdleActivationPolicy.Evaluate(
            enabled: false,
            timerOwnsActiveLimit: false,
            transitionAttempted: false,
            unattended: true,
            canApply: true,
            canRestore: false));
    return Task.CompletedTask;
}

static Task IdleActivationAppliesOnlyRestoredProfile()
{
    AssertEqual(
        IdleActivationDirective.ApplyLimits,
        IdleActivationPolicy.Evaluate(
            enabled: true,
            timerOwnsActiveLimit: false,
            transitionAttempted: false,
            unattended: true,
            canApply: true,
            canRestore: false));
    AssertEqual(
        IdleActivationDirective.None,
        IdleActivationPolicy.Evaluate(
            enabled: true,
            timerOwnsActiveLimit: false,
            transitionAttempted: false,
            unattended: true,
            canApply: false,
            canRestore: true));
    AssertEqual(
        IdleActivationDirective.None,
        IdleActivationPolicy.Evaluate(
            enabled: true,
            timerOwnsActiveLimit: false,
            transitionAttempted: true,
            unattended: true,
            canApply: true,
            canRestore: false));
    return Task.CompletedTask;
}

static Task IdleActivationRestoresOnlyOwnedProfile()
{
    AssertEqual(
        IdleActivationDirective.RestoreOriginal,
        IdleActivationPolicy.Evaluate(
            enabled: true,
            timerOwnsActiveLimit: true,
            transitionAttempted: false,
            unattended: false,
            canApply: false,
            canRestore: true));
    AssertEqual(
        IdleActivationDirective.RestoreOriginal,
        IdleActivationPolicy.Evaluate(
            enabled: false,
            timerOwnsActiveLimit: true,
            transitionAttempted: false,
            unattended: true,
            canApply: false,
            canRestore: true));
    AssertEqual(
        IdleActivationDirective.None,
        IdleActivationPolicy.Evaluate(
            enabled: true,
            timerOwnsActiveLimit: false,
            transitionAttempted: false,
            unattended: false,
            canApply: false,
            canRestore: true));
    return Task.CompletedTask;
}

static Task IdleActivationValidatesConfiguredDelay()
{
    AssertEqual(1, IdleActivationPolicy.ValidateMinutes(1));
    AssertEqual(120, IdleActivationPolicy.ValidateMinutes(120));
    AssertThrows<ArgumentOutOfRangeException>(() => IdleActivationPolicy.ValidateMinutes(0));
    AssertThrows<ArgumentOutOfRangeException>(() => IdleActivationPolicy.ValidateMinutes(121));
    return Task.CompletedTask;
}

static Task StartupLaunchCommandQuotesExecutablePath()
{
    const string executable = @"C:\Apps\AFK Power Saver\AFKPowerSaver.exe";
    AssertEqual(
        $"\"{executable}\" {StartupLaunchPolicy.StartupArgument}",
        StartupLaunchPolicy.BuildCommand(executable));
    AssertThrows<ArgumentException>(() =>
        StartupLaunchPolicy.BuildCommand(@"C:\Apps\AFK Power Saver\AFKPowerSaver.dll"));
    return Task.CompletedTask;
}

static Task StartupLaunchPolicyRecognizesOnlyCurrentExecutable()
{
    const string executable = @"C:\Apps\AFK Power Saver\AFKPowerSaver.exe";
    var command = StartupLaunchPolicy.BuildCommand(executable);
    AssertTrue(
        StartupLaunchPolicy.MatchesCurrentExecutable($"  {command.ToUpperInvariant()}  ", executable),
        "The current executable command should match without case sensitivity.");
    AssertTrue(
        !StartupLaunchPolicy.MatchesCurrentExecutable(
            "\"C:\\Old\\AFKPowerSaver.exe\" --startup",
            executable),
        "A stale executable path must not be reported as enabled.");
    return Task.CompletedTask;
}

static Task ActivityPreviewCountsDownDuringActiveUse()
{
    var decision = PausePreviewPolicy.Evaluate(
        TimeSpan.FromMinutes(1),
        sessionLocked: false,
        TimeSpan.FromMinutes(5));

    AssertTrue(!decision.WouldPause, "Active input must remain a preview countdown.");
    AssertEqual(PausePreviewReason.ActiveInput, decision.Reason);
    AssertEqual(TimeSpan.FromMinutes(4), decision.RemainingUntilPause);
    return Task.CompletedTask;
}

static Task ActivityPreviewRecommendsPauseAtIdleThreshold()
{
    var decision = PausePreviewPolicy.Evaluate(
        TimeSpan.FromMinutes(5),
        sessionLocked: false,
        TimeSpan.FromMinutes(5));

    AssertTrue(decision.WouldPause, "The exact idle threshold should recommend a pause.");
    AssertEqual(PausePreviewReason.IdleThresholdReached, decision.Reason);
    AssertEqual(TimeSpan.Zero, decision.RemainingUntilPause);
    return Task.CompletedTask;
}

static Task ActivityPreviewRecommendsPauseWhileLocked()
{
    var decision = PausePreviewPolicy.Evaluate(
        TimeSpan.Zero,
        sessionLocked: true,
        TimeSpan.FromMinutes(5));

    AssertTrue(decision.WouldPause, "A locked session should be treated as unattended.");
    AssertEqual(PausePreviewReason.SessionLocked, decision.Reason);
    return Task.CompletedTask;
}

static Task ActivityPreviewRejectsInvalidDurations()
{
    AssertThrows<ArgumentOutOfRangeException>(() => PausePreviewPolicy.Evaluate(
        TimeSpan.FromSeconds(-1),
        sessionLocked: false,
        TimeSpan.FromMinutes(5)));
    AssertThrows<ArgumentOutOfRangeException>(() => PausePreviewPolicy.Evaluate(
        TimeSpan.Zero,
        sessionLocked: false,
        TimeSpan.Zero));
    return Task.CompletedTask;
}

static Task PersistentToggleProfileAcceptsRecoverableStages()
{
    foreach (var profile in PersistentToggleProfiles.All)
    {
        var prepared = CrashSimulationProfile.CreateToggleSnapshot(profile, Utc(5));
        var applied = prepared.MarkApplied(profile.TargetLimitMilliwatts, Utc(6));
        var restored = applied.MarkRestored(CrashSimulationProfile.OriginalLimitMilliwatts, Utc(7));

        CrashSimulationProfile.ValidateToggleSnapshot(prepared, profile);
        CrashSimulationProfile.ValidateToggleSnapshot(applied, profile);
        CrashSimulationProfile.ValidateToggleSnapshot(restored, profile);
    }

    AssertThrows<InvalidOperationException>(() =>
        CrashSimulationProfile.ValidateToggleSnapshot(
            CrashSimulationProfile.CreateSnapshot(Guid.NewGuid(), Utc(5))));
    return Task.CompletedTask;
}

static Task DesktopAcceptsPersistentFakePauseEvidence()
{
    var result = PersistentFakeToggleEvidenceEvaluator.Evaluate(
        0,
        CreatePersistentToggleTranscript(paused: true, profileId: "eco-350"),
        string.Empty);

    AssertTrue(result.Passed, "Complete persistent fake pause evidence should pass.");
    AssertEqual(PersistentFakeToggleState.Paused, result.State);
    AssertEqual("eco-350", result.Target?.ProfileId);
    return Task.CompletedTask;
}

static Task DesktopAcceptsPersistentFakeRestoreEvidence()
{
    var result = PersistentFakeToggleEvidenceEvaluator.Evaluate(
        0,
        CreatePersistentToggleTranscript(
            paused: false,
            profileId: "eco-350",
            requestedProfileId: "eco-250"),
        string.Empty);

    AssertTrue(result.Passed, "Complete persistent fake restoration evidence should pass.");
    AssertEqual(PersistentFakeToggleState.Restored, result.State);
    return Task.CompletedTask;
}

static Task DesktopRejectsConflictingPersistentToggleEvidence()
{
    var conflicting = CreatePersistentToggleTranscript(paused: true) + Environment.NewLine +
        "Authenticated toggle: Executed / Restored" + Environment.NewLine +
        "Persistent fake state: Restored" + Environment.NewLine +
        "Final persistent fake limit: 450 W" + Environment.NewLine +
        "Recovery journal stage: Restored" + Environment.NewLine +
        "Protected simulation files cleaned: True";
    var result = PersistentFakeToggleEvidenceEvaluator.Evaluate(0, conflicting, string.Empty);

    AssertTrue(!result.Passed, "Conflicting pause and restore evidence must fail closed.");
    AssertEqual(SimulationEvidenceOutcome.IncompleteEvidence, result.Outcome);
    return Task.CompletedTask;
}

static Task DesktopParsesDisplayOnlyToggleReceipts()
{
    const string paused = "EcoPause Persistent Fake Toggle Receipt 1.5\nReceipt state: Paused\nReceipt profile: eco-350\nReceipt authority: display-only\n";
    const string restored = "EcoPause Persistent Fake Toggle Receipt 1.5\nReceipt state: Restored\nReceipt profile: eco-350\nReceipt authority: display-only\n";

    AssertEqual(PersistentFakeToggleState.Paused, PersistentFakeToggleReceiptParser.Parse(paused).State);
    AssertEqual(PersistentFakeToggleState.Restored, PersistentFakeToggleReceiptParser.Parse(restored).State);
    AssertEqual("eco-350", PersistentFakeToggleReceiptParser.Parse(paused).Target?.ProfileId);
    return Task.CompletedTask;
}

static Task DesktopRejectsTamperedToggleReceipts()
{
    const string tampered = "EcoPause Persistent Fake Toggle Receipt 1.5\nReceipt state: Paused\nReceipt profile: arbitrary-275\nReceipt authority: display-only\n";
    AssertEqual(PersistentFakeToggleState.Unknown, PersistentFakeToggleReceiptParser.Parse(tampered).State);
    AssertEqual(
        PersistentFakeToggleState.Unknown,
        PersistentFakeToggleReceiptParser.Parse(
            new string('X', PersistentFakeToggleReceiptParser.MaximumReceiptCharacters + 1)).State);
    return Task.CompletedTask;
}

static Task PersistentToggleProfilesAreFixedAndUnique()
{
    AssertEqual(4, PersistentToggleProfiles.All.Count);
    AssertEqual(4, PersistentToggleProfiles.All.Select(profile => profile.Id).Distinct().Count());
    AssertEqual(4, PersistentToggleProfiles.All.Select(profile => profile.RunId).Distinct().Count());
    AssertEqual(4, PersistentToggleProfiles.All.Select(profile => profile.TargetLimitMilliwatts).Distinct().Count());
    AssertEqual("eco-400", PersistentToggleProfiles.Default.Id);
    foreach (var profile in PersistentToggleProfiles.All)
    {
        AssertEqual(
            profile.TargetLimitMilliwatts / 1000m,
            FakeTargetPolicy.ResolveProfile(profile.Id).Watts);
    }

    AssertThrows<ArgumentException>(() => PersistentToggleProfiles.ResolveId("eco-275"));
    return Task.CompletedTask;
}

static Task FakeTargetsValidateAgainstLiveConstraints()
{
    foreach (var option in FakeTargetPolicy.Options)
    {
        var result = FakeTargetPolicy.Validate(option, 450m, 150m, 600m);
        AssertTrue(result.Accepted, $"{option.ProfileId} should be valid for the live RTX test range.");
    }

    return Task.CompletedTask;
}

static Task FakeTargetsRejectUnsafeLiveConstraints()
{
    AssertTrue(
        !FakeTargetPolicy.Validate(FakeTargetPolicy.ResolveProfile("eco-400"), 400m, 150m, 600m).Accepted,
        "A fake target equal to the current limit must be rejected.");
    AssertTrue(
        !FakeTargetPolicy.Validate(FakeTargetPolicy.ResolveProfile("eco-250"), 450m, 300m, 600m).Accepted,
        "A fake target below the live driver minimum must be rejected.");
    AssertThrows<ArgumentException>(() => FakeTargetPolicy.ResolveProfile("eco-275"));
    return Task.CompletedTask;
}

static Task LivePowerProfilesAreFixedAndMatchDesktop()
{
    AssertEqual(6, LivePowerProfiles.All.Count);
    AssertEqual(6, LivePowerProfiles.All.Select(profile => profile.Id).Distinct().Count());
    AssertEqual(6, LivePowerProfiles.All.Select(profile => profile.TargetLimitMilliwatts).Distinct().Count());
    AssertEqual(150_000u, LivePowerProfiles.All[0].TargetLimitMilliwatts);
    AssertEqual(400_000u, LivePowerProfiles.Default.TargetLimitMilliwatts);
    AssertThrows<ArgumentException>(() => LivePowerProfiles.ResolveId("live-175"));

    var gpu = ReadOnlyGpuProbeParser.Parse(CreateReadOnlyProbeJson(), Utc(5)).Devices.Single();
    AssertEqual(33, DesktopLiveTargetPolicy.GetMinimumPercentage(gpu));
    AssertEqual(150m, DesktopLiveTargetPolicy.ResolvePercentage(33, gpu).Watts);
    AssertEqual(400m, DesktopLiveTargetPolicy.ResolvePercentage(89, gpu).Watts);
    AssertEqual("percent-89", DesktopLiveTargetPolicy.ResolvePercentage(89, gpu).ProfileId);
    AssertTrue(
        DesktopLiveTargetPolicy.ResolveProfile("live-175", gpu) is null,
        "Desktop must reject absolute-watt profile IDs.");

    var portableState = PortableLivePowerPolicy.ValidateProbeReport(CreateLiveCanaryProbeReport(450_000));
    var portable = PortableLivePowerPolicy.ResolveForDevice(
        new PortableLivePowerRequest(89),
        portableState);
    AssertEqual("percent-89", portable.Id);
    AssertEqual(400_000u, portable.TargetLimitMilliwatts);
    return Task.CompletedTask;
}

static Task LiveCanaryAcceptsOnlyExactBaseline()
{
    var state = LiveCanaryPolicy.ValidateProbeReport(CreateLiveCanaryProbeReport(450_000));
    AssertTrue(state.CanStartNewCanary, "The exact 450 W baseline should permit a new canary.");
    AssertTrue(!state.RecoveryMayBePending, "The normal baseline must not be reported as a pending target.");
    AssertEqual(400_000u, LiveCanaryPolicy.TargetLimitMilliwatts);
    AssertEqual(5_000, LiveCanaryPolicy.HoldDurationMilliseconds);
    return Task.CompletedTask;
}

static Task PortableLivePercentagesAdaptToAnotherGpu()
{
    var state = PortableLivePowerPolicy.ValidateDevice(
        "NVIDIA Test GPU",
        new DeviceFingerprint("sha256:BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB"),
        currentLimitMilliwatts: 300_000,
        defaultLimitMilliwatts: 300_000,
        minimumLimitMilliwatts: 100_000,
        maximumLimitMilliwatts: 360_000);
    AssertEqual(33, PortableLivePowerPolicy.GetMinimumPercentage(state));
    AssertEqual(100_000u, PortableLivePowerPolicy.ResolveForDevice(
        new PortableLivePowerRequest(33), state).TargetLimitMilliwatts);
    AssertEqual(267_000u, PortableLivePowerPolicy.ResolveForDevice(
        new PortableLivePowerRequest(89), state).TargetLimitMilliwatts);

    var profile = PortableLivePowerPolicy.ResolveForDevice(
        new PortableLivePowerRequest(75), state);
    var snapshot = PortableLivePowerPolicy.CreateSnapshot(state, profile, Utc(1));
    AssertEqual(300_000u, snapshot.OriginalPowerLimitMilliwatts);
    AssertEqual(225_000u, snapshot.TargetPowerLimitMilliwatts);
    return Task.CompletedTask;
}

static Task PortableLivePercentagesPreserveAmdDriverSteps()
{
    var power = GpuPowerCapabilities.SupportedDefaultRelativePercentage(
        currentPercentage: 100,
        minimumPercentage: 90,
        maximumPercentage: 115,
        percentageStep: 2);
    var report = new GpuProbeReport(
        "AMD",
        "ADLX",
        GpuProbeStatus.Ready,
        "AMD test report.",
        [new GpuDeviceInfo(
            0,
            "AMD Radeon Test GPU",
            power,
            "sha256:CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC")]);

    var state = PortableLivePowerPolicy.ValidateProbeReport(report);
    AssertEqual(GpuPowerLimitKind.DefaultRelativePercentage, state.LimitKind);
    AssertEqual("AMD", state.Provider);
    AssertEqual("ADLX-ManualPowerTuning", state.Backend);
    AssertEqual(2_000u, state.LimitStepMilliwatts);
    AssertEqual(90_000u, PortableLivePowerPolicy.ResolveForDevice(
        new PortableLivePowerRequest(90), state).TargetLimitMilliwatts);
    AssertThrows<InvalidOperationException>(() => PortableLivePowerPolicy.ResolveForDevice(
        new PortableLivePowerRequest(91), state));

    var profile = PortableLivePowerPolicy.ResolveForDevice(new PortableLivePowerRequest(94), state);
    var snapshot = PortableLivePowerPolicy.CreateSnapshot(state, profile, Utc(1));
    AssertEqual("AMD", snapshot.Provider);
    AssertEqual("ADLX-ManualPowerTuning", snapshot.Backend);
    AssertEqual(100_000u, snapshot.OriginalPowerLimitMilliwatts);
    AssertEqual(94_000u, snapshot.TargetPowerLimitMilliwatts);

    var recoveryPower = GpuPowerCapabilities.SupportedDefaultRelativePercentage(
        currentPercentage: 94,
        minimumPercentage: 90,
        maximumPercentage: 115,
        percentageStep: 2);
    var recoveryState = PortableLivePowerPolicy.ValidateProbeReport(report with
    {
        Devices = [report.Devices[0] with { Power = recoveryPower }]
    });
    AssertTrue(recoveryState.RecoveryMayBePending, "A valid AMD driver-step target must remain recoverable.");
    AssertEqual("percent-94", PortableLivePowerPolicy.ResolveTargetForDevice(
        recoveryState.CurrentLimitMilliwatts,
        recoveryState).Id);
    return Task.CompletedTask;
}

static Task LiveCanaryRecognizesFixedRecoveryState()
{
    var state = LiveCanaryPolicy.ValidateProbeReport(CreateLiveCanaryProbeReport(400_000));
    AssertTrue(!state.CanStartNewCanary, "A GPU already at 400 W must not begin a second canary.");
    AssertTrue(state.RecoveryMayBePending, "The fixed 400 W state should prioritize protected-journal recovery.");
    return Task.CompletedTask;
}

static Task LiveCanaryRejectsAmbiguousDevicesAndLimits()
{
    var oneDevice = CreateLiveCanaryProbeReport(450_000);
    AssertThrows<InvalidOperationException>(() => LiveCanaryPolicy.ValidateProbeReport(
        oneDevice with { Devices = [.. oneDevice.Devices, .. oneDevice.Devices] }));
    AssertThrows<InvalidOperationException>(() =>
        LiveCanaryPolicy.ValidateProbeReport(CreateLiveCanaryProbeReport(425_000)));

    var unsupportedRange = GpuPowerCapabilities.Supported(
        new Watts(450),
        new Watts(450),
        new Watts(425),
        new Watts(600));
    AssertThrows<InvalidOperationException>(() => LiveCanaryPolicy.ValidateProbeReport(
        oneDevice with
        {
            Devices =
            [
                oneDevice.Devices[0] with { Power = unsupportedRange }
            ]
        }));
    return Task.CompletedTask;
}

static Task LiveCanarySnapshotsAreFixedAndRecoverable()
{
    var state = LiveCanaryPolicy.ValidateProbeReport(CreateLiveCanaryProbeReport(450_000));
    var prepared = LiveCanaryPolicy.CreateSnapshot(state, Utc(0));
    AssertEqual("NVIDIA", prepared.Provider);
    AssertEqual("NVML-Live-Canary", prepared.Backend);
    AssertEqual(450_000u, prepared.OriginalPowerLimitMilliwatts);
    AssertEqual(400_000u, prepared.TargetPowerLimitMilliwatts);
    LiveCanaryPolicy.ValidateRecoverySnapshot(prepared, state);

    var applied = prepared.MarkApplied(400_000, Utc(1));
    var recoveryState = state with { CurrentLimitMilliwatts = 400_000 };
    LiveCanaryPolicy.ValidateRecoverySnapshot(applied, recoveryState);
    AssertThrows<InvalidOperationException>(() =>
        LiveCanaryPolicy.ValidateRecoverySnapshot(
            applied.MarkRestored(450_000, Utc(2)),
            state));
    return Task.CompletedTask;
}

static Task LiveRecoverySnapshotsPreserveSelectedProfile()
{
    var baseline = LiveCanaryPolicy.ValidateProbeReport(CreateLiveCanaryProbeReport(450_000));
    var profile = LivePowerProfiles.ResolveId("live-150");
    var prepared = LiveCanaryPolicy.CreateSnapshot(baseline, profile, Utc(0));
    AssertEqual(150_000u, prepared.TargetPowerLimitMilliwatts);
    LiveCanaryPolicy.ValidateRecoverySnapshot(prepared, baseline);

    var recoveryState = LiveCanaryPolicy.ValidateProbeReport(CreateLiveCanaryProbeReport(150_000));
    LiveCanaryPolicy.ValidateRecoverySnapshot(prepared.MarkApplied(150_000, Utc(1)), recoveryState);
    AssertThrows<InvalidOperationException>(() =>
        LiveCanaryPolicy.ValidateRecoverySnapshot(
            prepared.MarkApplied(150_000, Utc(1)),
            recoveryState with { CurrentLimitMilliwatts = 200_000 }));
    return Task.CompletedTask;
}

static Task LiveCanaryResponseTimeoutExceedsHold()
{
    AssertTrue(
        LiveCanaryPolicy.ResultTimeoutMilliseconds >=
            LiveCanaryPolicy.HoldDurationMilliseconds + 5_000,
        "The launcher must leave at least five seconds for apply, restore, and pipe response overhead.");
    return Task.CompletedTask;
}

static Task LiveRecoveryDrillAcceptsExactEvidence()
{
    using var interruptionSession = HardwareHostSessionFactory.CreateTransportSession(
        new FixedTimeProvider(Utc(10)));
    var interruptionBootstrap = CreateDrillBootstrap(
        interruptionSession.Bootstrap,
        LiveRecoveryDrillPhase.CrashAfterVerifiedTarget,
        HardwareHostOperation.ActivatePrepared);
    var interruptionResult = new HardwareHostWireResult(
        HardwareHostProcessOutcome.Executed,
        nameof(ActivationOutcome.Activated),
        "Activated.");
    var interruptionSummary = new LiveRecoveryDrillSummary(
        true,
        LiveRecoveryDrillPhase.CrashAfterVerifiedTarget,
        "NVIDIA GeForce RTX 4090",
        TargetAppliedAndVerified: true,
        OriginalRestoredAndVerified: false,
        400_000,
        "Applied",
        SafePhaseState: true,
        RecoveryArtifactsCleaned: false);
    AssertTrue(
        LiveRecoveryDrillEvidenceEvaluator.IsVerifiedInterruption(
            interruptionBootstrap,
            interruptionResult,
            interruptionSummary,
            LiveCanaryPolicy.IntentionalRecoveryDrillExitCode),
        "The exact intentional-interruption evidence should pass.");

    using var recoverySession = HardwareHostSessionFactory.CreateTransportSession(
        new FixedTimeProvider(Utc(10)));
    var recoveryBootstrap = CreateDrillBootstrap(
        recoverySession.Bootstrap,
        LiveRecoveryDrillPhase.RecoveryOnly,
        HardwareHostOperation.RestorePending);
    var recoveryResult = new HardwareHostWireResult(
        HardwareHostProcessOutcome.Executed,
        nameof(ActivationOutcome.Restored),
        "Restored.");
    var recoverySummary = new LiveRecoveryDrillSummary(
        true,
        LiveRecoveryDrillPhase.RecoveryOnly,
        "NVIDIA GeForce RTX 4090",
        TargetAppliedAndVerified: false,
        OriginalRestoredAndVerified: true,
        450_000,
        "Restored",
        SafePhaseState: true,
        RecoveryArtifactsCleaned: true);
    AssertTrue(
        LiveRecoveryDrillEvidenceEvaluator.IsVerifiedRecovery(
            recoveryBootstrap,
            recoveryResult,
            recoverySummary,
            helperExitCode: 0),
        "The exact fresh-helper recovery evidence should pass.");
    return Task.CompletedTask;
}

static Task LiveRecoveryDrillRejectsConflictingEvidence()
{
    using var session = HardwareHostSessionFactory.CreateTransportSession(
        new FixedTimeProvider(Utc(10)));
    var bootstrap = CreateDrillBootstrap(
        session.Bootstrap,
        LiveRecoveryDrillPhase.CrashAfterVerifiedTarget,
        HardwareHostOperation.ActivatePrepared);
    var result = new HardwareHostWireResult(
        HardwareHostProcessOutcome.Executed,
        nameof(ActivationOutcome.Activated),
        "Activated.");
    var summary = new LiveRecoveryDrillSummary(
        true,
        LiveRecoveryDrillPhase.CrashAfterVerifiedTarget,
        "NVIDIA GeForce RTX 4090",
        TargetAppliedAndVerified: true,
        OriginalRestoredAndVerified: false,
        450_000,
        "Applied",
        SafePhaseState: true,
        RecoveryArtifactsCleaned: false);
    AssertTrue(
        !LiveRecoveryDrillEvidenceEvaluator.IsVerifiedInterruption(
            bootstrap,
            result,
            summary,
            LiveCanaryPolicy.IntentionalRecoveryDrillExitCode),
        "A claimed interruption at 450 W must be rejected.");
    AssertTrue(
        !LiveRecoveryDrillEvidenceEvaluator.IsVerifiedInterruption(
            bootstrap,
            result,
            summary with { FinalLimitMilliwatts = 400_000 },
            helperExitCode: 0),
        "A normal exit must not count as intentional termination.");
    return Task.CompletedTask;
}

static Task LivePersistentToggleAcceptsExactPauseEvidence()
{
    using var session = HardwareHostSessionFactory.CreateTransportSession(
        new FixedTimeProvider(Utc(10)));
    var bootstrap = CreateDrillBootstrap(
        session.Bootstrap,
        LiveRecoveryDrillPhase.PauseAtVerifiedTarget,
        HardwareHostOperation.ActivatePrepared,
        "percent-89");
    var result = new HardwareHostWireResult(
        HardwareHostProcessOutcome.Executed,
        nameof(ActivationOutcome.Activated),
        "Activated.");
    var summary = new LiveRecoveryDrillSummary(
        true,
        LiveRecoveryDrillPhase.PauseAtVerifiedTarget,
        "NVIDIA GeForce RTX 4090",
        TargetAppliedAndVerified: true,
        OriginalRestoredAndVerified: false,
        400_000,
        "Applied",
        SafePhaseState: true,
        RecoveryArtifactsCleaned: false);

    AssertTrue(
        LivePersistentToggleEvidenceEvaluator.IsVerifiedPause(
            bootstrap,
            result,
            summary,
            helperExitCode: 0),
        "An orderly fixed pause with a retained recovery journal should pass.");
    return Task.CompletedTask;
}

static Task LivePersistentToggleRejectsCrashStyleEvidence()
{
    using var session = HardwareHostSessionFactory.CreateTransportSession(
        new FixedTimeProvider(Utc(10)));
    var bootstrap = CreateDrillBootstrap(
        session.Bootstrap,
        LiveRecoveryDrillPhase.PauseAtVerifiedTarget,
        HardwareHostOperation.ActivatePrepared,
        "percent-89");
    var result = new HardwareHostWireResult(
        HardwareHostProcessOutcome.Executed,
        nameof(ActivationOutcome.Activated),
        "Activated.");
    var summary = new LiveRecoveryDrillSummary(
        true,
        LiveRecoveryDrillPhase.PauseAtVerifiedTarget,
        "NVIDIA GeForce RTX 4090",
        TargetAppliedAndVerified: true,
        OriginalRestoredAndVerified: false,
        400_000,
        "Applied",
        SafePhaseState: true,
        RecoveryArtifactsCleaned: false);

    AssertTrue(
        !LivePersistentToggleEvidenceEvaluator.IsVerifiedPause(
            bootstrap,
            result,
            summary,
            LiveCanaryPolicy.IntentionalRecoveryDrillExitCode),
        "An intentional-crash exit must not count as an orderly persistent pause.");
    AssertTrue(
        !LivePersistentToggleEvidenceEvaluator.IsVerifiedPause(
            bootstrap,
            result,
            summary with { RecoveryArtifactsCleaned = true },
            helperExitCode: 0),
        "A pause cannot claim its required recovery artifact was cleaned.");
    return Task.CompletedTask;
}

static LiveRecoveryDrillBootstrap CreateDrillBootstrap(
    HardwareHostSessionBootstrap session,
    LiveRecoveryDrillPhase phase,
    HardwareHostOperation operation,
    string? profileId = null) =>
    new(
        session,
        Guid.Parse("77777777-7777-4777-8777-777777777777"),
        operation,
        phase,
        profileId ?? LivePowerProfiles.Default.Id,
        "NVIDIA GeForce RTX 4090",
        "sha256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
        450_000,
        400_000,
        150_000,
        600_000);

static GpuProbeReport CreateLiveCanaryProbeReport(uint currentLimitMilliwatts)
{
    var capabilities = GpuPowerCapabilities.Supported(
        Watts.FromMilliwatts(currentLimitMilliwatts),
        new Watts(450),
        new Watts(150),
        new Watts(600));
    return new GpuProbeReport(
        "NVIDIA",
        "NVML",
        GpuProbeStatus.Ready,
        "Read-only test report.",
        [
            new GpuDeviceInfo(
                0,
                "NVIDIA GeForce RTX 4090",
                capabilities,
                "sha256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")
        ]);
}

static string CreatePersistentToggleTranscript(
    bool paused,
    string profileId = "eco-400",
    string? requestedProfileId = null)
{
    var target = FakeTargetPolicy.ResolveProfile(profileId);
    var requested = requestedProfileId ?? profileId;
    return $$"""
    EcoPause Persistent Fake Toggle Simulation 1.5
    Launcher privilege: normal user
    Hardware: persistent file-backed fake only
    Requested fake target profile: {{requested}}
    Mutual process-ID verification: PASS
    Elevated helper startup: PASS
    Authenticated toggle: Executed / {{(paused ? "Activated" : "Restored")}}
    Toggle helper exit: 0
    Persistent fake state: {{(paused ? "Paused" : "Restored")}}
    Fake target profile: {{profileId}}
    Configured fake target: {{target.Watts:0}} W
    Final persistent fake limit: {{(paused ? target.Watts.ToString("0", System.Globalization.CultureInfo.InvariantCulture) : "450")}} W
    Recovery journal stage: {{(paused ? "Applied" : "Restored")}}
    Protected simulation files cleaned: {{(paused ? "False" : "True")}}
    Persistent fake toggle: PASS
    Real NVIDIA access: NONE
    """;
}

static string CreateDesktopLiveToggleTranscript(bool paused) =>
    paused
        ? """
          AFK Power Saver Live GPU Limit Session 1.22
          Launcher privilege: administrator startup broker
          Desktop GPU control: PERCENTAGE LIMIT / EXACT DEFAULT RESTORE
          Desktop activation triggers: BUTTON, ASSIGNED GLOBAL HOTKEY, OR ENABLED IDLE TIMER
          Current limit: 450 W
          Default limit: 450 W
          Requested limit profile: percent-33 / 33% / 150 W
          Preflight state: READY TO LIMIT
          Startup UAC session: PASS / mutually verified normal-user launcher
          Per-action confirmation: NOT REQUIRED / authorized button, hotkey, or enabled idle timer
          Startup-authorized broker launched the journal-selected helper without another UAC prompt.
          Requested live profile: percent-33 / 33% / 150 W
          Helper PID verified by Windows: PASS (1234)
          Helper-selected action: ApplyVerifiedLimit
          Helper-selected live profile: percent-33 / 33% / 150 W
          Authenticated operation: Executed / Activated
          Helper exit: 0
          Final helper limit: 150 W
          Recovery journal stage: Applied
          150 W exact persistent limit: PASS
          Protected recovery artifacts retained: PASS
          Independent final live limit: 150 W
          Protected exact-restoration journal retained: PASS
          Live GPU toggle: PASS / Limited
          GPU lifecycle: PERCENTAGE LIMIT / EXACT DEFAULT RESTORE
          Idle activation hardware path: STARTUP-AUTHORIZED BROKER ONLY
          """
        : """
          AFK Power Saver Live GPU Limit Session 1.22
          Launcher privilege: administrator startup broker
          Desktop GPU control: PERCENTAGE LIMIT / EXACT DEFAULT RESTORE
          Desktop activation triggers: BUTTON, ASSIGNED GLOBAL HOTKEY, OR ENABLED IDLE TIMER
          Current limit: 150 W
          Default limit: 450 W
          Requested limit profile: percent-89 / 89% / 400 W
          Preflight state: RECOVERY EXPECTED
          Startup UAC session: PASS / mutually verified normal-user launcher
          Per-action confirmation: NOT REQUIRED / authorized button, hotkey, or enabled idle timer
          Startup-authorized broker launched the journal-selected helper without another UAC prompt.
          Requested live profile: percent-89 / 89% / 400 W
          Helper PID verified by Windows: PASS (5678)
          Helper-selected action: RestoreOriginal
          Helper-selected live profile: percent-33 / 33% / 150 W
          Authenticated operation: Executed / Restored
          Helper exit: 0
          Final helper limit: 450 W
          Recovery journal stage: Restored
          450 W exact restoration: PASS
          Protected recovery artifacts cleaned: True
          Independent final live limit: 450 W
          Live GPU toggle: PASS / Restored
          GPU lifecycle: PERCENTAGE LIMIT / EXACT DEFAULT RESTORE
          Idle activation hardware path: STARTUP-AUTHORIZED BROKER ONLY
          """;

static string CreateReadOnlyProbeJson() => """
    {
      "Provider": "NVIDIA",
      "Backend": "NVML",
      "Status": "Ready",
      "Message": "Read-only queries loaded.",
      "Devices": [
        {
          "Index": 0,
          "Name": "NVIDIA GeForce RTX 4090",
          "Power": {
            "IsSupported": true,
            "CurrentLimit": { "Value": 450 },
            "DefaultLimit": { "Value": 450 },
            "MinimumLimit": { "Value": 150 },
            "MaximumLimit": { "Value": 600 },
            "CurrentUsage": { "Value": 108.451 },
            "UnavailableReason": null
          }
        }
      ]
    }
    """;

static string CreateAmdReadOnlyProbeJson() => """
    {
      "Provider": "AMD",
      "Backend": "ADLX",
      "Status": "Ready",
      "Message": "AMD ADLX initialized successfully.",
      "Devices": [
        {
          "Index": 0,
          "Name": "AMD Radeon Test GPU",
          "Power": {
            "IsSupported": true,
            "LimitKind": "DefaultRelativePercentage",
            "CurrentRelativePercentage": 100,
            "DefaultRelativePercentage": 100,
            "MinimumRelativePercentage": 90,
            "MaximumRelativePercentage": 115,
            "RelativePercentageStep": 1,
            "CurrentUsage": null,
            "UnavailableReason": null
          }
        }
      ]
    }
    """;

static void AssertSafeRollback(ActivationResult result, TestPowerController controller)
{
    AssertEqual(ActivationOutcome.FailedRolledBack, result.Outcome);
    AssertEqual(450_000u, controller.CurrentLimitMilliwatts);
    AssertTrue(result.OriginalLimitVerified, "Rollback must verify the exact original value.");
    AssertTrue(result.JournalVerified, "Rollback must close the journal.");
    AssertEqual(RecoveryStage.Restored, result.Snapshot.Stage);
}

static RecoverySnapshot CreateSnapshot(uint target = 400_000) =>
    RecoverySnapshot.Prepare(
        Utc(0),
        "NVIDIA",
        "NVML",
        DeviceFingerprint.FromStableIdentifier("GPU-test-identifier"),
        originalPowerLimitMilliwatts: 450_000,
        targetPowerLimitMilliwatts: target,
        minimumPowerLimitMilliwatts: 150_000,
        maximumPowerLimitMilliwatts: 600_000,
        snapshotId: Guid.Parse("11111111-2222-3333-4444-555555555555"));

static RecoverySnapshot CreateLiveSnapshot(Guid? snapshotId = null) =>
    RecoverySnapshot.Prepare(
        Utc(0),
        "Simulation",
        "InMemory",
        DeviceFingerprint.FromStableIdentifier("TEST-GPU"),
        450_000,
        400_000,
        150_000,
        600_000,
        snapshotId: snapshotId ?? Guid.Parse("AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE"));

static RecoverySnapshot CreateAppliedLiveSnapshot() =>
    CreateLiveSnapshot().MarkApplied(400_000, Utc(1));

static RecoveryDiscoveryItem CreatePendingItem(RecoverySnapshot snapshot, string path) =>
    new(
        Path.GetFullPath(path),
        RecoveryDiscoveryKind.PendingLiveRecovery,
        snapshot,
        "Pending test recovery.");

static StartupRecoveryCoordinator CreateStartupCoordinator(IRecoveryJournalStore store) =>
    new(
        new GpuActivationEngine(store, new FixedTimeProvider(Utc(9))),
        new FixedTimeProvider(Utc(6)));

static async Task<HostTestHarness> CreateHostHarnessAsync()
{
    var timeProvider = new MutableTimeProvider(Utc(10));
    var snapshot = CreateLiveSnapshot();
    var store = new InMemoryJournalStore();
    await store.SaveAsync("host-test.json", snapshot);
    var controller = CreatePowerController();
    var session = HardwareHostSessionFactory.Create(timeProvider);
    var registry = new TestHardwareHostRegistry(store, controller, "host-test.json");
    var processor = new HardwareHostCommandProcessor(
        session.Host,
        registry,
        new GpuActivationEngine(store, timeProvider),
        timeProvider);

    return new HostTestHarness(
        timeProvider,
        snapshot,
        store,
        controller,
        session,
        processor);
}

static TestPowerController CreatePowerController(
    List<string>? events = null,
    TestPowerFailure failureMode = TestPowerFailure.None) =>
    new(
        DeviceFingerprint.FromStableIdentifier("TEST-GPU"),
        initialLimitMilliwatts: 450_000,
        targetLimitMilliwatts: 400_000,
        failureMode,
        events);

static IGpuHardwareProbe CreateFakeProbe(string? deviceFingerprint = "sha256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")
{
    var capabilities = GpuPowerCapabilities.Supported(
        new Watts(450),
        new Watts(450),
        new Watts(150),
        new Watts(600),
        new Watts(50));

    var report = new GpuProbeReport(
        "NVIDIA",
        "NVML",
        GpuProbeStatus.Ready,
        "Fake read-only probe ready.",
        new[] { new GpuDeviceInfo(0, "Test GPU", capabilities, deviceFingerprint) });

    return new FakeGpuProbe(report);
}

static DateTimeOffset Utc(int minutes) =>
    new(2026, 8, 3, 10, minutes, 0, TimeSpan.Zero);

static async Task WithTemporaryJournal(Func<string, Task> test)
{
    var directory = Path.Combine(Path.GetTempPath(), "EcoPause.Tests", Guid.NewGuid().ToString("N"));
    var path = Path.Combine(directory, "recovery.json");

    try
    {
        await test(path);
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

static void AssertEqual<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected '{expected}', received '{actual}'.");
    }
}

static void AssertTrue(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertThrows<TException>(Action action)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

static async Task AssertThrowsAsync<TException>(Func<Task> action)
    where TException : Exception
{
    try
    {
        await action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

internal sealed class FakeGpuProbe(GpuProbeReport report) : IGpuHardwareProbe
{
    public string ProviderName => report.Provider;

    public Task<GpuProbeReport> ProbeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(report);
    }
}

internal sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => utcNow;
}

internal sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private DateTimeOffset _utcNow = utcNow;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);
}

internal enum TestPowerFailure
{
    None,
    TargetReadBackMismatch,
    InterruptAfterTargetWrite,
    TargetAndRollbackReadBackMismatch
}

internal sealed class TestPowerController(
    DeviceFingerprint deviceFingerprint,
    uint initialLimitMilliwatts,
    uint targetLimitMilliwatts,
    TestPowerFailure failureMode = TestPowerFailure.None,
    List<string>? events = null) : IGpuPowerController
{
    private readonly List<string> _events = events ?? [];
    private bool _targetFailureTriggered;

    public DeviceFingerprint DeviceFingerprint { get; } = deviceFingerprint;

    public uint CurrentLimitMilliwatts { get; private set; } = initialLimitMilliwatts;

    public int SetCalls { get; private set; }

    public bool RollbackReceivedUncancelledToken { get; private set; }

    public void SetCurrentLimitForRecovery(uint powerLimitMilliwatts) =>
        CurrentLimitMilliwatts = powerLimitMilliwatts;

    public Task<uint> ReadPowerLimitMilliwattsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _events.Add($"READ {CurrentLimitMilliwatts}");
        return Task.FromResult(CurrentLimitMilliwatts);
    }

    public Task SetPowerLimitMilliwattsAsync(
        uint powerLimitMilliwatts,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SetCalls++;
        _events.Add($"SET {powerLimitMilliwatts}");

        if (powerLimitMilliwatts == 450_000 && _targetFailureTriggered)
        {
            RollbackReceivedUncancelledToken = !cancellationToken.CanBeCanceled;
            CurrentLimitMilliwatts = failureMode == TestPowerFailure.TargetAndRollbackReadBackMismatch
                ? 449_000
                : powerLimitMilliwatts;
            return Task.CompletedTask;
        }

        if (!_targetFailureTriggered && powerLimitMilliwatts == targetLimitMilliwatts)
        {
            _targetFailureTriggered = true;
            CurrentLimitMilliwatts = failureMode is TestPowerFailure.TargetReadBackMismatch or
                TestPowerFailure.TargetAndRollbackReadBackMismatch
                ? powerLimitMilliwatts + 1_000
                : powerLimitMilliwatts;

            if (failureMode == TestPowerFailure.InterruptAfterTargetWrite)
            {
                throw new OperationCanceledException("Injected interruption after target write.");
            }

            return Task.CompletedTask;
        }

        CurrentLimitMilliwatts = powerLimitMilliwatts;
        return Task.CompletedTask;
    }
}

internal sealed class InMemoryJournalStore : IRecoveryJournalStore
{
    private readonly List<string> _events;
    private readonly int? _failOnSaveCall;
    private RecoverySnapshot? _snapshot;

    public InMemoryJournalStore(List<string>? events = null, int? failOnSaveCall = null)
    {
        _events = events ?? [];
        _failOnSaveCall = failOnSaveCall;
    }

    public InMemoryJournalStore(int failOnSaveCall)
        : this(null, failOnSaveCall)
    {
    }

    public int SaveCalls { get; private set; }

    public RecoverySnapshot? CurrentSnapshot => _snapshot;

    public Task SaveAsync(
        string path,
        RecoverySnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SaveCalls++;
        _events.Add($"SAVE {snapshot.Stage}");

        if (SaveCalls == _failOnSaveCall)
        {
            throw new IOException("Injected journal persistence failure.");
        }

        _snapshot = snapshot;
        return Task.CompletedTask;
    }

    public Task<RecoverySnapshot> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_snapshot ?? throw new RecoveryJournalException("No in-memory journal exists."));
    }
}

internal sealed class TestHardwareHostRegistry(
    InMemoryJournalStore store,
    TestPowerController controller,
    string journalPath) : IHardwareHostTargetRegistry
{
    public Task<HardwareHostTarget?> ResolveAsync(
        Guid snapshotId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = store.CurrentSnapshot;
        HardwareHostTarget? target = snapshot?.SnapshotId == snapshotId
            ? new HardwareHostTarget(journalPath, snapshot, controller)
            : null;
        return Task.FromResult(target);
    }
}

internal sealed record HostTestHarness(
    MutableTimeProvider TimeProvider,
    RecoverySnapshot Snapshot,
    InMemoryJournalStore Store,
    TestPowerController Controller,
    HardwareHostSessionPair Session,
    HardwareHostCommandProcessor Processor);
