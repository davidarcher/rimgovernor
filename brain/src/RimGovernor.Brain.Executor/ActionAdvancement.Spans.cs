using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Telemetry;
namespace RimGovernor.Brain.Executor;

public sealed partial class ActionAdvancement
{
    /// <summary>
    /// Ends a hands.dispatch span with the batch's outcome: a returned error marks the span an error, a refusal
    /// the game or the executor declined (no error, the write was not applied) marks it refused with the reasons,
    /// and anything else leaves it ok.
    /// </summary>
    private static void EndDispatchSpan(System.Diagnostics.Activity? span, IReadOnlyList<ExecutorResult> results, IReadOnlyList<Exception?> errors)
    {
        if (Join([.. errors]) is { } error)
        {
            Spans.EndSpan(span, error);
            return;
        }
        var reasons = new List<string>();
        foreach (var result in results)
        {
            reasons.AddRange(WorkerRefusedReasons(result));
        }
        if (reasons.Count > 0)
        {
            Spans.Refuse(span, [.. reasons]);
        }
        span?.Dispose();
    }

    private static Exception? Join(params Exception?[] errors)
    {
        var present = errors.Where(e => e is not null).Select(e => e!).ToArray();
        return present.Length switch
        {
            0 => null,
            1 => present[0],
            _ => new AggregateException(present),
        };
    }
}
