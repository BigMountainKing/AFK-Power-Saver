namespace EcoPause.Desktop.Safety;

public static class StartupLaunchPolicy
{
    public const string RegistryValueName = "AFKPowerSaver";
    public const string StartupArgument = "--startup";

    public static string BuildCommand(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        var fullPath = Path.GetFullPath(executablePath);
        if (!string.Equals(Path.GetExtension(fullPath), ".exe", StringComparison.OrdinalIgnoreCase) ||
            fullPath.Contains('"', StringComparison.Ordinal))
        {
            throw new ArgumentException("The startup executable path is invalid.", nameof(executablePath));
        }

        return $"\"{fullPath}\" {StartupArgument}";
    }

    public static bool MatchesCurrentExecutable(string? savedCommand, string executablePath) =>
        string.Equals(
            savedCommand?.Trim(),
            BuildCommand(executablePath),
            StringComparison.OrdinalIgnoreCase);
}
