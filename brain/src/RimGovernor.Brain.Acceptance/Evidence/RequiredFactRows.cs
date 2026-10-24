using RimGovernor.Brain.Telemetry;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The harness-wide required-fact check (#3753 D3): a planner run the brain skipped for an unread fact writes a
/// <c>required_fact</c> row (<see cref="RequiredFactTelemetry"/>), and any such row in a case's log slice fails the case,
/// whatever its body asserted. <see cref="CaseRunner"/> applies it to every case after the body.
/// </summary>
public static class RequiredFactRows
{
    /// <summary>Each required_fact row of the log ring at <paramref name="path"/> as <c>planner: fact</c>, distinct, oldest first.</summary>
    public static IReadOnlyList<string> Skips(string path) =>
    [
        .. ServiceLogs.ReadLogs(path)
            .Where(row => row.Kind == TelemetryKinds.RequiredFact.Name)
            .Select(row => row.Fields())
            .Select(f => RowValue.Text(f.GetValueOrDefault(Decision.TargetKey)) + ": " + RowValue.Text(f.GetValueOrDefault(RequiredFactTelemetry.FactKey)))
            .Distinct(StringComparer.Ordinal),
    ];

    /// <summary>The failure the required_fact rows of the log ring at <paramref name="path"/> give the case, naming each fact; null when there are none.</summary>
    public static AcceptanceException? Failure(string path) =>
        Skips(path) is { Count: > 0 } skips
            ? new AcceptanceException("planner runs skipped for unread required facts (required_fact): " + string.Join(", ", skips))
            : null;
}
