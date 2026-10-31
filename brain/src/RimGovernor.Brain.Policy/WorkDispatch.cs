using RimGovernor.Brain.GameFacts.Observations;

namespace RimGovernor.Brain.Policy;

// The bench dispatcher is the decision half of the work ledger: how many benches should carry
// each stock-target order (the rate model, BenchesWanted), whether production keeps up with that prediction (a
// rolling one-day window of actual recipe output) and, when it does not, whether one more bench would help or what
// stops it. buildingruntime only feeds it observations and keeps the memory it returns.

/// <summary>Says why a kind's throughput stays unmet.</summary>
public enum UnmetReason
{
    [WireName("")]
    None,

    /// <summary>The rate model wants more benches than exist (or none of the kind can take the recipe). A further bench would help.</summary>
    [WireName("no_bench")]
    NoBench,

    /// <summary>Every capable bench carries the 15-bill cap.</summary>
    [WireName("bench_slots_full")]
    SlotsFull,

    /// <summary>Measured production is short because the recipe's ingredients are out; a further bench would not help.</summary>
    [WireName("ingredient_bound")]
    Ingredients,

    /// <summary>Active bills and stocked ingredients had no observed exact bill job; this does not attribute the missing work to hauling or promise continuous observation.</summary>
    [WireName("work_unstarted")]
    WorkUnstarted,

    /// <summary>Exact bill jobs showed pending ingredients and no actual recipe work in the sampled window. This is not proof of a hauling stall.</summary>
    [WireName("ingredients_pending")]
    IngredientsPending,

    /// <summary>Actual recipe work was not observed; the cause of the measured production gap is unassessed.</summary>
    [WireName("recipe_work_unassessed")]
    RecipeWorkUnassessed,

    /// <summary>
    /// Exact bill jobs were observed, ingredients are stocked, and ingredient gathering dominated the sampled bill work
    /// (at least <see cref="WorkRules.DeliveryBoundGatheringShare"/> of the busy samples); a further bench would also gather.
    /// It claims nothing about which pawn hauls or about storage layout, and is never inferred from stock or an idle bench.
    /// </summary>
    [WireName("delivery_bound")]
    DeliveryBound,

    /// <summary>Production stays short with a bill on every usable bench; only a new bench can raise it.</summary>
    [WireName("benches_exhausted")]
    BenchesExhausted,
}

/// <summary>The throughput one bench kind lacks, in product units per day, and why. The facilities ladder reads it as its demand signal.</summary>
public sealed record UnmetThroughput
{
    public string BenchKind { get; init; } = "";

    public double ShortPerDay { get; init; }

    public UnmetReason Reason { get; init; }
}

/// <summary>How the rate model sized one order.</summary>
public abstract record DispatchSizing
{
    private DispatchSizing()
    {
    }

    /// <summary>
    /// The model does not size the order, which wants one bench: it is no stock target, no usable bench offers its
    /// recipe, or the stock census, the recipe's work or a worker's work hours is unread.
    /// </summary>
    public sealed record Unsized : DispatchSizing;

    /// <summary>
    /// The units per day that clear the product's deficit and the mean units per day of one eligible bench
    /// with an average worker.
    /// </summary>
    public sealed record Sized(double Demand, double Capacity) : DispatchSizing;
}

/// <summary>The rate model's answer for one order.</summary>
public sealed record OrderDispatch
{
    /// <summary>How many benches should carry the order: the rate model's benches wanted plus calibration widening, within one and the eligible benches (one when the model does not size it or no capacity meets it).</summary>
    public required int Copies { get; init; }

    public required int Eligible { get; init; }

    public required DispatchSizing Sizing { get; init; }

    /// <summary>The units per day the eligible benches cannot make when the model wants more benches than exist.</summary>
    public required double NoBenchShort { get; init; }
}

/// <summary>One order's rolling calibration window. It is memory only: a world change empties it.</summary>
public sealed record ThroughputWindow
{
    public bool Open { get; init; }

    public long Start { get; init; }

    public long StartProduced { get; init; }

    public string LoadToken { get; init; } = "";

    public ThroughputBasis? Basis { get; init; }

    /// <summary>The units short of target at Start.</summary>
    public long Deficit { get; init; }

    /// <summary>The least supported conditional rate sampled during the window; unread capacity is not zero.</summary>
    public ForecastTerm PerDay { get; init; } = new ForecastTerm.Unassessed("labor_capacity_unread");

    public int Carrying { get; init; }

    /// <summary>Set once the stock reached target in the window: a stopped bill is not a shortfall.</summary>
    public bool Met { get; init; }

    /// <summary>Number of samples, including the opening reading.</summary>
    public int Samples { get; init; }

    /// <summary>Samples with a current job for one of the order's exact bills; this includes ingredient gathering, not just recipe work.</summary>
    public int Busy { get; init; }

    /// <summary>Busy samples whose strongest exact-bill job evidence was pending ingredient targets and no recipe work.</summary>
    public int GatheringSamples { get; init; }

    /// <summary>Busy samples whose strongest exact-bill job evidence was recipe work already performed; no completed output is implied.</summary>
    public int RecipeWorkSamples { get; init; }

    /// <summary>Set when a carrying bill was not known active.</summary>
    public bool Inactive { get; init; }
}

/// <summary>One Round's reading of an order.</summary>
public sealed record ThroughputObservation
{
    public long Tick { get; init; }

    public long Stock { get; init; }

    /// <summary>Cumulative product units from this order's exact bills; absent means production is unread.</summary>
    public ProductionOutput Output { get; init; } = new ProductionOutput.CensusUnread();

    public ThroughputBasis? Basis { get; init; }

    public long Target { get; init; }

    /// <summary>The benches with a standing bill of the order.</summary>
    public int Carrying { get; init; }

    /// <summary>The benches that could carry one.</summary>
    public int Eligible { get; init; }

    /// <summary>Units per day of one bench.</summary>
    public ForecastTerm Capacity { get; init; } = new ForecastTerm.Unassessed("labor_capacity_unread");

    /// <summary>Every carrying bill is known active.</summary>
    public bool Active { get; init; }

    /// <summary>A pawn's current job carries one of the order's exact bill identities. Neither a completion nor a particular recipe toil is implied.</summary>
    public bool Busy { get; init; }

    public BillWorkEvidence BillWork { get; init; }

    /// <summary>The recipe has stocked alternatives, lacks an ingredient, or has no eligible bench.</summary>
    public ThroughputIngredients Ingredients { get; init; }
}

/// <summary>Only stable production inputs; current stock, job changes and elapsed time do not reset a rate.</summary>
public sealed record ThroughputBasis(OrderSpec Order, EquatableArray<ThroughputBench> Benches, EquatableArray<ThroughputWorker> Workers);
public sealed record ThroughputBench(string Id, bool Usable, double Speed, GearRecipe? Recipe, EquatableArray<ThroughputBill> Bills);
public sealed record ThroughputBill(string Id, bool Active, string Worker);
public sealed record ThroughputWorker(PawnId Id, bool Available, bool Applies, EquatableArray<WorkPriority> Priorities,
    EquatableArray<RimGovernor.Brain.GameFacts.Observations.WorkSpeedRead> Speeds,
    EquatableArray<RimGovernor.Brain.GameFacts.Observations.Skill> Skills);

public abstract record ProductionOutput
{
    private protected ProductionOutput() { }
    public sealed record Observed(string LoadToken, long Units) : ProductionOutput;
    public sealed record CensusUnread : ProductionOutput;
}

/// <summary>The observed recipe stock, or the absence of a bench offering that recipe.</summary>
public enum ThroughputIngredients { NoRecipe, Missing, Stocked }

/// <summary>A closed window's verdict.</summary>
public sealed record Calibration
{
    /// <summary>A full window ended this Round.</summary>
    public bool Closed { get; init; }

    /// <summary>Its actual recipe output was under ShortfallFraction of the prediction.</summary>
    public bool IsShort { get; init; }

    /// <summary>One more bench should carry the order. Otherwise a Short window has Reason.</summary>
    public bool Widen { get; init; }

    public UnmetReason Reason { get; init; }

    /// <summary>The predicted minus realized recipe output per day.</summary>
    public double ShortPerDay { get; init; }
}

public static partial class WorkRules
{
    /// <summary>Compare only production inputs, without volatile stock/job state or native snapshot tokens.</summary>
    public static bool SameThroughputBasis(ThroughputBasis? a, ThroughputBasis? b) => a is not null && b is not null
        && a.Order == b.Order && a.Benches.Count == b.Benches.Count && a.Workers.Count == b.Workers.Count
        && a.Benches.Zip(b.Benches).All(p => p.First.Id == p.Second.Id && p.First.Usable == p.Second.Usable
            && p.First.Speed == p.Second.Speed && p.First.Recipe == p.Second.Recipe
            && p.First.Bills.Count == p.Second.Bills.Count && p.First.Bills.Zip(p.Second.Bills).All(q =>
                q.First.Id == q.Second.Id && q.First.Active == q.Second.Active && q.First.Worker == q.Second.Worker))
        && a.Workers.Zip(b.Workers).All(p => p.First.Id == p.Second.Id && p.First.Available == p.Second.Available
            && p.First.Applies == p.Second.Applies && p.First.Priorities == p.Second.Priorities
            && p.First.Speeds == p.Second.Speeds && p.First.Skills == p.Second.Skills);

    /// <summary>The calibration window: one game day.</summary>
    public const long ThroughputWindowTicks = GameTime.TicksPerDay;

    /// <summary>The share of predicted output below which a full window counts as far below the prediction.</summary>
    public const double ShortfallFraction = 0.5;

    /// <summary>The ingredient stock at or below which a recipe is ingredient-bound.</summary>
    public const long IngredientBoundStock = 1;

    /// <summary>The share of busy samples that must show ingredient gathering without recipe work for a short window to be delivery-bound.</summary>
    public const double DeliveryBoundGatheringShare = 0.75;

    /// <summary>Whether the order is a stock target the rate model can size: the declarer named the product and the target is positive.</summary>
    public static bool Stocked(this OrderSpec s) => s.Product.Value.Length != 0 && WireClass(s.Mode) == BillMode.StockTarget && s.Target > 0;

    /// <summary>The usable benches of the order's kind that offer its recipe, ordered by id.</summary>
    public static IReadOnlyList<GearBench> EligibleBenches(OrderSpec order, IReadOnlyList<GearBench> benches) =>
        [.. benches.Where(b => b.Def == order.BenchKind && BenchOffers(b, order.Recipe)).OrderBy(b => b.Id, StringComparer.Ordinal)];

    /// <summary>
    /// Sizes one order. An order the model does not size (DispatchSizing.Unsized), and one no capacity meets
    /// (BenchesWanted.NoCapacity), wants one bench. widen is the calibration's extra benches. The workers are the pawns
    /// that can work the recipe (TryDispatchWorkers).
    /// </summary>
    public static OrderDispatch DispatchOrder(OrderSpec order, IReadOnlyDictionary<Resource, long> have, IReadOnlyList<GearBench> benches, IReadOnlyList<WorkPawn> pawns,
        RimGovernor.Brain.GameFacts.Observations.NeedsForecastRead? needs, int widen)
    {
        var eligible = EligibleBenches(order, benches);
        var d = new OrderDispatch { Copies = 1, Eligible = eligible.Count, Sizing = new DispatchSizing.Unsized(), NoBenchShort = 0 };
        if (!order.Stocked() || eligible.Count == 0)
        {
            return d;
        }
        if (RecipeOf(eligible[0], order.Recipe) is not { } recipe
            || !TryDispatchWorkers(pawns, recipe.RequiredWork, recipe.WorkSpeedStat, needs, out var workers) || !TryMeanCapacity(order, eligible, AverageWorker(workers), out var capacity))
        {
            return d;
        }
        var held = have.GetValueOrDefault(order.Product);
        var demand = BenchRateRules.DemandRate((long)order.Target - held, order.Class);
        d = d with { Sizing = new DispatchSizing.Sized(demand, capacity) };
        if (BenchRateRules.Wanted(demand, capacity) is not BenchesWanted.Count { Benches: var n })
        {
            return d;
        }
        if (n > eligible.Count)
        {
            d = d with { NoBenchShort = demand - (eligible.Count * capacity) };
        }
        if (workers.Count > 0 && n > workers.Count)
        {
            n = workers.Count;
        }
        return d with { Copies = Math.Min(Math.Max(n + widen, 1), eligible.Count) };
    }

    // The bench's row of the recipe; null when the bench lists none.
    private static GearRecipe? RecipeOf(GearBench b, string recipe)
    {
        foreach (var r in b.Recipes)
        {
            if (r.Definition == recipe)
            {
                return r;
            }
        }
        return null;
    }

    // The mean units per day one eligible bench makes with the worker; false while a bench lists no row of the recipe.
    private static bool TryMeanCapacity(OrderSpec order, IReadOnlyList<GearBench> eligible, BenchWorker worker, out double capacity)
    {
        double sum = 0;
        capacity = 0;
        foreach (var b in eligible)
        {
            if (RecipeOf(b, order.Recipe) is not { WorkAmount: var work })
            {
                return false;
            }
            sum += BenchRateRules.BenchCapacity(new BenchRecipe { WorkPerUnit = work, BenchWorkSpeed = b.WorkSpeed }, [worker]);
        }
        capacity = sum / eligible.Count;
        return true;
    }

    /// <summary>The mean of the workers; no workers is a worker of zero hours.</summary>
    public static BenchWorker AverageWorker(IReadOnlyList<BenchWorker> workers)
    {
        if (workers.Count == 0)
        {
            return new BenchWorker { WorkHoursPerDay = 0, WorkSpeed = 0 };
        }
        double hours = 0;
        double speed = 0;
        foreach (var w in workers)
        {
            hours += w.WorkHoursPerDay;
            speed += w.WorkSpeed;
        }
        double n = workers.Count;
        return new BenchWorker { WorkHoursPerDay = hours / n, WorkSpeed = speed / n };
    }

    /// <summary>The pawn stat building and removing work goes at (JobDriver_ConstructFinishFrame, JobDriver_RemoveBuilding).</summary>
    internal const string ConstructionSpeedStat = "ConstructionSpeed";

    /// <summary>Those drivers' work a tick is the ConstructionSpeed stat × 1.7 (kept-constants, mirrored game literals).</summary>
    private const double ConstructionWorkFactor = 1.7;

    /// <summary>
    /// The construction work a builder at <paramref name="constructionSpeed"/> does a tick: its ConstructionSpeed × 1.7
    /// (JobDriver_ConstructFinishFrame, JobDriver_RemoveBuilding). The stuff's ConstructionSpeedFactor is not applied: the
    /// one caller prices removals, and construction timing is the native schedule's.
    /// </summary>
    internal static double ConstructionWorkPerTick(double constructionSpeed) => constructionSpeed * ConstructionWorkFactor;

    /// <summary>
    /// The pawns that can work a bench: available and (when the recipe names work types) with one of them enabled.
    /// Speed is the pawn's <paramref name="stat"/> as read (<see cref="WorkPawn.WorkSpeeds"/>, #3868), 1 for a work that names
    /// none (Toils_Recipe.DoRecipeWork). False while such a pawn's work hours or that stat are unread.
    /// </summary>
    public static bool TryDispatchWorkers(IReadOnlyList<WorkPawn> pawns, IReadOnlyList<WorkRequirement> work, string stat,
        RimGovernor.Brain.GameFacts.Observations.NeedsForecastRead? needs, out IReadOnlyList<BenchWorker> workers)
    {
        var output = new List<BenchWorker>();
        workers = output;
        foreach (var p in pawns)
        {
            if (!p.Available)
            {
                continue;
            }
            if (work.Count > 0 && !PawnWorks(p, work))
            {
                continue;
            }
            if (NativeLaborCapacity.HoursPerDay(p.Observed, needs, [.. work.Select(w => w.Work)]) is not ForecastTerm.Assessed(var hours))
            {
                workers = [];
                return false;
            }
            var speed = stat.Length == 0 ? 1 : p.WorkSpeeds.FirstOrDefault(s => s.Stat == stat)?.Value;
            if (speed is not { } read)
            {
                workers = [];
                return false;
            }
            output.Add(new BenchWorker { WorkHoursPerDay = hours, WorkSpeed = Math.Max(read, 0) });
        }
        return true;
    }

    private static bool PawnWorks(WorkPawn p, IReadOnlyList<WorkRequirement> work)
    {
        foreach (var row in p.Work.Priorities())
        {
            if (row.Priority <= 0 || row.Disabled)
            {
                continue;
            }
            foreach (var req in work)
            {
                if (row.Work == req.Work && (req.Minimum <= 0 || p.Skills.Any(s => s.DefName == req.Skill && !s.Disabled && s.Level >= req.Minimum)))
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Advances the order's window with this Round's reading. The window restarts whenever the carrying count
    /// changes, and only a full window gives a verdict. A short window widens by one bench unless the shortfall is
    /// ingredient-bound (a known ingredient stock at or below IngredientBoundStock) or unstarted (every carrying
    /// bill active, ingredients present, yet no exact bill job observed all window), delivery-bound (stocked
    /// ingredients and gathering in at least DeliveryBoundGatheringShare of the busy samples), or lacking any
    /// sampled recipe work; a short window with a bill on every eligible bench is UnmetBenchesExhausted.
    /// </summary>
    public static (ThroughputWindow Window, Calibration Calibration) CalibrateThroughput(ThroughputWindow w, ThroughputObservation o)
    {
        var stock = o.Stock;
        var capacity = o.Capacity;
        if (o.Carrying == 0 || o.Output is not ProductionOutput.Observed(var loadToken, var produced) || loadToken.Length == 0 || o.Basis is null)
        {
            return (new ThroughputWindow(), new Calibration());
        }
        ThroughputWindow Fresh() =>
            new()
            {
                Open = true,
                Start = o.Tick,
                StartProduced = produced,
                LoadToken = loadToken,
                Basis = o.Basis,
                Deficit = Math.Max(o.Target - stock, 0),
                PerDay = capacity is ForecastTerm.Assessed(var rate) ? new ForecastTerm.Assessed(rate * o.Carrying) : capacity,
                Carrying = o.Carrying,
                Met = stock >= o.Target,
                Samples = 1,
                Busy = o.Busy ? 1 : 0,
                GatheringSamples = o.Busy && o.BillWork == BillWorkEvidence.IngredientsPending ? 1 : 0,
                RecipeWorkSamples = o.Busy && o.BillWork == BillWorkEvidence.RecipeWorkObserved ? 1 : 0,
                Inactive = !o.Active,
            };
        if (!w.Open || w.Carrying != o.Carrying || o.Tick < w.Start || w.LoadToken != loadToken
            || !SameThroughputBasis(w.Basis, o.Basis) || produced < w.StartProduced)
        {
            return (Fresh(), new Calibration());
        }
        w = w with
        {
            Met = w.Met || stock >= o.Target, Samples = w.Samples + 1,
            PerDay = w.PerDay is ForecastTerm.Unassessed ? w.PerDay
                : capacity is ForecastTerm.Unassessed ? capacity
                : new ForecastTerm.Assessed(Math.Min(((ForecastTerm.Assessed)w.PerDay).Value,
                    ((ForecastTerm.Assessed)capacity).Value * o.Carrying)),
        };
        if (o.Busy)
        {
            w = w with { Busy = w.Busy + 1,
                GatheringSamples = w.GatheringSamples + (o.BillWork == BillWorkEvidence.IngredientsPending ? 1 : 0),
                RecipeWorkSamples = w.RecipeWorkSamples + (o.BillWork == BillWorkEvidence.RecipeWorkObserved ? 1 : 0) };
        }
        if (!o.Active)
        {
            w = w with { Inactive = true };
        }
        if (o.Tick - w.Start < ThroughputWindowTicks)
        {
            return (w, new Calibration());
        }
        var days = (double)(o.Tick - w.Start) / GameTime.TicksPerDay;
        var cal = new Calibration { Closed = true };
        if (w.PerDay is not ForecastTerm.Assessed(var supported)) return (Fresh(), cal);
        var expected = Math.Min(supported * days, (double)w.Deficit);
        var gain = (double)(produced - w.StartProduced);
        if (!w.Met && expected > 0 && gain < ShortfallFraction * expected)
        {
            cal = cal with { IsShort = true, ShortPerDay = (expected - gain) / days };
            if (o.Ingredients == ThroughputIngredients.Missing)
            {
                cal = cal with { Reason = UnmetReason.Ingredients };
            }
            else if (o.Ingredients == ThroughputIngredients.Stocked && !w.Inactive && w.Samples > 0 && w.Busy == 0)
            {
                cal = cal with { Reason = UnmetReason.WorkUnstarted };
            }
            else if (o.Ingredients == ThroughputIngredients.Stocked && w.Busy > 0
                && w.GatheringSamples >= DeliveryBoundGatheringShare * w.Busy)
            {
                cal = cal with { Reason = UnmetReason.DeliveryBound };
            }
            else if (w.RecipeWorkSamples == 0)
            {
                cal = cal with { Reason = w.GatheringSamples > 0
                    ? UnmetReason.IngredientsPending : UnmetReason.RecipeWorkUnassessed };
            }
            else if (o.Carrying < o.Eligible)
            {
                cal = cal with { Widen = true };
            }
            else
            {
                cal = cal with { Reason = UnmetReason.BenchesExhausted };
            }
        }
        return (Fresh(), cal);
    }

    /// <summary>
    /// The throughput lost to orders no bench could take: with a capable bench (all at the slot cap) the reason is
    /// SlotsFull, otherwise NoBench. The shortfall is one bench's capacity, or the whole demand when no bench is
    /// capable; zero when the model cannot size it.
    /// </summary>
    public static IReadOnlyList<UnmetThroughput> UnplacedUnmet(IReadOnlyList<OrderSpec> unplaced, IReadOnlyList<GearBench> benches, IReadOnlyDictionary<string, OrderDispatch> dispatch)
    {
        var output = new List<UnmetThroughput>();
        foreach (var order in unplaced)
        {
            var sized = dispatch.TryGetValue(order.Key(), out var d) ? d.Sizing as DispatchSizing.Sized : null;
            var u = new UnmetThroughput { BenchKind = order.BenchKind, Reason = UnmetReason.SlotsFull };
            if (EligibleBenches(order, benches).Count == 0)
            {
                u = u with { Reason = UnmetReason.NoBench, ShortPerDay = sized?.Demand ?? 0 };
            }
            else
            {
                u = u with { ShortPerDay = sized?.Capacity ?? 0 };
            }
            output.Add(u);
        }
        return output;
    }

    /// <summary>
    /// One row per bench kind, sorted by kind: the shortfalls summed and the reason of the largest contributor (ties
    /// by reason name). A row short by zero (the model could not size it) still carries its reason.
    /// </summary>
    public static IReadOnlyList<UnmetThroughput> MergeUnmet(IReadOnlyList<UnmetThroughput> items)
    {
        var byKind = new Dictionary<string, (double Sum, double Top, UnmetReason Reason)>();
        foreach (var u in items)
        {
            (double Sum, double Top, UnmetReason Reason) a = byKind.TryGetValue(u.BenchKind, out var held) ? held : (0.0, -1.0, UnmetReason.None);
            a.Sum += u.ShortPerDay;
            if (u.ShortPerDay > a.Top || (u.ShortPerDay == a.Top && string.CompareOrdinal(Wire.Name(u.Reason), Wire.Name(a.Reason)) < 0))
            {
                a.Top = u.ShortPerDay;
                a.Reason = u.Reason;
            }
            byKind[u.BenchKind] = a;
        }
        return [.. byKind.Keys.Order(StringComparer.Ordinal).Select(k => new UnmetThroughput { BenchKind = k, ShortPerDay = byKind[k].Sum, Reason = byKind[k].Reason })];
    }
}
