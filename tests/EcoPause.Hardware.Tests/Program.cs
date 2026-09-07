using System.Reflection;
using AFKPowerSaver.OverlayModel;
using AFKPowerSaver.PowerPlan;
using EcoPause.Hardware.Abstractions;
using EcoPause.Hardware.Amd;
using EcoPause.Hardware.Nvidia;
using EcoPause.Hardware.Nvidia.Control;

var tests = new (string Name, Action Run)[]
{
    ("milliwatts convert exactly to watts", MilliwattsConvertExactlyToWatts),
    ("supported capabilities preserve limits", SupportedCapabilitiesPreserveLimits),
    ("AMD capabilities preserve factory-relative limits", AmdCapabilitiesPreserveRelativeLimits),
    ("reversed constraints are rejected", ReversedConstraintsAreRejected),
    ("unsupported capabilities require a reason", UnsupportedCapabilitiesRequireReason),
    ("public NVIDIA probe API exposes no mutation methods", NvidiaProbeExposesNoMutationMethods),
    ("public AMD probe API exposes no mutation methods", AmdProbeExposesNoMutationMethods),
    ("AMD probe initializes or degrades safely", AmdProbeInitializesOrDegradesSafely),
    ("live hardware mutex releases on its owner thread", LiveHardwareMutexReleasesOnOwnerThread),
    ("processor-state snapshot preserves exact AC and DC values", ProcessorStateSnapshotPreservesValues),
    ("processor-state percentages above 100 are rejected", ProcessorStateRejectsOutOfRangeValues),
    ("read-only processor plan API exposes no mutation methods", ProcessorPlanReaderExposesNoMutationMethods),
    ("CPU limit policy accepts only the bounded production range", CpuLimitPolicyIsBounded),
    ("CPU limit policy never raises existing AC or DC values", CpuLimitPolicyNeverRaisesValues),
    ("CPU recovery record preserves exact original values", CpuRecoveryRecordPreservesOriginalValues),
    ("overlay policy converts percentage opacity exactly", OverlayPolicyConvertsOpacityExactly),
    ("overlay policy rejects unsafe opacity values", OverlayPolicyRejectsUnsafeOpacity),
    ("overlay canary duration remains tightly bounded", OverlayCanaryDurationIsBounded),
    ("connected display selection is preserved", ConnectedDisplaySelectionIsPreserved),
    ("disconnected display selection falls back to all", DisconnectedDisplaySelectionFallsBackToAll),
    ("multiple connected display selections are preserved", MultipleConnectedDisplaySelectionsArePreserved),
    ("disconnected entries are removed from a display selection", DisconnectedEntriesAreRemovedFromDisplaySelection),
    ("all displays dominates an individual display selection", AllDisplaysDominatesIndividualSelection)
};

var failures = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS: {test.Name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL: {test.Name} - {exception.Message}");
    }
}

Console.WriteLine();
Console.WriteLine($"{tests.Length - failures}/{tests.Length} safety tests passed.");
return failures == 0 ? 0 : 1;

static void MilliwattsConvertExactlyToWatts()
{
    AssertEqual(450m, Watts.FromMilliwatts(450_000).Value);
    AssertEqual(123.456m, Watts.FromMilliwatts(123_456).Value);
}

static void SupportedCapabilitiesPreserveLimits()
{
    var capabilities = GpuPowerCapabilities.Supported(
        new Watts(450),
        new Watts(450),
        new Watts(150),
        new Watts(600),
        new Watts(72.5m));

    AssertTrue(capabilities.IsSupported, "Capabilities should be marked supported.");
    AssertEqual(150m, capabilities.MinimumLimit?.Value);
    AssertEqual(600m, capabilities.MaximumLimit?.Value);
    AssertEqual(72.5m, capabilities.CurrentUsage?.Value);
}

static void AmdCapabilitiesPreserveRelativeLimits()
{
    var capabilities = GpuPowerCapabilities.SupportedDefaultRelativePercentage(
        currentPercentage: 100,
        minimumPercentage: 90,
        maximumPercentage: 115,
        percentageStep: 1);

    AssertTrue(capabilities.IsSupported, "AMD capabilities should be marked supported.");
    AssertEqual(GpuPowerLimitKind.DefaultRelativePercentage, capabilities.LimitKind);
    AssertEqual(100, capabilities.DefaultRelativePercentage);
    AssertEqual(90, capabilities.MinimumRelativePercentage);
    AssertEqual(115, capabilities.MaximumRelativePercentage);
    AssertEqual(1, capabilities.RelativePercentageStep);
    AssertEqual<Watts?>(null, capabilities.CurrentLimit);
}

static void ReversedConstraintsAreRejected()
{
    AssertThrows<ArgumentException>(() => GpuPowerCapabilities.Supported(
        new Watts(200),
        new Watts(200),
        new Watts(300),
        new Watts(100)));
}

static void UnsupportedCapabilitiesRequireReason()
{
    AssertThrows<ArgumentException>(() => GpuPowerCapabilities.Unsupported(" "));
}

static void NvidiaProbeExposesNoMutationMethods()
{
    var suspiciousPrefixes = new[] { "Set", "Apply", "Restore", "Write" };
    var publicMethods = typeof(NvidiaGpuProbe)
        .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
        .Select(method => method.Name)
        .ToArray();

    AssertTrue(
        publicMethods.All(method => suspiciousPrefixes.All(prefix => !method.StartsWith(prefix, StringComparison.Ordinal))),
        $"Unexpected mutation-like method found: {string.Join(", ", publicMethods)}");
}

static void AmdProbeExposesNoMutationMethods()
{
    var suspiciousPrefixes = new[] { "Set", "Apply", "Restore", "Write" };
    var publicMethods = typeof(AmdGpuProbe)
        .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
        .Select(method => method.Name)
        .ToArray();

    AssertTrue(
        publicMethods.All(method => suspiciousPrefixes.All(prefix => !method.StartsWith(prefix, StringComparison.Ordinal))),
        $"Unexpected mutation-like method found: {string.Join(", ", publicMethods)}");
}

static void AmdProbeInitializesOrDegradesSafely()
{
    var report = new AmdGpuProbe().ProbeAsync().GetAwaiter().GetResult();
    AssertEqual("AMD", report.Provider);
    AssertTrue(
        report.Status is GpuProbeStatus.Ready or
            GpuProbeStatus.BackendUnavailable or
            GpuProbeStatus.InitializationFailed or
            GpuProbeStatus.QueryFailed,
        "The AMD probe returned an unknown status.");
    if (report.Status == GpuProbeStatus.Ready)
    {
        AssertEqual(1, report.Devices.Count);
        AssertEqual(GpuPowerLimitKind.DefaultRelativePercentage, report.Devices[0].Power.LimitKind);
    }
    else
    {
        AssertEqual(0, report.Devices.Count);
    }
}

static void LiveHardwareMutexReleasesOnOwnerThread()
{
    if (!OperatingSystem.IsWindows())
    {
        return;
    }

    var mutexName = $@"Local\EcoPause-LockTest-{Guid.NewGuid():N}";
    var first = LiveHardwareProcessLock.AcquireForTesting(mutexName);

    AssertThrows<InvalidOperationException>(() =>
        LiveHardwareProcessLock.AcquireForTesting(mutexName));

    Task.Run(first.Dispose).GetAwaiter().GetResult();

    using var reacquired = LiveHardwareProcessLock.AcquireForTesting(mutexName);
}

static void ProcessorStateSnapshotPreservesValues()
{
    var scheme = Guid.NewGuid();
    var snapshot = new ProcessorMaximumStateSnapshot(scheme, 83, 61);

    snapshot.Validate();

    AssertEqual(scheme, snapshot.SchemeId);
    AssertEqual(83u, snapshot.AcMaximumPercent);
    AssertEqual(61u, snapshot.DcMaximumPercent);
}

static void ProcessorStateRejectsOutOfRangeValues()
{
    AssertThrows<ArgumentOutOfRangeException>(() =>
        new ProcessorMaximumStateSnapshot(Guid.NewGuid(), 101, 50).Validate());
    AssertThrows<ArgumentOutOfRangeException>(() =>
        new ProcessorMaximumStateSnapshot(Guid.NewGuid(), 50, 101).Validate());
}

static void ProcessorPlanReaderExposesNoMutationMethods()
{
    var suspiciousPrefixes = new[] { "Set", "Apply", "Restore", "Write" };
    var publicMethods = typeof(WindowsProcessorPowerPlanReader)
        .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly)
        .Select(method => method.Name)
        .ToArray();

    AssertTrue(
        publicMethods.All(method => suspiciousPrefixes.All(prefix => !method.StartsWith(prefix, StringComparison.Ordinal))),
        $"Unexpected mutation-like method found: {string.Join(", ", publicMethods)}");
}

static void CpuLimitPolicyIsBounded()
{
    AssertEqual(20, ProcessorLimitPolicy.ValidateTarget(20));
    AssertEqual(99, ProcessorLimitPolicy.ValidateTarget(99));
    AssertThrows<ArgumentOutOfRangeException>(() => ProcessorLimitPolicy.ValidateTarget(19));
    AssertThrows<ArgumentOutOfRangeException>(() => ProcessorLimitPolicy.ValidateTarget(100));
}

static void CpuLimitPolicyNeverRaisesValues()
{
    var original = new ProcessorMaximumStateSnapshot(Guid.NewGuid(), 65, 40);
    var targets = ProcessorLimitPolicy.CalculateTargets(original, 80);

    AssertEqual(65u, targets.AcPercent);
    AssertEqual(40u, targets.DcPercent);
}

static void CpuRecoveryRecordPreservesOriginalValues()
{
    var original = new ProcessorMaximumStateSnapshot(Guid.NewGuid(), 97, 73);
    var restored = ProcessorLimitRecoveryRecord.FromSnapshot(original).ToSnapshot();

    AssertEqual(original, restored);
}

static void OverlayPolicyConvertsOpacityExactly()
{
    var settings = OverlayCanaryPolicy.Create(70);

    AssertEqual(70, settings.OpacityPercent);
    AssertEqual(0.7d, settings.OpacityFraction);
    AssertEqual(TimeSpan.FromSeconds(15), settings.HoldDuration);
}

static void OverlayPolicyRejectsUnsafeOpacity()
{
    AssertThrows<ArgumentOutOfRangeException>(() => OverlayCanaryPolicy.Create(9));
    AssertThrows<ArgumentOutOfRangeException>(() => OverlayCanaryPolicy.Create(96));
}

static void OverlayCanaryDurationIsBounded()
{
    AssertTrue(
        OverlayCanaryPolicy.QualifiedHoldDuration >= OverlayCanaryPolicy.MinimumHoldDuration,
        "The qualified duration is shorter than the minimum.");
    AssertTrue(
        OverlayCanaryPolicy.QualifiedHoldDuration <= OverlayCanaryPolicy.MaximumHoldDuration,
        "The qualified duration exceeds the maximum.");
}

static void ConnectedDisplaySelectionIsPreserved()
{
    const string selected = @"\\.\DISPLAY2";
    var normalized = DisplayTargetPolicy.NormalizeSavedTarget(
        selected,
        [@"\\.\DISPLAY1", @"\\.\DISPLAY2"]);

    AssertEqual(selected, normalized);
}

static void DisconnectedDisplaySelectionFallsBackToAll()
{
    var normalized = DisplayTargetPolicy.NormalizeSavedTarget(
        @"\\.\DISPLAY9",
        [@"\\.\DISPLAY1", @"\\.\DISPLAY2"]);

    AssertEqual(DisplayTargetPolicy.AllDisplaysTargetId, normalized);
}

static void MultipleConnectedDisplaySelectionsArePreserved()
{
    var normalized = DisplayTargetPolicy.NormalizeSavedTargets(
        [@"\\.\DISPLAY1", @"\\.\DISPLAY3"],
        [@"\\.\DISPLAY1", @"\\.\DISPLAY2", @"\\.\DISPLAY3"]);

    AssertEqual(@"\\.\DISPLAY1|\\.\DISPLAY3", string.Join('|', normalized));
}

static void DisconnectedEntriesAreRemovedFromDisplaySelection()
{
    var normalized = DisplayTargetPolicy.NormalizeSavedTargets(
        [@"\\.\DISPLAY1", @"\\.\DISPLAY9"],
        [@"\\.\DISPLAY1", @"\\.\DISPLAY2"]);

    AssertEqual(@"\\.\DISPLAY1", string.Join('|', normalized));
}

static void AllDisplaysDominatesIndividualSelection()
{
    var normalized = DisplayTargetPolicy.NormalizeSavedTargets(
        [@"\\.\DISPLAY2", DisplayTargetPolicy.AllDisplaysTargetId],
        [@"\\.\DISPLAY1", @"\\.\DISPLAY2"]);

    AssertEqual(DisplayTargetPolicy.AllDisplaysTargetId, string.Join('|', normalized));
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
