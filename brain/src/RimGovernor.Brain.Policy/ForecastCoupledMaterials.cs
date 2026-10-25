namespace RimGovernor.Brain.Policy;

/// <summary>What a schedule's bills may draw: the free units per material after the native holds, the standing promises and the
/// construction owed, or the reason the budget could not be read (the census, a standing owed, a promise or a hold was not).</summary>
internal abstract record MaterialBudget
{
    private MaterialBudget() { }
    internal sealed record Read(IReadOnlyDictionary<Resource, double> Free) : MaterialBudget;
    internal sealed record Unread(string Reason) : MaterialBudget;
}

public static class ForecastCoupledMaterials
{
    /// <summary>The shared owner's holds before any schedule's bills: held promises by id, the protected materials, and
    /// the construction owed. Null when the evidence is unread.</summary>
    internal sealed record MaterialHolds(Dictionary<string, ForecastCommitment> Held, IReadOnlyDictionary<Resource, double> Protected,
        Dictionary<Resource, long> Owed);

    internal static MaterialHolds? MaterialHoldsOf(ColonyForecastRequest request, IReadOnlyDictionary<string, ForecastCommitment> commitments)
    {
        var context = request.Coupled!; var evidence = context.Resources.Evidence;
        if (evidence.Standing is not { Owed: var construction } || evidence.Promises is not ProductionPromiseSet.Assessed(var production)) return null;
        var held = WorkRules.MergeWorkMaterialPromises(production).ToDictionary(c => c.Id, StringComparer.Ordinal);
        // The delta addresses declared identities. Native holds still enter through the shared owner's reservations.
        foreach (var original in request.Commitments.Where(c => !commitments.ContainsKey(c.Id))) held.Remove(original.Id);
        foreach (var commitment in commitments.Values.Where(c => c.ActivityCompletions.Count == 0 && c.MaterialClaims.Count != 0)) held[commitment.Id] = commitment;
        var owed = construction.ToDictionary(p => p.Key, p => p.Value);
        var liveClaims = context.Resources.Claims.Materials;
        foreach (var claim in liveClaims) held[claim.Id] = claim; // A projected removal is not evidence of journal release.
        // The same owner's timed debit replaces only that debit's unlinked reservation, not another hold.
        foreach (var commitment in commitments.Values.Where(c => c.ActivityCompletions.Count == 0 && held.ContainsKey(c.Id)))
        {
            var debit = commitment.StockChanges.Where(s => s.Day <= request.HorizonDays && s.Units < 0)
                .GroupBy(s => s.Resource).ToDictionary(g => g.Key, g => -g.Sum(s => s.Units));
            var owner = held[commitment.Id];
            held[commitment.Id] = owner with
            {
                MaterialClaims = EquatableArray.From(owner.MaterialClaims.Select(m =>
                m.ReservationBill.Length == 0 && m.Units is MaterialUnits.Exact(var exact) && debit.TryGetValue(m.Resource, out var units)
                    ? m with { Units = new MaterialUnits.Exact(Math.Max(0, exact - units)) } : m))
            };
        }
        var protectedMaterials = ProductionRules.ProductionMaterialHoldsOf(new ProductionPromiseSet.Assessed(WorkRules.MergeWorkMaterialPromises(held.Values)), evidence.Reservations);
        if (protectedMaterials is not MaterialAvailability.Assessed(var holds) || holds.Values.Any(h => h is MaterialUnits.Open)) return null;
        return new(held, holds.ToDictionary(h => h.Key, h => ((MaterialUnits.Exact)h.Value).Count), owed);
    }

    /// <summary>Reproject the shared owner for this schedule, with optional sites and exact admitted work claims.
    /// Native reservations for unrelated bills are retained even when a proposed commitment is removed.</summary>
    internal static MaterialBudget CoupledMaterialBudgetOf(ColonyForecastRequest request,
        MaterialHolds? fixedHolds, IReadOnlyDictionary<string, ForecastActivity> tasks, ForecastActivityPlan plan)
    {
        if (fixedHolds is not var (held, holds, owed)) return new MaterialBudget.Unread("material_holds_unread");
        var context = request.Coupled!; var evidence = context.Resources.Evidence;
        var selected = plan.Allocations.Select(a => context.Bindings.GetValueOrDefault((a.Activity, a.Pawn))).OfType<Methods.ActivityTaskCandidate>().ToArray();
        var adjusted = new Dictionary<Resource, double>(holds);
        foreach (var bill in selected.Where(b => (b.Kind == PawnActivityKind.Recipe || b.Kind == PawnActivityKind.Work && b.Job == "DoBill")
            && b.Recipe is { Bill.Length: > 0 }).GroupBy(b => b.Recipe!.Bill))
            foreach (var resource in bill.SelectMany(b => tasks[b.Id].Inputs).GroupBy(i => i.Resource))
            {
                var promised = held.Values.SelectMany(c => c.MaterialClaims).Where(c => c.Resource == resource.Key && c.ReservationBill == bill.Key).ToArray();
                var native = evidence.Reservations.Where(r => r.Bill == bill.Key).SelectMany(r => r.Items).Where(i => i.Resource == resource.Key).Sum(i => (double)i.Count);
                if (promised.Any(p => p.Units is MaterialUnits.Open)) continue;
                var credit = Math.Min(resource.Sum(i => i.Units), Math.Max(native, promised.Select(p => ((MaterialUnits.Exact)p.Units).Count).DefaultIfEmpty(0).Max()));
                adjusted[resource.Key] = Math.Max(0, adjusted.GetValueOrDefault(resource.Key) - credit);
            }
        var shared = ProductionRules.SharedMaterialBudgetOf(evidence.Stock, owed,
            new MaterialAvailability.Assessed(adjusted.ToDictionary(p => p.Key, p => (MaterialUnits)new MaterialUnits.Exact(p.Value))));
        return shared is MaterialAvailability.Assessed(var free) && free.Values.All(v => v is MaterialUnits.Exact)
            ? new MaterialBudget.Read(free.ToDictionary(p => p.Key, p => ((MaterialUnits.Exact)p.Value).Count))
            : new MaterialBudget.Unread("material_budget_unread");
    }
}
