namespace RimGovernor.Brain.Runtime;

// Tier-styled buildings: the shell, flooring and lighting planners read their definitions and stuff from
// policy's tier rules, each f(tier, role, stock) with one stock rung of fallback. Every helper here folds the
// projection into the rule's inputs: the tech tier, the stock census, the finished research and whether a
// powered source runs. The projection derives its total tier from the observed finished research census.
internal static class TierStyle
{
    /// <summary>The observed tech tier the rules read.</summary>
    internal static Pol.TechTier StyleTier(RimGovernor.Brain.GameAccess.ColonyReading facts) =>
        TechTierRules.SelectTechTier(facts.Tables.FinishedResearch.Select(x => new ResearchProjectId(x)).ToArray(), facts.Snapshot.PlayerTechLevel);

    /// <summary>The stock census the rules read.</summary>
    internal static Pol.TierStyleStock StyleStock(RimGovernor.Brain.GameAccess.ColonyReading facts) =>
        new Pol.TierStyleStock(ResourceStockRules.Count(facts.Snapshot.Resources));

    /// <summary>Whether any observed power network generates.</summary>
    internal static bool PoweredSource(RimGovernor.Brain.GameAccess.ColonyReading facts) =>
        facts.Facts.EssentialInputs.Power.Networks.Any(net => net.GenerationW > 0);

    /// <summary>
    /// The wall stuff a pen's fence and a power room's walls are styled in: ranked by the Wall def's stats among the
    /// stuffs the stock covers a whole shell of (BulkBuildStuff), steel only while it is resupplied. Wood where the
    /// catalog names none, which the placement preview then refuses on stock as it always has.
    /// </summary>
    internal static string ShellStyleOf(RimGovernor.Brain.GameAccess.ColonyReading facts)
    {
        bool Guarded(string stuff) => stuff == TierStyleRules.GuardedWallStuff && !SteelResupplied(facts);
        var bulk = ColonyPlanningRules.BulkBuildStuffExcept(facts.Facts, ResourceStockRules.Count(facts.Snapshot.Resources), ContainmentCellRules.ShellWallDefinition, TierStyleRules.ShellWallBudget, Guarded);
        return bulk != "" ? bulk : "WoodLog";
    }

    /// <summary>An observed source of steel (a deposit or salvage).</summary>
    internal static bool SteelResupplied(RimGovernor.Brain.GameAccess.ColonyReading facts) =>
        facts.Facts.Acquisition.Any(s => s.Resource == TierStyleRules.GuardedWallStuff && !s.Tree);

    /// <summary>The floor rule the flooring planner prefers per room role before it scores the census: null at Camp, where no floor is styled.</summary>
    internal static Func<RoomRole, (string Def, bool Ok)>? FloorStyle(RimGovernor.Brain.GameAccess.ColonyReading facts) =>
        ColonyPlanningRules.FloorStyle(facts.Facts, facts.Tables.FinishedResearch.Select(x => new ResearchProjectId(x)).ToArray());

    /// <summary>The fixture the lighting planner prefers for a work cell: the module lighting rule for a non-farm module, empty when the tier styles no lamp.</summary>
    internal static string LampStyle(RimGovernor.Brain.GameAccess.ColonyReading facts)
    {
        var powered = PoweredSource(facts);
        var (lamp, ok) = TierStyleRules.PlannedLighting(StyleTier(facts), false, StyleStock(facts), powered);
        return ok ? lamp.Definition : "";
    }
}
