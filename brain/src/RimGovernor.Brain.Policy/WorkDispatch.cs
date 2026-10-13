namespace RimGovernor.Brain.Policy;

// Go: work_dispatch.go. The bench dispatcher is the decision half of the work ledger: how many benches should carry
// each stock-target order (the rate model, BenchesWanted), whether production keeps up with that prediction (a
// rolling one-day window of measured stock gain) and, when it does not, whether one more bench would help or what
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

    /// <summary>The bill is active and its ingredients exist but the bench sat idle all window; hauling, not bench count, limits it.</summary>
    [WireName("haul_bound")]
    Haul,

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

/// <summary>The rate model's answer for one order.</summary>
public sealed record OrderDispatch
{
    /// <summary>How many benches should carry the order: the rate model's benches wanted plus calibration widening, within one and the eligible benches (one when the model is unknown).</summary>
    public int Copies { get; init; }

    public int Eligible { get; init; }

    /// <summary>Units per day to clear the deficit.</summary>
    public Fact<double> Demand { get; init; }

    /// <summary>Units per day of one bench with an average worker.</summary>
    public Fact<double> Capacity { get; init; }

    public Fact<int> Wanted { get; init; }

    /// <summary>The units per day the eligible benches cannot make when the model wants more benches than exist.</summary>
    public double NoBenchShort { get; init; }
}

/// <summary>One order's rolling calibration window. It is memory only: a world change empties it.</summary>
public sealed record ThroughputWindow
{
    public bool Open { get; init; }

    public long Start { get; init; }

    public long StartStock { get; init; }

    /// <summary>The units short of target at Start.</summary>
    public long Deficit { get; init; }

    /// <summary>The predicted units per day of the Carrying benches.</summary>
    public double PerDay { get; init; }

    public int Carrying { get; init; }

    /// <summary>Set once the stock reached target in the window: a stopped bill is not a shortfall.</summary>
    public bool Met { get; init; }

    /// <summary>Rounds sampled.</summary>
    public int Samples { get; init; }

    /// <summary>Rounds a pawn worked a carrying bench.</summary>
    public int Busy { get; init; }

    /// <summary>Set when a carrying bill was not known active.</summary>
    public bool Inactive { get; init; }
}

/// <summary>One Round's reading of an order.</summary>
public sealed record ThroughputObservation
{
    public long Tick { get; init; }

    public Fact<long> Stock { get; init; }

    public long Target { get; init; }

    /// <summary>The benches with a standing bill of the order.</summary>
    public int Carrying { get; init; }

    /// <summary>The benches that could carry one.</summary>
    public int Eligible { get; init; }

    /// <summary>Units per day of one bench.</summary>
    public Fact<double> Capacity { get; init; }

    /// <summary>Every carrying bill is known active.</summary>
    public bool Active { get; init; }

    /// <summary>A pawn works a carrying bench this Round.</summary>
    public bool Busy { get; init; }

    /// <summary>The recipe's ingredient stock is above IngredientBoundStock (known true), at or below it (known false) or unread.</summary>
    public Fact<bool> Ingredients { get; init; }
}

/// <summary>A closed window's verdict.</summary>
public sealed record Calibration
{
    /// <summary>A full window ended this Round.</summary>
    public bool Closed { get; init; }

    /// <summary>Its stock gain was under ShortfallFraction of the prediction.</summary>
    public bool IsShort { get; init; }

    /// <summary>One more bench should carry the order. Otherwise a Short window has Reason.</summary>
    public bool Widen { get; init; }

    public UnmetReason Reason { get; init; }

    /// <summary>The predicted minus measured gain per day.</summary>
    public double ShortPerDay { get; init; }
}

public static partial class PolicyFunctions
{
    /// <summary>The calibration window: one game day.</summary>
    public const long ThroughputWindowTicks = GameTime.TicksPerDay;

    /// <summary>The share of predicted stock gain below which a full window counts as far below the prediction.</summary>
    public const double ShortfallFraction = 0.5;

    /// <summary>The ingredient stock at or below which a recipe is ingredient-bound.</summary>
    public const long IngredientBoundStock = 1;

    /// <summary>Whether the order is a stock target the rate model can size: the declarer named the product and the target is positive.</summary>
    public static bool Stocked(this OrderSpec s) => s.Product.Value.Length != 0 && WireClass(s.Mode) == BillMode.StockTarget && s.Target > 0;

    /// <summary>The usable benches of the order's kind that offer its recipe, ordered by id.</summary>
    public static IReadOnlyList<GearBench> EligibleBenches(OrderSpec order, IReadOnlyList<GearBench> benches) =>
        [.. benches.Where(b => b.Def == order.BenchKind && BenchOffers(b, order.Recipe)).OrderBy(b => b.Id, StringComparer.Ordinal)];

    /// <summary>
    /// Sizes one order. Orders the model cannot size (not a stock target, stock, workers or recipe work unknown) want
    /// one bench. widen is the calibration's extra benches. The workers are the pawns that can work the recipe
    /// (DispatchWorkers).
    /// </summary>
    public static OrderDispatch DispatchOrder(OrderSpec order, Fact<IReadOnlyDictionary<Resource, long>> stock, IReadOnlyList<GearBench> benches, IReadOnlyList<WorkPawn> pawns, int widen)
    {
        var eligible = EligibleBenches(order, benches);
        var d = new OrderDispatch { Copies = 1, Eligible = eligible.Count };
        if (!order.Stocked() || eligible.Count == 0)
        {
            return d;
        }
        if (!stock.TryGetValue(out var have))
        {
            return d;
        }
        d = d with { Demand = DemandRate(Known((double)((long)order.Target - have.GetValueOrDefault(order.Product))), order.Class) };
        RecipeOf(eligible[0], order.Recipe).RequiredWork.TryGetValue(out var required);
        var workers = DispatchWorkers(pawns, required);
        d = d with { Capacity = MeanCapacity(order, eligible, AverageWorker(workers)) };
        d = d with { Wanted = BenchesWanted(d.Demand, d.Capacity) };
        if (!d.Wanted.TryGetValue(out var n))
        {
            return d;
        }
        d.Capacity.TryGetValue(out var capacity);
        if (n > eligible.Count)
        {
            d.Demand.TryGetValue(out var demand);
            d = d with { NoBenchShort = demand - (eligible.Count * capacity) };
        }
        if (workers.Count > 0 && n > workers.Count)
        {
            n = workers.Count;
        }
        return d with { Copies = Math.Min(Math.Max(n + widen, 1), eligible.Count) };
    }

    // The bench's row of the recipe; the default row (every fact unknown) when it has none.
    private static GearRecipe RecipeOf(GearBench b, string recipe)
    {
        if (b.Recipes.TryGetValue(out var recipes))
        {
            foreach (var r in recipes)
            {
                if (r.Definition == recipe)
                {
                    return r;
                }
            }
        }
        return new GearRecipe();
    }

    // The mean units per day one eligible bench makes with the worker; unknown when any bench's is.
    private static Fact<double> MeanCapacity(OrderSpec order, IReadOnlyList<GearBench> eligible, BenchWorker worker)
    {
        double sum = 0;
        foreach (var b in eligible)
        {
            var recipe = new BenchRecipe { WorkPerUnit = RecipeOf(b, order.Recipe).WorkAmount, BenchWorkSpeed = b.WorkSpeed };
            if (!BenchCapacity(recipe, [worker]).TryGetValue(out var c))
            {
                return Unknown<double>();
            }
            sum += c;
        }
        return Known(sum / eligible.Count);
    }

    /// <summary>The mean of the workers. Any unknown input makes the mean unknown; no workers is a worker of zero hours.</summary>
    public static BenchWorker AverageWorker(IReadOnlyList<BenchWorker> workers)
    {
        if (workers.Count == 0)
        {
            return new BenchWorker { WorkHoursPerDay = Known(0.0), WorkSpeed = Known(0.0) };
        }
        double hours = 0;
        double speed = 0;
        foreach (var w in workers)
        {
            if (!w.WorkHoursPerDay.TryGetValue(out var h) || !w.WorkSpeed.TryGetValue(out var s))
            {
                return new BenchWorker { WorkHoursPerDay = Unknown<double>(), WorkSpeed = Unknown<double>() };
            }
            hours += h;
            speed += s;
        }
        double n = workers.Count;
        return new BenchWorker { WorkHoursPerDay = Known(hours / n), WorkSpeed = Known(speed / n) };
    }

    /// <summary>
    /// The pawns that can work a bench: available, with work hours and speed known, and (when the recipe names work
    /// types) with one of them enabled. Speed is the global work speed, one plus the pawn's summed WorkSpeedGlobal offset.
    /// </summary>
    public static IReadOnlyList<BenchWorker> DispatchWorkers(IReadOnlyList<WorkPawn> pawns, IReadOnlyList<WorkRequirement> work)
    {
        var output = new List<BenchWorker>();
        foreach (var p in pawns)
        {
            if (!p.Available.TryGetValue(out var avail) || !avail)
            {
                continue;
            }
            if (work.Count > 0 && !PawnWorks(p, work))
            {
                continue;
            }
            var profile = BuildProfile(p);
            var w = new BenchWorker { WorkHoursPerDay = Unknown<double>(), WorkSpeed = Known(Math.Max(1 + profile.Effects.WorkSpeed, 0)) };
            if (profile.WorkHours.TryGetValue(out var h))
            {
                w = w with { WorkHoursPerDay = Known((double)h) };
            }
            output.Add(w);
        }
        return output;
    }

    private static bool PawnWorks(WorkPawn p, IReadOnlyList<WorkRequirement> work)
    {
        if (!p.Work.TryGetValue(out var rows))
        {
            return false;
        }
        foreach (var row in rows)
        {
            if (row.Priority <= 0 || row.Disabled)
            {
                continue;
            }
            foreach (var req in work)
            {
                if (row.Work == req.Work)
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>Whether any pawn's current job works one of the benches.</summary>
    public static bool BenchesBusy(IReadOnlyList<WorkPawn> pawns, IReadOnlyList<string> benches)
    {
        foreach (var p in pawns)
        {
            if (!p.Job.TryGetValue(out var job) || !job.Target.TryGetValue(out var target))
            {
                continue;
            }
            foreach (var b in benches)
            {
                if (target.Thing == b)
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
    /// ingredient-bound (a known ingredient stock at or below IngredientBoundStock) or haul-bound (every carrying
    /// bill active, ingredients present, yet no pawn worked a carrying bench all window); a short window with a bill
    /// on every eligible bench is UnmetBenchesExhausted.
    /// </summary>
    public static (ThroughputWindow Window, Calibration Calibration) CalibrateThroughput(ThroughputWindow w, ThroughputObservation o)
    {
        var stockKnown = o.Stock.TryGetValue(out var stock);
        var capKnown = o.Capacity.TryGetValue(out var capacity);
        if (!stockKnown || !capKnown || o.Carrying == 0)
        {
            return (new ThroughputWindow(), new Calibration());
        }
        ThroughputWindow Fresh() =>
            new()
            {
                Open = true,
                Start = o.Tick,
                StartStock = stock,
                Deficit = Math.Max(o.Target - stock, 0),
                PerDay = capacity * o.Carrying,
                Carrying = o.Carrying,
                Met = stock >= o.Target,
                Samples = 0,
            };
        if (!w.Open || w.Carrying != o.Carrying || o.Tick < w.Start)
        {
            return (Fresh(), new Calibration());
        }
        w = w with { Met = w.Met || stock >= o.Target, Samples = w.Samples + 1 };
        if (o.Busy)
        {
            w = w with { Busy = w.Busy + 1 };
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
        var expected = Math.Min(w.PerDay * days, (double)w.Deficit);
        var cal = new Calibration { Closed = true };
        var gain = (double)(stock - w.StartStock);
        if (!w.Met && expected > 0 && gain < ShortfallFraction * expected)
        {
            cal = cal with { IsShort = true, ShortPerDay = (expected - gain) / days };
            var ingredientsKnown = o.Ingredients.TryGetValue(out var ingredients);
            if (ingredientsKnown && !ingredients)
            {
                cal = cal with { Reason = UnmetReason.Ingredients };
            }
            else if (ingredientsKnown && !w.Inactive && w.Samples > 0 && w.Busy == 0)
            {
                cal = cal with { Reason = UnmetReason.Haul };
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
            var d = dispatch.GetValueOrDefault(order.Key()) ?? new OrderDispatch();
            var u = new UnmetThroughput { BenchKind = order.BenchKind, Reason = UnmetReason.SlotsFull };
            double shortPerDay;
            if (EligibleBenches(order, benches).Count == 0)
            {
                u = u with { Reason = UnmetReason.NoBench };
                d.Demand.TryGetValue(out shortPerDay);
            }
            else
            {
                d.Capacity.TryGetValue(out shortPerDay);
            }
            output.Add(u with { ShortPerDay = shortPerDay });
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
