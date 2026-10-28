namespace RimGovernor.Brain.Policy.Methods.Binding;

/// <summary>Selected routine recipes create ordinary finite bills, never a pawn assignment or promised output.</summary>
internal static class NativeProductionBinding
{
    internal static EquatableArray<ProductionBill> Selected(ForecastActivityOutcomeComparison selection, ForecastCoupledContext context)
    {
        if (selection.Selected.Length == 0 || selection.Plan is not { } plan) return [];
        var native = context.Activities.Activities.Where(a => a.NativeExecution).Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        var recipes = plan.Allocations.Where(a => native.Contains(a.Activity)).DistinctBy(a => a.Activity)
            .Select(a => context.Bindings.TryGetValue((a.Activity, a.Pawn), out var binding) ? binding
                : throw new RequiredFactException("native production binding:" + a.Activity + ":" + a.Pawn))
            .Where(b => b.Kind == PawnActivityKind.Recipe
                && b.Recipe is { Bill.Length: 0, Unfinished.Length: 0, Stuff.Length: > 0 }).ToArray();
        return EquatableArray.From(recipes.GroupBy(b => (Bench: b.Target, b.Recipe!.Recipe, b.Recipe.Stuff))
            .OrderBy(g => g.Key.Bench, StringComparer.Ordinal).ThenBy(g => g.Key.Recipe, StringComparer.Ordinal)
            .ThenBy(g => g.Key.Stuff, StringComparer.Ordinal)
            .Select(g => new ProductionBill(g.Key.Bench, g.Key.Recipe, BillMode.GearBatch, g.Count(), g.Key.Stuff)));
    }
}
