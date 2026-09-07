using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Principal;
using EcoPause.Hardware.Nvidia;
using EcoPause.HardwareHost.Protocol;
using EcoPause.HardwareHost.Transport;
using EcoPause.LiveCanary.Model;

namespace EcoPause.LiveCanary;

[SupportedOSPlatform("windows")]
internal static class Program
{
    private const int UacCancelledError = 1223;
    private const uint DllInitializationFailed = 0xC0000142;

    public static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("The live GPU canary requires Windows.");
            return 2;
        }

        if (IsElevated())
        {
            Console.Error.WriteLine("Safety stop: launch the canary from a normal, non-administrator terminal.");
            return 3;
        }

        if (args.Length != 1 || args[0] is not "--preflight" and not "--run-live-canary")
        {
            PrintHelp();
            return args.Length == 1 && args[0] == "--help" ? 0 : 2;
        }

        LiveCanaryDeviceState preflight;
        try
        {
            preflight = await RunReadOnlyPreflightAsync();
        }
#pragma warning disable CA1031 // The normal-user launcher reports bounded preflight failures.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            Console.Error.WriteLine($"Live-canary preflight failed safely: {exception.Message}");
            Console.WriteLine("No UAC helper was started and no GPU setting was changed.");
            return 1;
        }

        PrintPreflight(preflight);
        if (args[0] == "--preflight")
        {
            Console.WriteLine();
            Console.WriteLine("Preflight only: no UAC prompt appeared and no GPU setting was changed.");
            return 0;
        }

        Console.WriteLine();
        Console.WriteLine("LIVE GPU WRITE WARNING");
        Console.WriteLine("Close games, renderers, AI workloads, and other important GPU work before continuing.");
        Console.WriteLine("The helper will choose recovery first, or apply exactly 400 W for five seconds and restore 450 W.");
        Console.WriteLine("Independent administrator rollback: nvidia-smi --power-limit=450");
        Console.WriteLine("A reboot or NVIDIA driver reload is the final fallback because this limit is not persistent.");
        Console.WriteLine();
        Console.WriteLine($"Type exactly: {LiveCanaryPolicy.ConfirmationPhrase}");
        Console.Write("> ");
        var confirmation = Console.ReadLine();
        if (!string.Equals(confirmation, LiveCanaryPolicy.ConfirmationPhrase, StringComparison.Ordinal))
        {
            Console.WriteLine("Confirmation did not match. No UAC helper was started and no GPU setting was changed.");
            return 4;
        }

        try
        {
            return await RunElevatedCanaryAsync(preflight);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == UacCancelledError)
        {
            Console.WriteLine("UAC was declined. The helper did not run and no GPU setting was changed.");
            return 5;
        }
        catch (Win32Exception exception) when ((uint)exception.NativeErrorCode == DllInitializationFailed)
        {
            Console.WriteLine("Windows could not create the UAC helper from this terminal.");
            Console.WriteLine("Run the same command in Visual Studio's normal terminal.");
            return 6;
        }
#pragma warning disable CA1031 // The launcher reports a controlled failure and preserves recovery guidance.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            Console.Error.WriteLine($"Live canary did not complete cleanly: {exception.Message}");
            Console.Error.WriteLine("Run this same command again to prioritize protected-journal recovery.");
            Console.Error.WriteLine("Independent administrator rollback: nvidia-smi --power-limit=450");
            return 1;
        }
    }

    private static async Task<LiveCanaryDeviceState> RunReadOnlyPreflightAsync()
    {
        var report = await new NvidiaGpuProbe().ProbeAsync();
        return LiveCanaryPolicy.ValidateProbeReport(report);
    }

    private static void PrintPreflight(LiveCanaryDeviceState device)
    {
        Console.WriteLine("EcoPause Live GPU Canary Preflight 1.6");
        Console.WriteLine("Launcher privilege: normal user");
        Console.WriteLine("Desktop manual live toggle: separate fixed 400/450 W path");
        Console.WriteLine("Desktop live triggers: separate qualified toggle path");
        Console.WriteLine("Idle timer: PREVIEW ONLY");
        Console.WriteLine($"GPU: {device.Name}");
        Console.WriteLine($"Current limit: {device.CurrentLimitMilliwatts / 1000m:0.###} W");
        Console.WriteLine($"Default limit: {device.DefaultLimitMilliwatts / 1000m:0.###} W");
        Console.WriteLine($"Permitted range: {device.MinimumLimitMilliwatts / 1000m:0.###}-{device.MaximumLimitMilliwatts / 1000m:0.###} W");
        Console.WriteLine($"Fixed canary target: {LiveCanaryPolicy.TargetLimitMilliwatts / 1000m:0.###} W");
        Console.WriteLine($"Preflight state: {(device.CanStartNewCanary ? "READY" : "RECOVERY MAY BE PENDING")}");
    }

    private static async Task<int> RunElevatedCanaryAsync(LiveCanaryDeviceState preflight)
    {
        var helperPath = FindHelperExecutable();
        var pipeName = HardwareHostPipeName.Create();
        await using var server = HardwareHostNamedPipe.CreateServer(pipeName);
        using var helper = StartElevatedHelper(helperPath, pipeName, Environment.ProcessId);

        Console.WriteLine("Approve the UAC prompt for the isolated live-canary helper.");
        await HardwareHostNamedPipe.WaitForConnectionAsync(server, TimeSpan.FromMinutes(2));
        HardwareHostPipePeer.VerifyClientProcess(server, helper.Id);
        Console.WriteLine("Helper PID verified by Windows: PASS");

        RequireStatus(await ReadStatusAsync(server, helper), "peer-verification");
        Console.WriteLine("Elevated helper verified launcher PID: PASS");
        RequireStatus(await ReadStatusAsync(server, helper), "host-initialization");
        Console.WriteLine("Elevated helper initialization: PASS");

        var bootstrap = await BoundedJsonPipe.ReadAsync<LiveCanaryBootstrap>(server);
        ValidateBootstrap(bootstrap, preflight);
        Console.WriteLine($"Helper-selected mode: {bootstrap.Mode}");
        Console.WriteLine($"Helper-selected fixed target: {bootstrap.TargetLimitMilliwatts / 1000m:0.###} W");
        Console.WriteLine($"Automatic hold: {bootstrap.HoldDurationMilliseconds / 1000m:0.###} seconds");

        HardwareHostWireResult result;
        using (bootstrap.Session)
        using (var session = HardwareHostSessionFactory.ImportClientSession(bootstrap.Session))
        {
            var command = session.CreateCommand(bootstrap.Operation, bootstrap.SnapshotId);
            await BoundedJsonPipe.WriteAsync(server, command);
            result = await BoundedJsonPipe.ReadAsync<HardwareHostWireResult>(
                server,
                TimeSpan.FromMilliseconds(LiveCanaryPolicy.ResultTimeoutMilliseconds));
        }

        var summary = await BoundedJsonPipe.ReadAsync<LiveCanarySummary>(server);
        using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await helper.WaitForExitAsync(exitTimeout.Token);

        var passed = ValidateResult(helper.ExitCode, bootstrap, result, summary);
        Console.WriteLine($"Authenticated helper operation: {result.Outcome} / {result.ExecutionOutcome ?? "None"}");
        Console.WriteLine($"{bootstrap.TargetLimitMilliwatts / 1000m:0.###} W target applied and read back: {(summary.TargetAppliedAndVerified ? "PASS" : bootstrap.Mode == LiveCanaryMode.RecoveryOnly ? "NOT REQUIRED" : "FAIL")}");
        Console.WriteLine($"450 W original restored and read back: {(summary.OriginalRestoredAndVerified ? "PASS" : "FAIL")}");
        Console.WriteLine($"Final live GPU limit: {summary.FinalLimitMilliwatts / 1000m:0.###} W");
        Console.WriteLine($"Recovery journal stage: {summary.RecoveryStage}");
        Console.WriteLine($"Protected recovery artifacts cleaned: {summary.RecoveryArtifactsCleaned}");
        Console.WriteLine($"Live GPU canary: {(passed ? "PASS" : "FAIL")}");
        Console.WriteLine("Desktop manual live toggle: separate fixed 400/450 W path");
        Console.WriteLine("Desktop live triggers: separate qualified toggle path");
        Console.WriteLine("Idle timer real-GPU access: NONE");
        return passed ? 0 : 1;
    }

    private static void ValidateBootstrap(LiveCanaryBootstrap bootstrap, LiveCanaryDeviceState preflight)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        var expectedOperation = bootstrap.Mode switch
        {
            LiveCanaryMode.Canary => HardwareHostOperation.ActivatePrepared,
            LiveCanaryMode.RecoveryOnly => HardwareHostOperation.RestorePending,
            _ => throw new InvalidOperationException("The helper selected an unknown live-canary mode.")
        };
        var selectedProfile = LivePowerProfiles.ResolveId(bootstrap.ProfileId);
        var profileMatchesMode = bootstrap.TargetLimitMilliwatts == selectedProfile.TargetLimitMilliwatts &&
            (bootstrap.Mode == LiveCanaryMode.RecoveryOnly || selectedProfile == LivePowerProfiles.Default);
        LivePowerProfiles.ValidateForDevice(selectedProfile, preflight);
        if (bootstrap.SnapshotId == Guid.Empty ||
            bootstrap.Operation != expectedOperation ||
            !profileMatchesMode ||
            bootstrap.DeviceName != preflight.Name ||
            bootstrap.DeviceFingerprintSha256 != preflight.DeviceFingerprint.Value ||
            bootstrap.OriginalLimitMilliwatts != LiveCanaryPolicy.RequiredOriginalLimitMilliwatts ||
            bootstrap.MinimumLimitMilliwatts != preflight.MinimumLimitMilliwatts ||
            bootstrap.MaximumLimitMilliwatts != preflight.MaximumLimitMilliwatts ||
            bootstrap.HoldDurationMilliseconds != LiveCanaryPolicy.HoldDurationMilliseconds)
        {
            throw new InvalidOperationException("The elevated helper's fixed canary plan did not match the read-only preflight.");
        }
    }

    private static bool ValidateResult(
        int helperExitCode,
        LiveCanaryBootstrap bootstrap,
        HardwareHostWireResult result,
        LiveCanarySummary summary)
    {
        var expectedExecution = bootstrap.Mode == LiveCanaryMode.Canary
            ? nameof(Core.Activation.ActivationOutcome.Activated)
            : result.ExecutionOutcome;
        var operationPassed = bootstrap.Mode switch
        {
            LiveCanaryMode.Canary =>
                result.Outcome == HardwareHostProcessOutcome.Executed &&
                result.ExecutionOutcome == expectedExecution &&
                summary.TargetAppliedAndVerified,
            LiveCanaryMode.RecoveryOnly =>
                result.Outcome == HardwareHostProcessOutcome.Executed &&
                result.ExecutionOutcome is nameof(Core.Activation.ActivationOutcome.Restored) or
                    nameof(Core.Activation.ActivationOutcome.AlreadyRestored),
            _ => false
        };
        return helperExitCode == 0 &&
            operationPassed &&
            summary.PeerVerified &&
            summary.Mode == bootstrap.Mode &&
            summary.DeviceName == bootstrap.DeviceName &&
            summary.OriginalRestoredAndVerified &&
            summary.FinalLimitMilliwatts == LiveCanaryPolicy.RequiredOriginalLimitMilliwatts &&
            summary.RecoveryStage == "Restored" &&
            summary.SafeFinalState &&
            summary.RecoveryArtifactsCleaned;
    }

    private static async Task<HardwareHostElevationStatus> ReadStatusAsync(Stream pipe, Process helper)
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

    private static void RequireStatus(HardwareHostElevationStatus status, string expectedStage)
    {
        if (!status.Succeeded || !string.Equals(status.Stage, expectedStage, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Elevated helper startup stopped at '{status.Stage}': {status.Message}");
        }
    }

    private static Process StartElevatedHelper(string helperPath, string pipeName, int parentProcessId)
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
        startInfo.ArgumentList.Add(parentProcessId.ToString(CultureInfo.InvariantCulture));
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows did not start the elevated live-canary helper.");
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
            "EcoPause.ElevatedHost.LiveCanary",
            "bin",
            configuration,
            "net10.0",
            "EcoPause.ElevatedHost.LiveCanary.exe");
        if (!File.Exists(helperPath))
        {
            throw new FileNotFoundException("Build the live-canary project before starting the UAC helper.", helperPath);
        }

        return helperPath;
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void PrintHelp()
    {
        Console.WriteLine("EcoPause Live GPU Canary 1.6");
        Console.WriteLine();
        Console.WriteLine("Read-only preflight:");
        Console.WriteLine("  EcoPause.LiveCanary --preflight");
        Console.WriteLine();
        Console.WriteLine("Supervised fixed 400 W -> 450 W live canary:");
        Console.WriteLine("  EcoPause.LiveCanary --run-live-canary");
        Console.WriteLine();
        Console.WriteLine("The live command still requires an exact typed confirmation and one UAC approval.");
    }
}
