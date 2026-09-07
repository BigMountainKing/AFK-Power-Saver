using System.IO;
using System.Runtime.Versioning;
using EcoPause.Desktop.Safety;
using Microsoft.Win32;

namespace EcoPause.Desktop;

[SupportedOSPlatform("windows")]
internal static class StartupLaunchRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsEnabled()
    {
        var executablePath = GetCurrentExecutablePath();
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        var savedCommand = key?.GetValue(
            StartupLaunchPolicy.RegistryValueName,
            defaultValue: null,
            RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        return StartupLaunchPolicy.MatchesCurrentExecutable(savedCommand, executablePath);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("The Windows startup registry key is unavailable.");
        if (enabled)
        {
            var command = StartupLaunchPolicy.BuildCommand(GetCurrentExecutablePath());
            key.SetValue(StartupLaunchPolicy.RegistryValueName, command, RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(StartupLaunchPolicy.RegistryValueName, throwOnMissingValue: false);
        }
    }

    private static string GetCurrentExecutablePath() =>
        Environment.ProcessPath is { Length: > 0 } path
            ? Path.GetFullPath(path)
            : throw new InvalidOperationException("The current AFK Power Saver executable path is unavailable.");
}
