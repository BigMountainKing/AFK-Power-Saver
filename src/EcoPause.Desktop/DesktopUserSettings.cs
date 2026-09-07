using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AFKPowerSaver.OverlayModel;
using AFKPowerSaver.PowerPlan;
using EcoPause.Desktop.Safety;

namespace EcoPause.Desktop;

[Flags]
internal enum HotKeyModifiers : uint
{
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Windows = 0x0008
}

internal sealed record HotKeyDefinition(
    HotKeyModifiers Modifiers,
    uint VirtualKey,
    string KeyName)
{
    public static HotKeyDefinition Default { get; } =
        new(HotKeyModifiers.Control | HotKeyModifiers.Shift, 0x72, "F3");

    public string DisplayName => $"{FormatModifiers(Modifiers)}+{KeyName}";

    public static string FormatModifiers(HotKeyModifiers modifiers)
    {
        var names = new List<string>(4);
        if (modifiers.HasFlag(HotKeyModifiers.Control)) names.Add("Ctrl");
        if (modifiers.HasFlag(HotKeyModifiers.Alt)) names.Add("Alt");
        if (modifiers.HasFlag(HotKeyModifiers.Shift)) names.Add("Shift");
        if (modifiers.HasFlag(HotKeyModifiers.Windows)) names.Add("Win");
        return string.Join('+', names);
    }
}

internal sealed record HotKeyModifierOption(HotKeyModifiers Value, string DisplayName)
{
    public override string ToString() => DisplayName;
}

internal sealed record HotKeyKeyOption(uint VirtualKey, string DisplayName)
{
    public override string ToString() => DisplayName;
}

internal static class HotKeyOptions
{
    public static IReadOnlyList<HotKeyModifierOption> Modifiers { get; } =
    [
        new(HotKeyModifiers.Control | HotKeyModifiers.Shift, "Ctrl + Shift"),
        new(HotKeyModifiers.Control | HotKeyModifiers.Alt, "Ctrl + Alt"),
        new(HotKeyModifiers.Alt | HotKeyModifiers.Shift, "Alt + Shift"),
        new(HotKeyModifiers.Control | HotKeyModifiers.Alt | HotKeyModifiers.Shift, "Ctrl + Alt + Shift"),
        new(HotKeyModifiers.Control | HotKeyModifiers.Windows, "Ctrl + Win"),
        new(HotKeyModifiers.Alt | HotKeyModifiers.Windows, "Alt + Win")
    ];

    public static IReadOnlyList<HotKeyKeyOption> Keys { get; } = CreateKeys();

    public static HotKeyDefinition Validate(HotKeyDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var modifiers = Modifiers.SingleOrDefault(option => option.Value == definition.Modifiers)
            ?? throw new InvalidOperationException("The saved hotkey modifier combination is not supported.");
        var key = Keys.SingleOrDefault(option =>
            option.VirtualKey == definition.VirtualKey &&
            string.Equals(option.DisplayName, definition.KeyName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("The saved hotkey key is not supported.");
        return new HotKeyDefinition(modifiers.Value, key.VirtualKey, key.DisplayName);
    }

    private static List<HotKeyKeyOption> CreateKeys()
    {
        var keys = new List<HotKeyKeyOption>();
        for (var index = 1; index <= 12; index++)
        {
            keys.Add(new HotKeyKeyOption((uint)(0x6F + index), $"F{index}"));
        }
        for (var key = 'A'; key <= 'Z'; key++)
        {
            keys.Add(new HotKeyKeyOption(key, key.ToString()));
        }
        return keys;
    }
}

internal enum CloseButtonPreference
{
    Ask = 0,
    MinimizeToTray = 1,
    ExitApplication = 2
}

internal sealed record DesktopUserSettings(
    HotKeyDefinition HotKey,
    int PowerPercentage,
    bool ScreenDimmingEnabled = false,
    int OverlayOpacityPercent = OverlayCanaryPolicy.DefaultOpacityPercent,
    string OverlayTargetId = DisplayTargetPolicy.AllDisplaysTargetId,
    bool CpuLimitEnabled = false,
    int CpuMaximumPercent = ProcessorLimitPolicy.DefaultPercent,
    bool IdleActivationEnabled = false,
    int IdleActivationMinutes = IdleActivationPolicy.DefaultMinutes,
    bool GpuLimitEnabled = true,
    bool HotKeyUserConfigured = false,
    bool DisplaySelectionConfigured = false,
    string[]? OverlayTargetIds = null,
    CloseButtonPreference CloseButtonPreference = CloseButtonPreference.Ask)
{
    public static DesktopUserSettings Default { get; } =
        new(HotKeyDefinition.Default, 89);
}

internal static class DesktopUserSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    public static DesktopUserSettings Load()
    {
        try
        {
            var path = GetLoadPath();
            if (!File.Exists(path))
            {
                return DesktopUserSettings.Default;
            }

            var json = File.ReadAllText(path);
            if (json.Length is <= 0 or > 16 * 1024)
            {
                return DesktopUserSettings.Default;
            }
            var settings = JsonSerializer.Deserialize<DesktopUserSettings>(json, JsonOptions);
            if (settings is null || settings.PowerPercentage is < 1 or > 99)
            {
                return DesktopUserSettings.Default;
            }

            var opacity = settings.OverlayOpacityPercent == 0
                ? OverlayCanaryPolicy.DefaultOpacityPercent
                : settings.OverlayOpacityPercent;
            if (opacity is < OverlayCanaryPolicy.MinimumOpacityPercent or > OverlayCanaryPolicy.MaximumOpacityPercent)
            {
                return DesktopUserSettings.Default;
            }

            var targetId = string.IsNullOrWhiteSpace(settings.OverlayTargetId) || settings.OverlayTargetId.Length > 256
                ? DisplayTargetPolicy.AllDisplaysTargetId
                : settings.OverlayTargetId;
            var cpuPercent = settings.CpuMaximumPercent == 0
                ? ProcessorLimitPolicy.DefaultPercent
                : settings.CpuMaximumPercent;
            _ = ProcessorLimitPolicy.ValidateTarget(cpuPercent);
            var idleMinutes = settings.IdleActivationMinutes == 0
                ? IdleActivationPolicy.DefaultMinutes
                : settings.IdleActivationMinutes;
            _ = IdleActivationPolicy.ValidateMinutes(idleMinutes);
            var normalizedDisplayIds = settings.DisplaySelectionConfigured
                ? (settings.OverlayTargetIds ?? [])
                    .Where(target => !string.IsNullOrWhiteSpace(target) && target.Length <= 256)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(16)
                    .ToArray()
                : [];
            if (!Enum.IsDefined(settings.CloseButtonPreference))
            {
                return DesktopUserSettings.Default;
            }
            var normalized = settings with
            {
                HotKey = HotKeyOptions.Validate(settings.HotKey),
                OverlayOpacityPercent = opacity,
                OverlayTargetId = targetId,
                CpuMaximumPercent = cpuPercent,
                IdleActivationMinutes = idleMinutes,
                OverlayTargetIds = normalizedDisplayIds
            };
            if (!string.Equals(path, GetPath(), StringComparison.OrdinalIgnoreCase))
            {
                TryMigrate(normalized);
            }
            return normalized;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
        {
            return DesktopUserSettings.Default;
        }
    }

    public static void Save(DesktopUserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _ = HotKeyOptions.Validate(settings.HotKey);
        if (settings.PowerPercentage is < 1 or > 99)
        {
            throw new ArgumentOutOfRangeException(nameof(settings));
        }
        if (settings.OverlayOpacityPercent is < OverlayCanaryPolicy.MinimumOpacityPercent or > OverlayCanaryPolicy.MaximumOpacityPercent)
        {
            throw new ArgumentOutOfRangeException(nameof(settings));
        }
        if (!string.IsNullOrWhiteSpace(settings.OverlayTargetId) && settings.OverlayTargetId.Length > 256)
        {
            throw new ArgumentException("The selected display identifier is invalid.", nameof(settings));
        }
        _ = ProcessorLimitPolicy.ValidateTarget(settings.CpuMaximumPercent);
        _ = IdleActivationPolicy.ValidateMinutes(settings.IdleActivationMinutes);
        if (settings.OverlayTargetIds is { Length: > 16 } ||
            settings.OverlayTargetIds?.Any(target => string.IsNullOrWhiteSpace(target) || target.Length > 256) == true)
        {
            throw new ArgumentException("The selected display identifiers are invalid.", nameof(settings));
        }
        if (!Enum.IsDefined(settings.CloseButtonPreference))
        {
            throw new ArgumentOutOfRangeException(nameof(settings));
        }

        var path = GetPath();
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("The settings directory is unavailable.");
        Directory.CreateDirectory(directory);
        var temporary = path + ".new";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporary, path, overwrite: true);
    }

    private static string GetPath()
    {
        var local = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);
        return Path.GetFullPath(Path.Combine(local, "AFK Power Saver", "settings.json"));
    }

    private static string GetLoadPath()
    {
        var currentPath = GetPath();
        if (File.Exists(currentPath))
        {
            return currentPath;
        }

        var local = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);
        var legacyPath = Path.GetFullPath(Path.Combine(local, "EcoPause", "settings.json"));
        return File.Exists(legacyPath) ? legacyPath : currentPath;
    }

    private static void TryMigrate(DesktopUserSettings settings)
    {
        try
        {
            Save(settings);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            // Loading valid legacy settings is more important than completing this best-effort migration.
        }
    }
}
