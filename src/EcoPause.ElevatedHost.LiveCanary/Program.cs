using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using EcoPause.Core.Activation;
using EcoPause.Core.Recovery;
using EcoPause.Hardware.Nvidia.Control;
using EcoPause.HardwareHost.Protocol;
using EcoPause.HardwareHost.Transport;
using EcoPause.LiveCanary.Model;

namespace EcoPause.ElevatedHost.LiveCanary;

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
#pragma warning disable CA1031 // A distinct exit code identifies a pre-channel connection failure.
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
#pragma warning disable CA1031 // A distinct exit code identifies a failed verified-channel acknowledgement.
                catch (Exception)
#pragma warning restore CA1031
                {
                    return PeerVerificationFailureExitCode;
                }

                CanaryContext? context = null;
                LiveHardwareProcessLock? hardwareLock = null;
                try
                {
                    hardwareLock = LiveHardwareProcessLock.Acquire();
                    context = await CreateContextAsync();
                }
#pragma warning disable CA1031 // Initialization failures are reported without exposing device identity.
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
                    return await RunOneFixedOperationAsync(pipe, context);
                }
            }
        }
#pragma warning disable CA1031 // The hidden helper fails closed at its outermost boundary.
        catch (Exception)
#pragma warning restore CA1031
        {
            return GeneralFailureExitCode;
        }
    }

    private static async Task<int> RunOneFixedOperationAsync(Stream pipe, CanaryContext context)
    {
        var envelope = await BoundedJsonPipe.ReadAsync<HardwareHostCommandEnvelope>(pipe);
        HardwareHostProcessResult commandResult;
        if (envelope.Command.Operation != context.Operation)
        {
            commandResult = new HardwareHostProcessResult(
                HardwareHostProcessOutcome.RejectedTarget,
                "The command operation did not match the helper-selected live-canary mode.",
                Execution: null);
        }
        else
        {
            commandResult = await context.Processor.ProcessAsync(envelope);
        }

        var targetAppliedAndVerified =
            context.Mode == LiveCanaryMode.Canary &&
            commandResult.Outcome == HardwareHostProcessOutcome.Executed &&
            commandResult.Execution?.Outcome == ActivationOutcome.Activated;
        ActivationResult? restoration = context.Mode == LiveCanaryMode.RecoveryOnly
            ? commandResult.Execution
            : null;
        var emergencyRestoreVerified = false;

        if (context.Mode == LiveCanaryMode.Canary &&
            commandResult.Execution?.HardwareWriteAttempted == true)
        {
            try
            {
                if (targetAppliedAndVerified)
                {
                    await Task.Delay(LiveCanaryPolicy.HoldDurationMilliseconds, CancellationToken.None);
                }

                var latest = await context.Store.LoadAsync(context.JournalPath, CancellationToken.None);
                if (latest.IsRecoveryPending)
                {
                    restoration = await context.Engine.RestoreExpectedAsync(
                        context.JournalPath,
                        latest,
                        context.Controller,
                        CancellationToken.None);
                }
            }
#pragma warning disable CA1031 // A fixed emergency restore is mandatory after any possible write.
            catch (Exception)
#pragma warning restore CA1031
            {
                emergencyRestoreVerified = await TryEmergencyFixedRestoreAsync(context.Controller);
            }
        }

        var finalLimit = await context.Controller.ReadPowerLimitMilliwattsAsync(CancellationToken.None);
        RecoverySnapshot? finalSnapshot = null;
        try
        {
            finalSnapshot = await context.Store.LoadAsync(context.JournalPath, CancellationToken.None);
        }
#pragma warning disable CA1031 // The summary must distinguish safe hardware from an unreadable journal.
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        var originalRestoredAndVerified =
            finalLimit == LiveCanaryPolicy.RequiredOriginalLimitMilliwatts &&
            (restoration?.OriginalLimitVerified == true || emergencyRestoreVerified ||
             (context.Mode == LiveCanaryMode.Canary && commandResult.Execution?.OriginalLimitVerified == true));
        var recoveryStage = finalSnapshot?.Stage.ToString() ?? "Unavailable";
        var safeFinalState =
            originalRestoredAndVerified &&
            finalSnapshot?.Stage == RecoveryStage.Restored;
        var artifactsCleaned = safeFinalState && TryCleanRecoveryArtifacts(context.StateDirectory);
        var operationPassed = context.Mode switch
        {
            LiveCanaryMode.Canary => targetAppliedAndVerified,
            LiveCanaryMode.RecoveryOnly =>
                commandResult.Outcome == HardwareHostProcessOutcome.Executed &&
                commandResult.Execution?.Outcome is ActivationOutcome.Restored or ActivationOutcome.AlreadyRestored,
            _ => false
        };
        var passed = operationPassed && safeFinalState && artifactsCleaned;

        await BoundedJsonPipe.WriteAsync(
            pipe,
            new HardwareHostWireResult(
                commandResult.Outcome,
                commandResult.Execution?.Outcome.ToString(),
                commandResult.Message));
        await BoundedJsonPipe.WriteAsync(
            pipe,
            new LiveCanarySummary(
                PeerVerified: true,
                context.Mode,
                context.Device.Name,
                targetAppliedAndVerified,
                originalRestoredAndVerified,
                finalLimit,
                recoveryStage,
                commandResult.Execution?.Outcome.ToString(),
                restoration?.Outcome.ToString() ?? (emergencyRestoreVerified ? "EmergencyFixedRestore" : null),
                safeFinalState,
                artifactsCleaned));
        return passed ? 0 : 1;
    }

    private static async Task<bool> TryEmergencyFixedRestoreAsync(NvidiaLivePowerController controller)
    {
        try
        {
            await controller.SetPowerLimitMilliwattsAsync(
                LiveCanaryPolicy.RequiredOriginalLimitMilliwatts,
                CancellationToken.None);
            return await controller.ReadPowerLimitMilliwattsAsync(CancellationToken.None) ==
                LiveCanaryPolicy.RequiredOriginalLimitMilliwatts;
        }
#pragma warning disable CA1031 // The caller reports that emergency restoration was not verified.
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }

    private static async Task<CanaryContext> CreateContextAsync()
    {
        var stateDirectory = GetStateDirectory();
        EnsureProtectedDirectory(stateDirectory);
        EnsureOnlyKnownArtifacts(stateDirectory);
        var journalPath = Path.Combine(stateDirectory, LiveCanaryPolicy.JournalFileName);
        var store = new FileRecoveryJournalStore();
        var controller = NvidiaLivePowerController.OpenSingleCanaryDevice();

        try
        {
            RecoverySnapshot snapshot;
            LivePowerProfile profile;
            LiveCanaryMode mode;
            HardwareHostOperation operation;
            if (File.Exists(journalPath))
            {
                RejectReparsePoint(journalPath);
                snapshot = await store.LoadAsync(journalPath);
                if (snapshot.Stage == RecoveryStage.Restored)
                {
                    if (await controller.ReadPowerLimitMilliwattsAsync() !=
                        LiveCanaryPolicy.RequiredOriginalLimitMilliwatts ||
                        !TryCleanRecoveryArtifacts(stateDirectory))
                    {
                        throw new InvalidOperationException("A closed live-canary journal could not be cleaned safely.");
                    }

                    EnsureProtectedDirectory(stateDirectory);
                    snapshot = await PrepareNewSnapshotAsync(controller, journalPath, store);
                    profile = LivePowerProfiles.Default;
                    mode = LiveCanaryMode.Canary;
                    operation = HardwareHostOperation.ActivatePrepared;
                }
                else
                {
                    LiveCanaryPolicy.ValidateRecoverySnapshot(snapshot, controller.State);
                    profile = LivePowerProfiles.ResolveTarget(snapshot.TargetPowerLimitMilliwatts);
                    mode = LiveCanaryMode.RecoveryOnly;
                    operation = HardwareHostOperation.RestorePending;
                }
            }
            else
            {
                snapshot = await PrepareNewSnapshotAsync(controller, journalPath, store);
                profile = LivePowerProfiles.Default;
                mode = LiveCanaryMode.Canary;
                operation = HardwareHostOperation.ActivatePrepared;
            }

            var session = HardwareHostSessionFactory.CreateTransportSession();
            var engine = new GpuActivationEngine(store);
            var registry = new CanaryTargetRegistry(store, controller, journalPath);
            var processor = new HardwareHostCommandProcessor(session.Host, registry, engine);
            return new CanaryContext(
                stateDirectory,
                journalPath,
                snapshot,
                controller.State,
                profile,
                mode,
                operation,
                store,
                controller,
                session,
                engine,
                processor);
        }
        catch
        {
            controller.Dispose();
            throw;
        }
    }

    private static async Task<RecoverySnapshot> PrepareNewSnapshotAsync(
        NvidiaLivePowerController controller,
        string journalPath,
        FileRecoveryJournalStore store)
    {
        if (!controller.State.CanStartNewCanary)
        {
            throw new InvalidOperationException("The GPU is below 450 W but no valid protected recovery journal exists.");
        }

        var snapshot = LiveCanaryPolicy.CreateSnapshot(controller.State, DateTimeOffset.UtcNow);
        await store.SaveAsync(journalPath, snapshot);
        return snapshot;
    }

    private static async Task SendReadyAsync(Stream pipe, CanaryContext context)
    {
        await SendStatusAsync(pipe, succeeded: true, "host-initialization", exception: null);
        await BoundedJsonPipe.WriteAsync(
            pipe,
            new LiveCanaryBootstrap(
                context.Session.Bootstrap,
                context.Snapshot.SnapshotId,
                context.Operation,
                context.Mode,
                context.Profile.Id,
                context.Device.Name,
                context.Device.DeviceFingerprint.Value,
                LiveCanaryPolicy.RequiredOriginalLimitMilliwatts,
                context.Profile.TargetLimitMilliwatts,
                context.Device.MinimumLimitMilliwatts,
                context.Device.MaximumLimitMilliwatts,
                LiveCanaryPolicy.HoldDurationMilliseconds));
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

    private static string GetStateDirectory()
    {
        var commonApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.CommonApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);
        return Path.GetFullPath(Path.Combine(commonApplicationData, LiveCanaryPolicy.StateDirectoryName));
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
#pragma warning disable CA1031 // Cleanup status is reported without masking verified restoration.
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
        if (args.Length != 4 ||
            args[0] != "--pipe" ||
            args[2] != "--parent-pid" ||
            !int.TryParse(args[3], out var parentProcessId))
        {
            throw new ArgumentException("The elevated helper received invalid launch arguments.", nameof(args));
        }

        HardwareHostPipeName.Validate(args[1]);
        HardwareHostPipePeer.ValidateExpectedProcessId(parentProcessId);
        return new LaunchArguments(args[1], parentProcessId);
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private sealed record LaunchArguments(string PipeName, int ParentProcessId);

    private sealed class CanaryContext(
        string stateDirectory,
        string journalPath,
        RecoverySnapshot snapshot,
        LiveCanaryDeviceState device,
        LivePowerProfile profile,
        LiveCanaryMode mode,
        HardwareHostOperation operation,
        IRecoveryJournalStore store,
        NvidiaLivePowerController controller,
        HardwareHostTransportSession session,
        GpuActivationEngine engine,
        HardwareHostCommandProcessor processor) : IDisposable
    {
        public string StateDirectory { get; } = stateDirectory;

        public string JournalPath { get; } = journalPath;

        public RecoverySnapshot Snapshot { get; } = snapshot;

        public LiveCanaryDeviceState Device { get; } = device;

        public LivePowerProfile Profile { get; } = profile;

        public LiveCanaryMode Mode { get; } = mode;

        public HardwareHostOperation Operation { get; } = operation;

        public IRecoveryJournalStore Store { get; } = store;

        public NvidiaLivePowerController Controller { get; } = controller;

        public HardwareHostTransportSession Session { get; } = session;

        public GpuActivationEngine Engine { get; } = engine;

        public HardwareHostCommandProcessor Processor { get; } = processor;

        public void Dispose()
        {
            Session.Dispose();
            Controller.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}

internal sealed class CanaryTargetRegistry(
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
