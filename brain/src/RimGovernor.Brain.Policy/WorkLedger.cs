using System.Globalization;

namespace RimGovernor.Brain.Policy;

// Go: work_ledger.go, work_ledger_concern.go, work_ledger_unread.go.

/// <summary>
/// A production order's identity. It carries no native tag: a bill on a bench is the same order as a declared one
/// when their specs have the same Key, so a restart rebuilds the ledger from native readback.
/// </summary>
public sealed record OrderSpec
{
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

    /// <summary>The concern that declared the order. Not identity either: two concerns declaring one spec coalesce into one order (OrderOwners).</summary>
    public ConcernId Owner { get; init; }

    /// <summary>
    /// The canonical spec identity. The mode is its wire class: native tells bills apart by repeat mode and count
    /// only, so a food target and a stock target are one order, and a bill read back as a target cannot be told from
    /// a beer reserve nor a forever bill from a human butcher bill.
    /// </summary>
    public string Key()
    {
        var ing = Ingredients.Items.Order(StringComparer.Ordinal);
        return string.Join("|", BenchKind, Recipe, PolicyFunctions.BillModeName(PolicyFunctions.WireClass(Mode)), Target.ToString(CultureInfo.InvariantCulture), Worker, string.Join(",", ing));
    }
}

/// <summary>Names the input a concern lacked this Round. Its silence about a bill proves nothing, so the ledger removes nothing while any concern abstains.</summary>
public sealed record Abstain(ConcernId Concern, Cause Fact);

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
            Abstains.Add(new Abstain(default, fact));
        }
        return this;
    }

    /// <summary>Whether any concern abstained.</summary>
    public bool Abstained() => Abstains.Count > 0;

    /// <summary>
    /// The declaration made on behalf of concern: every order without an owner is owned by it and every abstain
    /// without a concern names it. A declarer serving several concerns calls For per concern's part.
    /// </summary>
    public Declared For(ConcernId concern) =>
        new()
        {
            Orders = [.. Orders.Select(o => o.Owner.IsEmpty ? o with { Owner = concern } : o)],
            Abstains = [.. Abstains.Select(a => a.Concern.IsEmpty ? a with { Concern = concern } : a)],
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

    public IReadOnlyList<ActualBill> Keep { get; init; } = [];

    /// <summary>The next Round's counter state, keyed by ActualBill.Id; callers keep it in memory.</summary>
    public IReadOnlyDictionary<string, int> Orphans { get; init; } = new Dictionary<string, int>();

    /// <summary>The bills that stand for a wanted order (a split of Keep for the reader).</summary>
    public IReadOnlyList<ActualBill> Matched { get; init; } = [];

    /// <summary>The orphans not yet removed (in grace or held by an abstain).</summary>
    public IReadOnlyList<ActualBill> Held { get; init; } = [];

    /// <summary>The excluded-kind bills no order wants.</summary>
    public IReadOnlyList<ActualBill> Excluded { get; init; } = [];
}

/// <summary>A wanted order assigned to the bench that will carry it.</summary>
public sealed record LedgerPlacement(string Bench, OrderSpec Spec);

public static partial class PolicyFunctions
{
    /// <summary>
    /// How many consecutive Rounds a bill that no planner declared must stay undeclared before Reconcile removes it.
    /// The counters are memory only: a restart empties them and only delays removal.
    /// </summary>
    public const int OrphanGraceRounds = 3;

    /// <summary>Keeps the benches' production bills equal to the orders the planners declared (ReconcileLedger).</summary>
    public static readonly ConcernId MaintainWorkLedger = new("MaintainWorkLedger");

    private const int WorkLedgerPriority = 3;

    /// <summary>The vanilla bill stack cap of one bench.</summary>
    public const int LedgerBenchSlots = 15;

    /// <summary>The Go string of a bill mode.</summary>
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

    /// <summary>Every concern that declared each wanted spec, by spec Key. Two concerns declaring one spec coalesce into one order that both own.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<ConcernId>> OrderOwners(IReadOnlyList<Declared> declared)
    {
        var owners = new Dictionary<string, List<ConcernId>>();
        foreach (var d in declared)
        {
            foreach (var o in d.Orders)
            {
                var k = o.Key();
                if (!o.Owner.IsEmpty)
                {
                    if (!owners.TryGetValue(k, out var list))
                    {
                        list = owners[k] = [];
                    }
                    if (!list.Contains(o.Owner))
                    {
                        list.Add(o.Owner);
                    }
                }
            }
        }
        var output = new Dictionary<string, IReadOnlyList<ConcernId>>();
        foreach (var (k, list) in owners)
        {
            list.Sort(static (a, b) => string.CompareOrdinal(a.Value, b.Value));
            output[k] = list;
        }
        return output;
    }

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
        var keep = new List<ActualBill>();
        var remove = new List<ActualBill>();
        var matchedBills = new List<ActualBill>();
        var held = new List<ActualBill>();
        var excluded = new List<ActualBill>();
        foreach (var b in actual)
        {
            var k = b.Spec.Key();
            var isWanted = wanted.ContainsKey(k);
            if (isWanted && matched.GetValueOrDefault(k) < WantedCopies(copies, k) && !b.Spent)
            {
                matched[k] = matched.GetValueOrDefault(k) + 1;
                keep.Add(b);
                matchedBills.Add(b);
            }
            else if (b.Kind != LedgerBillKind.Production)
            {
                keep.Add(b);
                excluded.Add(b);
            }
            else if (abstain)
            {
                keep.Add(b);
                held.Add(b);
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
                keep.Add(b);
                held.Add(b);
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
        return new LedgerPlan { Place = place, Remove = remove, Keep = keep, Orphans = nextOrphans, Matched = matchedBills, Held = held, Excluded = excluded };
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

    /// <summary>
    /// Reads the benches' bills in the ledger's terms. It is unknown (Ok false) when any bench's bills, or any
    /// bill's spec, was unread: a diff against a partial readback would place duplicates and remove wanted bills.
    /// </summary>
    public static (IReadOnlyList<ActualBill> Bills, bool Ok) LedgerActuals(IReadOnlyList<GearBench> benches)
    {
        var output = new List<ActualBill>();
        foreach (var bench in benches)
        {
            if (!bench.Bills.TryGetValue(out var bills) || bench.Def.Length == 0)
            {
                return ([], false);
            }
            foreach (var bill in bills)
            {
                if (!bill.Spec.TryGetValue(out var spec) || bill.Id.Length == 0)
                {
                    return ([], false);
                }
                output.Add(new ActualBill { Id = bill.Id, Bench = bench.Id, Kind = bill.Kind, Spec = spec, Spent = bill.Spent });
            }
        }
        return (output, true);
    }

    /// <summary>MaintainWorkLedger's finding: owed while the plan places or removes anything.</summary>
    public static bool LedgerDiffOwed(LedgerPlan plan) => plan.Place.Count > 0 || plan.Remove.Count > 0;

    /// <summary>
    /// Assigns each order (one entry per wanted copy) to an eligible bench (EligibleBenches) that is not full and
    /// does not already carry the order: the fastest bench by work speed, an unknown speed last, then the one
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
            if (b.Bills.TryGetValue(out var bills))
            {
                load[b.Id] = bills.Count;
                foreach (var bill in bills)
                {
                    if (bill.Spec.TryGetValue(out var spec) && !bill.Spent)
                    {
                        carries.Add((b.Id, spec.Key()));
                    }
                }
            }
            else
            {
                load[b.Id] = 0;
            }
            if (b.WorkSpeed.TryGetValue(out var s))
            {
                speed[b.Id] = s;
            }
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

    // Whether bench a beats b: higher known work speed, then fewer bills. Ids were visited in order, so a tie keeps the earlier bench.
    private static bool FasterBench(Dictionary<string, double> speed, Dictionary<string, int> load, string a, string b)
    {
        var aKnown = speed.TryGetValue(a, out var sa);
        var bKnown = speed.TryGetValue(b, out var sb);
        if (aKnown != bKnown)
        {
            return aKnown;
        }
        if (sa != sb)
        {
            return sa > sb;
        }
        return load.GetValueOrDefault(a) < load.GetValueOrDefault(b);
    }

    internal static bool BenchOffers(GearBench b, string recipe)
    {
        if (!b.Usable.TryGetValue(out var usable) || !usable)
        {
            return false;
        }
        if (!b.Recipes.TryGetValue(out var recipes))
        {
            return false;
        }
        foreach (var r in recipes)
        {
            if (r.Definition == recipe)
            {
                return r.AvailableOn.TryGetValue(out var on) && on;
            }
        }
        return false;
    }

    // Unknown while the declarations or the bench readback are unread, and then it raises no Concern.
    internal static void InspectWorkLedger(RoundsRun c)
    {
        c.Assess(MaintainWorkLedger, WorkLedgerPriority, NotFact(c.F.LedgerOwed));
        if (c.F.LedgerOwed.TryGetValue(out var owed) && owed)
        {
            c.Raise(MaintainWorkLedger, WorkLedgerPriority);
        }
    }
}
