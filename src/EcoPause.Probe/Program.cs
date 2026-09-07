using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using EcoPause.Core.Activation;
using EcoPause.Hardware.Abstractions;
using EcoPause.Hardware.Amd;
using EcoPause.Hardware.Nvidia;

if (!ProbeOptions.TryParse(args, out var options, out var optionError))
{
    Console.Error.WriteLine(optionError);
    Console.Error.WriteLine("Run with --help for usage.");
    return 2;
}

if (options.ShowHelp)
{
    PrintHelp();
    return 0;
}

var probe = new AutoGpuProbe();
if (options.DryRunTargetMilliwatts is not null)
{
    try
    {
        var coordinator = new DryRunActivationCoordinator(probe);
        var journalPath = options.JournalPath ?? Path.Combine(
            Environment.CurrentDirectory,
            "artifacts",
            "dry-run",
            "recovery-plan.json");

        var plan = await coordinator.CreatePlanAsync(
            options.GpuIndex,
            options.DryRunTargetMilliwatts.Value,
            journalPath);

        PrintDryRunPlan(plan);
        return 0;
    }
    catch (ActivationPlanningException exception)
    {
        Console.Error.WriteLine($"Dry-run planning failed: {exception.Message}");
        if (exception.InnerException is not null)
        {
            Console.Error.WriteLine($"Safety check: {exception.InnerException.Message}");
        }

        return 1;
    }
}

var report = await probe.ProbeAsync();
if (options.Json)
{
    var serializerOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    Console.WriteLine(JsonSerializer.Serialize(report, serializerOptions));
}
else
{
    PrintReport(report);
}

return report.Status == GpuProbeStatus.Ready ? 0 : 1;

static void PrintHelp()
{
    Console.WriteLine("AFK Power Saver Hardware Probe");
    Console.WriteLine();
    Console.WriteLine("Read-only probe:");
    Console.WriteLine("  AFKPowerSaver.Probe [--json]");
    Console.WriteLine();
    Console.WriteLine("Persist a dry-run recovery plan without changing hardware:");
    Console.WriteLine("  AFKPowerSaver.Probe --dry-run-watts <watts> [--gpu-index <index>] [--journal <path>]");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --json                  Emit a machine-readable, non-identifying probe report.");
    Console.WriteLine("  --dry-run-watts <watts> Validate and persist a dry-run plan.");
    Console.WriteLine("  --gpu-index <index>     Select a GPU; defaults to 0.");
    Console.WriteLine("  --journal <path>        Choose the dry-run journal location.");
    Console.WriteLine("  --help                  Show this help.");
}

static void PrintDryRunPlan(DryRunActivationPlan plan)
{
    var snapshot = plan.Snapshot;

    Console.WriteLine("AFK Power Saver Dry-Run Activation Plan 0.3");
    Console.WriteLine("Mode: DRY RUN (no hardware write functions were called)");
    Console.WriteLine();
    Console.WriteLine($"GPU:              {plan.DeviceName}");
    Console.WriteLine($"Original limit:   {snapshot.OriginalPowerLimitMilliwatts / 1000m:0.###} W");
    Console.WriteLine($"Proposed target:  {snapshot.TargetPowerLimitMilliwatts / 1000m:0.###} W");
    Console.WriteLine($"Permitted range:  {snapshot.MinimumPowerLimitMilliwatts / 1000m:0.###} W - " +
                      $"{snapshot.MaximumPowerLimitMilliwatts / 1000m:0.###} W");
    Console.WriteLine($"Journal purpose:  {snapshot.Purpose}");
    Console.WriteLine($"Journal stage:    {snapshot.Stage}");
    Console.WriteLine($"Recovery pending: {snapshot.IsRecoveryPending}");
    Console.WriteLine($"Plan file:        {plan.JournalPath}");
    Console.WriteLine();
    Console.WriteLine("The plan was written and read back successfully. No hardware settings were changed.");
}

static void PrintReport(GpuProbeReport report)
{
    Console.WriteLine("AFK Power Saver Hardware Probe 0.1");
    Console.WriteLine("Mode: READ-ONLY (this build contains no hardware mutation functions)");
    Console.WriteLine();
    Console.WriteLine($"Provider: {report.Provider}");
    Console.WriteLine($"Backend:  {report.Backend}");
    Console.WriteLine($"Status:   {report.Status}");
    Console.WriteLine($"Details:  {report.Message}");

    foreach (var device in report.Devices)
    {
        Console.WriteLine();
        Console.WriteLine($"GPU {device.Index}");
        Console.WriteLine($"  Model: {device.Name}");
        Console.WriteLine($"  Power-limit control: {(device.Power.IsSupported ? "Supported" : "Unavailable")}");

        if (device.Power.IsSupported)
        {
            if (device.Power.LimitKind == GpuPowerLimitKind.DefaultRelativePercentage)
            {
                Console.WriteLine($"  Current limit: {device.Power.CurrentRelativePercentage}% of factory");
                Console.WriteLine($"  Default limit: {device.Power.DefaultRelativePercentage}% of factory");
                Console.WriteLine($"  Permitted range: {device.Power.MinimumRelativePercentage}% - " +
                                  $"{device.Power.MaximumRelativePercentage}% of factory " +
                                  $"(step {device.Power.RelativePercentageStep}%)");
            }
            else
            {
                Console.WriteLine($"  Current limit: {device.Power.CurrentLimit}");
                Console.WriteLine($"  Default limit: {device.Power.DefaultLimit}");
                Console.WriteLine($"  Permitted range: {device.Power.MinimumLimit} - {device.Power.MaximumLimit}");
            }
            Console.WriteLine($"  Current draw: {device.Power.CurrentUsage?.ToString() ?? "Not reported"}");
        }
        else
        {
            Console.WriteLine($"  Reason: {device.Power.UnavailableReason}");
        }
    }

    Console.WriteLine();
    Console.WriteLine("No hardware settings were changed.");
}

internal sealed record ProbeOptions(
    bool ShowHelp,
    bool Json,
    uint? DryRunTargetMilliwatts,
    uint GpuIndex,
    string? JournalPath)
{
    public static bool TryParse(
        string[] arguments,
        out ProbeOptions options,
        out string error)
    {
        var showHelp = false;
        var json = false;
        uint? dryRunTarget = null;
        uint gpuIndex = 0;
        string? journalPath = null;

        for (var index = 0; index < arguments.Length; index++)
        {
            switch (arguments[index].ToLowerInvariant())
            {
                case "--help":
                case "-h":
                    showHelp = true;
                    break;
                case "--json":
                    json = true;
                    break;
                case "--dry-run-watts":
                    if (!TryReadValue(arguments, ref index, out var wattsText) ||
                        !TryConvertWattsToMilliwatts(wattsText, out var milliwatts))
                    {
                        options = Empty;
                        error = "--dry-run-watts requires a positive watt value with at most three decimal places.";
                        return false;
                    }

                    dryRunTarget = milliwatts;
                    break;
                case "--gpu-index":
                    if (!TryReadValue(arguments, ref index, out var gpuIndexText) ||
                        !uint.TryParse(gpuIndexText, NumberStyles.None, CultureInfo.InvariantCulture, out gpuIndex))
                    {
                        options = Empty;
                        error = "--gpu-index requires a non-negative integer.";
                        return false;
                    }

                    break;
                case "--journal":
                    if (!TryReadValue(arguments, ref index, out journalPath))
                    {
                        options = Empty;
                        error = "--journal requires a file path.";
                        return false;
                    }

                    break;
                default:
                    options = Empty;
                    error = $"Unknown option: {arguments[index]}";
                    return false;
            }
        }

        if (json && dryRunTarget is not null)
        {
            options = Empty;
            error = "--json cannot be combined with --dry-run-watts.";
            return false;
        }

        if (journalPath is not null && dryRunTarget is null)
        {
            options = Empty;
            error = "--journal is only valid with --dry-run-watts.";
            return false;
        }

        options = new ProbeOptions(showHelp, json, dryRunTarget, gpuIndex, journalPath);
        error = string.Empty;
        return true;
    }

    private static ProbeOptions Empty { get; } = new(false, false, null, 0, null);

    private static bool TryReadValue(string[] arguments, ref int index, out string value)
    {
        if (index + 1 >= arguments.Length || arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            value = string.Empty;
            return false;
        }

        value = arguments[++index];
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryConvertWattsToMilliwatts(string text, out uint milliwatts)
    {
        milliwatts = 0;
        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var watts) || watts <= 0)
        {
            return false;
        }

        var scaled = watts * 1000m;
        if (scaled != decimal.Truncate(scaled) || scaled > uint.MaxValue)
        {
            return false;
        }

        milliwatts = (uint)scaled;
        return true;
    }
}

internal sealed class AutoGpuProbe : IGpuHardwareProbe
{
    public string ProviderName => "Auto";

    public async Task<GpuProbeReport> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var nvidia = await new NvidiaGpuProbe().ProbeAsync(cancellationToken);
        if (nvidia.Status == GpuProbeStatus.Ready && nvidia.Devices.Count > 0)
        {
            return nvidia;
        }

        var amd = await new AmdGpuProbe().ProbeAsync(cancellationToken);
        if (amd.Status == GpuProbeStatus.Ready && amd.Devices.Count > 0)
        {
            return amd;
        }

        return new GpuProbeReport(
            "GPU",
            "NVML+ADLX",
            GpuProbeStatus.BackendUnavailable,
            $"No supported NVIDIA or AMD GPU backend is ready. NVIDIA: {nvidia.Message} AMD: {amd.Message}",
            []);
    }
}
