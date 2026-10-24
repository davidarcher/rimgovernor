namespace RimGovernor.Brain.Policy;

public static class ForecastCoupledMaterials
{
    /// <summary>The shared owner's holds before any schedule's bills: held promises by id, the protected materials, and
    /// the construction owed. Null when the evidence is unknown.</summary>
    internal sealed record MaterialHolds(Dictionary<string, ForecastCommitment> Held, IReadOnlyDictionary<Resource, Fact<double>> Protected,
        Dictionary<Resource, long> Owed);

    internal static MaterialHolds? MaterialHoldsOf(ColonyForecastRequest request, IReadOnlyDictionary<string, ForecastCommitment> commitments)
    {
        var context = request.Coupled!; var evidence = context.Resources.Evidence;
        if (evidence.Standing is not { Owed: var construction } || !evidence.ForecastPromises.TryGetValue(out var production)) return null;
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
            var debit = commitment.StockChanges.Where(s => s.Day.IsKnown && s.Day.Value <= request.HorizonDays && s.Units.IsKnown && s.Units.Value < 0)
                .GroupBy(s => s.Resource).ToDictionary(g => g.Key, g => -g.Sum(s => s.Units.Value));
            var owner = held[commitment.Id];
            held[commitment.Id] = owner with
            {
                MaterialClaims = EquatableArray.From(owner.MaterialClaims.Select(m =>
                m.ReservationBill == Known("") && m.Units.IsKnown && debit.TryGetValue(m.Resource, out var units)
                    ? m with { Units = Known(Math.Max(0, m.Units.Value - units)) } : m))
            };
        }
        var protectedMaterials = ProductionRules.ProductionMaterialHoldsOf(Known(WorkRules.MergeWorkMaterialPromises(held.Values)), evidence.Reservations);
        return protectedMaterials.TryGetValue(out var holds) ? new(held, holds, owed) : null;
    }

    /// <summary>Reproject the shared owner for this schedule, with optional sites and exact admitted work claims.
    /// Native reservations for unrelated bills are retained even when a proposed commitment is removed.</summary>
    internal static Fact<IReadOnlyDictionary<Resource, Fact<double>>> CoupledMaterialBudgetOf(ColonyForecastRequest request,
        MaterialHolds? fixedHolds, IReadOnlyDictionary<string, ForecastActivity> tasks, ForecastActivityPlan plan)
    {
        if (fixedHolds is not var (held, holds, owed)) return default;
        var context = request.Coupled!; var evidence = context.Resources.Evidence;
        var selected = plan.Allocations.Select(a => context.Bindings.GetValueOrDefault((a.Activity, a.Pawn))).OfType<Methods.ActivityTaskCandidate>().ToArray();
        var adjusted = new Dictionary<Resource, Fact<double>>(holds);
        foreach (var bill in selected.Where(b => (b.Kind == PawnActivityKind.Recipe || b.Kind == PawnActivityKind.Work && b.Job == "DoBill")
            && b.Recipe is { Bill.Length: > 0 }).GroupBy(b => b.Recipe!.Bill))
            foreach (var resource in bill.SelectMany(b => tasks[b.Id].Inputs).GroupBy(i => i.Resource))
            {
                var promised = held.Values.SelectMany(c => c.MaterialClaims).Where(c => c.Resource == resource.Key && c.ReservationBill == Known(bill.Key)).ToArray();
                var native = evidence.Reservations.Where(r => r.Bill == bill.Key).SelectMany(r => r.Items).Where(i => i.Resource == resource.Key).Sum(i => (double)i.Count);
                var before = adjusted.GetValueOrDefault(resource.Key, Known(0.0));
                if (!before.IsKnown || resource.Any(i => !i.Units.IsKnown) || promised.Any(p => !p.Units.IsKnown)) continue;
                var credit = Math.Min(resource.Sum(i => i.Units.Value), Math.Max(native, promised.Select(p => p.Units.Value).DefaultIfEmpty(0).Max()));
                adjusted[resource.Key] = Known(Math.Max(0, before.Value - credit));
            }
        return ProductionRules.SharedMaterialBudgetOf(evidence.Stock, owed, Known<IReadOnlyDictionary<Resource, Fact<double>>>(adjusted));
    }
}
