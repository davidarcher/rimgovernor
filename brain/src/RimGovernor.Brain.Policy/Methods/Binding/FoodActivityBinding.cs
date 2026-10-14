using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy.Methods.Binding;

internal static class FoodActivityBinding
{
    internal static PawnActivities? Reconsider(ForecastActivityOutcomeComparison? selection,
        IReadOnlyDictionary<(string Task, string Pawn), ActivityTaskCandidate> bindings, PawnActivities previous, long tick)
    {
        var selected = CoordinatedActivityBinding.Start(selection, bindings, previous.AuthorityGeneration, previous.Orders[0].Method, tick);
        if (selected is null) return null;
        var next = new List<PawnActivityOrder>();
        foreach (var old in previous.Orders)
        {
            var replacement = selected.Orders.SingleOrDefault(o => o.Pawn == old.Pawn);
            next.Add(replacement is null ? old with { ExpectedRevision = old.Revision, Revision = old.Revision + 1,
                Release = true, Steps = [], Task = "", Preparing = false, OutputDelivery = null }
                : replacement with { Assignment = old.Assignment, ExpectedRevision = old.Revision, Revision = old.Revision + 1 });
        }
        if (selected.Orders.Any(o => !previous.Orders.Any(p => p.Pawn == o.Pawn))) return null;
        return selected with { Orders = next.ToArray() };
    }
    internal static PawnActivities? StartCoordinated(ForecastActivityOutcomeComparison? selection,
        IReadOnlyDictionary<(string Task, string Pawn), ActivityTaskCandidate> bindings, ulong authority, string method, long tick) =>
        CoordinatedActivityBinding.Start(selection, bindings, authority, method, tick);
}
