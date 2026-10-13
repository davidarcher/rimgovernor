namespace RimGovernor.Brain.BuildingRuntime;

// Tier-styled buildings: the shell, flooring and lighting planners read their definitions and stuff from
// policy's tier rules, each f(tier, role, stock) with one stock rung of fallback. Every helper here folds the
// projection into the rule's inputs: the tech tier, the stock census, the finished research and whether a
// powered source runs. An unknown tier reads as Camp, so a colony whose research census has not arrived yet is
// styled as a Camp and never receives a stone, powered or floored proposal.
internal static partial class BuildingRuntimePackage
{
    /// <summary>The tech tier the rules read: the projection's, Camp when unknown.</summary>
    internal static Pol.TechTier StyleTier(Observation.ColonyProjection facts) =>
        facts.TechTier.TryGetValue(out var tier) ? tier : Pol.TechTier.Camp;

    /// <summary>The stock census the rules read; an unknown census holds nothing, and the rules then fall back to their lowest rung.</summary>
    internal static Pol.TierStyleStock StyleStock(Observation.ColonyProjection facts) =>
        facts.Resources.TryGetValue(out var stock) ? new Pol.TierStyleStock(stock) : new Pol.TierStyleStock(new Dictionary<Pol.Resource, long>());

    /// <summary>Whether one ResearchProjectDef is finished; an unknown census reads as unfinished.</summary>
    internal static bool StyleResearchFinished(Observation.ColonyProjection facts, string project) =>
        ResearchGate([project], facts.Facts.Research) == "";

    /// <summary>Whether any observed power network generates: unknown without a power topology.</summary>
    internal static Fact<bool> PoweredSource(Observation.ColonyProjection facts)
    {
        if (!facts.PowerPlanning.TryGetValue(out var topology))
        {
            return Unknown<bool>();
        }
        foreach (var net in topology.Networks)
        {
            if (net.GenerationW.TryGetValue(out var generation) && generation > 0)
            {
                return Known<bool>(true);
            }
        }
        return Known<bool>(false);
    }

    /// <summary>
    /// The wall and door style a shell planner expands a ring with: the walls' stuff ranked by the Wall def's
    /// stats among the stuffs the stock covers a whole shell of (BulkBuildStuff), and the door from the door
    /// def's stats, an Autodoor once Autodoors is finished, a source generates and the stock covers one. A wood
    /// Door and wall stand where the catalog names none, which the placement preview then refuses on stock as
    /// it always has.
    /// </summary>
    internal static ShellStyle ShellStyleOf(Observation.ColonyProjection facts)
    {
        var style = new ShellStyle { WallDef = "Wall", DoorDef = "Door", DoorStuff = "WoodLog" };
        if (PoweredSource(facts).TryGetValue(out var powered) && powered && StyleResearchFinished(facts, "Autodoors") && RoundsDefinitionsAvailable(facts, ["Autodoor"], true))
        {
            var ok = facts.TryStockedBuildStuff("Autodoor", out var stuff);
            if (ok)
            {
                style = style with { DoorDef = "Autodoor", DoorStuff = stuff };
            }
        }
        if (style.DoorDef == "Door")
        {
            var stuff = facts.BuildStuff("Door");
            if (stuff != "")
            {
                style = style with { DoorStuff = stuff };
            }
        }
        var wall = "WoodLog";
        bool Guarded(string stuff) => stuff == GuardedWallStuff && !SteelResupplied(facts);
        var bulk = facts.BulkBuildStuffExcept(style.WallDef, ShellWallBudget, Guarded);
        if (bulk != "")
        {
            wall = bulk;
        }
        return style with { WallStuff = wall };
    }

    /// <summary>An observed source of steel (a deposit or salvage); an unread acquisition census reads as none, so steel stays guarded.</summary>
    internal static bool SteelResupplied(Observation.ColonyProjection facts) =>
        facts.Acquisition.TryGetValue(out var sources) && sources.Any(s => s.Resource == GuardedWallStuff && !s.Tree);

    /// <summary>The floor rule the flooring planner prefers per room role before it scores the census: null at Camp, where no floor is styled.</summary>
    internal static Func<Pol.RoomRole, (string Def, bool Ok)>? FloorStyle(Observation.ColonyProjection facts)
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
            if (d.Costs.TryGetValue(out var known) && (d.Name == Carpet || d.Name == SterileTile || d.Name == StrawMatting))
            {
                costs[d.Name] = known;
            }
        }
        var research = new Pol.FloorStyleFacts { CarpetMaking = StyleResearchFinished(facts, "CarpetMaking"), SterileMaterials = StyleResearchFinished(facts, "SterileMaterials"), Costs = costs };
        return role => FloorDef(tier, role, stock, research);
    }

    /// <summary>The fixture the lighting planner prefers for a work cell: the module lighting rule for a non-farm module, empty when the tier styles no lamp.</summary>
    internal static string LampStyle(Observation.ColonyProjection facts)
    {
        PoweredSource(facts).TryGetValue(out var powered);
        var (lamp, ok) = PlannedLighting(StyleTier(facts), false, StyleStock(facts), powered);
        return ok ? lamp.Definition : "";
    }
}
