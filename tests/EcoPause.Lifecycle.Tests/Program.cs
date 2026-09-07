using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using AFKPowerSaver.PowerPlan;
using EcoPause.Core.Recovery;
using EcoPause.Desktop.Safety;
using EcoPause.Desktop;
using EcoPause.Hardware.Abstractions;
using EcoPause.LiveCanary.Model;

// Cross-process lease tests always use fake restoration; no Windows writer is invoked.
if (args.Length > 0)
{
    if (args[0] == "--fake-gpu-session")
    {
        while (await Console.In.ReadLineAsync() is { } command)
        {
            var shutdown = command == "SHUTDOWN";
            var limit = command.StartsWith("TOGGLE ", StringComparison.Ordinal);
            var result = new GpuOperationResult(limit ? GpuOperationState.Limited : GpuOperationState.Restored,
                true, limit, limit ? 400_000u : 450_000u, 450_000);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                kind = shutdown ? "shutdown" : "result", succeeded = true,
                message = "Verified fake result", transcript = "Free-form diagnostic wording", error = "", result
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            if (shutdown) break;
        }
        return 0;
    }
    if (args[0] == "--lease-worker")
    {
        using var parent = Process.GetProcessById(int.Parse(args[1], CultureInfo.InvariantCulture));
        _ = parent.Handle;
        Console.WriteLine("READY");
        await CpuRecoveryLease.RecoverAfterDisconnectAsync(Console.In, parent.WaitForExitAsync(), () =>
            File.WriteAllTextAsync(args[2], "restored"));
        return 0;
    }
    if (args[0] == "--lease-parent")
    {
        using var child = StartChild("--lease-worker", Environment.ProcessId.ToString(CultureInfo.InvariantCulture), args[1]);
        if (await child.StandardOutput.ReadLineAsync() != "READY") return 1;
        Console.WriteLine(child.Id);
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return 0;
    }
    return 2;
}

var tests = new List<(string Name, Func<Task> Run)>();
for (var mask = 1; mask < 8; mask++)
{
    var selection = new ProfileSelection((mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0);
    tests.Add(($"independent profile combination {mask}", async () =>
    {
        var ports = new FakeOperations();
        var coordinator = new ProfileCoordinator(ports);
        Check((await coordinator.ApplyAsync(selection)).Succeeded, "Profile should apply.");
        Check(coordinator.IsActive, "An applied profile must retain ownership, including a CPU no-op.");
        var calls = ports.Calls.Count;
        // A default GPU reading is a UI observation; it does not own CPU or display state.
        _ = DesktopLiveTogglePolicy.Evaluate(new ReadOnlyGpuProbeResult(false, "NVIDIA", "unavailable", []));
        Check(coordinator.IsActive && ports.Calls.Count == calls, "Observation must preserve the active profile.");
        Check((await coordinator.RestoreAsync()).Succeeded && !coordinator.IsActive, "Profile must restore.");
        Check(ports.Calls.Contains("gpu-apply") == selection.Gpu, "GPU selection mismatch.");
        Check(ports.Calls.Contains("cpu-apply") == selection.Cpu, "CPU selection mismatch.");
        Check(ports.Calls.Contains("display-show") == selection.Display, "Display selection mismatch.");
    }));
}
foreach (var failure in new[] { "gpu-apply", "cpu-apply", "display-show" })
{
    tests.Add(($"rollback after {failure} failure", async () =>
    {
        var ports = new FakeOperations { FailOnce = failure };
        var coordinator = new ProfileCoordinator(ports);
        var result = await coordinator.ApplyAsync(new(true, true, true));
        Check(!result.Succeeded && !result.Active, "Partial profile must roll back.");
        Check(ports.Calls.Contains("gpu-restore"), "Uncertain GPU application must recover.");
    }));
}
tests.Add(("CPU recovery failure never skips GPU or display recovery", async () =>
{
    var ports = new FakeOperations();
    var coordinator = new ProfileCoordinator(ports);
    await coordinator.ApplyAsync(new(true, true, true));
    ports.FailOnce = "cpu-restore";
    var result = await coordinator.RestoreAsync();
    Check(!result.Succeeded && coordinator.CpuPending, "Failed CPU recovery must remain owned.");
    Check(!coordinator.GpuPending && !coordinator.DisplayActive, "Other resources must restore independently.");
    var gpuWrites = ports.Calls.Count(call => call == "gpu-restore");
    Check((await coordinator.RestoreAsync()).Succeeded && !coordinator.IsActive, "CPU retry should succeed.");
    Check(ports.Calls.Count(call => call == "gpu-restore") == gpuWrites, "CPU retry must not toggle the GPU.");
}));
tests.Add(("unverified GPU result triggers recovery", async () =>
{
    var ports = new FakeOperations { InvalidGpuResult = true };
    var coordinator = new ProfileCoordinator(ports);
    Check(!(await coordinator.ApplyAsync(new(true, true, false))).Succeeded, "Unverified result must fail.");
    Check(!ports.Calls.Contains("cpu-apply") && ports.Calls.Contains("gpu-restore"), "Stop activation and recover.");
}));
tests.Add(("idle restores can retry while attended, activation stays suppressed", () =>
{
    Check(IdleActivationPolicy.Evaluate(true, true, true, false, false, true) == IdleActivationDirective.RestoreOriginal,
        "Recovery cannot share the activation suppression flag.");
    Check(IdleActivationPolicy.Evaluate(true, false, true, true, true, false) == IdleActivationDirective.None,
        "Failed activation must not continuously reapply.");
    var clock = new ManualClock();
    var retry = new RecoveryRetrySchedule(clock);
    for (var attempt = 1; attempt <= RecoveryRetrySchedule.MaximumAttempts; attempt++)
    {
        Check(retry.IsDue, "Retry should be due.");
        retry.Failed();
        Check(!retry.IsDue, "Retry must back off.");
        clock.Advance(TimeSpan.FromSeconds(1 << attempt));
    }
    Check(retry.Exhausted && !retry.IsDue, "Automatic retries must be bounded.");
    retry.Reset();
    Check(retry.IsDue, "Manual recovery should reset the retry budget.");
    return Task.CompletedTask;
}));

foreach (var phase in Enum.GetValues<RecoveryStage>())
foreach (var current in new uint[] { 400_000, 450_000 })
{
    tests.Add(($"recovery {phase} with interrupted replacement at {current}", async () =>
    {
        using var directory = new TestDirectory();
        using var gpu = new FakeGpu(current);
        var snapshot = MakeSnapshot(gpu.State);
        if (phase != RecoveryStage.Prepared) snapshot = snapshot.MarkApplied(400_000, DateTimeOffset.UtcNow);
        if (phase == RecoveryStage.Restored) snapshot = snapshot.MarkRestored(450_000, DateTimeOffset.UtcNow);
        await RecoveryJournalStore.SaveAsync(directory.Journal, snapshot);
        await File.WriteAllTextAsync(Path.Combine(directory.Path, $".recovery.json.{Guid.NewGuid():N}.tmp"), "partial");
        RecoveryArtifacts.CleanInterruptedWrites(directory.Path, "recovery.json");
        if (phase == RecoveryStage.Restored && current != 450_000)
        {
            await ThrowsAsync(() => GpuRecoveryOnly.RunAsync(directory.Journal, gpu, new FileRecoveryJournalStore()));
            Check(File.Exists(directory.Journal), "Mismatch must preserve the journal.");
        }
        else
        {
            var result = await GpuRecoveryOnly.RunAsync(directory.Journal, gpu, new FileRecoveryJournalStore());
            Check(result.IsValid && result.State == GpuOperationState.Restored, "Recovery must return a verified typed state.");
            Check(gpu.Current == 450_000 && !File.Exists(directory.Journal), "Recovery must verify and close the journal.");
            Check(gpu.Writes == (current == 450_000 ? 0 : 1), "Already restored hardware must not be written.");
        }
    }));
}
tests.Add(("no canonical journal never activates, even with a partial Prepared save", async () =>
{
    using var directory = new TestDirectory();
    using var gpu = new FakeGpu(450_000);
    await File.WriteAllTextAsync(Path.Combine(directory.Path, $".recovery.json.{Guid.NewGuid():N}.tmp"), "partial");
    RecoveryArtifacts.CleanInterruptedWrites(directory.Path, "recovery.json");
    var result = await GpuRecoveryOnly.RunAsync(directory.Journal, gpu, new FileRecoveryJournalStore());
    Check(result.State == GpuOperationState.Unchanged && gpu.Writes == 0, "No journal must mean no write.");
}));
tests.Add(("unknown recovery artifacts are rejected without deleting evidence", async () =>
{
    using var directory = new TestDirectory();
    var unknown = Path.Combine(directory.Path, ".recovery.json.not-a-guid.tmp");
    await File.WriteAllTextAsync(unknown, "retain");
    try { RecoveryArtifacts.CleanInterruptedWrites(directory.Path, "recovery.json"); throw new InvalidOperationException("Accepted unknown file."); }
    catch (RecoveryJournalException) { Check(File.Exists(unknown), "Unknown file must remain."); }
}));
tests.Add(("CPU journal preserves owner and reads legacy records", async () =>
{
    using var directory = new TestDirectory();
    var owner = Guid.NewGuid();
    var original = new ProcessorMaximumStateSnapshot(Guid.NewGuid(), 100, 65);
    ProcessorLimitRecoveryJournal.SaveRecord(directory.Journal, ProcessorLimitRecoveryRecord.FromSnapshot(original, owner));
    var loaded = ProcessorLimitRecoveryJournal.LoadRecord(directory.Journal);
    Check(loaded.OwnerId == owner && loaded.ToSnapshot() == original, "Exact values and ownership must survive persistence.");
    await File.WriteAllTextAsync(directory.Journal,
        $$"""{"version":1,"schemeId":"{{original.SchemeId}}","acMaximumPercent":100,"dcMaximumPercent":65}""");
    loaded = ProcessorLimitRecoveryJournal.LoadRecord(directory.Journal);
    Check(loaded.OwnerId == Guid.Empty && loaded.ToSnapshot() == original, "Legacy recovery must remain supported.");
}));
tests.Add(("CPU companion lease recovers after actual parent process termination", async () =>
{
    using var directory = new TestDirectory();
    var marker = Path.Combine(directory.Path, "fake-cpu-restored.txt");
    using var parent = StartChild("--lease-parent", marker);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    var workerLine = await parent.StandardOutput.ReadLineAsync(timeout.Token);
    Check(int.TryParse(workerLine, out var workerId), "Worker should acknowledge readiness.");
    using var worker = Process.GetProcessById(workerId);
    try
    {
        parent.Kill(entireProcessTree: false);
        await parent.WaitForExitAsync(timeout.Token);
        await worker.WaitForExitAsync(timeout.Token);
        Check(File.Exists(marker) && await File.ReadAllTextAsync(marker) == "restored", "Orphaned worker must execute recovery.");
    }
    finally
    {
        if (!parent.HasExited) parent.Kill();
        if (!worker.HasExited) worker.Kill();
    }
}));

foreach (var changedPlan in new[] { false, true })
{
    tests.Add(($"CPU restoration preserves intervening plan selection: {changedPlan}", () =>
    {
        var original = new ProcessorMaximumStateSnapshot(Guid.NewGuid(), 100, 65);
        var access = new FakeProcessorAccess(original, changedPlan ? Guid.NewGuid() : original.SchemeId);
        var active = access.Active;
        Check(ProcessorRestoration.RestoreAndVerify(original, access) == original, "Exact original values must restore.");
        Check(access.Active == active, "Restoration must preserve the active plan.");
        Check(access.Activations == (changedPlan ? 0 : 1), "Only the still-active plan should be refreshed.");
        return Task.CompletedTask;
    }));
}

tests.Add(("production desktop IPC reads typed apply, restore and shutdown results", async () =>
{
    using var child = StartChild("--fake-gpu-session");
    await using var controller = (LiveSessionProcessController)Activator.CreateInstance(
        typeof(LiveSessionProcessController), BindingFlags.Instance | BindingFlags.NonPublic,
        binder: null, args: [child], culture: null)!;
    typeof(LiveSessionProcessController).GetProperty(nameof(LiveSessionProcessController.IsReady))!.SetValue(controller, true);
    var limited = await controller.ToggleAsync(new DesktopLiveTargetOption(89, 400));
    Check(limited.Result is { IsValid: true, State: GpuOperationState.Limited }, "Typed application must survive the real parser.");
    var restored = await controller.RestoreAsync();
    Check(restored.Result is { IsValid: true, State: GpuOperationState.Restored }, "Typed recovery must survive the real parser.");
    await controller.DisposeAsync();
}));

var failures = 0;
foreach (var (name, run) in tests)
{
    try { await run(); Console.WriteLine("PASS: " + name); }
    catch (Exception ex) { failures++; Console.Error.WriteLine("FAIL: " + name + " — " + ex); }
}
Console.WriteLine($"{tests.Count - failures}/{tests.Count} lifecycle tests passed.");
return failures == 0 ? 0 : 1;

static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static async Task ThrowsAsync(Func<Task> action)
{
    try { await action(); }
    catch (InvalidOperationException) { return; }
    throw new InvalidOperationException("Expected recovery rejection.");
}
static RecoverySnapshot MakeSnapshot(LiveCanaryDeviceState state) =>
    PortableLivePowerPolicy.CreateSnapshot(state, new("percent-89", 400_000), DateTimeOffset.UtcNow);
static Process StartChild(params string[] arguments)
{
    var start = new ProcessStartInfo(Environment.ProcessPath!)
    {
        UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true
    };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    return Process.Start(start) ?? throw new InvalidOperationException("Test child failed to start.");
}

sealed class FakeOperations : IProfileOperations
{
    public List<string> Calls { get; } = [];
    public string? FailOnce { get; set; }
    public bool InvalidGpuResult { get; set; }
    private void Call(string name)
    {
        Calls.Add(name);
        if (FailOnce == name) { FailOnce = null; throw new InvalidOperationException("Injected " + name); }
    }
    public Task<GpuOperationResult> ApplyGpuAsync()
    {
        Call("gpu-apply");
        return Task.FromResult(new GpuOperationResult(GpuOperationState.Limited, !InvalidGpuResult, true, 400_000, 450_000));
    }
    public Task<GpuOperationResult> RestoreGpuAsync()
    {
        Call("gpu-restore");
        return Task.FromResult(new GpuOperationResult(GpuOperationState.Restored, true, false, 450_000, 450_000));
    }
    public Task ApplyCpuAsync() { Call("cpu-apply"); return Task.CompletedTask; }
    public Task RestoreCpuAsync() { Call("cpu-restore"); return Task.CompletedTask; }
    public void ShowDimming() => Call("display-show");
    public void HideDimming() => Call("display-hide");
}
sealed class FakeGpu(uint current) : ILiveGpuPowerController
{
    public uint Current { get; private set; } = current;
    public int Writes { get; private set; }
    public DeviceFingerprint DeviceFingerprint => State.DeviceFingerprint;
    public LiveCanaryDeviceState State { get; } = PortableLivePowerPolicy.ValidateDevice(
        "Test GPU", DeviceFingerprint.FromStableIdentifier("isolated-test-gpu"), current, 450_000, 150_000, 600_000);
    public Task<uint> ReadPowerLimitMilliwattsAsync(CancellationToken cancellationToken = default) => Task.FromResult(Current);
    public Task SetPowerLimitMilliwattsAsync(uint value, CancellationToken cancellationToken = default)
    { Current = value; Writes++; return Task.CompletedTask; }
    public void Dispose() { }
}
sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan duration) => _now += duration;
}
sealed class FakeProcessorAccess(ProcessorMaximumStateSnapshot original, Guid active) : IProcessorRestorationAccess
{
    private ProcessorMaximumStateSnapshot _stored = original with { AcMaximumPercent = 80, DcMaximumPercent = 60 };
    public Guid Active { get; private set; } = active;
    public int Activations { get; private set; }
    public Guid GetActiveScheme() => Active;
    public void WriteValues(ProcessorMaximumStateSnapshot snapshot) => _stored = snapshot;
    public void Activate(Guid schemeId) { Active = schemeId; Activations++; }
    public ProcessorMaximumStateSnapshot Read(Guid schemeId) => _stored;
}
sealed class TestDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AFKPowerSaver-Lifecycle-" + Guid.NewGuid().ToString("N"));
    public string Journal => System.IO.Path.Combine(Path, "recovery.json");
    public TestDirectory() => Directory.CreateDirectory(Path);
    public void Dispose() => Directory.Delete(Path, recursive: true);
}
