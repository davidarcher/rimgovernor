using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

// The stock-arrival model (#3868 of #3650): when a stock a rule waits on (a replacement part, an organ, a medicine kind) arrives
// once it is wanted. Each source gives the chance a count of units has not arrived t days after the want; independent sources
// multiply those chances, and a consumer prices the expected wait capped at the days it has left, E[min(W, R)]. Crafting is a step
// source (StockArrivalRules.Craft); trader visits an exponential one from the day their comp starts (TradeArrivalRules.Sources, #3869).

/// <summary>
/// One source of a resource (#3868): from the want on it makes <see cref="Batch"/> units an iteration at <see cref="PerDay"/> units a
/// day, up to <see cref="Units"/> of them (a craft source's ingredients on hand); it makes no more.
/// </summary>
public sealed record StockSource(Resource Resource, double PerDay, long Units, long Batch)
{
    /// <summary>The day after the want <paramref name="count"/> units have arrived: the iterations that make them; never when the source cannot make them.</summary>
    public double ArrivalDay(long count)
    {
        if (count <= 0)
        {
            return 0;
        }

        if (count > Units || !(PerDay > 0) || Batch <= 0)
        {
            return double.PositiveInfinity;
        }

        var iterations = (count + Batch - 1) / Batch;
        return iterations * Batch / PerDay;
    }

    /// <summary>The chance <paramref name="count"/> units have not arrived <paramref name="t"/> days after the want: a step at <see cref="ArrivalDay"/>.</summary>
    public double NotArrived(long count, double t) => ArrivalDay(count) <= t ? 0 : 1;
}

/// <summary>What a rule waits on (#3868): <see cref="Count"/> units of any one of <see cref="AnyOf"/> (a surgery takes any medicine kind it allows).</summary>
public sealed record StockNeed(EquatableArray<Resource> AnyOf, long Count);

/// <summary>
/// The medicine crafting the colony model runs (#3868, as the game runs a stock-target bill): while the medicine held is below
/// <see cref="Target"/> the best crafter makes <see cref="Kind"/> at <see cref="PerDay"/> units a day, until <see cref="Units"/> are made.
/// <see cref="Better"/> are the kinds of higher potency, which tending takes before it.
/// </summary>
public sealed record MedicineCraft(Resource Kind, double PerDay, double Units, double Target, EquatableArray<Resource> Better);

/// <summary>
/// The stock-arrival model of one review (#3868), built once at the read boundary (<see cref="StockArrivalRules.Craft"/>) and carried on
/// <see cref="ForecastCoupledContext.Stock"/>. A craft source's not-arrived chance is a step: 1 until its arrival day, 0 after (crafting
/// is a fixed delay once the bill and its inputs exist); a trade source's is 1 until its comp starts and exponential after (#3869); a
/// resource no source makes never arrives. <see cref="Unread"/> names the fact the boundary could not read; a rule that then waits on
/// stock raises <see cref="RequiredFactException"/>.
/// </summary>
public sealed record StockArrival
{
    public EquatableArray<StockSource> Sources { get; init; }

    /// <summary>The trader-visit sources (#3869), one per comp and resource.</summary>
    public EquatableArray<TradeSource> Trade { get; init; }

    public EquatableArray<string> Findings { get; init; }

    /// <summary>The fact the boundary could not read to build the model; empty when it is built.</summary>
    public string Unread { get; init; } = "";

    /// <summary>The medicine kinds (ItemFacts.MedicalPotency): a surgery's medicine need takes any of them.</summary>
    public EquatableArray<Resource> Medicines { get; init; }

    /// <summary>The chance <paramref name="need"/> has not arrived <paramref name="t"/> days after the want: the product over every source of a resource it may take.</summary>
    public double NotArrived(StockNeed need, double t)
    {
        Require();
        var chance = 1.0;
        foreach (var source in Sources)
        {
            if (need.AnyOf.Contains(source.Resource))
            {
                chance *= source.NotArrived(need.Count, t);
            }
        }

        foreach (var source in Trade)
        {
            if (need.AnyOf.Contains(source.Resource))
            {
                chance *= source.NotArrived(need.Count, t);
            }
        }

        return chance;
    }

    /// <summary>The chance some of <paramref name="needs"/> has not arrived <paramref name="t"/> days after the want: 1 − Π(1 − not arrived).</summary>
    public double NotReady(IReadOnlyList<StockNeed> needs, double t)
    {
        var ready = 1.0;
        foreach (var need in needs)
        {
            ready *= 1 - NotArrived(need, t);
        }

        return 1 - ready;
    }

    /// <summary>Whether <paramref name="needs"/> may all arrive within <paramref name="days"/> of the want.</summary>
    public bool Arrives(IReadOnlyList<StockNeed> needs, double days) => NotReady(needs, days) < 1;

    /// <summary>
    /// E[min(W, cap)] for W the day all of <paramref name="needs"/> have arrived: the integral of <see cref="NotReady"/> from 0 to
    /// <paramref name="cap"/>, exact: between the craft sources' steps and the trade sources' starts each need's not-arrived chance is
    /// c·e^(−r·s), so 1 − Π(1 − cᵢe^(−rᵢs)) integrates by inclusion–exclusion over the needs. 0 for no need.
    /// </summary>
    public double ExpectedWait(IReadOnlyList<StockNeed> needs, double cap)
    {
        if (needs.Count == 0 || !(cap > 0))
        {
            return 0;
        }

        Require();
        var steps = new SortedSet<double> { 0, cap };
        foreach (var need in needs)
        {
            var days = Sources.Where(s => need.AnyOf.Contains(s.Resource)).Select(s => s.ArrivalDay(need.Count))
                .Concat(Trade.Where(s => need.AnyOf.Contains(s.Resource)).Select(s => s.From));
            steps.UnionWith(days.Where(day => day > 0 && day < cap));
        }

        if (needs.Count > 16)
        {
            throw new DomainException($"stock wait over {needs.Count} needs");
        }

        var cuts = steps.ToArray();
        var total = 0.0;
        var chances = new double[needs.Count];
        var rates = new double[needs.Count];
        for (var i = 0; i + 1 < cuts.Length; i++)
        {
            var length = cuts[i + 1] - cuts[i];
            for (var n = 0; n < needs.Count; n++)
            {
                chances[n] = NotArrived(needs[n], cuts[i]);
                rates[n] = Trade.Where(s => needs[n].AnyOf.Contains(s.Resource) && s.From <= cuts[i]).Sum(s => s.Rate(needs[n].Count));
            }

            // ∫₀ᴸ 1 − Π(1 − cₙe^(−rₙs)) ds = Σ over non-empty subsets S of (−1)^(|S|+1) Π cₙ ∫₀ᴸ e^(−R_S s) ds.
            for (var subset = 1; subset < 1 << needs.Count; subset++)
            {
                var product = 1.0;
                var rate = 0.0;
                var size = 0;
                for (var n = 0; n < needs.Count; n++)
                {
                    if ((subset & (1 << n)) != 0)
                    {
                        product *= chances[n];
                        rate += rates[n];
                        size++;
                    }
                }

                var integral = rate > 0 ? -double.ExpM1(-rate * length) / rate : length;
                total += (size % 2 == 1 ? 1 : -1) * product * integral;
            }
        }

        return total;
    }

    /// <summary>
    /// The medicine crafting toward <paramref name="target"/> units held (#3868): the first of <paramref name="kindsBestFirst"/> some source
    /// makes, at its fastest source's rate and units; null when no source makes a medicine.
    /// </summary>
    public MedicineCraft? MedicineCraft(IReadOnlyList<Resource> kindsBestFirst, double target)
    {
        Require();
        for (var i = 0; i < kindsBestFirst.Count; i++)
        {
            var kind = kindsBestFirst[i];
            if (Sources.Where(s => s.Resource == kind && s.PerDay > 0 && s.Units > 0).OrderByDescending(s => s.PerDay).FirstOrDefault() is { } best)
            {
                return new MedicineCraft(kind, best.PerDay, best.Units, target, EquatableArray.From(kindsBestFirst.Take(i)));
            }
        }

        return null;
    }

    private void Require()
    {
        if (Unread.Length != 0)
        {
            throw new RequiredFactException(Unread);
        }
    }
}

/// <summary>
/// What a picked replacement waits on (#3868): its part when none is on the map, its medicine when too little is; no need when all
/// of it is on hand.
/// </summary>
public sealed record StockWait(StockArrival Stock, EquatableArray<StockNeed> Needs)
{
    /// <summary>E[min(W, <paramref name="cap"/>)] days from the want until the surgery has its ingredients.</summary>
    public double Expected(double cap) => Needs.Count == 0 ? 0 : Stock.ExpectedWait(Needs, cap);

    /// <summary>
    /// Names <paramref name="stock"/>'s findings (silver income unmodelled, a bench stat unread) when a replacement waits on its stock or is
    /// short of a part: the wait or the shortage is what those sources would change.
    /// </summary>
    internal static void Name(StockArrival stock, SurgeryWantReason reason, StockWait? wait, SortedSet<string> findings)
    {
        if (reason == SurgeryWantReason.PartShort || wait is { Needs.Count: > 0 })
        {
            findings.UnionWith(stock.Findings);
        }
    }
}

/// <summary>A crafter's level in a skill (SkillRecord.Level); a recipe's requirement of it is a <see cref="SkillRequirement"/>.</summary>
public sealed record SkillLevel(string Skill, int Level);

/// <summary>A bench of the bills census (#3868): its def, whether it takes a new bill, its WorkTableWorkSpeedFactor now.</summary>
public sealed record StockBench(string Def, bool Usable, double WorkSpeed);

/// <summary>A bench def hosting a stock recipe and the work type of the DoBill giver serving it there (the catalog's RecipeWork).</summary>
public sealed record StockRecipeBench(string Bench, WorkType Work);

/// <summary>
/// A colonist who may craft (#3868): its timetable's work hours, the work types it does (enabled, priority above 0), its skill levels
/// and its work speed stats (<see cref="PawnBiography.WorkSpeeds"/>).
/// </summary>
public sealed record StockCrafter(string Pawn, int WorkHours, EquatableArray<WorkType> Works, EquatableArray<SkillLevel> Skills, EquatableArray<WorkSpeedRead> Speeds);

/// <summary>
/// A recipe that makes a stock a rule waits on (#3868, the catalog's StockRecipes): a medicine, or the part an install recipe takes,
/// available with the research finished. One iteration makes <see cref="Batch"/> of <see cref="Product"/> and takes
/// <see cref="WorkAmount"/> work (<c>RecipeDef.WorkAmountTotal</c>) at the crafter's <see cref="WorkSpeedStat"/> ("" works at 1) times
/// the bench's <see cref="BenchSpeedStat"/> (<c>Toils_Recipe.DoRecipeWork</c>), from the ingredient slots' alternatives.
/// </summary>
public sealed record StockRecipe
{
    public string Recipe { get; init; } = "";
    public Resource Product { get; init; }
    public long Batch { get; init; }
    public double WorkAmount { get; init; }
    public string WorkSpeedStat { get; init; } = "";
    public string BenchSpeedStat { get; init; } = "";
    public EquatableArray<StockRecipeBench> Benches { get; init; }
    public EquatableArray<SkillRequirement> Skills { get; init; }
    public EquatableArray<EquatableArray<Amount>> Ingredients { get; init; }
}

public static class StockArrivalRules
{
    /// <summary>The bench stat the bills census reads (BillStackRow.WorkSpeed) and RecipeDef defaults workTableSpeedStat to.</summary>
    public const string BenchSpeedStat = "WorkTableWorkSpeedFactor";

    /// <summary>A recipe whose bench speed stat the bills census does not read: no craft source for it.</summary>
    public const string BenchSpeedUnmodelled = "stock_arrival_bench_speed_unmodelled";

    /// <summary>
    /// The crafting sources (#3868): for each recipe, the best eligible crafter at the best usable bench hosting it, at
    /// <c>work hours × TicksPerHour × the crafter's workSpeedStat × the bench's WorkTableWorkSpeedFactor ÷ WorkAmountTotal × batch</c>
    /// units a day (Toils_Recipe.DoRecipeWork), up to the iterations the stock of its scarcest ingredient slot pays for. A crafter is
    /// eligible with the bench's work type and every skill requirement met (<c>RecipeDef.PawnSatisfiesSkillRequirements</c>). A recipe
    /// no crafter, bench or ingredient serves is no source. A crafter without the recipe's speed stat read leaves the model unread
    /// (<see cref="StockArrival.Unread"/>).
    /// </summary>
    public static StockArrival Craft(IReadOnlyList<StockRecipe> recipes, IReadOnlyList<StockBench> benches, IReadOnlyList<StockCrafter> crafters,
        IReadOnlyDictionary<Resource, long> stock)
    {
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        var sources = new List<StockSource>();
        foreach (var recipe in recipes.OrderBy(r => r.Recipe, StringComparer.Ordinal))
        {
            if (recipe.BenchSpeedStat != BenchSpeedStat)
            {
                findings.Add(BenchSpeedUnmodelled + ":" + recipe.Recipe);
                continue;
            }

            if (!(recipe.WorkAmount > 0) || recipe.Batch <= 0)
            {
                throw new DomainException($"stock recipe {recipe.Recipe} makes {recipe.Batch} at {recipe.WorkAmount} work");
            }

            var best = 0.0;
            foreach (var host in recipe.Benches)
            {
                foreach (var bench in benches.Where(b => b.Usable && b.Def == host.Bench))
                {
                    foreach (var crafter in crafters.Where(c => c.Works.Contains(host.Work) && recipe.Skills.All(s => Level(c, s.Skill) >= s.MinLevel)))
                    {
                        if (Speed(crafter, recipe.WorkSpeedStat) is not { } speed)
                        {
                            return new StockArrival { Unread = "work_speed:" + crafter.Pawn + ":" + recipe.WorkSpeedStat };
                        }

                        best = Math.Max(best, crafter.WorkHours * GameTime.TicksPerHour * speed * bench.WorkSpeed / recipe.WorkAmount * recipe.Batch);
                    }
                }
            }

            var iterations = Iterations(recipe.Ingredients, stock);
            if (best > 0 && iterations > 0)
            {
                sources.Add(new StockSource(recipe.Product, best, iterations > long.MaxValue / recipe.Batch ? long.MaxValue : iterations * recipe.Batch, recipe.Batch));
            }
        }

        return new StockArrival { Sources = EquatableArray.From(sources), Findings = EquatableArray.From(findings) };
    }

    // SkillRequirement.PawnSatisfies reads the skill's level; a skill the pawn lacks is at 0.
    private static int Level(StockCrafter crafter, string skill) => crafter.Skills.FirstOrDefault(s => s.Skill == skill)?.Level ?? 0;

    private static double? Speed(StockCrafter crafter, string stat) => stat.Length == 0 ? 1 : crafter.Speeds.FirstOrDefault(s => s.Stat == stat)?.Value;

    // The iterations the stock pays for: each slot by its best-stocked alternative, the scarcest slot bounding them.
    private static long Iterations(EquatableArray<EquatableArray<Amount>> slots, IReadOnlyDictionary<Resource, long> stock)
    {
        var iterations = long.MaxValue;
        foreach (var slot in slots)
        {
            var paid = 0L;
            foreach (var alternative in slot.Where(a => a.Count > 0))
            {
                paid = Math.Max(paid, stock.GetValueOrDefault(alternative.Resource) / alternative.Count);
            }

            iterations = Math.Min(iterations, paid);
        }

        return iterations;
    }
}
