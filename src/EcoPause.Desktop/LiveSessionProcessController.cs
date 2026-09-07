using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using EcoPause.Desktop.Safety;
using EcoPause.Hardware.Abstractions;

namespace EcoPause.Desktop;

internal sealed record LiveSessionStartupResult(
    bool Succeeded,
    string Message,
    string StartupTranscript,
    string Error);

internal sealed record LiveSessionToggleResult(
    bool Succeeded,
    string Message,
    string Transcript,
    string Error,
    GpuOperationResult? Result);

internal sealed class LiveSessionProcessController : IAsyncDisposable
{
    private const int MaximumMessageCharacters = 128 * 1024;
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(2);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly Process _process;
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private bool _disposed;

    private LiveSessionProcessController(Process process)
    {
        _process = process;
    }

    public bool IsReady { get; private set; }

    public static async Task<(LiveSessionProcessController? Controller, LiveSessionStartupResult Result)>
        StartAsync()
    {
        var (solutionRoot, launcherPath) = FindLauncherExecutable();
        var startInfo = new ProcessStartInfo(launcherPath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
            WorkingDirectory = solutionRoot
        };
        startInfo.ArgumentList.Add("--desktop-live-session");
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The startup live-session launcher could not be started.");
        var controller = new LiveSessionProcessController(process);
        try
        {
            var message = await controller.ReadMessageAsync("ready", StartupTimeout);
            var result = new LiveSessionStartupResult(
                message.Succeeded,
                message.Message,
                message.Transcript,
                message.Error);
            if (!message.Succeeded)
            {
                await controller.DisposeAsync();
                return (null, result);
            }

            controller.IsReady = true;
            return (controller, result);
        }
        catch
        {
            await controller.DisposeAsync();
            throw;
        }
    }

    public async Task<LiveSessionToggleResult> ToggleAsync(DesktopLiveTargetOption target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Percentage is < 1 or > 99 ||
            target.ProfileId != $"percent-{target.Percentage}" ||
            target.Watts <= 0)
        {
            throw new InvalidOperationException("The live target is not a canonical percentage request.");
        }

        return await ExecuteCommandAsync($"TOGGLE {target.ProfileId}");
    }

    public Task<LiveSessionToggleResult> RestoreAsync() => ExecuteCommandAsync("RESTORE");

    private async Task<LiveSessionToggleResult> ExecuteCommandAsync(string command)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsReady)
        {
            throw new InvalidOperationException("The startup live session is not ready.");
        }

        await _commandLock.WaitAsync();
        try
        {
            await _process.StandardInput.WriteLineAsync(command);
            await _process.StandardInput.FlushAsync();
            var message = await ReadMessageAsync("result", CommandTimeout);
            return new LiveSessionToggleResult(
                message.Succeeded,
                message.Message,
                message.Transcript,
                message.Error,
                message.Result);
        }
        catch
        {
            IsReady = false;
            _process.StandardInput.Close();
            throw;
        }
        finally
        {
            _commandLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        IsReady = false;
        await _commandLock.WaitAsync();
        try
        {
            if (!_process.HasExited)
            {
                try
                {
                    await _process.StandardInput.WriteLineAsync("SHUTDOWN");
                    await _process.StandardInput.FlushAsync();
                    var shutdown = await ReadMessageAsync("shutdown", CommandTimeout);
                    if (!shutdown.Succeeded || shutdown.Result is not { IsValid: true } ||
                        shutdown.Result.State is not (GpuOperationState.Restored or GpuOperationState.Unchanged))
                    {
                        throw new InvalidOperationException(
                            string.IsNullOrWhiteSpace(shutdown.Error)
                                ? shutdown.Message
                                : shutdown.Error);
                    }
                }
                finally
                {
                    // Disconnect even when the response is invalid or restoration fails.
                    _process.StandardInput.Close();
                }
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try
                {
                    await _process.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    _process.Kill(entireProcessTree: false);
                }
            }
            else
                throw new InvalidOperationException("The GPU session exited before acknowledging recovery. Restart to verify the protected journal.");
        }
        finally
        {
            _commandLock.Release();
            _commandLock.Dispose();
            _process.Dispose();
        }
    }

    private async Task<DesktopSessionMessage> ReadMessageAsync(string expectedKind, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        var line = await _process.StandardOutput.ReadLineAsync(cancellation.Token)
            ?? throw new InvalidOperationException("The live-session launcher ended before responding.");
        if (line.Length is <= 0 or > MaximumMessageCharacters)
        {
            throw new InvalidOperationException("The live-session response exceeded its strict boundary.");
        }

        DesktopSessionMessage message;
        try
        {
            message = JsonSerializer.Deserialize<DesktopSessionMessage>(line, JsonOptions)
                ?? throw new InvalidOperationException("The live-session response was empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("The live-session response was malformed.", exception);
        }

        if (string.IsNullOrEmpty(message.Kind) ||
            message.Message is null ||
            message.Transcript is null ||
            message.Error is null ||
            !string.Equals(message.Kind, expectedKind, StringComparison.Ordinal) ||
            message.Message.Length > 512 ||
            message.Transcript.Length > DesktopLiveToggleEvidenceEvaluator.MaximumTranscriptCharacters ||
            message.Error.Length > 4 * 1024)
        {
            throw new InvalidOperationException("The live-session response failed strict validation.");
        }

        return message;
    }

    private static (string SolutionRoot, string LauncherPath) FindLauncherExecutable()
    {
        var colocated = Path.Combine(AppContext.BaseDirectory, "AFKPowerSaver.LiveSession.exe");
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
        var launcherPath = Path.Combine(
            directory.FullName,
            "src",
            "EcoPause.LiveRecoveryDrill",
            "bin",
            configuration,
            "net10.0",
            "AFKPowerSaver.LiveSession.exe");
        if (!File.Exists(launcherPath))
        {
            throw new FileNotFoundException("Build the complete solution before starting the live session.", launcherPath);
        }

        return (directory.FullName, launcherPath);
    }

    private sealed record DesktopSessionMessage(
        string Kind,
        bool Succeeded,
        string Message,
        string Transcript,
        string Error,
        GpuOperationResult? Result = null);
}
