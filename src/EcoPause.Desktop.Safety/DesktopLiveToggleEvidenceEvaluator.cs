using System.Globalization;
using EcoPause.Hardware.Abstractions;

namespace EcoPause.Desktop.Safety;

public enum DesktopLiveToggleState
{
    Unknown,
    Limited,
    Restored
}

public sealed record DesktopLiveToggleEvidenceResult(
    SimulationEvidenceOutcome Outcome,
    DesktopLiveToggleState State,
    DesktopLiveTargetOption? Target,
    string Message,
    IReadOnlyList<string> MissingEvidence)
{
    public bool Passed => Outcome == SimulationEvidenceOutcome.Passed;
}

public static class DesktopLiveToggleEvidenceEvaluator
{
    public const int MaximumTranscriptCharacters = 64 * 1024;

    private static readonly string[] CommonEvidence =
    [
        "AFK Power Saver Live GPU Limit Session 1.22",
        "Launcher privilege: administrator startup broker",
        "Desktop GPU control: PERCENTAGE LIMIT / EXACT DEFAULT RESTORE",
        "Desktop activation triggers: BUTTON, ASSIGNED GLOBAL HOTKEY, OR ENABLED IDLE TIMER",
        "Startup UAC session: PASS / mutually verified normal-user launcher",
        "Per-action confirmation: NOT REQUIRED / authorized button, hotkey, or enabled idle timer",
        "Startup-authorized broker launched the journal-selected helper without another UAC prompt.",
        "Helper exit: 0",
        "GPU lifecycle: PERCENTAGE LIMIT / EXACT DEFAULT RESTORE",
        "Idle activation hardware path: STARTUP-AUTHORIZED BROKER ONLY"
    ];

    public static DesktopLiveToggleEvidenceResult Evaluate(
        int exitCode,
        string standardOutput,
        string standardError)
    {
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);
        if (standardOutput.Length is <= 0 or > MaximumTranscriptCharacters)
        {
            return Failed("The live-toggle transcript exceeded its strict boundary.");
        }

        if (exitCode != 0 || !string.IsNullOrWhiteSpace(standardError))
        {
            return Failed($"The live-toggle launcher stopped safely with exit code {exitCode}.");
        }

        var allLines = standardOutput.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var lines = allLines.ToHashSet(StringComparer.Ordinal);
        var missing = CommonEvidence.Where(item => !lines.Contains(item)).ToList();
        if (!HasOneVerifiedProcessId(allLines))
        {
            missing.Add("One positive Windows-verified helper process ID");
        }

        var requestedPreflight = ParseOneProfile(allLines, "Requested limit profile: ");
        var requestedLaunch = ParseOneProfile(allLines, "Requested live profile: ");
        var helperProfile = ParseOneProfile(allLines, "Helper-selected live profile: ");
        if (requestedPreflight is null || requestedLaunch is null || requestedPreflight != requestedLaunch)
        {
            missing.Add("One consistent requested percentage profile");
        }
        if (helperProfile is null)
        {
            missing.Add("One helper-selected percentage profile");
        }

        var defaultLimit = ParseOneLimit(allLines, "Default limit: ");
        var currentLimit = ParseOneLimit(allLines, "Current limit: ");
        if (defaultLimit is null || currentLimit is null || defaultLimit.Kind != currentLimit.Kind)
        {
            missing.Add("One exact current/default power preflight");
        }

        var limited = helperProfile is not null && requestedLaunch == helperProfile &&
            defaultLimit is not null && currentLimit == defaultLimit &&
            helperProfile.LimitKind == defaultLimit.Kind &&
            IsCompleteLimit(lines, helperProfile);
        var restored = helperProfile is not null && defaultLimit is not null &&
            helperProfile.LimitKind == defaultLimit.Kind &&
            IsCompleteRestore(lines, helperProfile, defaultLimit);
        if (limited != restored && missing.Count == 0)
        {
            var state = limited ? DesktopLiveToggleState.Limited : DesktopLiveToggleState.Restored;
            return new DesktopLiveToggleEvidenceResult(
                SimulationEvidenceOutcome.Passed,
                state,
                helperProfile,
                limited
                    ? $"The real GPU is verified at {helperProfile!.FormattedLimit} with exact restoration pending."
                    : $"The real GPU is restored to its exact {defaultLimit!.Formatted} setting.",
                MissingEvidence: []);
        }

        missing.Add("One complete percentage limit or exact-default restoration result");
        return new DesktopLiveToggleEvidenceResult(
            SimulationEvidenceOutcome.IncompleteEvidence,
            DesktopLiveToggleState.Unknown,
            Target: null,
            "The launcher exited successfully but did not prove exactly one safe live GPU state.",
            missing.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static bool IsCompleteLimit(HashSet<string> lines, DesktopLiveTargetOption profile) =>
        lines.Contains("Preflight state: READY TO LIMIT") &&
        lines.Contains("Helper-selected action: ApplyVerifiedLimit") &&
        lines.Contains("Authenticated operation: Executed / Activated") &&
        lines.Contains($"Final helper limit: {profile.FormattedLimit}") &&
        lines.Contains("Recovery journal stage: Applied") &&
        lines.Contains($"{profile.FormattedLimit} exact persistent limit: PASS") &&
        lines.Contains("Protected recovery artifacts retained: PASS") &&
        lines.Contains($"Independent final live limit: {profile.FormattedLimit}") &&
        lines.Contains("Protected exact-restoration journal retained: PASS") &&
        lines.Contains("Live GPU toggle: PASS / Limited");

    private static bool IsCompleteRestore(
        HashSet<string> lines,
        DesktopLiveTargetOption profile,
        ParsedLimit defaultLimit)
    {
        var restoredExecution = lines.Contains("Authenticated operation: Executed / Restored") ||
            lines.Contains("Authenticated operation: Executed / AlreadyRestored");
        return restoredExecution &&
            lines.Contains("Helper-selected action: RestoreOriginal") &&
            lines.Contains($"Final helper limit: {defaultLimit.Formatted}") &&
            lines.Contains("Recovery journal stage: Restored") &&
            lines.Contains($"{defaultLimit.Formatted} exact restoration: PASS") &&
            lines.Contains("Protected recovery artifacts cleaned: True") &&
            lines.Contains($"Independent final live limit: {defaultLimit.Formatted}") &&
            lines.Contains("Live GPU toggle: PASS / Restored") &&
            (lines.Contains("Preflight state: RECOVERY EXPECTED") ||
                lines.Contains("Preflight state: READY TO LIMIT")) &&
            profile.Watts < defaultLimit.Value;
    }

    private static DesktopLiveTargetOption? ParseOneProfile(string[] lines, string prefix)
    {
        var matches = lines.Where(line => line.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1)
        {
            return null;
        }

        var parts = matches[0][prefix.Length..].Split(" / ", StringSplitOptions.None);
        if (parts.Length != 3 ||
            !parts[0].StartsWith("percent-", StringComparison.Ordinal) ||
            !int.TryParse(parts[0].AsSpan("percent-".Length), out var percentage) ||
            parts[1] != $"{percentage}%" ||
            !TryParseLimit(parts[2], out var limit) ||
            percentage is < 1 or > 99 ||
            limit.Value <= 0 ||
            limit.Kind == GpuPowerLimitKind.DefaultRelativePercentage &&
                limit.Value != percentage)
        {
            return null;
        }

        return new DesktopLiveTargetOption(percentage, limit.Value, limit.Kind);
    }

    private static ParsedLimit? ParseOneLimit(string[] lines, string prefix)
    {
        var matches = lines.Where(line => line.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        return matches.Length == 1 && TryParseLimit(matches[0][prefix.Length..], out var limit)
            ? limit
            : null;
    }

    private static bool TryParseLimit(string value, out ParsedLimit limit)
    {
        limit = default!;
        var kind = value.EndsWith(" W", StringComparison.Ordinal)
            ? GpuPowerLimitKind.AbsoluteWatts
            : value.EndsWith("% of factory", StringComparison.Ordinal)
                ? GpuPowerLimitKind.DefaultRelativePercentage
                : (GpuPowerLimitKind?)null;
        if (kind is null)
        {
            return false;
        }

        var suffixLength = kind == GpuPowerLimitKind.AbsoluteWatts ? 2 : 12;
        if (!decimal.TryParse(
                value.AsSpan(0, value.Length - suffixLength),
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var parsed))
        {
            return false;
        }

        limit = new ParsedLimit(parsed, kind.Value);
        return true;
    }

    private sealed record ParsedLimit(
        decimal Value,
        GpuPowerLimitKind Kind)
    {
        public string Formatted => Kind == GpuPowerLimitKind.DefaultRelativePercentage
            ? $"{Value:0.###}% of factory"
            : $"{Value:0.###} W";
    }

    private static bool HasOneVerifiedProcessId(string[] lines)
    {
        const string prefix = "Helper PID verified by Windows: PASS (";
        var matches = lines.Where(line => line.StartsWith(prefix, StringComparison.Ordinal) &&
            line.EndsWith(')')).ToArray();
        return matches.Length == 1 &&
            int.TryParse(matches[0].AsSpan(prefix.Length, matches[0].Length - prefix.Length - 1), out var processId) &&
            processId > 0;
    }

    private static DesktopLiveToggleEvidenceResult Failed(string message) =>
        new(
            SimulationEvidenceOutcome.ProcessFailed,
            DesktopLiveToggleState.Unknown,
            Target: null,
            message,
            MissingEvidence: []);
}
