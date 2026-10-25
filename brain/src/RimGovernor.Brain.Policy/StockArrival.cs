using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;

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

/// <summary>The medicine the routine trade expects a day (<see cref="PerDay"/>, #3907) once a trader comp's visits start, <see cref="From"/> days from now.</summary>
public sealed record MedicineTradeSource(double From, double PerDay);

/// <summary>
/// The medicine the routine trade buys (#3907, as the autopilot buys it: the cheapest medicine kind, on the medical reserve's latch).
/// While <see cref="Active"/> (below <see cref="Reserve"/>'s entry, held until its target; the colony's usable medicine without
/// <see cref="Best"/>) the visits of <see cref="Sources"/> bring medicine at their rates, <see cref="Kind"/>, until
/// <see cref="Units"/> are bought (the silver above the reserve pays for that many). <see cref="Claimed"/> units are today's gap, whose
/// cost is claimed before the parts' (<see cref="StockArrival.WithMedicineClaim"/>); a later buy is named by the model.
/// <see cref="Better"/> are the kinds of higher potency, which tending takes before it.
/// </summary>
public sealed record MedicineTrade(Resource Kind, EquatableArray<MedicineTradeSource> Sources, double Units, double Claimed, Resource Best, long Colonists,
    MedicalReservePolicy Reserve, bool Active, EquatableArray<Resource> Better)
{
    /// <summary>The model's finding that the trade buys more medicine than today's gap, which no part's silver is charged for.</summary>
    public const string LaterBuysFinding = "medicine_trade_later_buys_unclaimed";

    /// <summary>The units a day the visits that have started by day <paramref name="day"/> bring.</summary>
    public double PerDayAt(double day) => Sources.Where(s => s.From <= day).Sum(s => s.PerDay);
}

/// <summary>
/// The stock-arrival model (#3868), built at the read boundary (<see cref="StockArrivalRules.Craft"/>); each outcome adds only its
/// selected requests (#3917) on <see cref="ForecastCoupledContext.Stock"/>. A craft source's not-arrived chance is a step: 1 until its arrival day, 0 after (crafting
/// is a fixed delay once the bill and its inputs exist); a trade source's is 1 until its comp starts and exponential after (#3869); a
/// resource no source makes never arrives. <see cref="Unread"/> names the fact the boundary could not read; a rule that then waits on
/// stock raises <see cref="RequiredFactException"/>.
/// </summary>
public sealed record StockArrival
{
    public EquatableArray<StockSource> Sources { get; init; }

    /// <summary>The trader-visit sources (#3869), one per comp and resource.</summary>
    public EquatableArray<TradeSource> Trade { get; init; }

    /// <summary>Available comms alternatives; their sources enter Trade only in an outcome selecting them.</summary>
    public EquatableArray<ForecastTradeRequest> Requests { get; init; }

    /// <summary>Exact-pack settlement alternatives, independent of supply demand.</summary>
    public EquatableArray<ForecastSettlementMission> Missions { get; init; }
    public EquatableArray<ForecastTradeVisit> Visits { get; init; }

    public EquatableArray<string> Findings { get; init; }

    /// <summary>The medicine the routine trade buys over the horizon (#3907); null buys none. Set by <see cref="WithMedicineClaim"/>.</summary>
    public MedicineTrade? MedicineTrade { get; init; }
    /// <summary>Today's medical reserve gap, claimed before replacement parts.</summary>
    public long MedicineReplenish { get; init; }

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
    /// c·e^(−r·s), so Π(1 − cᵢe^(−rᵢs)) expands term by term into Σ a_R·e^(−R·s), terms of equal rate merged (#3909), and
    /// 1 − Σ a_R·e^(−R·s) integrates exactly. 0 for no need.
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

        var cuts = steps.ToArray();
        var total = 0.0;
        for (var i = 0; i + 1 < cuts.Length; i++)
        {
            var length = cuts[i + 1] - cuts[i];
            // Π(1 − cₙe^(−rₙs)) as Σ a_R·e^(−R·s): each need multiplies every term by 1 and adds it at rate R + rₙ times −cₙ.
            var terms = new Dictionary<double, double> { [0] = 1 };
            foreach (var need in needs)
            {
                var chance = NotArrived(need, cuts[i]);
                if (chance == 0)
                {
                    continue;
                }

                var rate = Trade.Where(s => need.AnyOf.Contains(s.Resource) && s.From <= cuts[i]).Sum(s => s.Rate(need.Count));
                var next = new Dictionary<double, double>(terms);
                foreach (var (r, a) in terms)
                {
                    next[r + rate] = next.GetValueOrDefault(r + rate) - (chance * a);
                }

                terms = next;
            }

            // ∫₀ᴸ 1 − Σ a_R·e^(−R·s) ds = L − Σ a_R·(1 − e^(−R·L)) ÷ R, a rate of 0 integrating to L.
            total += length;
            foreach (var (r, a) in terms)
            {
                total -= a * (r > 0 ? -double.ExpM1(-r * length) / r : length);
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

    /// <summary>
    /// The silver shared between medicine and parts (#3907, #3855 Q2 A): the routine trade buys medicine first, so its gap's cost
    /// (<see cref="TradePolicyRules.MedicineClaim"/> of <paramref name="review"/>'s replenish) is claimed from each source's silver
    /// (<see cref="TradeSource.Silver"/>: the silver above the routine trade's reserve that its trader is paid from) and the parts' sources see what is left; the medicine sources see all of it and bring medicine on the reserve's latch. An unread
    /// reserve leaves the model unread.
    /// </summary>
    public StockArrival WithMedicineClaim(ItemFacts items, MedicalReserveReview review, long colonists, MedicalReservePolicy reserve)
    {
        if (Unread.Length != 0 || items.MedicineTiers().Count == 0)
        {
            return this;
        }

        var replenish = review.Replenish;

        var claim = (long)Math.Ceiling(TradePolicyRules.MedicineClaim(items, replenish));
        var parts = Trade.Select(s => Medicines.Contains(s.Resource) ? s : s with { Silver = Math.Max(0, s.Silver - claim) });
        var (kind, price) = items.CheapestMedicine();
        var tiers = items.MedicineTiers().Reverse().ToList();
        var buying = Trade.Where(s => s.Resource == kind && s.UnitsPerDay() > 0).ToList();
        var visits = buying.Select(s => new MedicineTradeSource(s.From, s.UnitsPerDay())).OrderBy(s => s.From).ThenBy(s => s.PerDay).ToList();
        // The units the silver pays for: the largest pool a visit buys from (beacon silver is mostly the same silver as the map's).
        var units = buying.Count == 0 ? 0 : Math.Floor(buying.Max(s => s.Silver) / price);
        return this with
        {
            MedicineReplenish = replenish,
            Trade = EquatableArray.From(parts),
            Requests = EquatableArray.From(Requests.Select(r => r with { Sources = EquatableArray.From(r.Sources.Select(s => Medicines.Contains(s.Resource) ? s : s with { Silver = Math.Max(0, s.Silver - claim) })) })),
            Missions = EquatableArray.From(Missions.Select(r => r with { Sources = EquatableArray.From(r.Sources.Select(s => Medicines.Contains(s.Resource) ? s : s with { Silver = Math.Max(0, s.Silver - claim) })) })),
            Visits = EquatableArray.From(Visits.Select(r => r with { Sources = EquatableArray.From(r.Sources.Select(s => Medicines.Contains(s.Resource) ? s : s with { Silver = Math.Max(0, s.Silver - claim) })) })),
            MedicineTrade = visits.Count == 0 || !(units > 0)
                ? null
                : new MedicineTrade(kind, EquatableArray.From(visits), units, replenish, items.BestMedicine(), colonists, reserve, review.Active, EquatableArray.From(tiers.Take(tiers.IndexOf(kind)))),
        };
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

/// <summary>A bench def hosting a stock recipe and the work type of the DoBill giver serving it there (the catalog's RecipeWork).</summary>
public sealed record StockRecipeBench(string Bench, WorkType Work);

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
    public static StockArrival Craft(IReadOnlyList<StockRecipe> recipes, IReadOnlyList<RimGovernor.Brain.GameFacts.Observations.BillStackRow> benches, IReadOnlyList<RimGovernor.Brain.GameFacts.Observations.PawnState> crafters,
        IReadOnlyDictionary<Resource, long> stock)
    {
        var workers = new List<(RimGovernor.Brain.GameFacts.Observations.PawnState Pawn, int Hours)>();
        foreach (var pawn in crafters)
        {
            if (PawnRules.WorkHours([.. pawn.Settings.Schedule.Select(slot => slot.AssignmentDefName)]) is not { } hours)
            {
                return new StockArrival { Unread = "stock_arrival_schedule:" + pawn.Pawn.Id };
            }
            workers.Add((pawn, hours));
        }
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
                foreach (var bench in benches.Where(b => b.Usable && b.BenchDef == host.Bench))
                {
                    foreach (var crafter in workers.Where(c => DoesWork(c.Pawn, host.Work) && recipe.Skills.All(s => Level(c.Pawn, s.Skill) >= s.MinLevel)))
                    {
                        if (Speed(crafter.Pawn, recipe.WorkSpeedStat) is not { } speed)
                        {
                            return new StockArrival { Unread = "work_speed:" + crafter.Pawn.Pawn.Id + ":" + recipe.WorkSpeedStat };
                        }

                        best = Math.Max(best, crafter.Hours * GameTime.TicksPerHour * speed * bench.WorkSpeed / recipe.WorkAmount * recipe.Batch);
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
    private static int Level(RimGovernor.Brain.GameFacts.Observations.PawnState crafter, string skill) => crafter.Biography.Skills.FirstOrDefault(s => s.DefName == skill)?.Level ?? 0;

    private static double? Speed(RimGovernor.Brain.GameFacts.Observations.PawnState crafter, string stat) => stat.Length == 0 ? 1 : crafter.Biography.WorkSpeeds.FirstOrDefault(s => s.Stat == stat)?.Value;

    private static bool DoesWork(RimGovernor.Brain.GameFacts.Observations.PawnState pawn, WorkType work) =>
        pawn.Settings.Work is RimGovernor.Brain.GameFacts.Observations.PawnWork.Works working
        && working.Rows.Any(row => row.Priority > 0 && !row.Disabled && WorkRules.WorkTypeOf(row.DefName) == work);

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
