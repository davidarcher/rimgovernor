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
    /// The stuff an ordinary placement builds the def from: the best ranked allowed stuff the colony stocks
    /// (BuildCriteria), else the best ranked allowed stuff whatever the stock (RimWorld places the frame and holds it
    /// natively for material). A def not made from stuff and a def the rows cannot choose for yield
    /// the empty stuff.
    /// </summary>
    public static string BuildStuff(ReviewFacts facts, IReadOnlyDictionary<Resource, long> stock, string name)
    {
        if (TryStockedBuildStuff(facts, stock, name, out var stocked))
        {
            return stocked;
        }
        if (!TryDefinition(facts, name, out var d) || d.StuffOptions.Count == 0)
        {
            return "";
        }
        try
        {
            return d.ChooseStuff(d.BuildCriteria(), d.OrdinaryStuff()).Stuff;
        }
        catch (DomainException)
        {
            return "";
        }
    }

    /// <summary>BuildStuff over the stuffs the colony stocks to build the def once; false when none is stocked or the def has no stuff to choose.</summary>
    public static bool TryStockedBuildStuff(ReviewFacts facts, IReadOnlyDictionary<Resource, long> stock, string name, out string stuff)
    {
        stuff = "";
        if (!TryDefinition(facts, name, out var d) || d.StuffOptions.Count == 0)
        {
            return false;
        }
        var ordinary = d.OrdinaryStuff();
        try
        {
            stuff = d.ChooseStuff(d.BuildCriteria(), o => ordinary(o) && StuffChoiceRules.Stocked(o.Costs, stock)).Stuff;
            return true;
        }
        catch (DomainException)
        {
            return false;
        }
    }

    /// <summary>BuildStuff for a def raised in bulk (a shell's walls).</summary>
    public static string BulkBuildStuff(ReviewFacts facts, IReadOnlyDictionary<Resource, long> stock, string name, long units) => BulkBuildStuffExcept(facts, stock, name, units, null);

    /// <summary>
    /// BuildStuff for a def raised in bulk (a shell's walls): a stuff competes only when the stock covers
    /// <paramref name="units"/> placements of it, the wood standing as trees counting toward wood (an unread
    /// acquisition census reads as wooded). With none covered it is the best ranked ordinary stuff whatever the
    /// stock, so the ring waits for it to be gathered: the stone blocks of a map short of wood, once they are
    /// quarried. Without the stuffs <paramref name="excluded"/> names (null excludes none): when an exclusion is
    /// given and no remaining stuff is covered, the fallback is the best ranked remaining stuff the colony holds any
    /// of, so a ring never waits on a stuff the map cannot supply.
    /// </summary>
    public static string BulkBuildStuffExcept(ReviewFacts facts, IReadOnlyDictionary<Resource, long> stock, string name, long units, Func<string, bool>? excluded)
    {
        if (!TryDefinition(facts, name, out var d) || d.StuffOptions.Count == 0)
        {
            return "";
        }
        if (excluded is not null)
        {
            var first = BulkBuildStuffExcept(facts, stock, name, units, null);
            if (!excluded(first))
            {
                return first;
            }
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
        // alike; what tells the options apart is the stuff itself, so that is what the stock must cover.
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

        if (d.StuffOptions.Any(o => Allowed(o) && Covered(o)))
        {
            return d.ChooseStuff(criteria, o => Allowed(o) && Covered(o)).Stuff;
        }
        if (excluded is not null && d.StuffOptions.Any(o => Allowed(o) && stock.TryGetValue(new Resource(o.Stuff), out var held) && held > 0))
        {
            return d.ChooseStuff(criteria, o => Allowed(o) && stock.TryGetValue(new Resource(o.Stuff), out var held) && held > 0).Stuff;
        }
        return d.StuffOptions.Any(Allowed) ? d.ChooseStuff(criteria, Allowed).Stuff : "";
    }
}