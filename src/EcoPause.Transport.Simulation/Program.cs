using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using EcoPause.Core.Activation;
using EcoPause.Core.Recovery;
using EcoPause.HardwareHost.Protocol;
using EcoPause.HardwareHost.Transport;

namespace EcoPause.Transport.Simulation;

[SupportedOSPlatform("windows")]
internal static class Program
{
    private static readonly HardwareHostProcessOutcome[] ExpectedOutcomes =
    [
        HardwareHostProcessOutcome.Executed,
        HardwareHostProcessOutcome.RejectedReplay,
        HardwareHostProcessOutcome.RejectedAuthentication,
        HardwareHostProcessOutcome.Executed
    ];

    public static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("This named-pipe simulation requires Windows.");
            return 2;
        }

        if (IsElevated())
        {
            Console.Error.WriteLine("Safety stop: run this simulation from a normal, non-administrator terminal.");
            return 3;
        }

        try
        {
            return args.Length switch
            {
                0 => await RunOrchestratorAsync(),
                3 when args[0] == "--host" => await RunHostAsync(args[1], args[2]),
                2 when args[0] == "--client" => await RunClientAsync(args[1]),
                _ => ShowUsage()
            };
        }
#pragma warning disable CA1031 // The executable boundary reports a controlled failure code.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            Console.Error.WriteLine($"Transport simulation failed safely: {exception.Message}");
            return 1;
        }
    }

    private static async Task<int> RunOrchestratorAsync()
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The simulation executable path is unavailable.");
        var pipeName = HardwareHostPipeName.Create();
        var stateDirectory = Path.GetFullPath(Path.Combine(
            "artifacts",
            "transport",
            Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(stateDirectory);

        Console.WriteLine("EcoPause Named-Pipe Safety Simulation 0.7");
        Console.WriteLine("Privilege: normal user (not elevated)");
        Console.WriteLine("Hardware: in-memory fake only");
        Console.WriteLine("Starting separate host and client processes...");

        using var host = StartChild(executable, "--host", pipeName, stateDirectory);
        using var client = StartChild(executable, "--client", pipeName);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var hostOutput = host.StandardOutput.ReadToEndAsync(timeout.Token);
        var hostError = host.StandardError.ReadToEndAsync(timeout.Token);
        var clientOutput = client.StandardOutput.ReadToEndAsync(timeout.Token);
        var clientError = client.StandardError.ReadToEndAsync(timeout.Token);

        try
        {
            await Task.WhenAll(
                host.WaitForExitAsync(timeout.Token),
                client.WaitForExitAsync(timeout.Token));
        }
        catch (OperationCanceledException)
        {
            StopChild(host);
            StopChild(client);
            Console.Error.WriteLine("Safety stop: the process simulation exceeded 20 seconds.");
            return 1;
        }

        Console.Write(await clientOutput);
        Console.Write(await hostOutput);
        WriteChildError("client", await clientError);
        WriteChildError("host", await hostError);

        var passed = host.ExitCode == 0 && client.ExitCode == 0;
        Console.WriteLine($"Separate-process transport: {(passed ? "PASS" : "FAIL")}");
        Console.WriteLine($"No NVIDIA code referenced: {(!ReferencesNvidiaAssembly() ? "PASS" : "FAIL")}");
        return passed && !ReferencesNvidiaAssembly() ? 0 : 1;
    }

    private static async Task<int> RunHostAsync(string pipeName, string stateDirectory)
    {
        HardwareHostPipeName.Validate(pipeName);
        var journalPath = Path.Combine(Path.GetFullPath(stateDirectory), "recovery.json");
        var fingerprint = DeviceFingerprint.FromStableIdentifier("TRANSPORT-SIMULATED-GPU");
        var snapshot = RecoverySnapshot.Prepare(
            DateTimeOffset.UtcNow,
            "Simulation",
            "InMemory",
            fingerprint,
            originalPowerLimitMilliwatts: 450_000,
            targetPowerLimitMilliwatts: 400_000,
            minimumPowerLimitMilliwatts: 150_000,
            maximumPowerLimitMilliwatts: 600_000);
        var store = new FileRecoveryJournalStore();
        await store.SaveAsync(journalPath, snapshot);

        var controller = new SimulatedPowerController(fingerprint, 450_000);
        var registry = new FileHardwareHostTargetRegistry(store, controller, journalPath);
        using var session = HardwareHostSessionFactory.CreateTransportSession();
        var processor = new HardwareHostCommandProcessor(
            session.Host,
            registry,
            new GpuActivationEngine(store));

        await using var server = HardwareHostNamedPipe.CreateServer(pipeName);
        await HardwareHostNamedPipe.WaitForConnectionAsync(server);
        await BoundedJsonPipe.WriteAsync(
            server,
            new HardwareHostTransportBootstrap(session.Bootstrap, snapshot.SnapshotId));
        session.Bootstrap.Dispose();

        var actualOutcomes = new List<HardwareHostProcessOutcome>(ExpectedOutcomes.Length);
        for (var index = 0; index < ExpectedOutcomes.Length; index++)
        {
            var envelope = await BoundedJsonPipe.ReadAsync<HardwareHostCommandEnvelope>(server);
            var result = await processor.ProcessAsync(envelope);
            actualOutcomes.Add(result.Outcome);
            var wireResult = new HardwareHostWireResult(
                result.Outcome,
                result.Execution?.Outcome.ToString(),
                Describe(result.Outcome));
            await BoundedJsonPipe.WriteAsync(server, wireResult);
        }

        var journal = await store.LoadAsync(journalPath);
        var safeFinalState = controller.CurrentLimitMilliwatts == 450_000 &&
            journal.Stage == RecoveryStage.Restored;
        var passed = actualOutcomes.SequenceEqual(ExpectedOutcomes) && safeFinalState;

        Console.WriteLine($"Host access boundary (current user only): PASS");
        Console.WriteLine($"Final simulated limit: {controller.CurrentLimitMilliwatts / 1000} W");
        Console.WriteLine($"Recovery journal closed: {journal.Stage == RecoveryStage.Restored}");
        Console.WriteLine($"Safe final state: {safeFinalState}");
        return passed ? 0 : 1;
    }

    private static async Task<int> RunClientAsync(string pipeName)
    {
        HardwareHostPipeName.Validate(pipeName);
        await using var clientPipe = await HardwareHostNamedPipe.ConnectClientAsync(pipeName);
        var bootstrap = await BoundedJsonPipe.ReadAsync<HardwareHostTransportBootstrap>(clientPipe);
        using (bootstrap.Session)
        using (var clientSession = HardwareHostSessionFactory.ImportClientSession(bootstrap.Session))
        {
            var activate = clientSession.CreateCommand(
                HardwareHostOperation.ActivatePrepared,
                bootstrap.SnapshotId);
            var activation = await SendAsync(clientPipe, "Authenticated activation", activate);
            var replay = await SendAsync(clientPipe, "Replay attempt", activate);

            var restore = clientSession.CreateCommand(
                HardwareHostOperation.RestorePending,
                bootstrap.SnapshotId);
            var tampered = restore with
            {
                Command = restore.Command with { SnapshotId = Guid.NewGuid() }
            };
            var rejected = await SendAsync(clientPipe, "Tampered command", tampered);
            var restoration = await SendAsync(clientPipe, "Authenticated restoration", restore);

            var actualOutcomes = new[]
            {
                activation.Outcome,
                replay.Outcome,
                rejected.Outcome,
                restoration.Outcome
            };
            var executionOutcomesValid =
                activation.ExecutionOutcome == nameof(ActivationOutcome.Activated) &&
                restoration.ExecutionOutcome == nameof(ActivationOutcome.Restored);
            return actualOutcomes.SequenceEqual(ExpectedOutcomes) && executionOutcomesValid ? 0 : 1;
        }
    }

    private static async Task<HardwareHostWireResult> SendAsync(
        Stream pipe,
        string label,
        HardwareHostCommandEnvelope envelope)
    {
        await BoundedJsonPipe.WriteAsync(pipe, envelope);
        var result = await BoundedJsonPipe.ReadAsync<HardwareHostWireResult>(pipe);
        var execution = result.ExecutionOutcome is null ? string.Empty : $" / {result.ExecutionOutcome}";
        Console.WriteLine($"{label}: {result.Outcome}{execution}");
        return result;
    }

    private static Process StartChild(string executable, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("A simulation child process could not be started.");
    }

    private static void StopChild(Process child)
    {
        if (!child.HasExited)
        {
            child.Kill(entireProcessTree: true);
        }
    }

    private static void WriteChildError(string childName, string error)
    {
        if (!string.IsNullOrWhiteSpace(error))
        {
            Console.Error.WriteLine($"{childName}: {error.Trim()}");
        }
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static bool ReferencesNvidiaAssembly() =>
        AppDomain.CurrentDomain.GetAssemblies().Any(
            assembly => assembly.GetName().Name?.Contains("Nvidia", StringComparison.OrdinalIgnoreCase) == true);

    private static string Describe(HardwareHostProcessOutcome outcome) => outcome switch
    {
        HardwareHostProcessOutcome.Executed => "Authenticated command completed.",
        HardwareHostProcessOutcome.RejectedReplay => "One-use command rejected.",
        HardwareHostProcessOutcome.RejectedAuthentication => "Authentication failed.",
        _ => "Command did not complete."
    };

    private static int ShowUsage()
    {
        Console.Error.WriteLine("Run without arguments to start the safe two-process simulation.");
        return 2;
    }
}

internal sealed class SimulatedPowerController(
    DeviceFingerprint deviceFingerprint,
    uint initialLimitMilliwatts) : IGpuPowerController
{
    public DeviceFingerprint DeviceFingerprint { get; } = deviceFingerprint;

    public uint CurrentLimitMilliwatts { get; private set; } = initialLimitMilliwatts;

    public Task<uint> ReadPowerLimitMilliwattsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CurrentLimitMilliwatts);
    }

    public Task SetPowerLimitMilliwattsAsync(
        uint powerLimitMilliwatts,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CurrentLimitMilliwatts = powerLimitMilliwatts;
        return Task.CompletedTask;
    }
}

internal sealed class FileHardwareHostTargetRegistry(
    IRecoveryJournalStore store,
    IGpuPowerController controller,
    string journalPath) : IHardwareHostTargetRegistry
{
    public async Task<HardwareHostTarget?> ResolveAsync(
        Guid snapshotId,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await store.LoadAsync(journalPath, cancellationToken);
        return snapshot.SnapshotId == snapshotId
            ? new HardwareHostTarget(journalPath, snapshot, controller)
            : null;
    }
}
