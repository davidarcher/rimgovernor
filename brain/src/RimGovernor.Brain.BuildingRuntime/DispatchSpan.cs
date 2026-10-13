namespace RimGovernor.Brain.BuildingRuntime;

internal static class DispatchSpans
{
    /// <summary>
    /// Ends a hands.dispatch span with the batch's outcome: a returned error marks the span an error, a refusal
    /// the game or the executor declined (no error, the write was not applied) marks it refused with the reasons,
    /// and anything else leaves it ok.
    /// </summary>
    public static void EndDispatchSpan(System.Diagnostics.Activity? span, IReadOnlyList<ExecutorResult> results, IReadOnlyList<Exception> errors)
    {
        if (ErrorJoin.Of([.. errors]) is { } error)
        {
            span.Fail(error);
            return;
        }
        var reasons = new List<string>();
        foreach (var result in results)
        {
            reasons.AddRange(Worker.RefusedReasons(result));
        }
        if (reasons.Count > 0)
        {
            span.Refuse([.. reasons]);
        }
        span.Dispose();
    }
}
