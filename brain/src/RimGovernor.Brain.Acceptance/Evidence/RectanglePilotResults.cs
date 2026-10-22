using RimGovernor.Brain.Telemetry;
using RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance;

/// <summary>The reader of acceptance_case's durable rectangle evidence, including interrupted requests.</summary>
public static class RectanglePilotResults
{
    public static RectangleCaseResult? Last(IEnumerable<LogRow> rows, string runId)
    {
        RectangleCaseResult? last = null;
        foreach (var row in rows)
        {
            if (row.Kind != TelemetryKinds.AcceptanceCase.Name || row.Payload.GetValueOrDefault("run_id") as string != runId)
                continue;
            if (row.Payload.GetValueOrDefault("result") is not string json)
                throw new AcceptanceException("rectangle evidence: missing typed result");
            var result = FixtureJson.Deserialize<RectangleCaseResult>(json);
            if (result.RunId != runId) throw new AcceptanceException("rectangle evidence: mismatched run identity");
            last = result;
        }
        return last;
    }

    public static void RequireSuccess(string runId, RectangleCaseResult result)
    {
        if (result.RunId != runId) throw new AcceptanceException("rectangle result: mismatched run identity");
        if (!result.Success || result.Phase != "completed" || result.Error is not null)
            throw new AcceptanceException($"rectangle {result.Phase}: {result.Error ?? "no completed success"}");
    }
}
