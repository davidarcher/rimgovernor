namespace RimGovernor.Brain.Policy;

public static partial class ColonyPlanningRules
{
    public static FlooringFacts FlooringFacts(ReviewFacts facts, IReadOnlyList<ResearchProjectId> finished)
    {
        var definitions = new Dictionary<string, FloorDefinition>(StringComparer.Ordinal);
        var flooring = new FlooringFacts
        {
            Definitions = definitions,
            Stock = ResourceStockRules.Count(facts.Census.Resources),
            Style = FloorStyle(facts, finished),
            Layout = facts.LayoutPlan,
            Observation = facts.Upkeep.Flooring,
        };
        if (facts.Containment.Defs is ContainmentDefsRead.Read { Defs: var defs })
        {
            flooring = flooring with { ContainmentFloor = defs.Floor };
        }
        foreach (var d in facts.Definitions.Where(d => d.Floor is not null))
        {
            definitions[d.Name] = new FloorDefinition
            {
                Available = d.Available,
                Terrain = d.Terrain,
                Cleanliness = (d.Floor ?? throw new RequiredFactException("planning Floor")).Cleanliness,
                Beauty = (d.Floor ?? throw new RequiredFactException("planning Floor")).Beauty,
                Flammability = (d.Floor ?? throw new RequiredFactException("planning Floor")).Flammability,
                PathCost = d.PathCost,
                Costs = (d.FixedConstruction ?? throw new RequiredFactException("planning FixedConstruction")).Costs,
                WorkToBuild = (d.FixedConstruction ?? throw new RequiredFactException("planning FixedConstruction")).WorkToBuild,
                Tags = (d.Floor ?? throw new RequiredFactException("planning Floor")).FloorTags,
            };
        }
        return flooring;
    }

    public static Func<RoomRole, (string Def, bool Ok)>? FloorStyle(ReviewFacts facts, IReadOnlyList<ResearchProjectId> finished)
    {
        var tier = TechTierRules.SelectTechTier(finished, facts.Census.PlayerTechLevel);
        var stock = new TierStyleStock(ResourceStockRules.Count(facts.Census.Resources));
        if (tier < TechTier.Masonry)
        {
            return null;
        }
        var costs = new Dictionary<string, IReadOnlyList<Amount>>(StringComparer.Ordinal);
        foreach (var d in facts.Definitions)
        {
            if ((d.Name == TierStyleRules.Carpet || d.Name == TierStyleRules.SterileTile || d.Name == TierStyleRules.StrawMatting))
            {
                if (d.Available) costs[d.Name] = d.CheapestCosts();
            }
        }
        var research = new FloorStyleFacts { CarpetMaking = ResearchRules.ResearchGate(["CarpetMaking"], facts.Research) == "", SterileMaterials = ResearchRules.ResearchGate(["SterileMaterials"], facts.Research) == "", Costs = costs };
        return role => TierStyleRules.FloorDef(tier, role, stock, research);
    }
}
