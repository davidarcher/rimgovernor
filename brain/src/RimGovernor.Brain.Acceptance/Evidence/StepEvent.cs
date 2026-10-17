namespace RimGovernor.Brain.Acceptance;

/// <summary>Reads the service's worker-step rows (Go <c>stepevent.go</c>).</summary>
public static class StepEvents
{
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
