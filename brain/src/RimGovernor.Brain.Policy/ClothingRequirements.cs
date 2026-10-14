namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    // The equipment owner already netted physical stock and preserved standing work before calling this.
    private static ProductionRequirements ClothingRequirements(GearNeed[] needs, GearMethod method,
        IReadOnlyList<GearBench> benches, GearPlanningRequest request)
    {
        var variants = new List<ProductionRequirementVariant>();
        var bench = benches.Single(b => b.Id == method.Bench);
        var recipe = bench.Recipes.Value.Single(r => r.Definition == method.Recipe);
        if (needs.Length == 0 || !recipe.Ingredients.TryGetValue(out var slots)) return new([]);
        var materials = needs.SelectMany(n => n.Pawn.LoadoutModel.ValueOrDefault?.Options ?? [])
            .Where(o => o.Source == GearSource.Bill && o.Definition == needs[0].Need.Definition)
            .Select(o => o.Stuff).Distinct().OrderBy(r => r.Value, StringComparer.Ordinal);
        foreach (var stuff in materials)
        {
            var chosen = new List<Amount>();
            foreach (var slot in slots)
            {
                var members = slot.Where(a => method.Filter.Contains(a.Resource)).ToArray();
                var matching = members.Where(a => a.Resource == stuff).ToArray();
                if (matching.Length == 1) chosen.Add(matching[0]);
                else if (members.Length == 1) chosen.Add(members[0]);
            }
            if (chosen.Count != slots.Count) continue;
            if (!string.IsNullOrEmpty(stuff.Value) && !chosen.Any(a => a.Resource == stuff)) continue;
            var filter = EquatableArray.From(chosen.Select(a => a.Resource.Value).Distinct().Order(StringComparer.Ordinal));
            if (slots.Any(s => s.Count(a => filter.Contains(a.Resource.Value)) != 1)) continue;
            var proposed = new OrderSpec { BenchKind = bench.Def, Recipe = method.Recipe, Mode = BillMode.GearBatch, Target = method.Count, Ingredients = filter };
            // Native treats identical bill creation as idempotent, not as another unit of queued output.
            if (benches.SelectMany(b => b.Bills.Value).Any(b => !b.Spent && b.Spec.TryGetValue(out var existing) && existing.Key() == proposed.Key())) continue;
            var relief = new List<ForecastRequirementRelief>();
            var known = true;
            foreach (var n in needs)
            {
                if (!n.Pawn.LoadoutModel.TryGetValue(out var input)) { known = false; break; }
                input = input with { Climate = n.Pawn.Climate ?? input.Climate };
                foreach (var trait in input.Role.Work.Traits.ValueOrDefault)
                    input = input with { Nudist = input.Nudist || trait.Effects.Nudist, TaintFree = input.TaintFree || trait.Effects.TaintFree };
                var option = input.Options.FirstOrDefault(o => o.Source == GearSource.Bill && o.Definition == n.Need.Definition
                    && o.Stuff == stuff && GearEligible(input, o));
                if (option is null) { known = false; break; }
                var effects = ClothingRelief(n.Pawn, input, option);
                if (effects is null) { known = false; break; }
                relief.AddRange(effects);
            }
            if (!known) continue;
            Fact<double> cost = default, work = default;
            if (chosen.All(a => request.MaterialPrices.TryGetValue(a.Resource, out var price) && double.IsFinite(price) && price >= 0))
                cost = Known(chosen.Sum(a => a.Count * request.MaterialPrices[a.Resource]) * method.Count);
            if (recipe.WorkAmount.TryGetValue(out var amount) && double.IsFinite(amount) && amount > 0)
                work = Known(amount * method.Count);
            variants.Add(new(filter, new(EquatableArray.From(relief), cost, work)));
        }
        return new(EquatableArray.From(variants.DistinctBy(v => string.Join(",", v.Ingredients))));
    }

    private static List<ForecastRequirementRelief>? ClothingRelief(GearPawn pawn, GearLoadoutInput input, GearOption option)
    {
        var result = new List<ForecastRequirementRelief>();
        var remaining = input.Worn.Where(w => !GearConflicts(w, option)).ToArray();
        var after = remaining.Append(option).ToArray();
        var prefix = "clothing/" + pawn.Pawn.Value + "/";
        if (!input.Nudist && GearCoreGroups.Any(g => input.Worn.Any(w => w.Groups.Contains(g)) && !after.Any(w => w.Groups.Contains(g)))) return null;
        if (!input.Nudist)
            foreach (var group in GearCoreGroups)
                if (!input.Worn.Any(w => w.Groups.Contains(group)) && after.Any(w => w.Groups.Contains(group)))
                    result.Add(new(prefix + "coverage/" + group, ProductionNeedPriority.Required, 1));
        foreach (var worn in input.Worn.Where(w => GearConflicts(w, option) && w.Condition <= GearTatteredCondition))
            result.Add(new(prefix + "condition/" + worn.ID, ProductionNeedPriority.Maintenance, 1));
        if (pawn.Policy.TryGetValue(out var policy) && policy.Required.Contains(option.Definition.Value)
            && !input.Worn.Any(w => w.Definition == option.Definition))
            result.Add(new(prefix + "required/" + option.Definition.Value, ProductionNeedPriority.Required, 1));
        var (low, high) = input.Climate.TemperatureRange(input.Ambient);
        double Insulation(IEnumerable<GearOption> items, bool cold) => items.Sum(o => (cold ? o.Cold : o.Heat) * GearQualityMultipliers(o.Quality).Insulation);
        foreach (var cold in new[] { true, false })
        {
            var needed = Math.Max(0, cold ? input.ComfortableMin - low : high - input.ComfortableMax);
            var before = Math.Max(0, needed - Insulation(input.Worn, cold));
            var later = Math.Max(0, needed - Insulation(after, cold));
            if (later > before) return null;
            if (before > later)
                result.Add(new(prefix + (cold ? "cold" : "heat"), ProductionNeedPriority.Safety, (before - later) / before));
        }
        return result;
    }
}
