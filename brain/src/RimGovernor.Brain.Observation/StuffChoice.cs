namespace RimGovernor.Brain.Observation;

/// <summary>
/// The stat a planner ranks the allowed stuffs of a def by. Every criterion reads the stuff options' stat values
/// from the game's stat table, so no stuff is named in code.
/// </summary>
public enum StuffCriterion
{
    /// <summary>The least market value of the adjusted cost list.</summary>
    CheapestStuff,

    /// <summary>The most MaxHitPoints per unit of cost value.</summary>
    MaxHitPointsPerCost,

    /// <summary>The least Flammability.</summary>
    LowestFlammability,

    /// <summary>The most BedRestEffectiveness.</summary>
    MaxRestEffectiveness,

    /// <summary>The most DoorOpenSpeed.</summary>
    MaxDoorOpenSpeed,
}

/// <summary>
/// StuffChoice's refusal when no allowed stuff of the def is stocked to build it once: the planner decides what to
/// do (hold, or wait for the native frame to collect material).
/// </summary>
public sealed class NoStuffInStockException(string message) : DomainException(message);

/// <summary>
/// StuffChoice's refusal when the catalog row cannot state the choice: a stuffed def with no allowed stuff, a
/// missing cost list or a criterion stat the game shows no value of.
/// </summary>
public sealed class NoStuffDataException(string message) : DomainException(message);

/// <summary>FireproofStuff's refusal when even the least flammable allowed stuff of the def burns.</summary>
public sealed class FlammableStuffException(string message) : DomainException(message);

/// <summary>A def's chosen stuff with its adjusted cost list; Stuff is empty for a def not made from stuff.</summary>
public sealed record StuffPrice(string Stuff, IReadOnlyList<Amount> Costs);

public sealed partial record PlanningDefinition
{
    /// <summary>
    /// Prices a def: a def not made from stuff is priced from its cost list directly; a stuffed def takes the allowed
    /// stuff that the criterion ranks best among those the stock covers once (null stock covers none). A tie goes to
    /// the lower stuff name. The def's rows lacking what the choice needs is <see cref="NoStuffDataException"/>;
    /// nothing stocked is <see cref="NoStuffInStockException"/>.
    /// </summary>
    public StuffPrice StuffChoice(StuffCriterion criterion, IReadOnlyDictionary<Resource, long>? stock) =>
        ChooseStuff([criterion], option => ObservationStuff.Stocked(option.Costs, stock));

    /// <summary>
    /// The allowed stuff with the least Flammability, read from the game's stat table, and refused when that stuff
    /// still burns: a wall or door that must hold a fire in is never wood. A def not made from stuff has no stuff to
    /// choose and no flammability to read, so it is refused as <see cref="NoStuffDataException"/>.
    /// </summary>
    public string FireproofStuff()
    {
        if (StuffOptions.Count == 0)
        {
            throw new NoStuffDataException($"the definition's rows do not state the stuff choice: {Name} has no stuff options");
        }
        var price = UnstockedStuffChoice(StuffCriterion.LowestFlammability);
        foreach (var option in StuffOptions)
        {
            if (option.Stuff == price.Stuff && option.Stats.TryGetValue(BridgeConstants.StatFlammability, out var flammability) && flammability > 0)
            {
                throw new FlammableStuffException($"every allowed stuff of the definition is flammable: {Name}, least flammable {price.Stuff}");
            }
        }
        return price.Stuff;
    }

    /// <summary>StuffChoice over every allowed stuff, stocked or not: the choice for a planner whose frame the game fills natively.</summary>
    public StuffPrice UnstockedStuffChoice(StuffCriterion criterion) => ChooseStuff([criterion], _ => true);

    /// <summary>The allowed option the criteria rank best, each criterion breaking the ties of the one before it, the lower stuff name the last.</summary>
    internal StuffPrice ChooseStuff(IReadOnlyList<StuffCriterion> criteria, Func<StuffOption, bool> allowed)
    {
        if (StuffOptions.Count == 0)
        {
            if (!Costs.TryGetValue(out var costs) || Stuffed)
            {
                throw new NoStuffDataException($"the definition's rows do not state the stuff choice: {Name} has no stuff options and no cost list");
            }
            return new StuffPrice("", [.. costs]);
        }
        StuffOption? best = null;
        double[] bestScores = [];
        foreach (var option in StuffOptions)
        {
            if (!allowed(option))
            {
                continue;
            }
            var scores = new double[criteria.Count];
            for (var j = 0; j < criteria.Count; j++)
            {
                scores[j] = Score(criteria[j], option);
            }
            if (best is null)
            {
                best = option;
                bestScores = scores;
                continue;
            }
            var c = ObservationStuff.Compare(bestScores, scores);
            if (c < 0 || (c == 0 && string.CompareOrdinal(option.Stuff, best.Stuff) < 0))
            {
                best = option;
                bestScores = scores;
            }
        }
        if (best is null)
        {
            throw new NoStuffInStockException($"no allowed stuff of the definition is in stock: {Name}");
        }
        return new StuffPrice(best.Stuff, [.. best.Costs]);
    }

    /// <summary>The option's rank under criterion, higher better.</summary>
    internal double Score(StuffCriterion criterion, StuffOption option)
    {
        double Stat(string name) =>
            option.Stats.TryGetValue(name, out var value)
                ? value
                : throw new NoStuffDataException($"the definition's rows do not state the stuff choice: {Name} made of {option.Stuff} shows no {name}");

        switch (criterion)
        {
            case StuffCriterion.CheapestStuff:
                return -option.Value;
            case StuffCriterion.MaxHitPointsPerCost:
                var hp = Stat(BridgeConstants.StatMaxHitPoints);
                if (option.Value <= 0)
                {
                    throw new NoStuffDataException($"the definition's rows do not state the stuff choice: {Name} made of {option.Stuff} has no cost value");
                }
                return hp / option.Value;
            case StuffCriterion.LowestFlammability:
                return -Stat(BridgeConstants.StatFlammability);
            case StuffCriterion.MaxRestEffectiveness:
                return Stat(BridgeConstants.StatBedRestEffectiveness);
            case StuffCriterion.MaxDoorOpenSpeed:
                return Stat(BridgeConstants.StatDoorOpenSpeed);
            default:
                throw new NoStuffDataException($"the definition's rows do not state the stuff choice: unknown stuff criterion {(int)criterion}");
        }
    }

    /// <summary>
    /// Whether the def can be built from stuff: any def can be built from no stuff when it is not made from stuff, and
    /// a stuffed def from each of its allowed stuffs.
    /// </summary>
    public bool MakeableFrom(string stuff) =>
        StuffOptions.Count == 0 ? stuff == "" : StuffOptions.Any(o => o.Stuff == stuff);

    /// <summary>
    /// The cost list of the cheapest way to build the def, whatever is in stock: the planning price of infrastructure a
    /// planner has not yet chosen material for; unknown when the rows cannot state it.
    /// </summary>
    public Fact<EquatableArray<Amount>> CheapestCosts()
    {
        try
        {
            return Known(EquatableArray.From(UnstockedStuffChoice(StuffCriterion.CheapestStuff).Costs));
        }
        catch (DomainException)
        {
            return Unknown<EquatableArray<Amount>>();
        }
    }

    /// <summary>The cost list of the def built from stuff (empty for a def not made from stuff); false when the def has no such cost list.</summary>
    public bool TryCostsMadeOf(string stuff, out IReadOnlyList<Amount> costs)
    {
        if (StuffOptions.Count == 0)
        {
            var known = Costs.TryGetValue(out var list);
            costs = list is null ? [] : [.. list];
            return known && !Stuffed && stuff == "";
        }
        foreach (var option in StuffOptions)
        {
            if (option.Stuff == stuff)
            {
                costs = option.Costs;
                return true;
            }
        }
        costs = [];
        return false;
    }

    /// <summary>
    /// How an ordinary placement ranks the def's allowed stuffs, read from the stats the game shows for the def made
    /// of each: a bed by rest effectiveness and an unpowered door by open speed first, then every def by hit points per
    /// cost; flammability breaks the ties. Beauty is no criterion: ranked blind to cost it would spend trade gold on
    /// tables. A stat any option lacks leaves its criterion out, and a def whose rows state none ranks by cheapest.
    /// </summary>
    public IReadOnlyList<StuffCriterion> BuildCriteria()
    {
        bool Shown(string stat) => !StuffOptions.Any(o => !o.Stats.ContainsKey(stat));
        var criteria = new List<StuffCriterion>();
        NeedsPower.TryGetValue(out var powered);
        if (Shown(BridgeConstants.StatBedRestEffectiveness))
        {
            criteria.Add(StuffCriterion.MaxRestEffectiveness);
        }
        else if (Shown(BridgeConstants.StatDoorOpenSpeed) && !powered)
        {
            criteria.Add(StuffCriterion.MaxDoorOpenSpeed);
        }
        if (Shown(BridgeConstants.StatMaxHitPoints) && !StuffOptions.Any(o => o.Value <= 0))
        {
            criteria.Add(StuffCriterion.MaxHitPointsPerCost);
        }
        if (Shown(BridgeConstants.StatFlammability))
        {
            criteria.Add(StuffCriterion.LowestFlammability);
        }
        return criteria.Count == 0 ? [StuffCriterion.CheapestStuff] : criteria;
    }

    /// <summary>
    /// The stuffs a def is planned from: the ordinary ones while the def allows one (Bioferrite is cheap and durable,
    /// but a colony never builds a bench, a bed or a wall from it), else any.
    /// </summary>
    internal Func<StuffOption, bool> OrdinaryStuff() =>
        StuffOptions.Any(o => o.Common) ? (o => o.Common) : (_ => true);
}

internal static class ObservationStuff
{
    /// <summary>Whether the stock covers the cost list at least once.</summary>
    internal static bool Stocked(IReadOnlyList<Amount> costs, IReadOnlyDictionary<Resource, long>? stock)
    {
        if (costs.Count == 0)
        {
            return false;
        }
        foreach (var cost in costs)
        {
            var held = stock is not null && stock.TryGetValue(cost.Resource, out var n) ? n : 0;
            if (cost.Count > 0 && held < cost.Count)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Lexicographic comparison of equal-length score lists.</summary>
    internal static int Compare(double[] a, double[] b)
    {
        for (var i = 0; i < a.Length && i < b.Length; i++)
        {
            var c = a[i].CompareTo(b[i]);
            if (c != 0)
            {
                return c;
            }
        }
        return a.Length.CompareTo(b.Length);
    }
}

/// <summary>
/// The first def named is the one stuff a set of defs built together is made from: the first def's cheapest allowed
/// stuff, which every other def must be makeable from too (defs not made from stuff share the empty stuff).
/// </summary>
public static partial class ObservationPackage
{
    /// <summary>The one stuff the defs share; false when they cannot share one.</summary>
    public static bool SharedStuff(PlanningDefinition first, PlanningDefinition other, out string stuff) => SharedStuff(first, [other], out stuff);

    public static bool SharedStuff(PlanningDefinition first, IReadOnlyList<PlanningDefinition> others, out string stuff)
    {
        stuff = "";
        if (first.StuffOptions.Count > 0)
        {
            try
            {
                stuff = first.UnstockedStuffChoice(StuffCriterion.CheapestStuff).Stuff;
            }
            catch (DomainException)
            {
                stuff = "";
                return false;
            }
        }
        if (!first.MakeableFrom(stuff))
        {
            return false;
        }
        foreach (var d in others)
        {
            if (!d.MakeableFrom(stuff))
            {
                return false;
            }
        }
        return true;
    }
}

public sealed partial record ColonyProjection
{
    /// <summary>Name's resolved planning row.</summary>
    public bool TryDefinition(string name, out PlanningDefinition definition)
    {
        foreach (var d in Definitions)
        {
            if (d.Name == name)
            {
                definition = d;
                return true;
            }
        }
        definition = new PlanningDefinition();
        return false;
    }

    /// <summary>The colony's resource census; false when it is unknown.</summary>
    public bool TryStock(out IReadOnlyDictionary<Resource, long> stock) => Resources.TryGetValue(out stock!);

    /// <summary>
    /// The stuff an ordinary placement builds the def from: the best ranked allowed stuff the colony stocks
    /// (BuildCriteria), else the best ranked allowed stuff whatever the stock (RimWorld places the frame and holds it
    /// natively for material). A def not made from stuff, an unknown stock and a def the rows cannot choose for yield
    /// the empty stuff.
    /// </summary>
    public string BuildStuff(string name)
    {
        if (TryStockedBuildStuff(name, out var stocked))
        {
            return stocked;
        }
        if (!TryDefinition(name, out var d) || d.StuffOptions.Count == 0)
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
    public bool TryStockedBuildStuff(string name, out string stuff)
    {
        stuff = "";
        var ok = TryDefinition(name, out var d);
        var known = TryStock(out var stock);
        if (!ok || d.StuffOptions.Count == 0 || !known)
        {
            return false;
        }
        var ordinary = d.OrdinaryStuff();
        try
        {
            stuff = d.ChooseStuff(d.BuildCriteria(), o => ordinary(o) && ObservationStuff.Stocked(o.Costs, stock)).Stuff;
            return true;
        }
        catch (DomainException)
        {
            return false;
        }
    }

    /// <summary>BuildStuff for a def raised in bulk (a shell's walls).</summary>
    public string BulkBuildStuff(string name, long units) => BulkBuildStuffExcept(name, units, null);

    /// <summary>
    /// BuildStuff for a def raised in bulk (a shell's walls): a stuff competes only when the stock covers
    /// <paramref name="units"/> placements of it, the wood standing as trees counting toward wood (an unread
    /// acquisition census reads as wooded). With none covered it is the best ranked ordinary stuff whatever the
    /// stock, so the ring waits for it to be gathered: the stone blocks of a map short of wood, once they are
    /// quarried. Without the stuffs <paramref name="excluded"/> names (null excludes none): when an exclusion is
    /// given and no remaining stuff is covered, the fallback is the best ranked remaining stuff the colony holds any
    /// of, so a ring never waits on a stuff the map cannot supply.
    /// </summary>
    public string BulkBuildStuffExcept(string name, long units, Func<string, bool>? excluded)
    {
        if (!TryDefinition(name, out var d) || d.StuffOptions.Count == 0)
        {
            return "";
        }
        if (excluded is not null)
        {
            var first = BulkBuildStuffExcept(name, units, null);
            if (!excluded(first))
            {
                return first;
            }
        }
        var criteria = d.BuildCriteria();
        var ordinary = d.OrdinaryStuff();
        bool Allowed(StuffOption o) => ordinary(o) && (excluded is null || !excluded(o.Stuff));
        if (TryStock(out var stock))
        {
            double Have(Resource resource)
            {
                double held = stock.TryGetValue(resource, out var n) ? n : 0;
                if (resource.Value != "WoodLog")
                {
                    return held;
                }
                if (!Acquisition.TryGetValue(out var sources))
                {
                    return double.PositiveInfinity;
                }
                foreach (var s in sources)
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

            try
            {
                return d.ChooseStuff(criteria, o => Allowed(o) && Covered(o)).Stuff;
            }
            catch (DomainException)
            {
            }
            if (excluded is not null)
            {
                try
                {
                    return d.ChooseStuff(criteria, o => Allowed(o) && stock.TryGetValue(new Resource(o.Stuff), out var held) && held > 0).Stuff;
                }
                catch (DomainException)
                {
                }
            }
        }
        try
        {
            return d.ChooseStuff(criteria, Allowed).Stuff;
        }
        catch (DomainException)
        {
            return "";
        }
    }

    /// <summary>
    /// Whether replacing a built def made of <paramref name="have"/> by <paramref name="want"/> is an upgrade the
    /// colony can afford: want ranks strictly above have under the def's build criteria (equal ranks are no upgrade,
    /// so the tie-break by name never swaps a wall back and forth) and the stock covers one of it. False when either
    /// stuff is not an allowed option or the stock is unknown.
    /// </summary>
    public bool StuffUpgrade(string name, string have, string want)
    {
        var ok = TryDefinition(name, out var d);
        var known = TryStock(out var stock);
        if (!ok || !known || have == want)
        {
            return false;
        }
        StuffOption? haveOption = null, wantOption = null;
        foreach (var option in d.StuffOptions)
        {
            if (option.Stuff == have)
            {
                haveOption = option;
            }
            else if (option.Stuff == want)
            {
                wantOption = option;
            }
        }
        if (haveOption is null || wantOption is null || !ObservationStuff.Stocked(wantOption.Costs, stock))
        {
            return false;
        }
        var haveScores = new List<double>();
        var wantScores = new List<double>();
        try
        {
            foreach (var criterion in d.BuildCriteria())
            {
                haveScores.Add(d.Score(criterion, haveOption));
                wantScores.Add(d.Score(criterion, wantOption));
            }
        }
        catch (DomainException)
        {
            return false;
        }
        return ObservationStuff.Compare([.. haveScores], [.. wantScores]) < 0;
    }
}
