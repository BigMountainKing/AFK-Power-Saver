using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using EcoPause.Core.Activation;
using EcoPause.Core.Recovery;
using EcoPause.Hardware.Amd;
using EcoPause.Hardware.Nvidia.Control;
using EcoPause.HardwareHost.Protocol;
using EcoPause.HardwareHost.Transport;
using EcoPause.LiveCanary.Model;

namespace EcoPause.ElevatedHost.LiveRecoveryDrill;

[SupportedOSPlatform("windows")]
internal static class Program
{
    private const int GeneralFailureExitCode = 10;
    private const int ConnectionFailureExitCode = 12;
    private const int PeerVerificationFailureExitCode = 13;
    private const int InitializationFailureExitCode = 14;

    public static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            return 2;
        }

        try
        {
            var launch = ParseLaunch(args);
            if (!IsElevated())
            {
                return 3;
            }

            NamedPipeClientStream pipe;
            try
            {
                pipe = await HardwareHostNamedPipe.ConnectVerifiedClientAsync(
                    launch.PipeName,
                    launch.ParentProcessId);
            }
#pragma warning disable CA1031 // A distinct code identifies pre-channel connection failure.
            catch (Exception)
#pragma warning restore CA1031
            {
                return ConnectionFailureExitCode;
            }

            await using (pipe)
            {
                try
                {
                    await SendStatusAsync(pipe, succeeded: true, "peer-verification", exception: null);
                }
#pragma warning disable CA1031 // A distinct code identifies failed peer acknowledgement.
                catch (Exception)
#pragma warning restore CA1031
                {
                    return PeerVerificationFailureExitCode;
                }

                LiveHardwareProcessLock? hardwareLock = null;
                DrillContext? context = null;
                try
                {
                    hardwareLock = LiveHardwareProcessLock.Acquire();
                    if (launch.Behavior == HelperBehavior.RecoveryOnly)
                    {
                        using (hardwareLock)
                            return await RunRecoveryOnlyAsync(pipe);
                    }
                    context = await CreateContextAsync(launch.Behavior, launch.RequestedProfileId);
                }
#pragma warning disable CA1031 // Initialization failures are sanitized over the verified pipe.
                catch (Exception exception)
#pragma warning restore CA1031
                {
                    hardwareLock?.Dispose();
                    await SendStatusAsync(pipe, succeeded: false, "host-initialization", exception);
                    return InitializationFailureExitCode;
                }

                using (hardwareLock)
                using (context)
                {
                    await SendReadyAsync(pipe, context);
                    return await RunFixedPhaseAsync(pipe, context);
                }
            }
        }
#pragma warning disable CA1031 // The helper fails closed at its outermost boundary.
        catch (Exception)
#pragma warning restore CA1031
        {
            return GeneralFailureExitCode;
        }
    }

    private static async Task<int> RunRecoveryOnlyAsync(Stream pipe)
    {
        RecoveryOnlyResponse response;
        try
        {
            using var controller = OpenSinglePortableController();
            var directory = GetStateDirectory(controller.State.StateDirectoryName);
            EnsureProtectedDirectory(directory);
            EnsureOnlyKnownArtifacts(directory);
            var result = await GpuRecoveryOnly.RunAsync(
                Path.Combine(directory, LiveCanaryPolicy.JournalFileName), controller, new FileRecoveryJournalStore());
            if (!TryCleanRecoveryArtifacts(directory))
                throw new InvalidOperationException("GPU recovery artifact cleanup did not complete.");
            response = new(true, string.Empty, result);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            response = new(false, exception.Message, null);
        }
        await BoundedJsonPipe.WriteAsync(pipe, response);
        return response.Succeeded ? 0 : 1;
    }

    private static async Task<int> RunFixedPhaseAsync(Stream pipe, DrillContext context)
    {
        var envelope = await BoundedJsonPipe.ReadAsync<HardwareHostCommandEnvelope>(pipe);
        HardwareHostProcessResult commandResult;
        if (envelope.Command.Operation != context.Operation)
        {
            commandResult = new HardwareHostProcessResult(
                HardwareHostProcessOutcome.RejectedTarget,
                "The command did not match the helper-selected live-hardware phase.",
                Execution: null);
        }
        else
        {
            commandResult = await context.Processor.ProcessAsync(envelope);
        }

        var journal = await context.Store.LoadAsync(context.JournalPath, CancellationToken.None);
        var observed = await context.Controller.ReadPowerLimitMilliwattsAsync(CancellationToken.None);
        var targetVerified =
            (context.Phase is LiveRecoveryDrillPhase.CrashAfterVerifiedTarget or
                LiveRecoveryDrillPhase.PauseAtVerifiedTarget) &&
            commandResult.Outcome == HardwareHostProcessOutcome.Executed &&
            commandResult.Execution?.Outcome == ActivationOutcome.Activated &&
            journal.Stage == RecoveryStage.Applied &&
            observed == context.Profile.TargetLimitMilliwatts;
        var originalVerified =
            context.Phase == LiveRecoveryDrillPhase.RecoveryOnly &&
            commandResult.Outcome == HardwareHostProcessOutcome.Executed &&
            commandResult.Execution?.Outcome is ActivationOutcome.Restored or ActivationOutcome.AlreadyRestored &&
            commandResult.Execution.OriginalLimitVerified &&
            journal.Stage == RecoveryStage.Restored &&
            observed == context.Device.DefaultLimitMilliwatts;
        var artifactsCleaned = originalVerified && TryCleanRecoveryArtifacts(context.StateDirectory);
        var safePhaseState = context.Phase switch
        {
            LiveRecoveryDrillPhase.CrashAfterVerifiedTarget => targetVerified,
            LiveRecoveryDrillPhase.PauseAtVerifiedTarget => targetVerified,
            LiveRecoveryDrillPhase.RecoveryOnly => originalVerified && artifactsCleaned,
            _ => false
        };

        await BoundedJsonPipe.WriteAsync(
            pipe,
            new HardwareHostWireResult(
                commandResult.Outcome,
                commandResult.Execution?.Outcome.ToString(),
                commandResult.Message));
        await BoundedJsonPipe.WriteAsync(
            pipe,
            new LiveRecoveryDrillSummary(
                PeerVerified: true,
                context.Phase,
                context.Device.Name,
                targetVerified,
                originalVerified,
                observed,
                journal.Stage.ToString(),
                safePhaseState,
                artifactsCleaned));
        await pipe.FlushAsync(CancellationToken.None);

        if (targetVerified && context.Phase == LiveRecoveryDrillPhase.CrashAfterVerifiedTarget)
        {
            Environment.Exit(LiveCanaryPolicy.IntentionalRecoveryDrillExitCode);
        }

        return safePhaseState ? 0 : 1;
    }

    private static async Task<DrillContext> CreateContextAsync(
        HelperBehavior behavior,
        string requestedProfileId)
    {
        ILiveGpuPowerController controller = behavior == HelperBehavior.PersistentToggle
            ? OpenSinglePortableController()
            : NvidiaLivePowerController.OpenSingleCanaryDevice();

        try
        {
            var stateDirectory = GetStateDirectory(controller.State.StateDirectoryName);
            EnsureProtectedDirectory(stateDirectory);
            EnsureOnlyKnownArtifacts(stateDirectory);
            var journalPath = Path.Combine(stateDirectory, LiveCanaryPolicy.JournalFileName);
            var store = new FileRecoveryJournalStore();
            var requestedProfile = behavior == HelperBehavior.PersistentToggle
                ? PortableLivePowerPolicy.ResolveIdForDevice(requestedProfileId, controller.State)
                : LivePowerProfiles.ResolveId(requestedProfileId);
            RecoverySnapshot snapshot;
            LivePowerProfile profile;
            LiveRecoveryDrillPhase phase;
            HardwareHostOperation operation;
            if (File.Exists(journalPath))
            {
                RejectReparsePoint(journalPath);
                snapshot = await store.LoadAsync(journalPath);
                if (snapshot.Stage == RecoveryStage.Restored)
                {
                    if (await controller.ReadPowerLimitMilliwattsAsync() !=
                        controller.State.DefaultLimitMilliwatts ||
                        !TryCleanRecoveryArtifacts(stateDirectory))
                    {
                        throw new InvalidOperationException("A closed live recovery journal could not be cleaned safely.");
                    }

                    EnsureProtectedDirectory(stateDirectory);
                    profile = requestedProfile;
                    snapshot = await PrepareNewSnapshotAsync(controller, journalPath, store, profile, behavior);
                    phase = GetTargetPhase(behavior);
                    operation = HardwareHostOperation.ActivatePrepared;
                }
                else
                {
                    profile = behavior == HelperBehavior.PersistentToggle
                        ? PortableLivePowerPolicy.ValidateRecoverySnapshot(snapshot, controller.State)
                        : ValidateFixedRecoverySnapshot(snapshot, controller.State);
                    phase = LiveRecoveryDrillPhase.RecoveryOnly;
                    operation = HardwareHostOperation.RestorePending;
                }
            }
            else
            {
                profile = requestedProfile;
                snapshot = await PrepareNewSnapshotAsync(controller, journalPath, store, profile, behavior);
                phase = GetTargetPhase(behavior);
                operation = HardwareHostOperation.ActivatePrepared;
            }

            var session = HardwareHostSessionFactory.CreateTransportSession();
            var engine = new GpuActivationEngine(store);
            var registry = new DrillTargetRegistry(store, controller, journalPath);
            var processor = new HardwareHostCommandProcessor(session.Host, registry, engine);
            return new DrillContext(
                stateDirectory,
                journalPath,
                snapshot,
                controller.State,
                profile,
                phase,
                operation,
                store,
                controller,
                session,
                processor);
        }
        catch
        {
            controller.Dispose();
            throw;
        }
    }

    private static async Task<RecoverySnapshot> PrepareNewSnapshotAsync(
        ILiveGpuPowerController controller,
        string journalPath,
        FileRecoveryJournalStore store,
        LivePowerProfile profile,
        HelperBehavior behavior)
    {
        var readyForNewTarget = behavior == HelperBehavior.PersistentToggle
            ? controller.State.CurrentLimitMilliwatts == controller.State.DefaultLimitMilliwatts
            : controller.State.CanStartNewCanary;
        if (!readyForNewTarget)
        {
            throw new InvalidOperationException("The GPU is below its verified default but no valid protected recovery journal exists.");
        }

        var snapshot = behavior == HelperBehavior.PersistentToggle
            ? PortableLivePowerPolicy.CreateSnapshot(controller.State, profile, DateTimeOffset.UtcNow)
            : LiveCanaryPolicy.CreateSnapshot(controller.State, profile, DateTimeOffset.UtcNow);
        await store.SaveAsync(journalPath, snapshot);
        return snapshot;
    }

    private static LivePowerProfile ValidateFixedRecoverySnapshot(
        RecoverySnapshot snapshot,
        LiveCanaryDeviceState device)
    {
        LiveCanaryPolicy.ValidateRecoverySnapshot(snapshot, device);
        return LivePowerProfiles.ResolveTarget(snapshot.TargetPowerLimitMilliwatts);
    }

    private static LiveRecoveryDrillPhase GetTargetPhase(HelperBehavior behavior) =>
        behavior switch
        {
            HelperBehavior.RecoveryDrill => LiveRecoveryDrillPhase.CrashAfterVerifiedTarget,
            HelperBehavior.PersistentToggle => LiveRecoveryDrillPhase.PauseAtVerifiedTarget,
            _ => throw new InvalidOperationException("The helper received an unknown fixed behavior.")
        };

    private static async Task SendReadyAsync(Stream pipe, DrillContext context)
    {
        await SendStatusAsync(pipe, succeeded: true, "host-initialization", exception: null);
        await BoundedJsonPipe.WriteAsync(
            pipe,
            new LiveRecoveryDrillBootstrap(
                context.Session.Bootstrap,
                context.Snapshot.SnapshotId,
                context.Operation,
                context.Phase,
                context.Profile.Id,
                context.Device.Name,
                context.Device.DeviceFingerprint.Value,
                context.Device.DefaultLimitMilliwatts,
                context.Profile.TargetLimitMilliwatts,
                context.Device.MinimumLimitMilliwatts,
                context.Device.MaximumLimitMilliwatts));
        context.Session.Bootstrap.Dispose();
    }

    private static Task SendStatusAsync(
        Stream pipe,
        bool succeeded,
        string stage,
        Exception? exception) =>
        BoundedJsonPipe.WriteAsync(
            pipe,
            new HardwareHostElevationStatus(
                succeeded,
                stage,
                exception is null
                    ? $"The helper completed {stage}."
                    : $"The helper stopped safely ({exception.GetType().Name})."));

    private static string GetStateDirectory(string stateDirectoryName)
    {
        var commonApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.CommonApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);
        return Path.GetFullPath(Path.Combine(commonApplicationData, stateDirectoryName));
    }

    private static ILiveGpuPowerController OpenSinglePortableController()
    {
        Exception? nvidiaFailure = null;
        try
        {
            return NvidiaLivePowerController.OpenSinglePortableDevice();
        }
        catch (Exception exception) when (exception is InvalidOperationException or DllNotFoundException)
        {
            nvidiaFailure = exception;
        }

        try
        {
            return AmdLivePowerController.OpenSinglePortableDevice();
        }
        catch (Exception amdFailure) when (amdFailure is InvalidOperationException or DllNotFoundException)
        {
            throw new InvalidOperationException(
                $"No supported live GPU controller is ready. NVIDIA: {nvidiaFailure?.Message} AMD: {amdFailure.Message}");
        }
    }

    private static void EnsureProtectedDirectory(string path)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(path));
        if (directory.Exists)
        {
            if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidOperationException("The protected live-canary directory is a reparse point.");
            }

            FileSystemAclExtensions.SetAccessControl(directory, CreateAdministratorOnlySecurity());
            return;
        }

        FileSystemAclExtensions.Create(directory, CreateAdministratorOnlySecurity());
    }

    private static void EnsureOnlyKnownArtifacts(string stateDirectory) =>
        RecoveryArtifacts.CleanInterruptedWrites(stateDirectory, LiveCanaryPolicy.JournalFileName);

    private static void RejectReparsePoint(string path)
    {
        if (new FileInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException("The protected live-canary journal is a reparse point.");
        }
    }

    private static bool TryCleanRecoveryArtifacts(string stateDirectory)
    {
        try
        {
            var directory = new DirectoryInfo(Path.GetFullPath(stateDirectory));
            if (!directory.Exists || directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return false;
            }

            EnsureOnlyKnownArtifacts(stateDirectory);
            File.Delete(Path.Combine(stateDirectory, LiveCanaryPolicy.JournalFileName));
            Directory.Delete(stateDirectory, recursive: false);
            return true;
        }
#pragma warning disable CA1031 // Cleanup status is reported without masking exact restoration.
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }

    private static DirectorySecurity CreateAdministratorOnlySecurity()
    {
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, domainSid: null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, domainSid: null);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(administrators);
        security.AddAccessRule(CreateFullControlRule(administrators));
        security.AddAccessRule(CreateFullControlRule(system));
        return security;
    }

    private static FileSystemAccessRule CreateFullControlRule(IdentityReference identity) =>
        new(
            identity,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow);

    private static LaunchArguments ParseLaunch(string[] args)
    {
        if (args.Length != 8 ||
            args[0] != "--pipe" ||
            args[2] != "--parent-pid" ||
            !int.TryParse(args[3], out var parentProcessId) ||
            args[4] != "--behavior" ||
            args[6] != "--profile")
        {
            throw new ArgumentException("The elevated helper received invalid launch arguments.", nameof(args));
        }

        var behavior = args[5] switch
        {
            "recovery-drill" => HelperBehavior.RecoveryDrill,
            "persistent-toggle" => HelperBehavior.PersistentToggle,
            "recovery-only" => HelperBehavior.RecoveryOnly,
            _ => throw new ArgumentException("The elevated helper received an unknown behavior.", nameof(args))
        };
        var requestedProfileId = args[7];
        if (behavior == HelperBehavior.RecoveryDrill &&
            LivePowerProfiles.ResolveId(requestedProfileId) != LivePowerProfiles.Default)
        {
            throw new ArgumentException("The recovery drill accepts only its fixed 400 W profile.", nameof(args));
        }
        if (behavior is HelperBehavior.PersistentToggle or HelperBehavior.RecoveryOnly)
        {
            _ = PortableLivePowerPolicy.ParseRequest(requestedProfileId);
        }

        HardwareHostPipeName.Validate(args[1]);
        HardwareHostPipePeer.ValidateExpectedProcessId(parentProcessId);
        return new LaunchArguments(args[1], parentProcessId, behavior, requestedProfileId);
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private sealed record LaunchArguments(
        string PipeName,
        int ParentProcessId,
        HelperBehavior Behavior,
        string RequestedProfileId);

    private enum HelperBehavior
    {
        RecoveryDrill,
        PersistentToggle,
        RecoveryOnly
    }

    private sealed class DrillContext(
        string stateDirectory,
        string journalPath,
        RecoverySnapshot snapshot,
        LiveCanaryDeviceState device,
        LivePowerProfile profile,
        LiveRecoveryDrillPhase phase,
        HardwareHostOperation operation,
        IRecoveryJournalStore store,
        ILiveGpuPowerController controller,
        HardwareHostTransportSession session,
        HardwareHostCommandProcessor processor) : IDisposable
    {
        public string StateDirectory { get; } = stateDirectory;

        public string JournalPath { get; } = journalPath;

        public RecoverySnapshot Snapshot { get; } = snapshot;

        public LiveCanaryDeviceState Device { get; } = device;

        public LivePowerProfile Profile { get; } = profile;

        public LiveRecoveryDrillPhase Phase { get; } = phase;

        public HardwareHostOperation Operation { get; } = operation;

        public IRecoveryJournalStore Store { get; } = store;

        public ILiveGpuPowerController Controller { get; } = controller;

        public HardwareHostTransportSession Session { get; } = session;

        public HardwareHostCommandProcessor Processor { get; } = processor;

        public void Dispose()
        {
            Session.Dispose();
            Controller.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}

internal sealed class DrillTargetRegistry(
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
