namespace RimGovernor.Brain.BuildingRuntime;

// Tier-styled buildings: the shell, flooring and lighting planners read their definitions and stuff from
// policy's tier rules, each f(tier, role, stock) with one stock rung of fallback. Every helper here folds the
// projection into the rule's inputs: the tech tier, the stock census, the finished research and whether a
// powered source runs. An unknown tier reads as Camp, so a colony whose research census has not arrived yet is
// styled as a Camp and never receives a stone, powered or floored proposal.
internal static class RoundsTierStyle
{
    /// <summary>The tech tier the rules read: the projection's, Camp when unknown.</summary>
    internal static Pol.TechTier StyleTier(Observation.ColonyProjection facts) =>
        facts.TechTier.TryGetValue(out var tier) ? tier : Pol.TechTier.Camp;

    /// <summary>The stock census the rules read; an unknown census holds nothing, and the rules then fall back to their lowest rung.</summary>
    internal static Pol.TierStyleStock StyleStock(Observation.ColonyProjection facts) =>
        facts.Resources.TryGetValue(out var stock) ? new Pol.TierStyleStock(stock) : new Pol.TierStyleStock(new Dictionary<Pol.Resource, long>());

    /// <summary>Whether one ResearchProjectDef is finished; an unknown census reads as unfinished.</summary>
    internal static bool StyleResearchFinished(Observation.ColonyProjection facts, string project) =>
        ResearchRules.ResearchGate([project], facts.Facts.Research) == "";

    /// <summary>Whether any observed power network generates: unknown without a power topology.</summary>
    internal static Fact<bool> PoweredSource(Observation.ColonyProjection facts)
    {
        if (!facts.PowerPlanning.TryGetValue(out var topology))
        {
            return Unknown<bool>();
        }
        return Known(topology.Networks.Any(net => net.GenerationW > 0));
    }

    /// <summary>
    /// The wall stuff a pen's fence and a power room's walls are styled in: ranked by the Wall def's stats among the
    /// stuffs the stock covers a whole shell of (BulkBuildStuff), steel only while it is resupplied. Wood where the
    /// catalog names none, which the placement preview then refuses on stock as it always has.
    /// </summary>
    internal static string ShellStyleOf(Observation.ColonyProjection facts)
    {
        bool Guarded(string stuff) => stuff == TierStyleRules.GuardedWallStuff && !SteelResupplied(facts);
        var bulk = facts.BulkBuildStuffExcept(ContainmentCellRules.ShellWallDefinition, TierStyleRules.ShellWallBudget, Guarded);
        return bulk != "" ? bulk : "WoodLog";
    }

    /// <summary>An observed source of steel (a deposit or salvage).</summary>
    internal static bool SteelResupplied(Observation.ColonyProjection facts) =>
        facts.Acquisition.Any(s => s.Resource == TierStyleRules.GuardedWallStuff && !s.Tree);

    /// <summary>The floor rule the flooring planner prefers per room role before it scores the census: null at Camp, where no floor is styled.</summary>
    internal static Func<RoomRole, (string Def, bool Ok)>? FloorStyle(Observation.ColonyProjection facts)
    {
        var tier = StyleTier(facts);
        var stock = StyleStock(facts);
        if (tier < Pol.TechTier.Masonry)
        {
            return null;
        }
        var costs = new Dictionary<string, IReadOnlyList<Pol.Amount>>(StringComparer.Ordinal);
        foreach (var d in facts.Definitions)
        {
            if (d.Costs.TryGetValue(out var known) && (d.Name == TierStyleRules.Carpet || d.Name == TierStyleRules.SterileTile || d.Name == TierStyleRules.StrawMatting))
            {
                costs[d.Name] = known;
            }
        }
        var research = new Pol.FloorStyleFacts { CarpetMaking = StyleResearchFinished(facts, "CarpetMaking"), SterileMaterials = StyleResearchFinished(facts, "SterileMaterials"), Costs = costs };
        return role => TierStyleRules.FloorDef(tier, role, stock, research);
    }

    /// <summary>The fixture the lighting planner prefers for a work cell: the module lighting rule for a non-farm module, empty when the tier styles no lamp.</summary>
    internal static string LampStyle(Observation.ColonyProjection facts)
    {
        PoweredSource(facts).TryGetValue(out var powered);
        var (lamp, ok) = TierStyleRules.PlannedLighting(StyleTier(facts), false, StyleStock(facts), powered);
        return ok ? lamp.Definition : "";
    }
}
