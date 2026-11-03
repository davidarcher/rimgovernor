namespace RimGovernor.Brain.Policy;

public static partial class ColonyPlanningRules
{

    /// <summary>The cheapest common construction stuff, with ordinal names breaking equal values.</summary>
    public static (double Work, IReadOnlyList<Amount> Costs)? HostPiece(ReviewFacts colony, string name)
    {
        if (!TryDefinition(colony, name, out var def)) return null;
        if (def.StuffOptions.Count == 0) return ShellPiece(colony, name, "");
        var cheapest = def.StuffOptions.Where(o => o.Common).OrderBy(o => o.Value).ThenBy(o => o.Stuff, StringComparer.Ordinal).FirstOrDefault();
        return cheapest is null ? null : ShellPiece(colony, name, cheapest.Stuff);
    }

    /// <summary>A shell piece's work to build and cost list in <paramref name="stuff"/> (its own for a def made from no stuff); null where the catalog shows neither.</summary>
    public static (double Work, IReadOnlyList<Amount> Costs)? ShellPiece(ReviewFacts colony, string name, string stuff)
    {
        if (!TryDefinition(colony, name, out var def))
        {
            return null;
        }
        if (def.StuffOptions.Count == 0)
        {
            var own = (def.FixedConstruction ?? throw new RequiredFactException("planning FixedConstruction")).WorkToBuild;
            var costs = (def.FixedConstruction ?? throw new RequiredFactException("planning FixedConstruction")).Costs;
            return (own, costs);
        }
        return def.StuffOptions.FirstOrDefault(o => o.Stuff == stuff) is { } option && option.Stats.TryGetValue(Domain.StatDefNames.StatWorkToBuild, out var work)
            ? (work, option.Costs) : null;
    }

    /// <summary>
    /// A furniture piece's effect on its room (RoomImprovementValue) in the stuff an ordinary placement builds it from
    /// (BuildStuff): its Beauty and MarketValue stats, its footprint, its work and cost list. Null where the catalog
    /// shows no row, no stuff option or one of those stats for it, which leaves the improvement unassessed.
    /// </summary>
    public static RoomPieceEffect? RoomPieceOf(ReviewFacts facts, IReadOnlyDictionary<Resource, long> stock, string name, Cell size)
    {
        if (!TryDefinition(facts, name, out var def) || def.StuffOptions.FirstOrDefault(o => o.Stuff == BuildStuff(facts, stock, name)) is not { } option)
        {
            return null;
        }
        return option.Stats.TryGetValue(Domain.StatDefNames.StatBeauty, out var beauty) && option.Stats.TryGetValue(Domain.StatDefNames.StatMarketValue, out var wealth)
            && option.Stats.TryGetValue(Domain.StatDefNames.StatWorkToBuild, out var work)
            ? new RoomPieceEffect(beauty, wealth, Math.Max(size.X, 1) * Math.Max(size.Z, 1), work, option.Costs) : null;
    }

    /// <summary>Name's resolved planning row.</summary>
    public static bool TryDefinition(ReviewFacts facts, string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out PlanningDefinition? definition)
    {
        foreach (var d in facts.Definitions)
        {
            if (d.Name == name)
            {
                definition = d;
                return true;
            }
        }
        definition = null;
        return false;
    }

    /// <summary>
    /// The stuff an ordinary placement builds the def from: BulkBuildStuff for one placement. A def not made from
    /// stuff, a def the rows cannot choose for and a def the colony supplies no stuff of yield the empty stuff, which
    /// the native placement builds in the game's default stuff.
    /// </summary>
    public static string BuildStuff(ReviewFacts facts, IReadOnlyDictionary<Resource, long> stock, string name) => BulkBuildStuff(facts, stock, name, 1);

    /// <summary>BuildStuff for a def raised in bulk (a shell's walls).</summary>
    public static string BulkBuildStuff(ReviewFacts facts, IReadOnlyDictionary<Resource, long> stock, string name, long units) => BulkBuildStuffExcept(facts, stock, name, units, null);

    /// <summary>
    /// The stuff a def is raised <paramref name="units"/> times from, ranked by BuildCriteria among the ordinary
    /// stuffs the colony supplies: the stock, with the wood standing as trees counting toward wood (an unread
    /// acquisition census reads as wooded). A stuff the supply covers every placement of ranks first; with none
    /// covered, a stuff the colony holds any of or the planned ground offers (RingStuffSources: a lying stack, or
    /// what deconstructing an unowned building there yields), so the frame waits only for material the colony can
    /// gather. A stuff the colony has none of is never chosen: nothing cuts the blocks a granite frame would wait
    /// for. With nothing supplied the choice is the empty stuff, built in the game's default stuff. Without the
    /// stuffs <paramref name="excluded"/> names (null excludes none).
    /// </summary>
    public static string BulkBuildStuffExcept(ReviewFacts facts, IReadOnlyDictionary<Resource, long> stock, string name, long units, Func<string, bool>? excluded)
    {
        if (!TryDefinition(facts, name, out var d) || d.StuffOptions.Count == 0)
        {
            return "";
        }
        var criteria = d.BuildCriteria();
        var ordinary = d.OrdinaryStuff();
        bool Allowed(StuffOption o) => ordinary(o) && (excluded is null || !excluded(o.Stuff));
        double Have(Resource resource)
        {
            double held = stock.TryGetValue(resource, out var n) ? n : 0;
            if (resource != ConstructionDemandRules.ConstructionWoodLog)
            {
                return held;
            }
            foreach (var s in facts.Acquisition)
            {
                if (s.Tree && s.Resource == resource.Value)
                {
                    held += s.Yield;
                }
            }
            return held;
        }

        // The catalog prices a stuff with the def's fixed cost list too (a bench's 25 steel), which every option pays
        // alike; what tells the options apart is the stuff itself, so that is what the supply must cover.
        bool Covered(StuffOption o)
        {
            foreach (var a in o.Costs)
            {
                if (a.Resource.Value == o.Stuff)
                {
                    return Have(a.Resource) >= (double)(a.Count * units);
                }
            }
            return o.Costs.Count > 0 && !o.Costs.Any(a => Have(a.Resource) < (double)(a.Count * units));
        }
        bool Supplied(StuffOption o) => Have(new Resource(o.Stuff)) > 0
            || (facts.RingSources is { } offered && (offered.Items.Contains(o.Stuff) || offered.Salvage.Contains(o.Stuff)));

        try
        {
            if (d.StuffOptions.Any(o => Allowed(o) && Covered(o)))
            {
                return d.ChooseStuff(criteria, o => Allowed(o) && Covered(o)).Stuff;
            }
            return d.StuffOptions.Any(o => Allowed(o) && Supplied(o)) ? d.ChooseStuff(criteria, o => Allowed(o) && Supplied(o)).Stuff : "";
        }
        catch (DomainException)
        {
            return "";
        }
    }
}
