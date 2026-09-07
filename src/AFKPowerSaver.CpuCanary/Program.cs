using System.ComponentModel;
using System.Runtime.Versioning;
using System.Text.Json;
using AFKPowerSaver.PowerPlan;

namespace AFKPowerSaver.CpuCanary;

internal static class Program
{
    private const uint TargetPercent = 80;
    private const string Confirmation = "APPLY GLOBAL CPU 80 PERCENT THEN RESTORE EXACTLY";

    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            return RunReadOnlyPreflight();
        }
        if (args.Length == 1 && string.Equals(args[0], "--run-live-cpu-canary", StringComparison.Ordinal))
        {
            return RunCanary();
        }

        Console.Error.WriteLine("Usage: AFK Power Saver CPU Preflight [--run-live-cpu-canary]");
        return 2;
    }

    private static int RunReadOnlyPreflight()
    {
        Console.WriteLine("AFK Power Saver CPU Maximum Processor State Preflight 1.16");
        Console.WriteLine("Mode: READ ONLY - no power-plan write API is called");
        Console.WriteLine();

        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Preflight state: BLOCKED - Windows is required.");
            return 1;
        }

        try
        {
            var snapshot = WindowsProcessorPowerPlanReader.ReadActive();
            PrintSnapshot(snapshot);
            Console.WriteLine("Read-only CPU power-plan preflight: PASS");
            Console.WriteLine("Power-plan settings changed: NONE");
            Console.WriteLine($"Fixed canary target: {TargetPercent}% AC / {TargetPercent}% DC");
            return 0;
        }
        catch (Exception exception) when (
            exception is Win32Exception or InvalidOperationException or ArgumentException)
        {
            Console.Error.WriteLine($"Read-only CPU preflight: BLOCKED - {exception.Message}");
            Console.Error.WriteLine("Power-plan settings changed: NONE");
            return 1;
        }
    }

    private static int RunCanary()
    {
        Console.WriteLine("AFK Power Saver CPU Maximum Processor State Canary 1.16");
        Console.WriteLine($"Fixed target: {TargetPercent}% AC / {TargetPercent}% DC");
        Console.WriteLine("Automatic hold: 5 seconds");
        Console.WriteLine();

        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Canary state: BLOCKED - Windows is required.");
            return 1;
        }

        var journalPath = CpuRecoveryJournal.GetPath();
        ProcessorMaximumStateSnapshot? original = null;
        try
        {
            if (File.Exists(journalPath))
            {
                Console.WriteLine("Pending CPU recovery journal detected: RECOVERY FIRST");
                var pending = CpuRecoveryJournal.Load(journalPath);
                _ = WindowsProcessorPowerPlanWriter.RestoreAndVerify(pending.ToSnapshot());
                CpuRecoveryJournal.Delete(journalPath);
                Console.WriteLine("Exact original AC/DC processor states restored: PASS");
                Console.WriteLine("Recovery journal cleaned: True");
                Console.WriteLine("CPU recovery-only run: PASS");
                return 0;
            }

            original = WindowsProcessorPowerPlanReader.ReadActive();
            PrintSnapshot(original);
            Console.WriteLine("Canary state: READY");
            Console.WriteLine();
            Console.WriteLine("GLOBAL CPU PERFORMANCE WARNING");
            Console.WriteLine("This changes the active Windows power plan for the whole PC for five seconds.");
            Console.WriteLine("It is a performance-state ceiling, not a CPU utilization limit or exact GHz value.");
            Console.WriteLine($"Type exactly: {Confirmation}");
            Console.Write("> ");
            if (!string.Equals(Console.ReadLine(), Confirmation, StringComparison.Ordinal))
            {
                Console.WriteLine("Confirmation did not match. Nothing changed.");
                return 2;
            }

            CpuRecoveryJournal.Save(journalPath, CpuRecoveryRecord.FromSnapshot(original));
            Console.WriteLine("Exact-restoration journal written before change: PASS");
            try
            {
                var applied = WindowsProcessorPowerPlanWriter.WriteAndActivate(original.SchemeId, TargetPercent, TargetPercent);
                if (applied.AcMaximumPercent != TargetPercent || applied.DcMaximumPercent != TargetPercent)
                {
                    throw new InvalidOperationException("The temporary processor-state values did not read back exactly.");
                }
                Console.WriteLine($"{TargetPercent}% AC/DC values applied and read back: PASS");
                for (var seconds = 5; seconds > 0; seconds--)
                {
                    Console.WriteLine($"Restoring in {seconds}...");
                    Thread.Sleep(TimeSpan.FromSeconds(1));
                }

                _ = WindowsProcessorPowerPlanWriter.RestoreAndVerify(original);
                CpuRecoveryJournal.Delete(journalPath);
                Console.WriteLine("Exact original AC/DC values restored and read back: PASS");
                Console.WriteLine("Recovery journal cleaned: True");
                Console.WriteLine("Global CPU maximum processor state canary: PASS");
                return 0;
            }
            catch
            {
                TryEmergencyRestore(original, journalPath);
                throw;
            }
        }
        catch (Exception exception) when (
            exception is Win32Exception or InvalidOperationException or ArgumentException or
            IOException or UnauthorizedAccessException or JsonException)
        {
            Console.Error.WriteLine($"Global CPU canary: STOPPED - {exception.Message}");
            Console.Error.WriteLine(File.Exists(journalPath)
                ? "Recovery journal retained. Run this same command again to restore first."
                : "Recovery journal absent; no pending CPU restoration remains.");
            return 1;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void TryEmergencyRestore(
        ProcessorMaximumStateSnapshot original,
        string journalPath)
    {
        try
        {
            _ = WindowsProcessorPowerPlanWriter.RestoreAndVerify(original);
            CpuRecoveryJournal.Delete(journalPath);
            Console.WriteLine("Emergency exact restoration: PASS");
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            Console.Error.WriteLine($"Emergency exact restoration incomplete: {exception.Message}");
        }
    }

    private static void PrintSnapshot(ProcessorMaximumStateSnapshot snapshot)
    {
        Console.WriteLine($"Active power scheme: {snapshot.SchemeId:D}");
        Console.WriteLine($"Maximum processor state on AC: {snapshot.AcMaximumPercent}%");
        Console.WriteLine($"Maximum processor state on DC: {snapshot.DcMaximumPercent}%");
    }
}

internal sealed record CpuRecoveryRecord(
    int Version,
    Guid SchemeId,
    uint AcMaximumPercent,
    uint DcMaximumPercent)
{
    public static CpuRecoveryRecord FromSnapshot(ProcessorMaximumStateSnapshot snapshot) =>
        new(1, snapshot.SchemeId, snapshot.AcMaximumPercent, snapshot.DcMaximumPercent);

    public ProcessorMaximumStateSnapshot ToSnapshot()
    {
        if (Version != 1)
        {
            throw new InvalidOperationException("The CPU recovery journal version is unsupported.");
        }
        var snapshot = new ProcessorMaximumStateSnapshot(SchemeId, AcMaximumPercent, DcMaximumPercent);
        snapshot.Validate();
        return snapshot;
    }
}

internal static class CpuRecoveryJournal
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static string GetPath()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local, "AFK Power Saver", "cpu-canary-recovery.json");
    }

    public static void Save(string path, CpuRecoveryRecord record)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("The CPU recovery directory is unavailable.");
        Directory.CreateDirectory(directory);
        var temporary = path + ".new";
        File.WriteAllText(temporary, JsonSerializer.Serialize(record, Options));
        File.Move(temporary, path, overwrite: true);
    }

    public static CpuRecoveryRecord Load(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length is <= 0 or > 4096)
        {
            throw new InvalidOperationException("The CPU recovery journal has an invalid size.");
        }
        return JsonSerializer.Deserialize<CpuRecoveryRecord>(File.ReadAllText(path), Options)
            ?? throw new InvalidOperationException("The CPU recovery journal is empty.");
    }

    public static void Delete(string path)
    {
        File.Delete(path);
        File.Delete(path + ".new");
    }
}
