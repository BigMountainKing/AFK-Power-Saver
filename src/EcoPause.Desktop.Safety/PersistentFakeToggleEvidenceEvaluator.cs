namespace EcoPause.Desktop.Safety;

public enum PersistentFakeToggleState
{
    Unknown,
    Paused,
    Restored
}

public sealed record PersistentFakeToggleEvidenceResult(
    SimulationEvidenceOutcome Outcome,
    PersistentFakeToggleState State,
    FakeTargetOption? Target,
    string Message,
    IReadOnlyList<string> MissingEvidence)
{
    public bool Passed => Outcome == SimulationEvidenceOutcome.Passed;
}

public sealed record PersistentFakeToggleReceipt(
    PersistentFakeToggleState State,
    FakeTargetOption? Target);

public static class PersistentFakeToggleEvidenceEvaluator
{
    private static readonly string[] CommonEvidence =
    [
        "EcoPause Persistent Fake Toggle Simulation 1.5",
        "Launcher privilege: normal user",
        "Hardware: persistent file-backed fake only",
        "Mutual process-ID verification: PASS",
        "Elevated helper startup: PASS",
        "Toggle helper exit: 0",
        "Persistent fake toggle: PASS",
        "Real NVIDIA access: NONE"
    ];

    private static readonly string[] PausedEvidence =
    [
        "Authenticated toggle: Executed / Activated",
        "Persistent fake state: Paused",
        "Recovery journal stage: Applied",
        "Protected simulation files cleaned: False"
    ];

    private static readonly string[] RestoredEvidence =
    [
        "Persistent fake state: Restored",
        "Final persistent fake limit: 450 W",
        "Recovery journal stage: Restored",
        "Protected simulation files cleaned: True"
    ];

    public static PersistentFakeToggleEvidenceResult Evaluate(
        int exitCode,
        string standardOutput,
        string standardError)
    {
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);

        if (exitCode == 4 && standardOutput.Contains("UAC was declined", StringComparison.Ordinal))
        {
            return new PersistentFakeToggleEvidenceResult(
                SimulationEvidenceOutcome.PermissionDeclined,
                PersistentFakeToggleState.Unknown,
                Target: null,
                "Windows permission was declined; the persistent fake state was unchanged.",
                MissingEvidence: []);
        }

        if (exitCode != 0 || !string.IsNullOrWhiteSpace(standardError))
        {
            return new PersistentFakeToggleEvidenceResult(
                SimulationEvidenceOutcome.ProcessFailed,
                PersistentFakeToggleState.Unknown,
                Target: null,
                $"The persistent fake toggle stopped safely with exit code {exitCode}.",
                MissingEvidence: []);
        }

        var lines = standardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
        var target = ResolveTarget(lines, "Fake target profile: ");
        var requestedTarget = ResolveTarget(lines, "Requested fake target profile: ");
        var targetEvidenceValid = target is not null && requestedTarget is not null &&
            lines.Contains($"Configured fake target: {target.Watts:0} W");
        var paused = targetEvidenceValid &&
            lines.Contains($"Final persistent fake limit: {target!.Watts:0} W") &&
            PausedEvidence.All(lines.Contains);
        var restoredExecution = lines.Contains("Authenticated toggle: Executed / Restored") ||
            lines.Contains("Authenticated toggle: Executed / AlreadyRestored");
        var restored = targetEvidenceValid && restoredExecution && RestoredEvidence.All(lines.Contains);
        var commonMissing = CommonEvidence.Where(item => !lines.Contains(item)).ToArray();

        if (paused != restored && commonMissing.Length == 0)
        {
            var state = paused ? PersistentFakeToggleState.Paused : PersistentFakeToggleState.Restored;
            return new PersistentFakeToggleEvidenceResult(
                SimulationEvidenceOutcome.Passed,
                state,
                target,
                state == PersistentFakeToggleState.Paused
                    ? $"The protected {target!.Watts:0} W fake pause is active and exact restoration remains pending."
                    : $"The {target!.Watts:0} W fake profile was restored exactly and its artifacts were cleaned.",
                MissingEvidence: []);
        }

        var missing = new List<string>(commonMissing);
        if (!targetEvidenceValid)
        {
            missing.Add("A single allow-listed target profile with a matching configured watt value");
        }

        if (!paused && !restored)
        {
            missing.Add("One complete state-specific pause or restoration result");
        }

        return new PersistentFakeToggleEvidenceResult(
            SimulationEvidenceOutcome.IncompleteEvidence,
            PersistentFakeToggleState.Unknown,
            Target: null,
            "The process exited successfully but did not prove exactly one safe configured toggle state.",
            missing.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static FakeTargetOption? ResolveTarget(HashSet<string> lines, string prefix)
    {
        var matches = lines.Where(line => line.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1)
        {
            return null;
        }

        try
        {
            return FakeTargetPolicy.ResolveProfile(matches[0][prefix.Length..]);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}

public static class PersistentFakeToggleReceiptParser
{
    public const int MaximumReceiptCharacters = 1024;

    public static PersistentFakeToggleReceipt Parse(string transcript)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        if (transcript.Length is <= 0 or > MaximumReceiptCharacters)
        {
            return new PersistentFakeToggleReceipt(PersistentFakeToggleState.Unknown, Target: null);
        }

        var lines = transcript
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length != 4 ||
            lines[0] != "EcoPause Persistent Fake Toggle Receipt 1.5" ||
            lines[3] != "Receipt authority: display-only" ||
            !lines[2].StartsWith("Receipt profile: ", StringComparison.Ordinal))
        {
            return new PersistentFakeToggleReceipt(PersistentFakeToggleState.Unknown, Target: null);
        }

        FakeTargetOption target;
        try
        {
            target = FakeTargetPolicy.ResolveProfile(lines[2]["Receipt profile: ".Length..]);
        }
        catch (ArgumentException)
        {
            return new PersistentFakeToggleReceipt(PersistentFakeToggleState.Unknown, Target: null);
        }

        var state = lines[1] switch
        {
            "Receipt state: Paused" => PersistentFakeToggleState.Paused,
            "Receipt state: Restored" => PersistentFakeToggleState.Restored,
            _ => PersistentFakeToggleState.Unknown
        };
        return state == PersistentFakeToggleState.Unknown
            ? new PersistentFakeToggleReceipt(state, Target: null)
            : new PersistentFakeToggleReceipt(state, target);
    }
}
