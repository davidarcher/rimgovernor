namespace RimGovernor.Brain.Policy;

public sealed record ProductionMaterialView(Fact<EquatableArray<ProductionRecipeInput>> Recipes,
    Fact<IReadOnlyDictionary<Resource, Fact<double>>> Availability);
public sealed record LedgerMaterialSelection(EquatableArray<LedgerPlacement> Selected, int Generated, bool GenerationTruncated, ForecastMaterialComparison Comparison);

public static class LedgerMaterialChoicesRules
{
    /// <summary>Generate ingredient variants before comparing batches jointly; never broaden a declarer's allowed filter.</summary>
    public static LedgerMaterialSelection SelectLedgerMaterials(IReadOnlyList<LedgerPlacement> placements,
        ProductionMaterialView view, int generationBudget = 1024, int searchBudget = 4096,
        ForecastProductionValuation? valuation = null)
    {
        if (generationBudget < 1) throw new DomainException("positive material generation budget required");
        var recipes = view.Recipes.TryGetValue(out var catalog) ? catalog.ToDictionary(r => r.Recipe, StringComparer.Ordinal) : [];
        var options = new List<ForecastMaterialOption>();
        var variants = new Dictionary<string, LedgerPlacement>(StringComparer.Ordinal);
        var demands = new HashSet<string>(StringComparer.Ordinal);
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        var generated = 0;
        var truncated = false;
        foreach (var placement in placements.Where(p => p.Spec.Mode == BillMode.GearBatch)
            .OrderBy(p => p.Bench, StringComparer.Ordinal).ThenBy(p => p.Spec.Key(), StringComparer.Ordinal))
        {
            var spec = placement.Spec;
            var demand = placement.Bench + "/" + spec.Key();
            if (!demands.Add(demand)) throw new DomainException("duplicate material demand");
            if (!recipes.TryGetValue(spec.Recipe, out var recipe))
            { findings.Add("unknown_recipe:" + demand); continue; }
            var slots = recipe.Slots;
            void Generate(int slot, HashSet<string> filter)
            {
                if (generated == generationBudget) { truncated = true; return; }
                generated++;
                if (slot < slots.Count)
                {
                    foreach (var material in slots[slot].Where(a => spec.Ingredients.Count == 0 || spec.Ingredients.Contains(a.Resource.Value))
                        .Select(a => a.Resource.Value).Distinct().Order(StringComparer.Ordinal))
                    {
                        Generate(slot + 1, new HashSet<string>(filter, StringComparer.Ordinal) { material });
                        if (truncated) return;
                    }
                    return;
                }
                var narrowed = spec with { Ingredients = EquatableArray.From(filter.Order(StringComparer.Ordinal)) };
                var id = demand + "/" + string.Join(",", narrowed.Ingredients);
                if (variants.ContainsKey(id)) return;
                var batch = ProductionRules.Priced(new ProductionBatch(new ProductionBill(placement.Bench, spec.Recipe, spec.Mode, spec.Target, [.. narrowed.Ingredients]),
                    new ProgressView { Action = new ActionId(id), Stage = Stage.Pending }), recipe);
                var promise = ProductionRules.ProductionPromisesOf(Known<EquatableArray<BatchPricing>>([batch]), default);
                // Native bills have one global filter. A union that reopens a slot is not a pinned assignment.
                if (!promise.TryGetValue(out var claims) || claims.Any(c => c.MaterialClaims.Any(m => !m.Units.IsKnown)))
                { findings.Add("unrepresentable_or_unknown_filter:" + id); return; }
                var cost = EquatableArray.From(claims.SelectMany(c => c.MaterialClaims).Select(c => new Amount(c.Resource, checked((long)c.Units.Value))));
                var requirement = spec.Requirements?.Variants.FirstOrDefault(v => v.Ingredients.Order(StringComparer.Ordinal).SequenceEqual(narrowed.Ingredients));
                if (spec.Requirements is not null && requirement is null)
                { findings.Add("requirement_variant_unavailable:" + id); return; }
                variants.Add(id, placement with { Spec = narrowed });
                // This live slice maximizes funded declared batches. It does not invent material prices or colony utility.
                options.Add(new(demand, id, valuation is null ? Known(spec.Requirements is null ? 1.0 : 0.0) : default, cost)
                    { Estimate = valuation?.ByOrderKey.GetValueOrDefault(narrowed.Key()), Requirements = requirement?.Assessment });
            }
            Generate(0, new(StringComparer.Ordinal));
        }
        var comparison = ForecastMaterialChoices.CompareMaterialPlans(options, view.Availability, searchBudget, valuation?.Values);
        comparison = comparison with { Findings = EquatableArray.From(comparison.Findings.Concat(findings).Distinct().Order(StringComparer.Ordinal)) };
        var selected = comparison.Selected.Select(o => variants[o.Id]).Concat(placements.Where(p => p.Spec.Mode != BillMode.GearBatch));
        return new(EquatableArray.From(selected), generated, truncated, comparison);
    }
}
