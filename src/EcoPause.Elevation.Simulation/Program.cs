using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using EcoPause.Core.Activation;
using EcoPause.HardwareHost.Protocol;
using EcoPause.HardwareHost.Transport;

namespace EcoPause.Elevation.Simulation;

[SupportedOSPlatform("windows")]
internal static class Program
{
    private const int UacCancelledError = 1223;
    private const uint DllInitializationFailed = 0xC0000142;

    private static readonly HardwareHostProcessOutcome[] ExpectedOutcomes =
    [
        HardwareHostProcessOutcome.Executed,
        HardwareHostProcessOutcome.RejectedReplay,
        HardwareHostProcessOutcome.RejectedAuthentication,
        HardwareHostProcessOutcome.Executed
    ];

    public static async Task<int> Main()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("This UAC simulation requires Windows.");
            return 2;
        }

        if (IsElevated())
        {
            Console.Error.WriteLine("Safety stop: launch this from a normal, non-administrator terminal.");
            return 3;
        }

        Console.WriteLine("EcoPause Elevation Safety Simulation 0.8");
        Console.WriteLine("Launcher privilege: normal user");
        Console.WriteLine("Hardware: in-memory fake only");
        Console.WriteLine("Windows should now ask permission to run the unsigned simulation helper.");

        try
        {
            return await RunAsync();
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == UacCancelledError)
        {
            Console.WriteLine("UAC was declined. No helper commands ran and no hardware was changed.");
            return 4;
        }
        catch (Win32Exception exception) when ((uint)exception.NativeErrorCode == DllInitializationFailed)
        {
            Console.WriteLine(
                "Windows could not create a UAC process from this hosted terminal. " +
                "Run the same command in Visual Studio's normal terminal.");
            Console.WriteLine("No helper commands ran and no hardware was changed.");
            return 5;
        }
#pragma warning disable CA1031 // The launcher reports a controlled failure code.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            Console.Error.WriteLine($"Elevation simulation failed safely: {exception.Message}");
            return 1;
        }
    }

    private static async Task<int> RunAsync()
    {
        var helperPath = FindHelperExecutable();
        var pipeName = HardwareHostPipeName.Create();
        await using var server = HardwareHostNamedPipe.CreateServer(pipeName);
        using var helper = StartElevatedHelper(helperPath, pipeName, Environment.ProcessId);

        await HardwareHostNamedPipe.WaitForConnectionAsync(
            server,
            TimeSpan.FromMinutes(2));
        HardwareHostPipePeer.VerifyClientProcess(server, helper.Id);
        Console.WriteLine("Mutual process-ID verification: PASS");

        var peerStatus = await ReadStatusAsync(server, helper);
        RequireStatus(peerStatus, "peer-verification");
        Console.WriteLine("Elevated helper verified launcher PID: PASS");

        var initializationStatus = await ReadStatusAsync(server, helper);
        RequireStatus(initializationStatus, "host-initialization");
        Console.WriteLine("Elevated helper initialization: PASS");
        var bootstrap = await BoundedJsonPipe.ReadAsync<HardwareHostTransportBootstrap>(server);
        HardwareHostWireResult[] results;
        using (bootstrap.Session)
        using (var session = HardwareHostSessionFactory.ImportClientSession(bootstrap.Session))
        {
            var activate = session.CreateCommand(
                HardwareHostOperation.ActivatePrepared,
                bootstrap.SnapshotId);
            var activation = await SendAsync(server, "Authenticated activation", activate);
            var replay = await SendAsync(server, "Replay attempt", activate);

            var restore = session.CreateCommand(
                HardwareHostOperation.RestorePending,
                bootstrap.SnapshotId);
            var tampered = restore with
            {
                Command = restore.Command with { SnapshotId = Guid.NewGuid() }
            };
            var rejected = await SendAsync(server, "Tampered command", tampered);
            var restoration = await SendAsync(server, "Authenticated restoration", restore);
            results = [activation, replay, rejected, restoration];
        }

        var summary = await BoundedJsonPipe.ReadAsync<HardwareHostElevationSummary>(server);
        using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await helper.WaitForExitAsync(exitTimeout.Token);

        var outcomesValid = results.Select(result => result.Outcome).SequenceEqual(ExpectedOutcomes);
        var executionValid =
            results[0].ExecutionOutcome == nameof(ActivationOutcome.Activated) &&
            results[3].ExecutionOutcome == nameof(ActivationOutcome.Restored);
        var passed = helper.ExitCode == 0 &&
            outcomesValid &&
            executionValid &&
            summary.PeerVerified &&
            summary.SafeFinalState &&
            summary.FinalLimitMilliwatts == 450_000 &&
            summary.RecoveryStage == "Restored";

        Console.WriteLine($"Elevated helper exit: {helper.ExitCode}");
        Console.WriteLine($"Final simulated limit: {summary.FinalLimitMilliwatts / 1000} W");
        Console.WriteLine($"Recovery journal closed: {summary.RecoveryStage == "Restored"}");
        Console.WriteLine($"Safe elevated lifecycle: {(passed ? "PASS" : "FAIL")}");
        Console.WriteLine("Real NVIDIA access: NONE");
        return passed ? 0 : 1;
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

    private static async Task<HardwareHostElevationStatus> ReadStatusAsync(
        Stream pipe,
        Process helper)
    {
        try
        {
            return await BoundedJsonPipe.ReadAsync<HardwareHostElevationStatus>(pipe);
        }
        catch (HardwareHostTransportException exception)
        {
            using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await helper.WaitForExitAsync(exitTimeout.Token);
            }
            catch (OperationCanceledException)
            {
                throw new HardwareHostTransportException(
                    "The elevated helper did not provide startup status and remained running.",
                    exception);
            }

            throw new HardwareHostTransportException(
                $"The elevated helper exited with code {helper.ExitCode} before providing startup status.",
                exception);
        }
    }

    private static void RequireStatus(
        HardwareHostElevationStatus status,
        string expectedStage)
    {
        if (!status.Succeeded || !string.Equals(status.Stage, expectedStage, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Elevated helper startup stopped at '{status.Stage}': {status.Message}");
        }
    }

    private static Process StartElevatedHelper(
        string helperPath,
        string pipeName,
        int parentProcessId)
    {
        var startInfo = new ProcessStartInfo(helperPath)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("--pipe");
        startInfo.ArgumentList.Add(pipeName);
        startInfo.ArgumentList.Add("--parent-pid");
        startInfo.ArgumentList.Add(parentProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows did not start the elevated helper.");
    }

    private static string FindHelperExecutable()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EcoPause.sln")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new FileNotFoundException("The EcoPause solution directory could not be located.");
        }

#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var helperPath = Path.Combine(
            directory.FullName,
            "src",
            "EcoPause.ElevatedHost.Simulation",
            "bin",
            configuration,
            "net10.0",
            "EcoPause.ElevatedHost.Simulation.exe");
        if (!File.Exists(helperPath))
        {
            throw new FileNotFoundException("Build the complete solution before starting the UAC simulation.", helperPath);
        }

        return helperPath;
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
