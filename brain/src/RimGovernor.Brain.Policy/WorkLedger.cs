using System.Globalization;

namespace RimGovernor.Brain.Policy;


/// <summary>
/// A production order's identity. It carries no native tag: a bill on a bench is the same order as a declared one
/// when their specs have the same Key, so a restart rebuilds the ledger from native readback.
/// </summary>
public sealed record OrderSpec
{
    /// <summary>Derived choice evidence from the declaring need owner; never native bill identity.</summary>
    public ProductionRequirements? Requirements { get; init; }
    public string Recipe { get; init; } = "";

    /// <summary>The ingredient filter; order is not identity.</summary>
    public EquatableArray<string> Ingredients { get; init; }

    /// <summary>Pins one pawn, empty for any.</summary>
    public string Worker { get; init; } = "";

    public BillMode Mode { get; init; }

    public int Target { get; init; }

    /// <summary>The workbench definition, not a particular bench.</summary>
    public string BenchKind { get; init; } = "";

    /// <summary>The resource the order stocks (the declarer's stock-target context for the dispatcher). Not identity (not in Key); a bill read back from a bench has none.</summary>
    public Resource Product { get; init; }

    /// <summary>How fast a shortfall must refill. Not identity.</summary>
    public ResourceClass Class { get; init; }

    /// <summary>
    /// The filing key of the owner that declared the order (#3231 decision 21): a Need's <c>NeedId.FilingKey</c> or an
    /// unmigrated Concern's id; empty when undeclared. Not identity either: two owners declaring one spec coalesce
    /// into one order (OrderOwners).
    /// </summary>
    public string Owner { get; init; } = "";

    /// <summary>
    /// The canonical spec identity. The mode is its wire class: native tells bills apart by repeat mode and count
    /// only, so a food target and a stock target are one order, and a bill read back as a target cannot be told from
    /// a beer reserve nor a forever bill from a human butcher bill.
    /// </summary>
    public string Key()
    {
        var ing = Ingredients.Items.Order(StringComparer.Ordinal);
        return string.Join("|", BenchKind, Recipe, WorkRules.BillModeName(WorkRules.WireClass(Mode)), Target.ToString(CultureInfo.InvariantCulture), Worker, string.Join(",", ing));
    }
}

/// <summary>
/// Names the input an owner (by filing key, as <see cref="OrderSpec.Owner"/>) lacked this Round. Its silence about a
/// bill proves nothing, so the ledger removes nothing while any owner abstains.
/// </summary>
public sealed record Abstain(string Owner, Cause Fact);

/// <summary>What one planner wants this Round. Abstains name the concerns that lacked a fact, so the planner's silence about a bill proves nothing.</summary>
public sealed class Declared
{
    public List<OrderSpec> Orders { get; init; } = [];

    public List<Abstain> Abstains { get; init; } = [];

    /// <summary>Records that the declaration lacked fact, once per fact. Returns this declaration.</summary>
    public Declared Unread(Cause fact)
    {
        if (!Abstains.Any(a => a.Fact == fact))
        {
            Abstains.Add(new Abstain("", fact));
        }
        return this;
    }

    /// <summary>Whether any concern abstained.</summary>
    public bool Abstained() => Abstains.Count > 0;

    /// <summary>
    /// The declaration made on behalf of the owner filed under <paramref name="owner"/> (a Need's filing key or a
    /// Concern id): every order and abstain without an owner is owned by it. A declarer serving several owners calls
    /// For per owner's part.
    /// </summary>
    public Declared For(string owner) =>
        new()
        {
            Orders = [.. Orders.Select(o => o.Owner.Length == 0 ? o with { Owner = owner } : o)],
            Abstains = [.. Abstains.Select(a => a.Owner.Length == 0 ? a with { Owner = owner } : a)],
        };

    /// <summary>Appends other's orders and abstains.</summary>
    public void Merge(Declared other)
    {
        Orders.AddRange(other.Orders);
        Abstains.AddRange(other.Abstains);
    }
}

/// <summary>
/// Classifies a bill on a bench by what its recipe makes (the bridge census sets it from the catalog). Everything
/// but Production is declare-only in the first pass: a bill of it satisfies a declared order the same as any other,
/// but is never removed, whoever placed it.
/// </summary>
public enum LedgerBillKind
{
    [WireName("")]
    Production,

    [WireName("mech_gestation")]
    MechGestation,

    /// <summary>A recipe making a body part item (a prosthetic or bionic for a restore operation).</summary>
    [WireName("surgery")]
    Surgery,

    /// <summary>A recipe making medicine.</summary>
    [WireName("medical")]
    Medical,

    /// <summary>A recipe making food a baby can ingest.</summary>
    [WireName("baby_food")]
    BabyFood,

    /// <summary>A private native anchor for an admitted activity, not a standing production request.</summary>
    [WireName("activity_execution")]
    ActivityExecution,
}

/// <summary>A bill read back from a bench.</summary>
public sealed record ActualBill
{
    /// <summary>The native bill identity and the key of the orphan counter.</summary>
    public string Id { get; init; } = "";

    public string Bench { get; init; } = "";

    public LedgerBillKind Kind { get; init; }

    public OrderSpec Spec { get; init; } = new();

    /// <summary>Marks a finished bill: native does not count it as standing, so it never satisfies a wanted spec and is an orphan like any other.</summary>
    public bool Spent { get; init; }
}

/// <summary>One Round's reconcile result.</summary>
public sealed record LedgerPlan
{
    public IReadOnlyList<OrderSpec> Place { get; init; } = [];

    public IReadOnlyList<ActualBill> Remove { get; init; } = [];

    /// <summary>The next Round's counter state, keyed by ActualBill.Id; callers keep it in memory.</summary>
    public IReadOnlyDictionary<string, int> Orphans { get; init; } = new Dictionary<string, int>();
}

/// <summary>A wanted order assigned to the bench that will carry it.</summary>
public sealed record LedgerPlacement(string Bench, OrderSpec Spec);

public static partial class WorkRules
{
    /// <summary>
    /// How many consecutive Rounds a bill that no planner declared must stay undeclared before Reconcile removes it.
    /// The counters are memory only: a restart empties them and only delays removal.
    /// </summary>
    public const int OrphanGraceRounds = 3;

    /// <summary>The vanilla bill stack cap of one bench.</summary>
    public const int LedgerBenchSlots = 15;

    /// <summary>The string of a bill mode.</summary>
    internal static string BillModeName(BillMode m) =>
        m switch
        {
            BillMode.FoodTarget => "food_target",
            BillMode.ButcherForever => "butcher_forever",
            BillMode.HumanButcherForever => "human_butcher_forever",
            BillMode.StockTarget => "stock_target",
            BillMode.GearBatch => "gear_batch",
            BillMode.BeerReserve => "beer_reserve",
            _ => "",
        };

    /// <summary>The bill mode native reads back for m: one of GearBatch (count), ButcherForever (forever) and StockTarget (target).</summary>
    internal static BillMode WireClass(BillMode m) =>
        m switch
        {
            BillMode.HumanButcherForever => BillMode.ButcherForever,
            BillMode.FoodTarget or BillMode.BeerReserve => BillMode.StockTarget,
            _ => m,
        };

    /// <summary>A declaration that abstains on the fact.</summary>
    public static Declared Abstaining(Cause fact) => new Declared().Unread(fact);

    /// <summary>A declaration of orders that abstains on unread, when set.</summary>
    public static Declared AbstainUnless(Cause? unread, IEnumerable<OrderSpec> orders)
    {
        var output = new Declared { Orders = [.. orders] };
        if (unread is { } cause && cause != Cause.None)
        {
            output.Unread(cause);
        }
        return output;
    }

    /// <summary>The fact when unread holds, else none.</summary>
    public static Cause UnreadIf(bool unread, Cause fact) => unread ? fact : Cause.None;

    /// <summary>
    /// Diffs the declared orders against the actual bills. Identical declared specs coalesce into one wanted order.
    /// A wanted spec with no actual bill is placed. An actual bill matching a wanted spec is kept, once per spec; any
    /// further bill of that spec is an orphan, as is a bill no planner declared. If any planner abstained nothing is
    /// removed and the counters do not advance; otherwise an orphan is removed once it has been an orphan for
    /// OrphanGraceRounds consecutive Rounds. Excluded kinds are always kept. copies is the wanted bill count per spec
    /// Key (the dispatcher's split across benches); a spec absent from it, or below one, wants one bill.
    /// </summary>
    public static LedgerPlan ReconcileLedger(
        IReadOnlyList<Declared> declared,
        IReadOnlyList<ActualBill> actual,
        IReadOnlyDictionary<string, int> orphans,
        IReadOnlyDictionary<string, int> copies)
    {
        var (wanted, abstain) = WantedOrders(declared);
        var nextOrphans = new Dictionary<string, int>();
        var matched = new Dictionary<string, int>();
        var remove = new List<ActualBill>();
        foreach (var b in actual.OrderBy(b => wanted.ContainsKey(b.Spec.Key()) ? 0 : 1).ThenBy(b => b.Id, StringComparer.Ordinal))
        {
            if (b.Kind == LedgerBillKind.ActivityExecution)
            {
                continue;
            }
            var k = b.Spec.Key();
            if (!wanted.ContainsKey(k))
                k = wanted.Where(p => IsMaterialSpecialization(p.Value, b.Spec) && matched.GetValueOrDefault(p.Key) < WantedCopies(copies, p.Key))
                    .Select(p => p.Key).Order(StringComparer.Ordinal).FirstOrDefault() ?? k;
            var isWanted = wanted.ContainsKey(k);
            if (isWanted && matched.GetValueOrDefault(k) < WantedCopies(copies, k) && !b.Spent)
            {
                matched[k] = matched.GetValueOrDefault(k) + 1;
            }
            else if (b.Kind != LedgerBillKind.Production)
            {
                continue;
            }
            else if (abstain)
            {
                if (orphans.TryGetValue(b.Id, out var n))
                {
                    nextOrphans[b.Id] = n;
                }
            }
            else
            {
                var n = orphans.GetValueOrDefault(b.Id) + 1;
                if (n >= OrphanGraceRounds)
                {
                    remove.Add(b);
                    continue;
                }
                nextOrphans[b.Id] = n;
            }
        }
        var place = new List<OrderSpec>();
        foreach (var k in wanted.Keys.Where(k => matched.GetValueOrDefault(k) < WantedCopies(copies, k)).Order(StringComparer.Ordinal))
        {
            for (var n = matched.GetValueOrDefault(k); n < WantedCopies(copies, k); n++)
            {
                place.Add(wanted[k]);
            }
        }
        return new LedgerPlan { Place = place, Remove = remove, Orphans = nextOrphans };
    }

    /// <summary>Coalesces the declared orders by spec Key (the first declaration of a key wins) and reports whether any planner abstained.</summary>
    public static (IReadOnlyDictionary<string, OrderSpec> Wanted, bool Abstain) WantedOrders(IReadOnlyList<Declared> declared)
    {
        var wanted = new Dictionary<string, OrderSpec>();
        var abstain = false;
        foreach (var d in declared)
        {
            abstain = abstain || d.Abstained();
            foreach (var o in d.Orders)
            {
                wanted.TryAdd(o.Key(), o);
            }
        }
        return (wanted, abstain);
    }

    // How many bills of the spec stand, at least one.
    private static int WantedCopies(IReadOnlyDictionary<string, int> copies, string key) => copies.TryGetValue(key, out var n) && n > 1 ? n : 1;

    /// <summary>A fixed batch's narrowed native filter still serves the original allowed-material demand.</summary>
    public static bool IsMaterialSpecialization(OrderSpec wanted, OrderSpec actual) =>
        wanted.Mode == BillMode.GearBatch && actual.Mode == BillMode.GearBatch
        && actual.Ingredients.Count > 0 && (actual with { Ingredients = wanted.Ingredients }).Key() == wanted.Key()
        && (wanted.Ingredients.Count == 0 || actual.Ingredients.All(wanted.Ingredients.Contains));

    /// <summary>
    /// Reads the benches' bills in the ledger's terms. Ok is false when a bench's definition or a bill's id is
    /// empty: a diff against a partial readback would place duplicates and remove wanted bills.
    /// </summary>
    public static (IReadOnlyList<ActualBill> Bills, bool Ok) LedgerActuals(IReadOnlyList<GearBench> benches)
    {
        var output = new List<ActualBill>();
        foreach (var bench in benches)
        {
            if (bench.Def.Length == 0)
            {
                return ([], false);
            }
            foreach (var bill in bench.Bills)
            {
                if (bill.Id.Length == 0)
                {
                    return ([], false);
                }
                output.Add(new ActualBill { Id = bill.Id, Bench = bench.Id, Kind = bill.Kind, Spec = bill.Spec, Spent = bill.Spent });
            }
        }
        return (output, true);
    }

    /// <summary>
    /// The admin/work-ledger Need's finding (#3304): owed while the plan removes a bill or a bench takes one of its
    /// placements (PlaceLedgerOrders). A wanted order no bench can hold is accepted, not owed (#3659): dispatch reports
    /// it unmet (NoBench), and the station graph hosts the bench.
    /// </summary>
    public static bool LedgerDiffOwed(LedgerPlan plan, IReadOnlyList<LedgerPlacement> placed) => placed.Count > 0 || plan.Remove.Count > 0;

    /// <summary>
    /// Assigns each order (one entry per wanted copy) to an eligible bench (EligibleBenches) that is not full and
    /// does not already carry the order: the fastest bench by work speed, then the one
    /// carrying the fewest bills, then by id. A bench is full when its readback bill count reaches LedgerBenchSlots.
    /// Placements earlier in the same plan count. Orders no bench can take are returned unplaced.
    /// </summary>
    public static (IReadOnlyList<LedgerPlacement> Placed, IReadOnlyList<OrderSpec> Unplaced) PlaceLedgerOrders(IReadOnlyList<OrderSpec> orders, IReadOnlyList<GearBench> benches)
    {
        var load = new Dictionary<string, int>();
        var carries = new HashSet<(string, string)>();
        var speed = new Dictionary<string, double>();
        foreach (var b in benches)
        {
            load[b.Id] = b.Bills.Count;
            foreach (var bill in b.Bills)
            {
                if (!bill.Spent)
                {
                    carries.Add((b.Id, bill.Spec.Key()));
                }
            }
            speed[b.Id] = b.WorkSpeed;
        }
        var sorted = benches.OrderBy(b => b.Id, StringComparer.Ordinal).ToList();

        // A plan carries one bill per bench and claim recipe (the store's rule), so a second order of the same recipe
        // takes another bench or waits a Round.
        var taken = new HashSet<(string, string)>();
        var placed = new List<LedgerPlacement>();
        var unplaced = new List<OrderSpec>();
        foreach (var order in orders)
        {
            string? best = null;
            var claim = order.Recipe;
            if (order.Mode == BillMode.HumanButcherForever)
            {
                claim += "/humanlike";
            }
            else if (order.Mode == BillMode.GearBatch && order.Worker.Length != 0)
            {
                claim += "/" + order.Worker;
            }
            foreach (var b in sorted)
            {
                if (b.Def != order.BenchKind || load.GetValueOrDefault(b.Id) >= LedgerBenchSlots || taken.Contains((b.Id, claim)) || carries.Contains((b.Id, order.Key())) || !BenchOffers(b, order.Recipe))
                {
                    continue;
                }
                if (best is null || FasterBench(speed, load, b.Id, best))
                {
                    best = b.Id;
                }
            }
            if (best is null)
            {
                unplaced.Add(order);
                continue;
            }
            load[best] = load.GetValueOrDefault(best) + 1;
            taken.Add((best, claim));
            carries.Add((best, order.Key()));
            placed.Add(new LedgerPlacement(best, order));
        }
        return (placed, unplaced);
    }

    // Whether bench a beats b: higher work speed, then fewer bills. Ids were visited in order, so a tie keeps the earlier bench.
    private static bool FasterBench(Dictionary<string, double> speed, Dictionary<string, int> load, string a, string b)
    {
        var sa = speed[a];
        var sb = speed[b];
        if (sa != sb)
        {
            return sa > sb;
        }
        return load.GetValueOrDefault(a) < load.GetValueOrDefault(b);
    }

    internal static bool BenchOffers(GearBench b, string recipe)
    {
        if (!b.Usable)
        {
            return false;
        }
        foreach (var r in b.Recipes)
        {
            if (r.Definition == recipe)
            {
                return r.AvailableOn;
            }
        }
        return false;
    }
}
