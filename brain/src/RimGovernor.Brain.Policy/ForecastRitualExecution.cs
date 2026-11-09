namespace RimGovernor.Brain.Policy;

/// <summary>One attendee's task of a ritual root (#4058): external work the ritual's begin action runs for everyone at once,
/// never a dispatched pawn activity. Root is the ritual at one site; Task is the attendee's place in the shared schedule. Every
/// row of a root carries the root's begin action and premise (#4059): what the review commits when the root is ready.</summary>
public sealed record ForecastRitualWork(string Root, string Task, string Pawn)
{
    /// <summary>The Ritual begin of the root: its organizer (the first pawn of the first filled slot), precept, site, slot fills and spectators.</summary>
    public required Ritual Begin { get; init; }

    /// <summary>The Method reason: the pattern, its due state (the game's obligation, or the days since the last performance), the site and the predicted quality.</summary>
    public required string Premise { get; init; }
}

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
