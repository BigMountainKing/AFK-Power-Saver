using System.Diagnostics;
using System.Globalization;
using System.IO;
using AFKPowerSaver.PowerPlan;

namespace EcoPause.Desktop;

internal sealed class CpuRecoveryCompanion : IAsyncDisposable
{
    private Process? _process;
    private bool _ready;
    public async Task EnsureReadyAsync()
    {
        if (_ready && _process is { HasExited: false }) return;
        if (_process is not null) await DisposeAsync();
        _ready = false;
        var executable = FindExecutable();
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        using var parent = Process.GetCurrentProcess();
        start.ArgumentList.Add(parent.Id.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(parent.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(WindowsProcessorLimitLifecycle.OwnerId.ToString("N"));
        _process = Process.Start(start) ?? throw new InvalidOperationException("CPU recovery companion did not start.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var ready = await _process.StandardOutput.ReadLineAsync(timeout.Token);
            if (ready != "READY " + WindowsProcessorLimitLifecycle.OwnerId.ToString("N"))
                throw new InvalidOperationException("CPU recovery companion did not acknowledge this desktop session.");
            _ready = true;
        }
        catch
        {
            _process.StandardInput.Close();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is null) return;
        _ready = false;
        using var process = _process;
        _process = null;
        if (!process.HasExited) process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await process.WaitForExitAsync(timeout.Token);
        if (process.ExitCode != 0)
            throw new InvalidOperationException("CPU companion recovery failed; the journal is retained.");
    }

    private static string FindExecutable()
    {
        const string name = "AFKPowerSaver.CpuRecovery.exe";
        var colocated = Path.Combine(AppContext.BaseDirectory, name);
        if (File.Exists(colocated)) return colocated;
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EcoPause.sln"))) directory = directory.Parent;
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var path = Path.Combine(directory?.FullName ?? "", "src", "AFKPowerSaver.CpuRecovery", "bin", configuration, "net10.0", name);
        return File.Exists(path) ? path : throw new FileNotFoundException("Build or reinstall the CPU recovery companion.");
    }
}
