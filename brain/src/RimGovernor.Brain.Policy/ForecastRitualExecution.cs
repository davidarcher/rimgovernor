namespace RimGovernor.Brain.Policy;

/// <summary>One attendee's task of a ritual root (#4058): external work the ritual's begin action runs for everyone at once,
/// never a dispatched pawn activity. Root is the ritual at one site; Task is the attendee's place in the shared schedule.</summary>
public sealed record ForecastRitualWork(string Root, string Task, string Pawn);

/// <summary>When a selected ritual begins (#4058), mirroring <see cref="ForecastTradeExecution.Ready"/>.</summary>
public static class ForecastRitualExecution
{
    /// <summary>The attendee rows of the selected ritual roots whose every attendee task is that pawn's first scheduled task and
    /// departs now. Earlier shared work runs first; its continuation requests a fresh review before the ritual is considered
    /// again. One attendee not ready holds the whole ritual: the game begins it for everyone together.</summary>
    public static EquatableArray<ForecastRitualWork> Ready(ForecastActivityOutcomeComparison selection, ForecastCoupledContext context)
    {
        if (selection.Plan is not { } plan) return [];
        var rows = context.RitualWork.Where(w => selection.Selected.Contains(w.Root, StringComparer.Ordinal)
            && plan.Allocations.Any(a => a.Activity == w.Task && a.Pawn == w.Pawn && a.Depart <= 0
                && plan.Allocations.Where(before => before.Pawn == a.Pawn).OrderBy(before => before.Depart)
                    .ThenBy(before => before.Activity, StringComparer.Ordinal).First().Activity == a.Activity)).ToArray();
        return EquatableArray.From(rows.Where(w => context.RitualWork.Where(r => r.Root == w.Root).All(rows.Contains)));
    }
}
