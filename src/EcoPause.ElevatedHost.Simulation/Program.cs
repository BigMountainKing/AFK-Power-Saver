using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using EcoPause.Core.Activation;
using EcoPause.Core.Recovery;
using EcoPause.CrashRecovery.SimulationModel;
using EcoPause.HardwareHost.Protocol;
using EcoPause.HardwareHost.Transport;

namespace EcoPause.ElevatedHost.Simulation;

[SupportedOSPlatform("windows")]
internal static class Program
{
    private const int NormalCommandCount = 4;
    private const int GeneralFailureExitCode = 10;
    private const int ConnectionFailureExitCode = 12;
    private const int PeerVerificationFailureExitCode = 13;
    private const int InitializationFailureExitCode = 14;
    private const int IntentionalCrashExitCode = 91;
    private const string CrashRootDirectoryName = "EcoPauseCrashRecoverySimulation";

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
#pragma warning disable CA1031 // A distinct exit code identifies pre-channel connection failure.
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

                return launch.Mode switch
                {
                    ElevatedHostMode.NormalLifecycle => await RunNormalLifecycleAsync(pipe),
                    ElevatedHostMode.CrashAfterActivation => await RunCrashActivationAsync(pipe, launch.RunId),
                    ElevatedHostMode.RecoverAfterCrash => await RunCrashRecoveryAsync(pipe, launch.RunId),
                    ElevatedHostMode.PersistentToggle => await RunPersistentToggleAsync(pipe, launch.RunId),
                    _ => GeneralFailureExitCode
                };
            }
        }
#pragma warning disable CA1031 // The hidden helper exits safely on every boundary failure.
        catch (Exception)
#pragma warning restore CA1031
        {
            return GeneralFailureExitCode;
        }
    }

    private static async Task<int> RunNormalLifecycleAsync(Stream pipe)
    {
        HostContext? context = await TryCreateContextAsync(
            pipe,
            CreateNormalContextAsync);
        if (context is null)
        {
            return InitializationFailureExitCode;
        }

        using (context)
        {
            await SendReadyAsync(pipe, context);
            for (var index = 0; index < NormalCommandCount; index++)
            {
                await ProcessOneCommandAsync(pipe, context.Processor);
            }

            var journal = await context.Store.LoadAsync(context.JournalPath);
            var observed = await context.Controller.ReadPowerLimitMilliwattsAsync();
            var safeFinalState = observed == 450_000 && journal.Stage == RecoveryStage.Restored;
            await BoundedJsonPipe.WriteAsync(
                pipe,
                new HardwareHostElevationSummary(
                    PeerVerified: true,
                    observed,
                    journal.Stage.ToString(),
                    safeFinalState));
            return safeFinalState ? 0 : 1;
        }
    }

    private static async Task<int> RunCrashActivationAsync(Stream pipe, Guid? runId)
    {
        if (runId is null)
        {
            return InitializationFailureExitCode;
        }

        HostContext? context = await TryCreateContextAsync(
            pipe,
            () => CreateCrashActivationContextAsync(runId.Value));
        if (context is null)
        {
            return InitializationFailureExitCode;
        }

        using (context)
        {
            await SendReadyAsync(pipe, context);
            var result = await ProcessOneCommandAsync(pipe, context.Processor);
            if (result.Outcome != HardwareHostProcessOutcome.Executed ||
                result.Execution?.Outcome != ActivationOutcome.Activated)
            {
                return 1;
            }

            await pipe.FlushAsync();
            Environment.Exit(IntentionalCrashExitCode);
            return IntentionalCrashExitCode;
        }
    }

    private static async Task<int> RunCrashRecoveryAsync(Stream pipe, Guid? runId)
    {
        if (runId is null)
        {
            return InitializationFailureExitCode;
        }

        HostContext? context = await TryCreateContextAsync(
            pipe,
            () => CreateCrashRecoveryContextAsync(runId.Value));
        if (context is null)
        {
            return InitializationFailureExitCode;
        }

        using (context)
        {
            await SendReadyAsync(pipe, context);
            var result = await ProcessOneCommandAsync(pipe, context.Processor);
            var journal = await context.Store.LoadAsync(context.JournalPath);
            var observed = await context.Controller.ReadPowerLimitMilliwattsAsync();
            var safeFinalState = result.Outcome == HardwareHostProcessOutcome.Executed &&
                result.Execution?.Outcome == ActivationOutcome.Restored &&
                observed == CrashSimulationProfile.OriginalLimitMilliwatts &&
                journal.Stage == RecoveryStage.Restored;
            var artifactsCleaned = safeFinalState && TryCleanCrashArtifacts(runId.Value);

            await BoundedJsonPipe.WriteAsync(
                pipe,
                new HardwareHostCrashRecoverySummary(
                    PeerVerified: true,
                    observed,
                    journal.Stage.ToString(),
                    safeFinalState,
                    artifactsCleaned));
            return safeFinalState && artifactsCleaned ? 0 : 1;
        }
    }

    private static async Task<int> RunPersistentToggleAsync(Stream pipe, Guid? runId)
    {
        if (runId is null)
        {
            return InitializationFailureExitCode;
        }

        PersistentToggleProfile selectedProfile;
        try
        {
            selectedProfile = PersistentToggleProfiles.ResolveRunId(runId.Value);
        }
        catch (ArgumentException exception)
        {
            await SendStatusAsync(pipe, succeeded: false, "host-initialization", exception);
            return InitializationFailureExitCode;
        }

        var pendingProfiles = PersistentToggleProfiles.All
            .Where(profile => ProtectedRunDirectoryExists(profile.RunId))
            .ToArray();
        if (pendingProfiles.Length > 1)
        {
            await SendStatusAsync(
                pipe,
                succeeded: false,
                "host-initialization",
                new InvalidOperationException("Multiple protected fake toggle profiles are pending."));
            return InitializationFailureExitCode;
        }

        var restoring = pendingProfiles.Length == 1;
        var activeProfile = restoring ? pendingProfiles[0] : selectedProfile;
        HostContext? context = await TryCreateContextAsync(
            pipe,
            restoring
                ? () => CreateToggleRecoveryContextAsync(activeProfile)
                : () => CreateToggleActivationContextAsync(activeProfile));
        if (context is null)
        {
            return InitializationFailureExitCode;
        }

        using (context)
        {
            var operation = restoring
                ? HardwareHostOperation.RestorePending
                : HardwareHostOperation.ActivatePrepared;
            await SendToggleReadyAsync(pipe, context, operation);
            var result = await ProcessOneCommandAsync(pipe, context.Processor);
            var journal = await context.Store.LoadAsync(context.JournalPath);
            var observed = await context.Controller.ReadPowerLimitMilliwattsAsync();

            var paused = !restoring &&
                result.Outcome == HardwareHostProcessOutcome.Executed &&
                result.Execution?.Outcome == ActivationOutcome.Activated &&
                observed == activeProfile.TargetLimitMilliwatts &&
                journal.Stage == RecoveryStage.Applied;
            var restored = restoring &&
                result.Outcome == HardwareHostProcessOutcome.Executed &&
                result.Execution?.Outcome is ActivationOutcome.Restored or ActivationOutcome.AlreadyRestored &&
                observed == CrashSimulationProfile.OriginalLimitMilliwatts &&
                journal.Stage == RecoveryStage.Restored;
            var artifactsCleaned = restored && TryCleanCrashArtifacts(activeProfile.RunId);
            var safeFinalState = paused || (restored && artifactsCleaned);
            var toggleState = paused ? "Paused" : restored ? "Restored" : "Unverified";

            await BoundedJsonPipe.WriteAsync(
                pipe,
                new HardwareHostPersistentToggleSummary(
                    PeerVerified: true,
                    toggleState,
                    activeProfile.Id,
                    activeProfile.TargetLimitMilliwatts,
                    observed,
                    journal.Stage.ToString(),
                    safeFinalState,
                    artifactsCleaned));
            return safeFinalState ? 0 : 1;
        }
    }

    private static async Task<HostContext?> TryCreateContextAsync(
        Stream pipe,
        Func<Task<HostContext>> createContext)
    {
        try
        {
            return await createContext();
        }
#pragma warning disable CA1031 // Report a sanitized startup stage before the helper exits.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            await SendStatusAsync(pipe, succeeded: false, "host-initialization", exception);
            return null;
        }
    }

    private static async Task<HostContext> CreateNormalContextAsync()
    {
        var stateDirectory = Path.Combine(
            Path.GetTempPath(),
            "EcoPause",
            "ElevationSimulation",
            Guid.NewGuid().ToString("N"));
        var journalPath = Path.Combine(stateDirectory, "recovery.json");
        var fingerprint = DeviceFingerprint.FromStableIdentifier("ELEVATED-SIMULATED-GPU");
        var snapshot = RecoverySnapshot.Prepare(
            DateTimeOffset.UtcNow,
            "Simulation",
            "ElevatedInMemory",
            fingerprint,
            originalPowerLimitMilliwatts: 450_000,
            targetPowerLimitMilliwatts: 400_000,
            minimumPowerLimitMilliwatts: 150_000,
            maximumPowerLimitMilliwatts: 600_000);
        var store = new FileRecoveryJournalStore();
        await store.SaveAsync(journalPath, snapshot);
        var controller = new SimulatedPowerController(fingerprint, 450_000);
        return CreateContext(snapshot, journalPath, store, controller);
    }

    private static async Task<HostContext> CreateCrashActivationContextAsync(Guid runId)
    {
        var runDirectory = CreateProtectedRunDirectory(runId, requireNew: true);
        var journalPath = Path.Combine(runDirectory, CrashSimulationProfile.JournalFileName);
        var statePath = Path.Combine(runDirectory, CrashSimulationProfile.HardwareStateFileName);
        var snapshot = CrashSimulationProfile.CreateSnapshot(runId, DateTimeOffset.UtcNow);
        var store = new FileRecoveryJournalStore();
        var controller = new PersistentFakePowerController(statePath, snapshot.DeviceFingerprint);
        await controller.InitializeAsync();
        await store.SaveAsync(journalPath, snapshot);
        return CreateContext(snapshot, journalPath, store, controller);
    }

    private static async Task<HostContext> CreateCrashRecoveryContextAsync(Guid runId)
    {
        var runDirectory = CreateProtectedRunDirectory(runId, requireNew: false);
        var journalPath = Path.Combine(runDirectory, CrashSimulationProfile.JournalFileName);
        var statePath = Path.Combine(runDirectory, CrashSimulationProfile.HardwareStateFileName);
        var store = new FileRecoveryJournalStore();
        var snapshot = await store.LoadAsync(journalPath);
        CrashSimulationProfile.ValidatePendingSnapshot(snapshot, runId);
        var controller = new PersistentFakePowerController(statePath, snapshot.DeviceFingerprint);
        var observed = await controller.ReadPowerLimitMilliwattsAsync();
        if (observed != CrashSimulationProfile.TargetLimitMilliwatts)
        {
            throw new InvalidOperationException("The persistent fake hardware is not at the expected interrupted limit.");
        }

        return CreateContext(snapshot, journalPath, store, controller);
    }

    private static async Task<HostContext> CreateToggleRecoveryContextAsync(
        PersistentToggleProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var runDirectory = CreateProtectedRunDirectory(profile.RunId, requireNew: false);
        var journalPath = Path.Combine(runDirectory, CrashSimulationProfile.JournalFileName);
        var statePath = Path.Combine(runDirectory, CrashSimulationProfile.HardwareStateFileName);
        var store = new FileRecoveryJournalStore();
        var snapshot = await store.LoadAsync(journalPath);
        CrashSimulationProfile.ValidateToggleSnapshot(snapshot, profile);
        var controller = new PersistentFakePowerController(statePath, snapshot.DeviceFingerprint);
        var observed = await controller.ReadPowerLimitMilliwattsAsync();
        if (observed != CrashSimulationProfile.OriginalLimitMilliwatts &&
            observed != profile.TargetLimitMilliwatts)
        {
            throw new InvalidOperationException("The persistent fake toggle is not at a recoverable fixed limit.");
        }

        return CreateContext(snapshot, journalPath, store, controller);
    }

    private static async Task<HostContext> CreateToggleActivationContextAsync(
        PersistentToggleProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var runDirectory = CreateProtectedRunDirectory(profile.RunId, requireNew: true);
        var journalPath = Path.Combine(runDirectory, CrashSimulationProfile.JournalFileName);
        var statePath = Path.Combine(runDirectory, CrashSimulationProfile.HardwareStateFileName);
        var snapshot = CrashSimulationProfile.CreateToggleSnapshot(profile, DateTimeOffset.UtcNow);
        var store = new FileRecoveryJournalStore();
        var controller = new PersistentFakePowerController(statePath, snapshot.DeviceFingerprint);
        await controller.InitializeAsync();
        await store.SaveAsync(journalPath, snapshot);
        return CreateContext(snapshot, journalPath, store, controller);
    }

    private static HostContext CreateContext(
        RecoverySnapshot snapshot,
        string journalPath,
        IRecoveryJournalStore store,
        IGpuPowerController controller)
    {
        var registry = new FileHardwareHostTargetRegistry(store, controller, journalPath);
        var session = HardwareHostSessionFactory.CreateTransportSession();
        var processor = new HardwareHostCommandProcessor(
            session.Host,
            registry,
            new GpuActivationEngine(store));
        return new HostContext(
            snapshot.SnapshotId,
            journalPath,
            store,
            controller,
            session,
            processor);
    }

    private static async Task SendReadyAsync(Stream pipe, HostContext context)
    {
        await SendStatusAsync(pipe, succeeded: true, "host-initialization", exception: null);
        await BoundedJsonPipe.WriteAsync(
            pipe,
            new HardwareHostTransportBootstrap(
                context.Session.Bootstrap,
                context.SnapshotId));
        context.Session.Bootstrap.Dispose();
    }

    private static async Task SendToggleReadyAsync(
        Stream pipe,
        HostContext context,
        HardwareHostOperation operation)
    {
        await SendStatusAsync(pipe, succeeded: true, "host-initialization", exception: null);
        await BoundedJsonPipe.WriteAsync(
            pipe,
            new HardwareHostPersistentToggleBootstrap(
                context.Session.Bootstrap,
                context.SnapshotId,
                operation));
        context.Session.Bootstrap.Dispose();
    }

    private static async Task<HardwareHostProcessResult> ProcessOneCommandAsync(
        Stream pipe,
        HardwareHostCommandProcessor processor)
    {
        var envelope = await BoundedJsonPipe.ReadAsync<HardwareHostCommandEnvelope>(pipe);
        var result = await processor.ProcessAsync(envelope);
        await BoundedJsonPipe.WriteAsync(
            pipe,
            new HardwareHostWireResult(
                result.Outcome,
                result.Execution?.Outcome.ToString(),
                Describe(result.Outcome)));
        return result;
    }

    private static string CreateProtectedRunDirectory(Guid runId, bool requireNew)
    {
        var commonApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.CommonApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);
        var root = Path.GetFullPath(Path.Combine(commonApplicationData, CrashRootDirectoryName));
        EnsureProtectedDirectory(root, requireNew: false);

        var runDirectory = CrashSimulationPaths.ResolveRunDirectory(root, runId);
        if (requireNew && Directory.Exists(runDirectory))
        {
            throw new InvalidOperationException("The crash-simulation run already exists.");
        }

        EnsureProtectedDirectory(runDirectory, requireNew);
        return runDirectory;
    }

    private static bool ProtectedRunDirectoryExists(Guid runId)
    {
        var commonApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.CommonApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);
        var root = Path.GetFullPath(Path.Combine(commonApplicationData, CrashRootDirectoryName));
        var runDirectory = CrashSimulationPaths.ResolveRunDirectory(root, runId);
        return Directory.Exists(runDirectory);
    }

    private static void EnsureProtectedDirectory(string path, bool requireNew)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(path));
        if (directory.Exists)
        {
            if (requireNew || directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidOperationException("The protected simulation directory is not in the expected state.");
            }

            FileSystemAclExtensions.SetAccessControl(directory, CreateAdministratorOnlySecurity());
            return;
        }

        FileSystemAclExtensions.Create(directory, CreateAdministratorOnlySecurity());
    }

    private static DirectorySecurity CreateAdministratorOnlySecurity()
    {
        var administrators = new SecurityIdentifier(
            WellKnownSidType.BuiltinAdministratorsSid,
            domainSid: null);
        var system = new SecurityIdentifier(
            WellKnownSidType.LocalSystemSid,
            domainSid: null);
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

    private static bool TryCleanCrashArtifacts(Guid runId)
    {
        try
        {
            var commonApplicationData = Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData,
                Environment.SpecialFolderOption.DoNotVerify);
            var root = Path.GetFullPath(Path.Combine(commonApplicationData, CrashRootDirectoryName));
            var runDirectory = CrashSimulationPaths.ResolveRunDirectory(root, runId);
            if (!Directory.Exists(runDirectory) ||
                new DirectoryInfo(runDirectory).Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return false;
            }

            File.Delete(Path.Combine(runDirectory, CrashSimulationProfile.JournalFileName));
            File.Delete(Path.Combine(runDirectory, CrashSimulationProfile.HardwareStateFileName));
            Directory.Delete(runDirectory, recursive: false);

            if (Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any())
            {
                Directory.Delete(root, recursive: false);
            }

            return true;
        }
#pragma warning disable CA1031 // Cleanup status is returned without masking verified restoration.
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
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

    private static LaunchArguments ParseLaunch(string[] args)
    {
        if (args.Length < 4 ||
            args[0] != "--pipe" ||
            args[2] != "--parent-pid" ||
            !int.TryParse(args[3], out var parentProcessId))
        {
            throw new ArgumentException("The elevated helper received invalid launch arguments.", nameof(args));
        }

        HardwareHostPipeName.Validate(args[1]);
        HardwareHostPipePeer.ValidateExpectedProcessId(parentProcessId);
        if (args.Length == 4)
        {
            return new LaunchArguments(
                args[1],
                parentProcessId,
                ElevatedHostMode.NormalLifecycle,
                RunId: null);
        }

        if (args.Length != 10 ||
            args[4] != "--scenario" ||
            args[5] != "crash-recovery" ||
            args[6] != "--mode" ||
            args[8] != "--run-id")
        {
            throw new ArgumentException("The elevated helper received an invalid simulation mode.", nameof(args));
        }

        var mode = args[7] switch
        {
            "activate-crash" => ElevatedHostMode.CrashAfterActivation,
            "recover" => ElevatedHostMode.RecoverAfterCrash,
            "toggle" => ElevatedHostMode.PersistentToggle,
            _ => throw new ArgumentException("The elevated helper received an unknown simulation mode.", nameof(args))
        };
        var runId = CrashSimulationPaths.ParseRunId(args[9]);
        return new LaunchArguments(args[1], parentProcessId, mode, runId);
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static string Describe(HardwareHostProcessOutcome outcome) => outcome switch
    {
        HardwareHostProcessOutcome.Executed => "Authenticated elevated command completed.",
        HardwareHostProcessOutcome.RejectedReplay => "One-use elevated command rejected.",
        HardwareHostProcessOutcome.RejectedAuthentication => "Elevated command authentication failed.",
        _ => "Elevated command did not complete."
    };

    private enum ElevatedHostMode
    {
        NormalLifecycle,
        CrashAfterActivation,
        RecoverAfterCrash,
        PersistentToggle
    }

    private sealed record LaunchArguments(
        string PipeName,
        int ParentProcessId,
        ElevatedHostMode Mode,
        Guid? RunId);

    private sealed class HostContext(
        Guid snapshotId,
        string journalPath,
        IRecoveryJournalStore store,
        IGpuPowerController controller,
        HardwareHostTransportSession session,
        HardwareHostCommandProcessor processor) : IDisposable
    {
        public Guid SnapshotId { get; } = snapshotId;

        public string JournalPath { get; } = journalPath;

        public IRecoveryJournalStore Store { get; } = store;

        public IGpuPowerController Controller { get; } = controller;

        public HardwareHostTransportSession Session { get; } = session;

        public HardwareHostCommandProcessor Processor { get; } = processor;

        public void Dispose()
        {
            Session.Dispose();
            GC.SuppressFinalize(this);
        }
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
