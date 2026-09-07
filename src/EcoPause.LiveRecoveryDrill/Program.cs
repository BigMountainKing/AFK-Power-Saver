using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;
using EcoPause.Hardware.Abstractions;
using EcoPause.Hardware.Amd;
using EcoPause.Hardware.Nvidia;
using EcoPause.HardwareHost.Protocol;
using EcoPause.HardwareHost.Transport;
using EcoPause.LiveCanary.Model;

namespace EcoPause.LiveRecoveryDrill;

[SupportedOSPlatform("windows")]
internal static class Program
{
    private const int UacCancelledError = 1223;
    private const uint DllInitializationFailed = 0xC0000142;
    private const int MaximumDesktopCommandCharacters = 64;
    private static readonly JsonSerializerOptions DesktopJsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("The live GPU safety tools require Windows.");
            return 2;
        }

        if (args.Length == 1 && args[0] == "--desktop-live-session")
        {
            if (IsElevated())
            {
                WriteDesktopMessage(new(
                    "ready",
                    false,
                    "The normal-user session launcher unexpectedly started elevated.",
                    string.Empty,
                    string.Empty));
                return 3;
            }

            return await RunDesktopSessionLauncherAsync();
        }

        if (TryParseBrokerLaunch(args, out var brokerLaunch))
        {
            if (!IsElevated())
            {
                return 3;
            }

            return await RunDesktopSessionBrokerAsync(brokerLaunch);
        }

        if (IsElevated())
        {
            Console.Error.WriteLine("Safety stop: launch from a normal, non-administrator terminal.");
            return 3;
        }

        var request = ParseLauncherRequest(args);
        if (request is null)
        {
            PrintHelp();
            return args.Length == 1 && args[0] == "--help" ? 0 : 2;
        }
        var mode = request.Mode;

        var isToggle = mode is LauncherMode.TogglePreflight or
            LauncherMode.RunToggle or
            LauncherMode.RunToggleFromDesktop;
        LiveCanaryDeviceState preflight;
        try
        {
            preflight = isToggle
                ? await RunPortableReadOnlyPreflightAsync()
                : await RunReadOnlyPreflightAsync();
        }
#pragma warning disable CA1031 // The launcher reports bounded read-only failures.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            Console.Error.WriteLine($"Live-hardware preflight failed safely: {exception.Message}");
            Console.WriteLine("No UAC helper was started and no GPU setting was changed.");
            return 1;
        }

        var requestedProfile = isToggle
            ? PortableLivePowerPolicy.ResolveIdForDevice(request.RequestedProfileId, preflight)
            : LivePowerProfiles.ResolveId(request.RequestedProfileId);
        if (isToggle)
        {
            PrintTogglePreflight(preflight, requestedProfile);
        }
        else
        {
            PrintDrillPreflight(preflight);
        }

        if (mode is LauncherMode.DrillPreflight or LauncherMode.TogglePreflight)
        {
            Console.WriteLine();
            Console.WriteLine("Preflight only: no UAC prompt appeared and no GPU setting was changed.");
            return 0;
        }

        if (mode == LauncherMode.RunToggleFromDesktop)
        {
            Console.WriteLine();
            Console.WriteLine("Desktop confirmation: PASS / explicit allow-listed live-toggle dialog accepted");
            Console.WriteLine("UAC consent is still required before the elevated helper can run.");
        }
        else if (isToggle)
        {
            PrintToggleWarning(preflight);
        }
        else
        {
            PrintDrillWarning(preflight);
        }

        if (mode != LauncherMode.RunToggleFromDesktop)
        {
            var confirmation = Console.ReadLine();
            var expectedConfirmation = isToggle
                ? LiveCanaryPolicy.PersistentToggleConfirmationPhrase
                : LiveCanaryPolicy.RecoveryDrillConfirmationPhrase;
            if (!string.Equals(
                confirmation,
                expectedConfirmation,
                StringComparison.Ordinal))
            {
                Console.WriteLine("Confirmation did not match. No UAC helper was started and no GPU setting was changed.");
                return 4;
            }
        }

        try
        {
            return isToggle
                ? await RunToggleAsync(preflight, requestedProfile)
                : await RunDrillAsync(preflight);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == UacCancelledError)
        {
            Console.WriteLine(preflight.CurrentLimitMilliwatts != preflight.DefaultLimitMilliwatts
                ? "UAC was declined while recovery may be pending at an allow-listed lower limit. Run this command again or use the independent rollback."
                : "UAC was declined before the helper ran. No GPU setting was changed.");
            PrintRollback(preflight);
            return 5;
        }
        catch (Win32Exception exception) when ((uint)exception.NativeErrorCode == DllInitializationFailed)
        {
            Console.WriteLine("Windows could not create the UAC helper from this terminal.");
            Console.WriteLine("Run the same command in Visual Studio's normal terminal.");
            return 6;
        }
#pragma warning disable CA1031 // Any incomplete drill must preserve explicit recovery guidance.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            Console.Error.WriteLine($"{(isToggle ? "Live persistent toggle" : "Live recovery drill")} did not complete cleanly: {exception.Message}");
            await ReportCurrentLimitSafelyAsync();
            Console.Error.WriteLine("Run this same command again; a protected pending journal forces recovery first.");
            PrintRollback(preflight);
            return 1;
        }
    }

    private static async Task<int> RunDesktopSessionLauncherAsync()
    {
        var pipeName = HardwareHostPipeName.Create();
        await using var server = HardwareHostNamedPipe.CreateServer(pipeName);
        Process? broker = null;
        try
        {
            broker = StartDesktopSessionBroker(pipeName, Environment.ProcessId);
            await HardwareHostNamedPipe.WaitForConnectionAsync(server, TimeSpan.FromMinutes(2));
            HardwareHostPipePeer.VerifyClientProcess(server, broker.Id);
            var status = await BoundedJsonPipe.ReadAsync<LiveSessionBrokerStatus>(
                server,
                TimeSpan.FromMinutes(2));
            if (!status.Succeeded)
            {
                WriteDesktopMessage(new("ready", false, status.Message, status.StartupTranscript, string.Empty));
                return 1;
            }

            WriteDesktopMessage(new(
                "ready",
                true,
                "Startup UAC broker is ready and mutually process-ID verified.",
                status.StartupTranscript,
                string.Empty));

            while (await Console.In.ReadLineAsync() is { } commandLine)
            {
                if (commandLine.Length is <= 0 or > MaximumDesktopCommandCharacters)
                {
                    WriteDesktopMessage(new("result", false, "The desktop command was invalid.", string.Empty, "Invalid command boundary."));
                    continue;
                }

                if (string.Equals(commandLine, "SHUTDOWN", StringComparison.Ordinal))
                {
                    await BoundedJsonPipe.WriteAsync(server, new LiveSessionBrokerRequest("shutdown", null));
                    var shutdown = await BoundedJsonPipe.ReadAsync<LiveSessionBrokerResponse>(
                        server,
                        TimeSpan.FromMinutes(2));
                    WriteDesktopMessage(new("shutdown", shutdown.Succeeded, shutdown.Message, shutdown.Transcript, shutdown.Error));
                    break;
                }

                const string togglePrefix = "TOGGLE ";
                if (!commandLine.StartsWith(togglePrefix, StringComparison.Ordinal))
                {
                    WriteDesktopMessage(new("result", false, "The desktop command was not recognized.", string.Empty, "Unknown command."));
                    continue;
                }

                PortableLivePowerRequest request;
                try
                {
                    request = PortableLivePowerPolicy.ParseRequest(commandLine[togglePrefix.Length..]);
                }
                catch (ArgumentException)
                {
                    WriteDesktopMessage(new("result", false, "The requested live profile was not allow-listed.", string.Empty, "Rejected profile ID."));
                    continue;
                }

                await BoundedJsonPipe.WriteAsync(server, new LiveSessionBrokerRequest("toggle", request.Id));
                var response = await BoundedJsonPipe.ReadAsync<LiveSessionBrokerResponse>(
                    server,
                    TimeSpan.FromMinutes(2));
                WriteDesktopMessage(new(
                    "result",
                    response.Succeeded,
                    response.Message,
                    response.Transcript,
                    response.Error));
            }

            return 0;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == UacCancelledError)
        {
            WriteDesktopMessage(new(
                "ready",
                false,
                "Startup UAC permission was declined. Live controls remain unavailable.",
                string.Empty,
                string.Empty));
            return 5;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            WriteDesktopMessage(new(
                "ready",
                false,
                "The startup live broker stopped safely.",
                string.Empty,
                exception.Message));
            return 1;
        }
        finally
        {
            if (broker is not null)
            {
                using (broker)
                {
                    if (!broker.HasExited)
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        try
                        {
                            await broker.WaitForExitAsync(timeout.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            // The broker is parent-bound and will exit when its verified pipe disconnects.
                        }
                    }
                }
            }
        }
    }

    private static async Task<int> RunDesktopSessionBrokerAsync(BrokerLaunch launch)
    {
        await using var pipe = await HardwareHostNamedPipe.ConnectVerifiedClientAsync(
            launch.PipeName,
            launch.ParentProcessId);
        try
        {
            var startupRecovery = await RestorePendingForBrokerAsync();
            if (!startupRecovery.Succeeded)
            {
                await BoundedJsonPipe.WriteAsync(
                    pipe,
                    new LiveSessionBrokerStatus(false, startupRecovery.Message, startupRecovery.Transcript));
                return 1;
            }

            await BoundedJsonPipe.WriteAsync(
                pipe,
                new LiveSessionBrokerStatus(
                    true,
                    "The elevated startup broker is ready.",
                    startupRecovery.Transcript));

            while (true)
            {
                LiveSessionBrokerRequest request;
                try
                {
                    request = await BoundedJsonPipe.ReadAsync<LiveSessionBrokerRequest>(
                        pipe,
                        TimeSpan.FromMinutes(2));
                }
                catch (OperationCanceledException)
                {
                    continue;
                }

                if (string.Equals(request.Operation, "shutdown", StringComparison.Ordinal))
                {
                    var restored = await RestorePendingForBrokerAsync();
                    await BoundedJsonPipe.WriteAsync(pipe, restored);
                    return restored.Succeeded ? 0 : 1;
                }

                if (!string.Equals(request.Operation, "toggle", StringComparison.Ordinal))
                {
                    await BoundedJsonPipe.WriteAsync(
                        pipe,
                        new LiveSessionBrokerResponse(false, "The broker rejected an unknown operation.", string.Empty, "Unknown operation."));
                    continue;
                }

                PortableLivePowerRequest profileRequest;
                try
                {
                    profileRequest = PortableLivePowerPolicy.ParseRequest(request.ProfileId ?? string.Empty);
                }
                catch (ArgumentException)
                {
                    await BoundedJsonPipe.WriteAsync(
                        pipe,
                        new LiveSessionBrokerResponse(false, "The broker rejected a non-allow-listed profile.", string.Empty, "Rejected profile ID."));
                    continue;
                }

                await BoundedJsonPipe.WriteAsync(pipe, await ExecuteBrokerToggleAsync(profileRequest.Id));
            }
        }
        catch (HardwareHostTransportException)
        {
            _ = await RestorePendingForBrokerAsync();
            return 1;
        }
    }

    private static async Task<LiveSessionBrokerResponse> RestorePendingForBrokerAsync()
    {
        try
        {
            var preflight = await RunPortableReadOnlyPreflightAsync();
            if (preflight.CurrentLimitMilliwatts == preflight.DefaultLimitMilliwatts)
            {
                return new(true, "The GPU is already at its verified default power limit.", string.Empty, string.Empty);
            }

            var profile = PortableLivePowerPolicy.ResolveTargetForDevice(
                preflight.CurrentLimitMilliwatts,
                preflight);
            return await ExecuteBrokerToggleAsync(profile.Id);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new(false, "The startup broker could not verify exact restoration.", string.Empty, exception.Message);
        }
    }

    private static async Task<LiveSessionBrokerResponse> ExecuteBrokerToggleAsync(string profileId)
    {
        var originalOut = Console.Out;
        using var transcriptWriter = new StringWriter(CultureInfo.InvariantCulture);
        Console.SetOut(transcriptWriter);
        try
        {
            var preflight = await RunPortableReadOnlyPreflightAsync();
            var profile = PortableLivePowerPolicy.ResolveIdForDevice(profileId, preflight);
            PrintTogglePreflight(preflight, profile, "administrator startup broker");
            Console.WriteLine("Startup UAC session: PASS / mutually verified normal-user launcher");
            Console.WriteLine("Per-action confirmation: NOT REQUIRED / authorized button, hotkey, or enabled idle timer");
            var exitCode = await RunToggleAsync(preflight, profile, startupBrokerAuthorized: true);
            var transcript = transcriptWriter.ToString();
            return exitCode == 0
                ? new(true, "The startup-authorized live toggle completed.", transcript, string.Empty)
                : new(false, "The startup-authorized live toggle did not complete.", transcript, $"Exit code {exitCode}.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new(false, "The startup-authorized live toggle stopped safely.", transcriptWriter.ToString(), exception.Message);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    private static Process StartDesktopSessionBroker(string pipeName, int parentProcessId)
    {
        var executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("The live-session executable path is unavailable.");
        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("--desktop-live-session-broker");
        startInfo.ArgumentList.Add("--pipe");
        startInfo.ArgumentList.Add(pipeName);
        startInfo.ArgumentList.Add("--parent-pid");
        startInfo.ArgumentList.Add(parentProcessId.ToString(CultureInfo.InvariantCulture));
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows did not start the elevated live-session broker.");
    }

    private static bool TryParseBrokerLaunch(string[] args, out BrokerLaunch launch)
    {
        launch = new BrokerLaunch(string.Empty, 0);
        if (args.Length != 5 ||
            args[0] != "--desktop-live-session-broker" ||
            args[1] != "--pipe" ||
            args[3] != "--parent-pid" ||
            !int.TryParse(args[4], NumberStyles.None, CultureInfo.InvariantCulture, out var parentProcessId))
        {
            return false;
        }

        HardwareHostPipeName.Validate(args[2]);
        HardwareHostPipePeer.ValidateExpectedProcessId(parentProcessId);
        launch = new BrokerLaunch(args[2], parentProcessId);
        return true;
    }

    private static void WriteDesktopMessage(DesktopSessionMessage message) =>
        Console.WriteLine(JsonSerializer.Serialize(message, DesktopJsonOptions));

    private static async Task<int> RunToggleAsync(
        LiveCanaryDeviceState initialPreflight,
        LivePowerProfile requestedProfile,
        bool startupBrokerAuthorized = false)
    {
        var percentage = PortableLivePowerPolicy.ParseRequest(requestedProfile.Id).Percentage;
        Console.WriteLine($"Requested live profile: {requestedProfile.Id} / {percentage}% / {FormatLimit(initialPreflight, requestedProfile.TargetLimitMilliwatts)}");
        Console.WriteLine(startupBrokerAuthorized
            ? "Startup-authorized broker launched the journal-selected helper without another UAC prompt."
            : "Approve the UAC prompt for the journal-selected persistent-toggle helper.");
        var run = await RunHelperAsync(
            initialPreflight,
            HelperBehavior.PersistentToggle,
            requestedProfile,
            inheritElevation: startupBrokerAuthorized);
        PrintToggleHelperPhase(run, initialPreflight);

        if (run.Bootstrap.Phase == LiveRecoveryDrillPhase.PauseAtVerifiedTarget)
        {
            if (!LivePersistentToggleEvidenceEvaluator.IsVerifiedPause(
                run.Bootstrap,
                run.Result,
                run.Summary,
                run.ExitCode))
            {
                throw new InvalidOperationException("The helper did not provide complete persistent-limit evidence.");
            }

            var limited = await RequireIndependentLimitAsync(
                run.Bootstrap.TargetLimitMilliwatts,
                initialPreflight.DeviceFingerprint.Value,
                portable: true);
            Console.WriteLine();
            Console.WriteLine($"Independent final live limit: {FormatLimit(limited, limited.CurrentLimitMilliwatts)}");
            Console.WriteLine("Protected exact-restoration journal retained: PASS");
            Console.WriteLine("Live GPU toggle: PASS / Limited");
            Console.WriteLine($"Run this same command again to restore exactly {FormatLimit(initialPreflight, initialPreflight.DefaultLimitMilliwatts)}.");
            Console.WriteLine("GPU lifecycle: PERCENTAGE LIMIT / EXACT DEFAULT RESTORE");
            Console.WriteLine("Desktop activation triggers: BUTTON, ASSIGNED GLOBAL HOTKEY, OR ENABLED IDLE TIMER");
            Console.WriteLine("Idle activation hardware path: STARTUP-AUTHORIZED BROKER ONLY");
            return 0;
        }

        if (!LivePersistentToggleEvidenceEvaluator.IsVerifiedRecovery(
            run.Bootstrap,
            run.Result,
            run.Summary,
            run.ExitCode))
        {
            throw new InvalidOperationException("The helper did not provide complete exact-restoration evidence.");
        }

        var restored = await RequireIndependentLimitAsync(
            initialPreflight.DefaultLimitMilliwatts,
            initialPreflight.DeviceFingerprint.Value,
            portable: true);
        Console.WriteLine();
        Console.WriteLine($"Independent final live limit: {FormatLimit(restored, restored.CurrentLimitMilliwatts)}");
        Console.WriteLine("Live GPU toggle: PASS / Restored");
        Console.WriteLine("GPU lifecycle: PERCENTAGE LIMIT / EXACT DEFAULT RESTORE");
        Console.WriteLine("Desktop activation triggers: BUTTON, ASSIGNED GLOBAL HOTKEY, OR ENABLED IDLE TIMER");
        Console.WriteLine("Idle activation hardware path: STARTUP-AUTHORIZED BROKER ONLY");
        return 0;
    }

    private static async Task<int> RunDrillAsync(LiveCanaryDeviceState initialPreflight)
    {
        Console.WriteLine("UAC prompt 1: approve the isolated journal-selected drill helper.");
        var first = await RunHelperAsync(
            initialPreflight,
            HelperBehavior.RecoveryDrill,
            LivePowerProfiles.Default);
        PrintDrillHelperPhase(first, 1);

        if (first.Bootstrap.Phase == LiveRecoveryDrillPhase.RecoveryOnly)
        {
            if (!LiveRecoveryDrillEvidenceEvaluator.IsVerifiedRecovery(
                first.Bootstrap,
                first.Result,
                first.Summary,
                first.ExitCode))
            {
                throw new InvalidOperationException("The first helper did not provide complete recovery evidence.");
            }

            var resumedFinal = await RequireIndependentLimitAsync(
                LiveCanaryPolicy.RequiredOriginalLimitMilliwatts,
                initialPreflight.DeviceFingerprint.Value);
            PrintFinalSuccess(resumedFinal, resumedRecovery: true);
            return 0;
        }

        if (!LiveRecoveryDrillEvidenceEvaluator.IsVerifiedInterruption(
            first.Bootstrap,
            first.Result,
            first.Summary,
            first.ExitCode))
        {
            throw new InvalidOperationException("The first helper did not provide complete intentional-interruption evidence.");
        }

        var interruptedState = await RequireIndependentLimitAsync(
            LiveCanaryPolicy.TargetLimitMilliwatts,
            initialPreflight.DeviceFingerprint.Value);
        Console.WriteLine("Independent post-termination 400 W read-back: PASS");
        Console.WriteLine("Pending recovery now exists outside the terminated elevated process.");
        Console.WriteLine();
        Console.WriteLine("UAC prompt 2: approve a fresh helper that must select RecoveryOnly.");

        HelperRun second;
        try
        {
            second = await RunHelperAsync(
                interruptedState,
                HelperBehavior.RecoveryDrill,
                LivePowerProfiles.Default);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == UacCancelledError)
        {
            Console.Error.WriteLine("Second UAC prompt was declined. The GPU remains deliberately limited to 400 W with protected recovery pending.");
            Console.Error.WriteLine("Run this same command again to restore before beginning any new drill.");
            PrintRollback();
            return 7;
        }
        catch (Win32Exception exception) when ((uint)exception.NativeErrorCode == DllInitializationFailed)
        {
            Console.Error.WriteLine("Windows could not create the second UAC helper. The GPU remains at 400 W with protected recovery pending.");
            Console.Error.WriteLine("Run this command again from Visual Studio's normal terminal to recover first.");
            PrintRollback();
            return 8;
        }

        PrintDrillHelperPhase(second, 2);
        if (!LiveRecoveryDrillEvidenceEvaluator.IsVerifiedRecovery(
            second.Bootstrap,
            second.Result,
            second.Summary,
            second.ExitCode))
        {
            throw new InvalidOperationException("The fresh helper did not provide complete exact-restoration evidence.");
        }

        var finalState = await RequireIndependentLimitAsync(
            LiveCanaryPolicy.RequiredOriginalLimitMilliwatts,
            initialPreflight.DeviceFingerprint.Value);
        PrintFinalSuccess(finalState, resumedRecovery: false);
        return 0;
    }

    private static async Task<HelperRun> RunHelperAsync(
        LiveCanaryDeviceState preflight,
        HelperBehavior behavior,
        LivePowerProfile requestedProfile,
        bool inheritElevation = false)
    {
        var helperPath = FindHelperExecutable();
        var pipeName = HardwareHostPipeName.Create();
        await using var server = HardwareHostNamedPipe.CreateServer(pipeName);
        using var helper = StartElevatedHelper(
            helperPath,
            pipeName,
            Environment.ProcessId,
            behavior,
            requestedProfile,
            inheritElevation);

        await HardwareHostNamedPipe.WaitForConnectionAsync(server, TimeSpan.FromMinutes(2));
        HardwareHostPipePeer.VerifyClientProcess(server, helper.Id);
        RequireStatus(await ReadStatusAsync(server, helper), "peer-verification");
        RequireStatus(await ReadStatusAsync(server, helper), "host-initialization");

        var bootstrap = await BoundedJsonPipe.ReadAsync<LiveRecoveryDrillBootstrap>(server);
        ValidateBootstrap(bootstrap, preflight, behavior, requestedProfile);
        HardwareHostWireResult result;
        using (bootstrap.Session)
        using (var session = HardwareHostSessionFactory.ImportClientSession(bootstrap.Session))
        {
            var command = session.CreateCommand(bootstrap.Operation, bootstrap.SnapshotId);
            await BoundedJsonPipe.WriteAsync(server, command);
            result = await BoundedJsonPipe.ReadAsync<HardwareHostWireResult>(server);
        }

        var summary = await BoundedJsonPipe.ReadAsync<LiveRecoveryDrillSummary>(server);
        using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await helper.WaitForExitAsync(exitTimeout.Token);
        return new HelperRun(helper.Id, helper.ExitCode, bootstrap, result, summary);
    }

    private static void ValidateBootstrap(
        LiveRecoveryDrillBootstrap bootstrap,
        LiveCanaryDeviceState preflight,
        HelperBehavior behavior,
        LivePowerProfile requestedProfile)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        var expectedOperation = bootstrap.Phase switch
        {
            LiveRecoveryDrillPhase.CrashAfterVerifiedTarget => HardwareHostOperation.ActivatePrepared,
            LiveRecoveryDrillPhase.PauseAtVerifiedTarget => HardwareHostOperation.ActivatePrepared,
            LiveRecoveryDrillPhase.RecoveryOnly => HardwareHostOperation.RestorePending,
            _ => throw new InvalidOperationException("The helper selected an unknown recovery-drill phase.")
        };
        var phaseMatchesBehavior = bootstrap.Phase == LiveRecoveryDrillPhase.RecoveryOnly ||
            behavior == HelperBehavior.RecoveryDrill &&
                bootstrap.Phase == LiveRecoveryDrillPhase.CrashAfterVerifiedTarget ||
            behavior == HelperBehavior.PersistentToggle &&
                bootstrap.Phase == LiveRecoveryDrillPhase.PauseAtVerifiedTarget;
        var selectedProfile = behavior == HelperBehavior.PersistentToggle
            ? PortableLivePowerPolicy.ResolveIdForDevice(bootstrap.ProfileId, preflight)
            : LivePowerProfiles.ResolveId(bootstrap.ProfileId);
        var profileMatchesPlan = bootstrap.TargetLimitMilliwatts == selectedProfile.TargetLimitMilliwatts &&
            (bootstrap.Phase == LiveRecoveryDrillPhase.RecoveryOnly || selectedProfile == requestedProfile);
        if (behavior == HelperBehavior.RecoveryDrill)
        {
            LivePowerProfiles.ValidateForDevice(selectedProfile, preflight);
        }
        if (bootstrap.SnapshotId == Guid.Empty ||
            !phaseMatchesBehavior ||
            !profileMatchesPlan ||
            bootstrap.Operation != expectedOperation ||
            bootstrap.DeviceName != preflight.Name ||
            bootstrap.DeviceFingerprintSha256 != preflight.DeviceFingerprint.Value ||
            bootstrap.OriginalLimitMilliwatts != preflight.DefaultLimitMilliwatts ||
            bootstrap.MinimumLimitMilliwatts != preflight.MinimumLimitMilliwatts ||
            bootstrap.MaximumLimitMilliwatts != preflight.MaximumLimitMilliwatts)
        {
            throw new InvalidOperationException("The elevated helper's fixed recovery-drill plan did not match read-only preflight.");
        }
    }

    private static async Task<LiveCanaryDeviceState> RequireIndependentLimitAsync(
        uint expectedLimitMilliwatts,
        string expectedFingerprint,
        bool portable = false)
    {
        var state = portable
            ? await RunPortableReadOnlyPreflightAsync()
            : await RunReadOnlyPreflightAsync();
        if (state.CurrentLimitMilliwatts != expectedLimitMilliwatts ||
            state.DeviceFingerprint.Value != expectedFingerprint)
        {
            throw new InvalidOperationException(
                $"Independent read-back expected {expectedLimitMilliwatts} mW on the same GPU, " +
                $"but observed {state.CurrentLimitMilliwatts} mW.");
        }

        return state;
    }

    private static async Task<LiveCanaryDeviceState> RunReadOnlyPreflightAsync()
    {
        var report = await new NvidiaGpuProbe().ProbeAsync();
        return LiveCanaryPolicy.ValidateProbeReport(report);
    }

    private static async Task<LiveCanaryDeviceState> RunPortableReadOnlyPreflightAsync()
    {
        var report = await ProbePortableGpuAsync();
        return PortableLivePowerPolicy.ValidateProbeReport(report);
    }

    private static async Task<GpuProbeReport> ProbePortableGpuAsync()
    {
        var nvidia = await new NvidiaGpuProbe().ProbeAsync();
        if (nvidia.Status == GpuProbeStatus.Ready && nvidia.Devices.Count > 0)
        {
            return nvidia;
        }

        var amd = await new AmdGpuProbe().ProbeAsync();
        if (amd.Status == GpuProbeStatus.Ready && amd.Devices.Count > 0)
        {
            return amd;
        }

        throw new InvalidOperationException(
            $"No supported NVIDIA or AMD GPU backend is ready. NVIDIA: {nvidia.Message} AMD: {amd.Message}");
    }

    private static async Task ReportCurrentLimitSafelyAsync()
    {
        try
        {
            var current = await RunPortableReadOnlyPreflightAsync();
            Console.Error.WriteLine($"Independent current GPU limit: {FormatLimit(current, current.CurrentLimitMilliwatts)}");
        }
#pragma warning disable CA1031 // Best-effort recovery diagnostics must not mask the original failure.
        catch (Exception)
#pragma warning restore CA1031
        {
            Console.Error.WriteLine("Independent current GPU limit: unavailable");
        }
    }

    private static void PrintDrillHelperPhase(HelperRun run, int promptNumber)
    {
        Console.WriteLine($"Helper {promptNumber} PID verified by Windows: PASS ({run.ProcessId})");
        Console.WriteLine($"Helper {promptNumber} selected phase: {run.Bootstrap.Phase}");
        Console.WriteLine($"Helper {promptNumber} selected profile: {run.Bootstrap.ProfileId} / {run.Bootstrap.TargetLimitMilliwatts / 1000m:0.###} W");
        Console.WriteLine($"Authenticated operation: {run.Result.Outcome} / {run.Result.ExecutionOutcome ?? "None"}");
        Console.WriteLine($"Helper exit: {run.ExitCode}");
        Console.WriteLine($"Final phase limit: {run.Summary.FinalLimitMilliwatts / 1000m:0.###} W");
        Console.WriteLine($"Recovery journal stage: {run.Summary.RecoveryStage}");
        if (run.Bootstrap.Phase == LiveRecoveryDrillPhase.CrashAfterVerifiedTarget)
        {
            Console.WriteLine($"Intentional helper termination ({LiveCanaryPolicy.IntentionalRecoveryDrillExitCode}): " +
                $"{(run.ExitCode == LiveCanaryPolicy.IntentionalRecoveryDrillExitCode ? "PASS" : "FAIL")}");
        }
        else
        {
            Console.WriteLine($"{run.Bootstrap.OriginalLimitMilliwatts / 1000m:0.###} W exact restoration: {(run.Summary.OriginalRestoredAndVerified ? "PASS" : "FAIL")}");
            Console.WriteLine($"Protected recovery artifacts cleaned: {run.Summary.RecoveryArtifactsCleaned}");
        }
    }

    private static void PrintToggleHelperPhase(HelperRun run, LiveCanaryDeviceState device)
    {
        var percentage = PortableLivePowerPolicy.ParseRequest(run.Bootstrap.ProfileId).Percentage;
        Console.WriteLine($"Helper PID verified by Windows: PASS ({run.ProcessId})");
        Console.WriteLine("Helper-selected action: " +
            (run.Bootstrap.Phase == LiveRecoveryDrillPhase.PauseAtVerifiedTarget
                ? "ApplyVerifiedLimit"
                : "RestoreOriginal"));
        Console.WriteLine($"Helper-selected live profile: {run.Bootstrap.ProfileId} / {percentage}% / {FormatLimit(device, run.Bootstrap.TargetLimitMilliwatts)}");
        Console.WriteLine($"Authenticated operation: {run.Result.Outcome} / {run.Result.ExecutionOutcome ?? "None"}");
        Console.WriteLine($"Helper exit: {run.ExitCode}");
        Console.WriteLine($"Final helper limit: {FormatLimit(device, run.Summary.FinalLimitMilliwatts)}");
        Console.WriteLine($"Recovery journal stage: {run.Summary.RecoveryStage}");
        if (run.Bootstrap.Phase == LiveRecoveryDrillPhase.PauseAtVerifiedTarget)
        {
            Console.WriteLine($"{FormatLimit(device, run.Bootstrap.TargetLimitMilliwatts)} exact persistent limit: {(run.Summary.TargetAppliedAndVerified ? "PASS" : "FAIL")}");
            Console.WriteLine("Protected recovery artifacts retained: " +
                $"{(!run.Summary.RecoveryArtifactsCleaned ? "PASS" : "FAIL")}");
        }
        else
        {
            Console.WriteLine($"{FormatLimit(device, run.Bootstrap.OriginalLimitMilliwatts)} exact restoration: {(run.Summary.OriginalRestoredAndVerified ? "PASS" : "FAIL")}");
            Console.WriteLine($"Protected recovery artifacts cleaned: {run.Summary.RecoveryArtifactsCleaned}");
        }
    }

    private static void PrintFinalSuccess(LiveCanaryDeviceState finalState, bool resumedRecovery)
    {
        Console.WriteLine();
        Console.WriteLine($"Independent final live limit: {finalState.CurrentLimitMilliwatts / 1000m:0.###} W");
        Console.WriteLine($"Live elevated recovery drill: PASS{(resumedRecovery ? " (resumed pending recovery)" : string.Empty)}");
        Console.WriteLine("Desktop manual live toggle: separate fixed 400/450 W path");
        Console.WriteLine("Desktop live triggers: separate qualified toggle path");
        Console.WriteLine("Idle timer real-GPU access: NONE");
    }

    private static void PrintDrillPreflight(LiveCanaryDeviceState device)
    {
        Console.WriteLine("AFK Power Saver Live Elevated Recovery Drill Preflight 1.7");
        Console.WriteLine("Launcher privilege: normal user");
        Console.WriteLine("Desktop manual live toggle: separate fixed 400/450 W path");
        Console.WriteLine("Desktop live triggers: separate qualified toggle path");
        Console.WriteLine("Idle timer: PREVIEW ONLY");
        Console.WriteLine($"GPU: {device.Name}");
        Console.WriteLine($"Current limit: {FormatLimit(device, device.CurrentLimitMilliwatts)}");
        Console.WriteLine($"Default limit: {FormatLimit(device, device.DefaultLimitMilliwatts)}");
        Console.WriteLine($"Permitted range: {FormatLimit(device, device.MinimumLimitMilliwatts)}-{FormatLimit(device, device.MaximumLimitMilliwatts)}");
        Console.WriteLine($"Preflight state: {(device.CanStartNewCanary ? "READY FOR TWO-PHASE DRILL" : "RECOVERY MAY BE PENDING")}");
    }

    private static void PrintDrillWarning(LiveCanaryDeviceState preflight)
    {
        Console.WriteLine();
        Console.WriteLine("LIVE GPU INTERRUPTION WARNING");
        Console.WriteLine("Close games, renderers, AI workloads, benchmarks, and GPU tuning tools.");
        Console.WriteLine(preflight.CanStartNewCanary
            ? "UAC 1 will apply/verify 400 W and intentionally terminate; UAC 2 will restore/verify 450 W."
            : "A 400 W state may already be pending; the first helper must recover it and will not begin another drill.");
        PrintRollback(preflight);
        Console.WriteLine();
        Console.WriteLine($"Type exactly: {LiveCanaryPolicy.RecoveryDrillConfirmationPhrase}");
        Console.Write("> ");
    }

    private static void PrintTogglePreflight(
        LiveCanaryDeviceState device,
        LivePowerProfile requestedProfile,
        string launcherPrivilege = "normal user")
    {
        var request = PortableLivePowerPolicy.ParseRequest(requestedProfile.Id);
        Console.WriteLine("AFK Power Saver Live GPU Limit Session 1.22");
        Console.WriteLine($"Launcher privilege: {launcherPrivilege}");
        Console.WriteLine("Desktop GPU control: PERCENTAGE LIMIT / EXACT DEFAULT RESTORE");
        Console.WriteLine("Desktop activation triggers: BUTTON, ASSIGNED GLOBAL HOTKEY, OR ENABLED IDLE TIMER");
        Console.WriteLine("Idle activation: OPTIONAL / USER-CONFIGURED");
        Console.WriteLine($"GPU: {device.Name}");
        Console.WriteLine($"Current limit: {FormatLimit(device, device.CurrentLimitMilliwatts)}");
        Console.WriteLine($"Default limit: {FormatLimit(device, device.DefaultLimitMilliwatts)}");
        Console.WriteLine($"Permitted range: {FormatLimit(device, device.MinimumLimitMilliwatts)}-{FormatLimit(device, device.MaximumLimitMilliwatts)}");
        Console.WriteLine($"Portable limit range: {PortableLivePowerPolicy.GetMinimumPercentage(device)}-{PortableLivePowerPolicy.HighestPercentage}% of verified default");
        Console.WriteLine($"Requested limit profile: {requestedProfile.Id} / {request.Percentage}% / {FormatLimit(device, requestedProfile.TargetLimitMilliwatts)}");
        Console.WriteLine($"Preflight state: {(device.CurrentLimitMilliwatts == device.DefaultLimitMilliwatts ? "READY TO LIMIT" : "RECOVERY EXPECTED")}");
    }

    private static void PrintToggleWarning(LiveCanaryDeviceState preflight)
    {
        Console.WriteLine();
        Console.WriteLine("LIVE GPU PERSISTENT-STATE WARNING");
        Console.WriteLine("Close games, renderers, AI workloads, benchmarks, and GPU tuning tools.");
        Console.WriteLine(preflight.CurrentLimitMilliwatts == preflight.DefaultLimitMilliwatts
            ? "One UAC-approved helper will apply/verify the selected GPU-relative percentage and retain exact recovery."
            : $"A percentage state may be pending; the helper must restore/verify {FormatLimit(preflight, preflight.DefaultLimitMilliwatts)} and clean recovery state.");
        Console.WriteLine("The helper—not this launcher—selects limit or recovery from the protected journal.");
        PrintRollback(preflight);
        Console.WriteLine();
        Console.WriteLine($"Type exactly: {LiveCanaryPolicy.PersistentToggleConfirmationPhrase}");
        Console.Write("> ");
    }

    private static void PrintRollback(LiveCanaryDeviceState? device = null)
    {
        if (device?.LimitKind == GpuPowerLimitKind.DefaultRelativePercentage)
        {
            Console.WriteLine("Independent rollback: AMD Software > Performance > Tuning > Power Tuning; return the adjustment to its original value.");
            Console.WriteLine("Final fallback: reboot Windows or reload the AMD display driver.");
            return;
        }

        Console.WriteLine("Independent administrator rollback: nvidia-smi --power-limit=450");
        Console.WriteLine("Final fallback: reboot Windows or reload the NVIDIA driver.");
    }

    private static string FormatLimit(LiveCanaryDeviceState device, uint encodedLimit) =>
        device.LimitKind == GpuPowerLimitKind.DefaultRelativePercentage
            ? $"{encodedLimit / 1000m:0.###}% of factory"
            : $"{encodedLimit / 1000m:0.###} W";

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

    private static Process StartElevatedHelper(
        string helperPath,
        string pipeName,
        int parentProcessId,
        HelperBehavior behavior,
        LivePowerProfile requestedProfile,
        bool inheritElevation)
    {
        var startInfo = new ProcessStartInfo(helperPath);
        if (inheritElevation)
        {
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
        }
        else
        {
            startInfo.UseShellExecute = true;
            startInfo.Verb = "runas";
            startInfo.WindowStyle = ProcessWindowStyle.Hidden;
        }
        startInfo.ArgumentList.Add("--pipe");
        startInfo.ArgumentList.Add(pipeName);
        startInfo.ArgumentList.Add("--parent-pid");
        startInfo.ArgumentList.Add(parentProcessId.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--behavior");
        startInfo.ArgumentList.Add(behavior == HelperBehavior.PersistentToggle
            ? "persistent-toggle"
            : "recovery-drill");
        startInfo.ArgumentList.Add("--profile");
        startInfo.ArgumentList.Add(requestedProfile.Id);
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows did not start the elevated recovery-drill helper.");
    }

    private static string FindHelperExecutable()
    {
        var colocated = Path.Combine(AppContext.BaseDirectory, "AFKPowerSaver.ElevatedHost.exe");
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
            throw new FileNotFoundException("The AFK Power Saver solution directory could not be located.");
        }

#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var helperPath = Path.Combine(
            directory.FullName,
            "src",
            "EcoPause.ElevatedHost.LiveRecoveryDrill",
            "bin",
            configuration,
            "net10.0",
            "AFKPowerSaver.ElevatedHost.exe");
        if (!File.Exists(helperPath))
        {
            throw new FileNotFoundException("Build the live recovery-drill project before starting UAC.", helperPath);
        }

        return helperPath;
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static LauncherRequest? ParseLauncherRequest(string[] args)
    {
        if (args.Length == 1)
        {
            var mode = args[0] switch
            {
                "--preflight" => LauncherMode.DrillPreflight,
                "--run-live-recovery-drill" => LauncherMode.RunDrill,
                "--preflight-live-toggle" => LauncherMode.TogglePreflight,
                "--run-live-toggle" => LauncherMode.RunToggle,
                _ => (LauncherMode?)null
            };
            if (mode is null)
            {
                return null;
            }

            var profileId = mode is LauncherMode.TogglePreflight or LauncherMode.RunToggle
                ? PortableLivePowerPolicy.Default.Id
                : LivePowerProfiles.Default.Id;
            return new LauncherRequest(mode.Value, profileId);
        }

        if (args.Length == 3 &&
            args[0] == "--run-live-toggle-desktop-confirmed" &&
            args[1] == "--profile")
        {
            return new LauncherRequest(
                LauncherMode.RunToggleFromDesktop,
                PortableLivePowerPolicy.ParseRequest(args[2]).Id);
        }

        return null;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("AFK Power Saver Live Hardware Safety Tools 1.13");
        Console.WriteLine();
        Console.WriteLine("Read-only preflight:");
        Console.WriteLine("  AFKPowerSaver.LiveSession --preflight");
        Console.WriteLine();
        Console.WriteLine("Supervised two-UAC interruption and recovery drill:");
        Console.WriteLine("  AFKPowerSaver.LiveSession --run-live-recovery-drill");
        Console.WriteLine();
        Console.WriteLine("Manual persistent-toggle preflight:");
        Console.WriteLine("  AFKPowerSaver.LiveSession --preflight-live-toggle");
        Console.WriteLine();
        Console.WriteLine("Manual persistent live toggle (one UAC prompt per invocation):");
        Console.WriteLine("  AFKPowerSaver.LiveSession --run-live-toggle");
        Console.WriteLine("  Console and desktop routes use a validated percentage of the GPU's own default limit.");
        Console.WriteLine();
        Console.WriteLine("A pending protected journal always forces recovery before any new activation.");
    }

    private enum LauncherMode
    {
        DrillPreflight,
        RunDrill,
        TogglePreflight,
        RunToggle,
        RunToggleFromDesktop
    }

    private enum HelperBehavior
    {
        RecoveryDrill,
        PersistentToggle
    }

    private sealed record LauncherRequest(
        LauncherMode Mode,
        string RequestedProfileId);

    private sealed record BrokerLaunch(string PipeName, int ParentProcessId);

    private sealed record LiveSessionBrokerStatus(
        bool Succeeded,
        string Message,
        string StartupTranscript);

    private sealed record LiveSessionBrokerRequest(string Operation, string? ProfileId);

    private sealed record LiveSessionBrokerResponse(
        bool Succeeded,
        string Message,
        string Transcript,
        string Error);

    private sealed record DesktopSessionMessage(
        string Kind,
        bool Succeeded,
        string Message,
        string Transcript,
        string Error);

    private sealed record HelperRun(
        int ProcessId,
        int ExitCode,
        LiveRecoveryDrillBootstrap Bootstrap,
        HardwareHostWireResult Result,
        LiveRecoveryDrillSummary Summary);
}
