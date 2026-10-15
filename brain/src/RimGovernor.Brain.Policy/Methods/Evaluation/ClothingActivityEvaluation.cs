namespace RimGovernor.Brain.Policy.Methods.Evaluation;

internal static class ClothingActivityEvaluation
{
    internal static Fact<bool> MaterialsFit(ForecastActivityOutcome[] selected,
        IReadOnlyDictionary<(string Task, string Pawn), ActivityTaskCandidate> bindings, WorkResourceView resources)
    {
        var recipes = selected.Where(o => !o.RetainedCompletion.IsKnown).Select(o => bindings.Values.First(t => t.Id == o.Id + "-recipe")).ToArray();
        if (recipes.Select(r => r.Target).Distinct().Count() != recipes.Length) return Known(false);
        var deliveries = selected.Where(o => !o.RetainedCompletion.IsKnown).Select(o => bindings.Values.First(t => t.Id == o.Id)).ToArray();
        if (deliveries.Select(d => d.Destination).Distinct().Count() != deliveries.Length) return Known(false);
        for (var i = 0; i < recipes.Length; i++)
        for (var j = i + 1; j < recipes.Length; j++)
            if (recipes[i].Clothing is { } a && recipes[j].Clothing is { } b && a.Wearer == b.Wearer
                && a.Layers.Intersect(b.Layers).Any() && a.Groups.Intersect(b.Groups).Any()) return Known(false);
        var inputs = recipes.SelectMany(r => r.Inputs).ToArray();
        if (inputs.GroupBy(i => i.Thing).Any(g => g.Sum(i => (long)i.Count) > g.Min(i => i.AvailableCount))) return Known(false);
        if (!resources.Materials.TryGetValue(out var budget)) return default;
        foreach (var group in inputs.GroupBy(i => i.Resource))
        {
            if (!budget.GetValueOrDefault(new Resource(group.Key), Known(0.0)).TryGetValue(out var free)) return default;
            var credits = recipes.Where(r => r.Recipe is { Bill.Length: > 0 } && r.Resource == group.Key)
                .GroupBy(r => r.Recipe!.Bill).Select(b => WorkMaterialEvaluation.ExistingBillCredit(resources, b.Key, new(group.Key),
                    b.SelectMany(r => r.Inputs).Where(i => i.Resource == group.Key).Sum(i => (double)i.Count))).ToArray();
            if (credits.Any(c => !c.IsKnown)) return default;
            if (group.Sum(i => (long)i.Count) > free + credits.Sum(c => c.Value)) return Known(false);
        }
        return Known(true);
    }
}
