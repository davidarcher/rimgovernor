namespace RimGovernor.Brain.Policy;

/// <summary>One gestator of the colony section.</summary>
public sealed record GestatorFact
{
    public string Id { get; init; } = "";

    /// <summary>Whether the gestator has an active bill (a mech forming).</summary>
    public Fact<bool> Active { get; init; }

    /// <summary>The wastepacks held in its own waste producer.</summary>
    public Fact<int> WasteCount { get; init; }
}

/// <summary>One stack of wastepacks as the colony section reads it.</summary>
public sealed record WastepackFact
{
    public int Count { get; init; }
    public Fact<bool> Frozen { get; init; }
    public Fact<bool> InAtomizer { get; init; }
}

/// <summary>
/// What the gestation goal decides from: the catalog, the colony's mechanitors and mechs, the work coverage, and
/// the gestators and wastepacks of the Biotech colony section, and the chargers.
/// </summary>
public sealed record MechGestation
{
    public MechCatalog Catalog { get; init; } = new();
    public IReadOnlyList<MechanitorInput> Mechanitors { get; init; } = [];
    public IReadOnlyList<MechInput> Mechs { get; init; } = [];
    public IReadOnlyList<WorkCoverage> Coverage { get; init; } = [];
    public IReadOnlyList<GestatorFact> Gestators { get; init; } = [];
    public IReadOnlyList<WastepackFact> Wastepacks { get; init; } = [];
    public IReadOnlyList<MechCharger> Chargers { get; init; } = [];
}

/// <summary>The mech kind the next gestation builds and who it is for.</summary>
public sealed record MechChoice
{
    public PawnId Mechanitor { get; init; }
    public string Kind { get; init; } = "";
}

/// <summary>
/// Ports <c>mech_gestation.go</c>. MaintainMechs is the mech gestation goal: a mechanitor with bandwidth to
/// spare, an idle mech gestator and no waste left lying about is owed one more mech, queued as a Bill_Mech on the
/// gestator. Gestation rules (numbers come from the game defs, never from here): bandwidth is the limit (free
/// bandwidth is TotalBandwidth - UsedBandwidth - GestationBandwidth); gestation is held while the gestators hold
/// their own waste or any wastepack is uncleared; colonist need decides what the free bandwidth buys
/// (MechRoleNext); chargers come before more mechs; one gestation at a time.
/// </summary>
public static partial class PolicyFunctions
{
    public static readonly ConcernId MaintainMechs = new("MaintainMechs");

    /// <summary>Ranks MaintainMechs with the other upkeep projects.</summary>
    internal const int MechPriority = 3;

    /// <summary>MaintainMechs' bill purpose: one single-count gestation bill (BillMode.GearBatch, target 1) on a gestator.</summary>
    public static readonly BillPurpose MechGestationBill = new("mech_gestation");

    /// <summary>
    /// Whether new gestation must wait on waste: a gestator holding waste, or a wastepack stack not frozen and not
    /// in an atomizer (a dissolved pack is no stack any more; an empty stack counts as dissolved). An unread count
    /// or flag cannot certify the waste cleared.
    /// </summary>
    public static bool UnclearedWaste(MechGestation g)
    {
        foreach (var gestator in g.Gestators)
        {
            if (!gestator.WasteCount.TryGetValue(out var n) || n > 0)
            {
                return true;
            }
        }
        foreach (var pack in g.Wastepacks)
        {
            if (pack.Count <= 0)
            {
                continue;
            }
            if (pack.Frozen.TryGetValue(out var frozen) && frozen)
            {
                continue;
            }
            if (pack.InAtomizer.TryGetValue(out var atomizer) && atomizer)
            {
                continue;
            }
            return true;
        }
        return false;
    }

    /// <summary>A mechanitor's bandwidth left for a new mech; Ok is false while any of the three figures is unread.</summary>
    public static (int Free, bool Ok) FreeBandwidth(PawnMechanitor m)
    {
        var tk = m.TotalBandwidth.TryGetValue(out var total);
        var uk = m.UsedBandwidth.TryGetValue(out var used);
        var gk = m.GestationBandwidth.TryGetValue(out var gestation);
        if (!tk || !uk || !gk)
        {
            return (0, false);
        }
        return (total - used - gestation, true);
    }

    /// <summary>
    /// Picks the next mech: for each mechanitor, most free bandwidth first, the role MechRoleNext names, then the
    /// best kind of that role the mechanitor can afford and offered says a gestator can make. Workers rank by how
    /// many short, uncovered work types the kind covers; guards by combat power per bandwidth; ties fall to the
    /// lower cost, then the name. Ok is false with nothing affordable. A kind or mech the catalog lacks throws.
    /// </summary>
    public static (MechChoice Choice, bool Ok) NextMech(MechGestation g, Func<string, bool> offered)
    {
        var ordered = g.Mechanitors.OrderByDescending(m => FreeBandwidth(m.Mechanitor).Free).ThenBy(m => m.Id).ToList();
        foreach (var m in ordered)
        {
            var (free, freeOk) = FreeBandwidth(m.Mechanitor);
            if (!freeOk || free <= 0)
            {
                continue;
            }
            var (role, ok) = MechRoleNext(g.Catalog, m, g.Mechs, g.Coverage);
            if (!ok)
            {
                continue;
            }
            var shortWork = ShortUncoveredWork(g.Catalog, m, g.Mechs, g.Coverage);
            var kinds = new List<MechKind>();
            foreach (var kind in g.Catalog.Kinds.Values)
            {
                if (kind.Role == role && kind.BandwidthCost > 0 && kind.BandwidthCost <= free && offered(kind.Name))
                {
                    kinds.Add(kind);
                }
            }
            double Score(MechKind k)
            {
                if (role == MechRole.Guard)
                {
                    return k.CombatPower / k.BandwidthCost;
                }
                var n = 0;
                foreach (var w in k.WorkTypes)
                {
                    if (shortWork.Contains(w))
                    {
                        n++;
                    }
                }
                return n;
            }
            // A worker is bought for the short work it covers; one that covers none of it answers no need.
            if (role == MechRole.Worker)
            {
                kinds.RemoveAll(k => Score(k) == 0);
            }
            if (kinds.Count == 0)
            {
                continue;
            }
            var best = kinds.OrderByDescending(Score).ThenBy(k => k.BandwidthCost).ThenBy(k => k.Name, StringComparer.Ordinal).First();
            return (new MechChoice { Mechanitor = m.Id, Kind = best.Name }, true);
        }
        return (new MechChoice(), false);
    }

    /// <summary>
    /// The goal's deficit: false while waste is uncleared (or unread), no charger is ready, a gestator is forming
    /// a mech, or no gestator exists; true when NextMech finds an affordable mech of the needed role. The catalog's
    /// kinds stand in for the recipes a gestator offers here; the bill planner narrows to the recipes it can
    /// actually queue.
    /// </summary>
    public static Fact<bool> MechGestationOwed(MechGestation g)
    {
        if (g.Gestators.Count == 0 || UnclearedWaste(g) || !MechChargerReady(g.Chargers))
        {
            return Known(false);
        }
        foreach (var gestator in g.Gestators)
        {
            if (!gestator.Active.TryGetValue(out var active) || active)
            {
                return Known(false);
            }
        }
        var (_, ok) = NextMech(g, _ => true);
        return Known(ok);
    }

    /// <summary>
    /// Queues the next mech's recipe on the best usable gestator: the recipe whose MechKind is the choice, bulk
    /// recipes first, then by recipe and bench name. When it queues nothing the gap says why: uncleared waste, no
    /// ready charger, a gestation already in production, no usable recipe for the mech wanted, full bench bill
    /// lists, or no mech affordable (nothing wanted).
    /// </summary>
    public static (BillSelection Selection, BillGap Gap) SelectMechGestationBill(IReadOnlyList<ProductionBench> benches, MechGestation g)
    {
        if (UnclearedWaste(g))
        {
            return (new BillSelection(), BillGap.Waste);
        }
        if (!MechChargerReady(g.Chargers))
        {
            return (new BillSelection(), BillGap.NoCharger);
        }
        var gestation = new HashSet<string>(StringComparer.Ordinal);
        foreach (var bench in benches)
        {
            foreach (var recipe in bench.Recipes)
            {
                if (recipe.MechKind != "")
                {
                    gestation.Add(recipe.Name);
                }
            }
        }
        foreach (var bench in benches)
        {
            foreach (var bill in bench.Bills)
            {
                // A standing gestation bill, active or not, is in production.
                if (gestation.Contains(bill.Recipe))
                {
                    return (new BillSelection(), BillGap.InProduction);
                }
            }
        }
        var available = new HashSet<string>(StringComparer.Ordinal);
        foreach (var bench in benches)
        {
            foreach (var recipe in bench.Recipes)
            {
                if (recipe.Available.TryGetValue(out var ok) && ok && recipe.MechKind != "" && UsableBench(bench))
                {
                    available.Add(recipe.MechKind);
                }
            }
        }
        foreach (var kind in available.Order(StringComparer.Ordinal))
        {
            if (!g.Catalog.Kinds.ContainsKey(kind))
            {
                throw new DomainException($"gestation recipe makes mech kind {kind} that is not in the catalog");
            }
        }
        var (choice, chosen) = NextMech(g, available.Contains);
        if (!chosen)
        {
            // A mech is wanted but no recipe on a usable bench makes it, or no mech is affordable at all.
            var (_, wanted) = NextMech(g, _ => true);
            return (new BillSelection(), wanted ? BillGap.NoRecipe : BillGap.NothingWanted);
        }
        var options = new List<BillSelection>();
        var bulk = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var bench in benches)
        {
            if (!UsableBench(bench) || bench.Bills.Count >= 15)
            {
                continue;
            }
            bench.Token.TryGetValue(out var token);
            foreach (var recipe in bench.Recipes)
            {
                if (recipe.Available.TryGetValue(out var ok) && ok && recipe.MechKind == choice.Kind)
                {
                    options.Add(new BillSelection { Bench = bench.Id, Recipe = recipe.Name, Token = token ?? "", Mode = BillMode.GearBatch, Target = 1 });
                    bulk[recipe.Name] = recipe.Bulk;
                }
            }
        }
        if (options.Count == 0)
        {
            return (new BillSelection(), BillGap.BenchFull);
        }
        var first = options
            .OrderByDescending(o => bulk.GetValueOrDefault(o.Recipe))
            .ThenBy(o => o.Recipe, StringComparer.Ordinal)
            .ThenBy(o => o.Bench, StringComparer.Ordinal)
            .First();
        return (first, BillGap.None);
    }

    internal static bool UsableBench(ProductionBench b) => b.Usable.TryGetValue(out var usable) && usable && b.Token.TryGetValue(out var token) && FoodId(token);
}
