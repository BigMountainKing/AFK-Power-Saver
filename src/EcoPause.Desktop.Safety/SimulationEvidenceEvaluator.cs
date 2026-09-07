namespace EcoPause.Desktop.Safety;

public enum SimulationEvidenceOutcome
{
    Passed,
    PermissionDeclined,
    ProcessFailed,
    IncompleteEvidence
}

public sealed record SimulationEvidenceResult(
    SimulationEvidenceOutcome Outcome,
    string Message,
    IReadOnlyList<string> MissingEvidence)
{
    public bool Passed => Outcome == SimulationEvidenceOutcome.Passed;
}

public static class SimulationEvidenceEvaluator
{
    private static readonly string[] RequiredEvidence =
    [
        "Mutual process-ID verification: PASS",
        "Elevated helper verified launcher PID: PASS",
        "Elevated helper initialization: PASS",
        "Authenticated activation: Executed / Activated",
        "Replay attempt: RejectedReplay",
        "Tampered command: RejectedAuthentication",
        "Authenticated restoration: Executed / Restored",
        "Elevated helper exit: 0",
        "Final simulated limit: 450 W",
        "Recovery journal closed: True",
        "Safe elevated lifecycle: PASS",
        "Real NVIDIA access: NONE"
    ];

    public static SimulationEvidenceResult Evaluate(
        int exitCode,
        string standardOutput,
        string standardError)
    {
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);

        if (exitCode == 4 && standardOutput.Contains("UAC was declined", StringComparison.Ordinal))
        {
            return new SimulationEvidenceResult(
                SimulationEvidenceOutcome.PermissionDeclined,
                "Windows permission was declined; no helper lifecycle ran.",
                MissingEvidence: []);
        }

        if (exitCode != 0 || !string.IsNullOrWhiteSpace(standardError))
        {
            return new SimulationEvidenceResult(
                SimulationEvidenceOutcome.ProcessFailed,
                $"The simulation process stopped safely with exit code {exitCode}.",
                MissingEvidence: []);
        }

        var missing = RequiredEvidence
            .Where(item => !standardOutput.Contains(item, StringComparison.Ordinal))
            .ToArray();
        if (missing.Length != 0)
        {
            return new SimulationEvidenceResult(
                SimulationEvidenceOutcome.IncompleteEvidence,
                "The process exited successfully but did not provide complete safety evidence.",
                missing);
        }

        return new SimulationEvidenceResult(
            SimulationEvidenceOutcome.Passed,
            "The elevated fake lifecycle restored and verified its exact original state.",
            MissingEvidence: []);
    }
}
