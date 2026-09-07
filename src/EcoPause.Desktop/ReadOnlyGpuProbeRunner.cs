using System.Diagnostics;
using System.IO;
using System.Text;
using EcoPause.Desktop.Safety;

namespace EcoPause.Desktop;

internal static class ReadOnlyGpuProbeRunner
{
    private const int MaximumErrorCharacters = 4 * 1024;

    public static async Task<ReadOnlyGpuProbeResult> RunAsync()
    {
        var (solutionRoot, probePath) = FindProbeExecutable();
        var startInfo = new ProcessStartInfo(probePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = solutionRoot
        };
        startInfo.ArgumentList.Add("--json");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The read-only GPU probe could not be started.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        try
        {
            var outputTask = ReadBoundedAsync(
                process.StandardOutput,
                ReadOnlyGpuProbeParser.MaximumJsonCharacters,
                timeout.Token);
            var errorTask = ReadBoundedAsync(
                process.StandardError,
                MaximumErrorCharacters,
                timeout.Token);
            await Task.WhenAll(process.WaitForExitAsync(timeout.Token), outputTask, errorTask);

            var output = await outputTask;
            var error = await errorTask;
            if (!string.IsNullOrWhiteSpace(error))
            {
                throw new ReadOnlyGpuProbeDataException("The read-only GPU probe reported an unexpected error.");
            }

            var report = ReadOnlyGpuProbeParser.Parse(output, DateTimeOffset.UtcNow);
            if (process.ExitCode == 0 && !report.Ready)
            {
                throw new ReadOnlyGpuProbeDataException("The read-only GPU probe returned inconsistent process status.");
            }

            if (process.ExitCode != 0 && report.Ready)
            {
                throw new ReadOnlyGpuProbeDataException("The read-only GPU probe failed despite reporting ready data.");
            }

            return report;
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }
    }

    private static async Task<string> ReadBoundedAsync(
        TextReader reader,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        var result = new StringBuilder();
        var buffer = new char[1024];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
            {
                return result.ToString();
            }

            if (result.Length + read > maximumCharacters)
            {
                throw new ReadOnlyGpuProbeDataException("The read-only GPU probe exceeded its output limit.");
            }

            result.Append(buffer, 0, read);
        }
    }

    private static (string SolutionRoot, string ProbePath) FindProbeExecutable()
    {
        var colocated = Path.Combine(AppContext.BaseDirectory, "AFKPowerSaver.Probe.exe");
        if (File.Exists(colocated))
        {
            return (AppContext.BaseDirectory, colocated);
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
        var probePath = Path.Combine(
            directory.FullName,
            "src",
            "EcoPause.Probe",
            "bin",
            configuration,
            "net10.0",
            "AFKPowerSaver.Probe.exe");
        if (!File.Exists(probePath))
        {
            throw new FileNotFoundException(
                "Build the complete solution before requesting live read-only GPU data.",
                probePath);
        }

        return (directory.FullName, probePath);
    }
}
