namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The service's latest worker-step row (the planner_step of target worker_step; the clock worker writes one per
/// change of step outcome): the row as the OTLP log ring held it and the isolated planner failures it carried.
/// </summary>
/// <param name="Line">The row's OTLP log line, distinct per row (it carries the observed time), so a caller can tell a new step from a re-read one.</param>
/// <param name="PlannerFailures">The row's planner failures rendered as "[a; b]".</param>
public sealed record SchedulerStep(string Line, string PlannerFailures)
{
    /// <summary>
    /// Whether the step's isolated planner failures include a native refusal (a native refusal is wrapped as
    /// "bridge read refused: &lt;tool&gt;"; planner-level refusals name themselves "refused" too). A step whose
    /// planners all succeeded, or failed on transport, is not.
    /// </summary>
    public bool Refused => PlannerFailures.Contains("refused", StringComparison.Ordinal);
}

/// <summary>Reads the service's worker-step rows (Go <c>stepevent.go</c>).</summary>
public static class StepEvents
{
    // Bounds how much of a growing OTLP log ring LastSchedulerStepFile reads per call: the latest step row is near
    // the end, and a poll must not re-read an hour of rows.
    private const long SchedulerStepTail = 256 * 1024;

    /// <summary>Reads the last worker-step row (planner_step, target worker_step) from the tail of the OTLP log ring at <paramref name="path"/>; null when it has none yet or cannot be read.</summary>
    public static SchedulerStep? LastSchedulerStepFile(string path)
    {
        if (LogTail.LastLogRow(path, SchedulerStepTail, RowKinds.IsWorkerStep) is not var (row, line))
        {
            return null;
        }

        return new SchedulerStep(line, PlannerFailures(row));
    }

    /// <summary>The row's planner_failures list rendered as "[a; b]" ("[]" when empty).</summary>
    internal static string PlannerFailures(LogRow row)
    {
        var parts = new List<string>();
        if (row.Fields().GetValueOrDefault("planner_failures") is List<object?> list)
        {
            foreach (var value in list)
            {
                parts.Add(value?.ToString() ?? "<nil>");
            }
        }

        return "[" + string.Join("; ", parts) + "]";
    }
}
