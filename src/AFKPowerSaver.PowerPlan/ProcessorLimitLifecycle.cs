using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.Versioning;

namespace AFKPowerSaver.PowerPlan;

public static class ProcessorLimitPolicy
{
    public const int MinimumPercent = 20;
    public const int MaximumPercent = 99;
    public const int DefaultPercent = 80;

    public static int ValidateTarget(int percentage)
    {
        if (percentage is < MinimumPercent or > MaximumPercent)
        {
            throw new ArgumentOutOfRangeException(
                nameof(percentage),
                percentage,
                $"The CPU ceiling must be between {MinimumPercent} and {MaximumPercent} percent.");
        }

        return percentage;
    }

    public static (uint AcPercent, uint DcPercent) CalculateTargets(
        ProcessorMaximumStateSnapshot original,
        int requestedPercent)
    {
        ArgumentNullException.ThrowIfNull(original);
        original.Validate();
        var target = (uint)ValidateTarget(requestedPercent);
        return (
            Math.Min(original.AcMaximumPercent, target),
            Math.Min(original.DcMaximumPercent, target));
    }
}

public sealed record ProcessorLimitRecoveryRecord(
    int Version,
    Guid SchemeId,
    uint AcMaximumPercent,
    uint DcMaximumPercent)
{
    public const int CurrentVersion = 1;

    public static ProcessorLimitRecoveryRecord FromSnapshot(ProcessorMaximumStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot.Validate();
        return new(CurrentVersion, snapshot.SchemeId, snapshot.AcMaximumPercent, snapshot.DcMaximumPercent);
    }

    public ProcessorMaximumStateSnapshot ToSnapshot()
    {
        if (Version != CurrentVersion)
        {
            throw new InvalidOperationException("The CPU recovery journal version is unsupported.");
        }

        var snapshot = new ProcessorMaximumStateSnapshot(SchemeId, AcMaximumPercent, DcMaximumPercent);
        snapshot.Validate();
        return snapshot;
    }
}

public sealed record ProcessorLimitApplyResult(
    ProcessorMaximumStateSnapshot Original,
    ProcessorMaximumStateSnapshot Applied,
    bool Changed);

public static class ProcessorLimitRecoveryJournal
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    public static string GetPath()
    {
        var local = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);
        return Path.GetFullPath(Path.Combine(local, "AFK Power Saver", "cpu-canary-recovery.json"));
    }

    public static bool Exists() => File.Exists(GetPath());

    public static void Save(ProcessorMaximumStateSnapshot original)
    {
        var path = GetPath();
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("The CPU recovery directory is unavailable.");
        Directory.CreateDirectory(directory);
        var temporary = path + ".new";
        File.WriteAllText(
            temporary,
            JsonSerializer.Serialize(ProcessorLimitRecoveryRecord.FromSnapshot(original), Options));
        File.Move(temporary, path, overwrite: true);
    }

    public static ProcessorMaximumStateSnapshot Load()
    {
        var path = GetPath();
        var file = new FileInfo(path);
        if (!file.Exists || file.Length is <= 0 or > 4096)
        {
            throw new InvalidOperationException("The CPU recovery journal has an invalid size.");
        }

        var record = JsonSerializer.Deserialize<ProcessorLimitRecoveryRecord>(File.ReadAllText(path), Options)
            ?? throw new InvalidOperationException("The CPU recovery journal is empty.");
        return record.ToSnapshot();
    }

    public static void Delete()
    {
        var path = GetPath();
        File.Delete(path);
        File.Delete(path + ".new");
    }
}

[SupportedOSPlatform("windows")]
public static class WindowsProcessorLimitLifecycle
{
    public static ProcessorLimitApplyResult ApplyAndVerify(int requestedPercent)
    {
        _ = ProcessorLimitPolicy.ValidateTarget(requestedPercent);
        if (ProcessorLimitRecoveryJournal.Exists())
        {
            throw new InvalidOperationException(
                "A pending CPU restoration exists. Restore it before applying another ceiling.");
        }

        var original = WindowsProcessorPowerPlanReader.ReadActive();
        var (acTarget, dcTarget) = ProcessorLimitPolicy.CalculateTargets(original, requestedPercent);
        if (acTarget == original.AcMaximumPercent && dcTarget == original.DcMaximumPercent)
        {
            return new ProcessorLimitApplyResult(original, original, Changed: false);
        }

        ProcessorLimitRecoveryJournal.Save(original);
        try
        {
            var applied = WindowsProcessorPowerPlanWriter.WriteAndActivate(
                original.SchemeId,
                acTarget,
                dcTarget);
            if (applied.AcMaximumPercent != acTarget || applied.DcMaximumPercent != dcTarget)
            {
                throw new InvalidOperationException("The CPU ceiling did not read back exactly.");
            }

            return new ProcessorLimitApplyResult(original, applied, Changed: true);
        }
        catch
        {
            TryEmergencyRestore(original);
            throw;
        }
    }

    public static ProcessorMaximumStateSnapshot? RestorePendingAndVerify()
    {
        if (!ProcessorLimitRecoveryJournal.Exists())
        {
            return null;
        }

        var original = ProcessorLimitRecoveryJournal.Load();
        var restored = WindowsProcessorPowerPlanWriter.RestoreAndVerify(original);
        ProcessorLimitRecoveryJournal.Delete();
        return restored;
    }

    private static void TryEmergencyRestore(ProcessorMaximumStateSnapshot original)
    {
        try
        {
            _ = WindowsProcessorPowerPlanWriter.RestoreAndVerify(original);
            ProcessorLimitRecoveryJournal.Delete();
        }
        catch
        {
            // Preserve the journal so the next startup can prioritize exact restoration.
        }
    }
}
