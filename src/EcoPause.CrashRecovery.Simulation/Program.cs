using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using EcoPause.Core.Activation;
using EcoPause.CrashRecovery.SimulationModel;
using EcoPause.HardwareHost.Protocol;
using EcoPause.HardwareHost.Transport;

namespace EcoPause.CrashRecovery.Simulation;

[SupportedOSPlatform("windows")]
internal static class Program
{
    private const int UacCancelledError = 1223;
    private const uint DllInitializationFailed = 0xC0000142;
    private const int IntentionalCrashExitCode = 91;
    private const string ToggleReceiptFileName = "persistent-fake-toggle.txt";

    public static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("This crash-recovery simulation requires Windows.");
            return 2;
        }

        if (IsElevated())
        {
            Console.Error.WriteLine("Safety stop: launch this from a normal, non-administrator terminal.");
            return 3;
        }

        if (args.Length == 1 && args[0] == "--toggle-status")
        {
            ShowToggleReceipt();
            return 0;
        }

        Guid runId;
        PersistentToggleProfile? requestedProfile = null;
        var recoveryOnly = false;
        var toggleOnly = false;
        if (args.Length == 0)
        {
            runId = Guid.NewGuid();
        }
        else if (args.Length == 2 && args[0] == "--recover-run")
        {
            runId = CrashSimulationPaths.ParseRunId(args[1]);
            recoveryOnly = true;
        }
        else if (args.Length == 1 && args[0] == "--toggle")
        {
            requestedProfile = PersistentToggleProfiles.Default;
            runId = requestedProfile.RunId;
            toggleOnly = true;
        }
        else if (args.Length == 3 && args[0] == "--toggle" && args[1] == "--profile")
        {
            try
            {
                requestedProfile = PersistentToggleProfiles.ResolveId(args[2]);
            }
            catch (ArgumentException exception)
            {
                Console.Error.WriteLine(exception.Message);
                return 2;
            }

            runId = requestedProfile.RunId;
            toggleOnly = true;
        }
        else
        {
            Console.Error.WriteLine(
                "Run without arguments, use --recover-run with a saved run ID, or use --toggle.");
            return 2;
        }

        var crashConfirmed = recoveryOnly;
        Console.WriteLine(toggleOnly
            ? "EcoPause Persistent Fake Toggle Simulation 1.5"
            : "EcoPause Elevated Crash-Recovery Simulation 0.9");
        Console.WriteLine("Launcher privilege: normal user");
        Console.WriteLine("Hardware: persistent file-backed fake only");
        Console.WriteLine(toggleOnly
            ? $"Requested fake target profile: {requestedProfile!.Id}"
            : $"Recovery run ID: {runId:N}");

        try
        {
            if (toggleOnly)
            {
                Console.WriteLine("UAC prompt: approve one authenticated fake toggle.");
                var togglePassed = await RunTogglePhaseAsync(runId, requestedProfile!);
                return togglePassed ? 0 : 1;
            }

            if (!recoveryOnly)
            {
                Console.WriteLine("UAC prompt 1 of 2: approve intentional helper termination.");
                await RunCrashPhaseAsync(runId);
                crashConfirmed = true;
            }

            Console.WriteLine("UAC prompt 2 of 2: approve fresh-helper recovery.");
            var passed = await RunRecoveryPhaseAsync(runId);
            return passed ? 0 : 1;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == UacCancelledError)
        {
            Console.WriteLine("UAC was declined. No real hardware was changed.");
            if (crashConfirmed)
            {
                ShowRecoveryCommand(runId);
            }

            return 4;
        }
        catch (Win32Exception exception) when ((uint)exception.NativeErrorCode == DllInitializationFailed)
        {
            Console.WriteLine("This hosted terminal cannot create a UAC process. Use Visual Studio's normal terminal.");
            if (crashConfirmed)
            {
                ShowRecoveryCommand(runId);
            }

            return 5;
        }
#pragma warning disable CA1031 // The launcher reports a controlled failure and a recovery command.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            Console.Error.WriteLine($"Crash-recovery simulation failed safely: {exception.Message}");
            if (crashConfirmed)
            {
                ShowRecoveryCommand(runId);
            }

            return 1;
        }
    }

    private static async Task<bool> RunTogglePhaseAsync(
        Guid runId,
        PersistentToggleProfile requestedProfile)
    {
        ArgumentNullException.ThrowIfNull(requestedProfile);
        var pipeName = HardwareHostPipeName.Create();
        await using var server = HardwareHostNamedPipe.CreateServer(pipeName);
        using var helper = StartElevatedHelper(pipeName, "toggle", runId);
        await ConnectAndVerifyAsync(server, helper);
        Console.WriteLine("Mutual process-ID verification: PASS");
        await RequireStartupAsync(server, helper);

        var bootstrap = await BoundedJsonPipe.ReadAsync<HardwareHostPersistentToggleBootstrap>(server);
        if (bootstrap.Operation is not (HardwareHostOperation.ActivatePrepared or HardwareHostOperation.RestorePending))
        {
            bootstrap.Session.Dispose();
            throw new InvalidOperationException("The helper selected an invalid toggle operation.");
        }

        HardwareHostWireResult toggle;
        using (bootstrap.Session)
        using (var session = HardwareHostSessionFactory.ImportClientSession(bootstrap.Session))
        {
            var command = session.CreateCommand(bootstrap.Operation, bootstrap.SnapshotId);
            toggle = await SendAsync(server, "Authenticated toggle", command);
        }

        var summary = await BoundedJsonPipe.ReadAsync<HardwareHostPersistentToggleSummary>(server);
        using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await helper.WaitForExitAsync(exitTimeout.Token);

        var resolvedProfile = PersistentToggleProfiles.ResolveId(summary.ProfileId);
        var profileValid = resolvedProfile.TargetLimitMilliwatts == summary.TargetLimitMilliwatts;

        var paused = bootstrap.Operation == HardwareHostOperation.ActivatePrepared &&
            toggle.Outcome == HardwareHostProcessOutcome.Executed &&
            toggle.ExecutionOutcome == nameof(ActivationOutcome.Activated) &&
            summary.ToggleState == "Paused" &&
            resolvedProfile.Id == requestedProfile.Id &&
            summary.FinalLimitMilliwatts == resolvedProfile.TargetLimitMilliwatts &&
            summary.RecoveryStage == "Applied" &&
            !summary.SimulationArtifactsCleaned;
        var restored = bootstrap.Operation == HardwareHostOperation.RestorePending &&
            toggle.Outcome == HardwareHostProcessOutcome.Executed &&
            toggle.ExecutionOutcome is nameof(ActivationOutcome.Restored) or nameof(ActivationOutcome.AlreadyRestored) &&
            summary.ToggleState == "Restored" &&
            summary.FinalLimitMilliwatts == CrashSimulationProfile.OriginalLimitMilliwatts &&
            summary.RecoveryStage == "Restored" &&
            summary.SimulationArtifactsCleaned;
        var passed = helper.ExitCode == 0 && profileValid && summary.PeerVerified &&
            summary.SafeFinalState && (paused || restored);
        if (passed)
        {
            SaveToggleReceipt(summary.ToggleState, resolvedProfile.Id);
        }

        Console.WriteLine($"Toggle helper exit: {helper.ExitCode}");
        Console.WriteLine($"Persistent fake state: {summary.ToggleState}");
        Console.WriteLine($"Fake target profile: {resolvedProfile.Id}");
        Console.WriteLine($"Configured fake target: {resolvedProfile.TargetLimitMilliwatts / 1000} W");
        Console.WriteLine($"Final persistent fake limit: {summary.FinalLimitMilliwatts / 1000} W");
        Console.WriteLine($"Recovery journal stage: {summary.RecoveryStage}");
        Console.WriteLine($"Protected simulation files cleaned: {summary.SimulationArtifactsCleaned}");
        Console.WriteLine($"Persistent fake toggle: {(passed ? "PASS" : "FAIL")}");
        Console.WriteLine("Real NVIDIA access: NONE");
        return passed;
    }

    private static async Task RunCrashPhaseAsync(Guid runId)
    {
        var pipeName = HardwareHostPipeName.Create();
        await using var server = HardwareHostNamedPipe.CreateServer(pipeName);
        using var helper = StartElevatedHelper(pipeName, "activate-crash", runId);
        await ConnectAndVerifyAsync(server, helper);
        await RequireStartupAsync(server, helper);

        var bootstrap = await BoundedJsonPipe.ReadAsync<HardwareHostTransportBootstrap>(server);
        using (bootstrap.Session)
        using (var session = HardwareHostSessionFactory.ImportClientSession(bootstrap.Session))
        {
            var command = session.CreateCommand(
                HardwareHostOperation.ActivatePrepared,
                bootstrap.SnapshotId);
            var result = await SendAsync(server, "Persisted fake activation", command);
            if (result.Outcome != HardwareHostProcessOutcome.Executed ||
                result.ExecutionOutcome != nameof(ActivationOutcome.Activated))
            {
                throw new InvalidOperationException("The fake activation did not complete before termination.");
            }
        }

        using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await helper.WaitForExitAsync(exitTimeout.Token);
        if (helper.ExitCode != IntentionalCrashExitCode)
        {
            throw new InvalidOperationException(
                $"The first helper exited with {helper.ExitCode}, not the intentional crash code {IntentionalCrashExitCode}.");
        }

        Console.WriteLine($"Intentional helper termination ({IntentionalCrashExitCode}): PASS");
        Console.WriteLine("Pending recovery now survives outside the terminated process.");
    }

    private static async Task<bool> RunRecoveryPhaseAsync(Guid runId)
    {
        var pipeName = HardwareHostPipeName.Create();
        await using var server = HardwareHostNamedPipe.CreateServer(pipeName);
        using var helper = StartElevatedHelper(pipeName, "recover", runId);
        await ConnectAndVerifyAsync(server, helper);
        await RequireStartupAsync(server, helper);

        var bootstrap = await BoundedJsonPipe.ReadAsync<HardwareHostTransportBootstrap>(server);
        HardwareHostWireResult restoration;
        using (bootstrap.Session)
        using (var session = HardwareHostSessionFactory.ImportClientSession(bootstrap.Session))
        {
            var command = session.CreateCommand(
                HardwareHostOperation.RestorePending,
                bootstrap.SnapshotId);
            restoration = await SendAsync(server, "Fresh-helper restoration", command);
        }

        var summary = await BoundedJsonPipe.ReadAsync<HardwareHostCrashRecoverySummary>(server);
        using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await helper.WaitForExitAsync(exitTimeout.Token);

        var passed = helper.ExitCode == 0 &&
            restoration.Outcome == HardwareHostProcessOutcome.Executed &&
            restoration.ExecutionOutcome == nameof(ActivationOutcome.Restored) &&
            summary.PeerVerified &&
            summary.FinalLimitMilliwatts == CrashSimulationProfile.OriginalLimitMilliwatts &&
            summary.RecoveryStage == "Restored" &&
            summary.SafeFinalState &&
            summary.SimulationArtifactsCleaned;

        Console.WriteLine($"Recovery helper exit: {helper.ExitCode}");
        Console.WriteLine($"Final persistent fake limit: {summary.FinalLimitMilliwatts / 1000} W");
        Console.WriteLine($"Recovery journal closed: {summary.RecoveryStage == "Restored"}");
        Console.WriteLine($"Protected simulation files cleaned: {summary.SimulationArtifactsCleaned}");
        Console.WriteLine($"Elevated crash recovery: {(passed ? "PASS" : "FAIL")}");
        Console.WriteLine("Real NVIDIA access: NONE");
        return passed;
    }

    private static async Task ConnectAndVerifyAsync(
        System.IO.Pipes.NamedPipeServerStream server,
        Process helper)
    {
        await HardwareHostNamedPipe.WaitForConnectionAsync(server, TimeSpan.FromMinutes(2));
        HardwareHostPipePeer.VerifyClientProcess(server, helper.Id);
        Console.WriteLine($"Helper PID {helper.Id} verified by Windows: PASS");
    }

    private static async Task RequireStartupAsync(Stream pipe, Process helper)
    {
        var peerStatus = await ReadStatusAsync(pipe, helper);
        RequireStatus(peerStatus, "peer-verification");
        var initializationStatus = await ReadStatusAsync(pipe, helper);
        RequireStatus(initializationStatus, "host-initialization");
        Console.WriteLine("Elevated helper startup: PASS");
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
        string pipeName,
        string mode,
        Guid runId)
    {
        var startInfo = new ProcessStartInfo(FindHelperExecutable())
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("--pipe");
        startInfo.ArgumentList.Add(pipeName);
        startInfo.ArgumentList.Add("--parent-pid");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--scenario");
        startInfo.ArgumentList.Add("crash-recovery");
        startInfo.ArgumentList.Add("--mode");
        startInfo.ArgumentList.Add(mode);
        startInfo.ArgumentList.Add("--run-id");
        startInfo.ArgumentList.Add(runId.ToString("N"));

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows did not start the elevated helper.");
    }

    private static string FindHelperExecutable()
    {
        var colocated = Path.Combine(AppContext.BaseDirectory, "EcoPause.ElevatedHost.Simulation.exe");
        if (File.Exists(colocated))
        {
            return colocated;
        }

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
            throw new FileNotFoundException("Build the complete solution before starting recovery.", helperPath);
        }

        return helperPath;
    }

    private static void ShowRecoveryCommand(Guid runId)
    {
        Console.WriteLine("Persistent fake recovery remains pending. Resume safely with:");
        Console.WriteLine($"dotnet run --project src/EcoPause.CrashRecovery.Simulation -- --recover-run {runId:N}");
    }

    private static void ShowToggleReceipt()
    {
        var receipt = ReadToggleReceipt();
        Console.WriteLine("EcoPause Persistent Fake Toggle Receipt 1.5");
        Console.WriteLine($"Receipt state: {receipt.State}");
        Console.WriteLine($"Receipt profile: {receipt.ProfileId}");
        Console.WriteLine("Receipt authority: display-only");
    }

    private static ToggleReceipt ReadToggleReceipt()
    {
        try
        {
            var path = GetToggleReceiptPath();
            if (!File.Exists(path) || new FileInfo(path).Length is <= 0 or > 64)
            {
                return ToggleReceipt.Unknown;
            }

            var value = File.ReadAllText(path).Trim();
            var parts = value.Split('|');
            if (parts.Length != 2 || parts[0] is not ("Paused" or "Restored"))
            {
                return ToggleReceipt.Unknown;
            }

            var profile = PersistentToggleProfiles.ResolveId(parts[1]);
            return new ToggleReceipt(parts[0], profile.Id);
        }
#pragma warning disable CA1031 // A local display hint must never prevent safe helper-resolved toggling.
        catch (Exception)
#pragma warning restore CA1031
        {
            return ToggleReceipt.Unknown;
        }
    }

    private static void SaveToggleReceipt(string state, string profileId)
    {
        if (state is not ("Paused" or "Restored"))
        {
            throw new ArgumentException("The toggle receipt state is invalid.", nameof(state));
        }

        var profile = PersistentToggleProfiles.ResolveId(profileId);

        var path = GetToggleReceiptPath();
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("The toggle receipt directory is unavailable.");
        Directory.CreateDirectory(directory);
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, $"{state}|{profile.Id}");
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static string GetToggleReceiptPath()
    {
        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);
        return Path.GetFullPath(Path.Combine(localApplicationData, "EcoPause", ToggleReceiptFileName));
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private sealed record ToggleReceipt(string State, string ProfileId)
    {
        public static ToggleReceipt Unknown { get; } = new("Unknown", "Unknown");
    }
}
