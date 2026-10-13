namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// What the latest declarations of one world derived for the sale path: the generic goods MaintainTrade ranks,
/// and the gear above demand (apparel from the export declaration, weapons from the armory's). Derived state in
/// memory only, empty until the first declaration after a restart; a weapon surplus can be one review stale when
/// the armory declares after the trade selection reads it.
/// </summary>
internal sealed class ExportMemory
{
    private readonly object _gate = new();
    private GenerationSnapshot _snapshot;
    private IReadOnlyList<Pol.Resource> _sale = [];
    private IReadOnlyList<Pol.GearStock> _apparel = [];
    private bool _apparelKnown;
    private IReadOnlyDictionary<Pol.Resource, int>? _weapons;
    private bool _weaponsKnown;
    private Pol.ExportView? _gap;

    // Callers hold the gate.
    private void World(GenerationSnapshot snapshot)
    {
        if (_snapshot != snapshot)
        {
            _snapshot = snapshot;
            _sale = [];
            _apparel = [];
            _apparelKnown = false;
            _weapons = null;
            _weaponsKnown = false;
            _gap = null;
        }
    }

    public void SetPlan(GenerationSnapshot snapshot, IReadOnlyList<Pol.Resource> sale, IReadOnlyList<Pol.GearStock> apparel, bool known)
    {
        lock (_gate)
        {
            World(snapshot);
            _sale = sale;
            _apparel = apparel;
            _apparelKnown = known;
        }
    }

    /// <summary>Keeps the latest declaration's silver gap for the ledger view.</summary>
    public void SetView(GenerationSnapshot snapshot, Pol.ExportView v)
    {
        lock (_gate)
        {
            World(snapshot);
            _gap = v;
        }
    }

    /// <summary>The silver gap of snapshot's world, null when none was read in it.</summary>
    public Pol.ExportView? View(GenerationSnapshot snapshot)
    {
        lock (_gate)
        {
            return _snapshot != snapshot ? null : _gap;
        }
    }

    public void SetWeapons(GenerationSnapshot snapshot, IReadOnlyDictionary<Pol.Resource, int> spare)
    {
        lock (_gate)
        {
            World(snapshot);
            _weapons = spare;
            _weaponsKnown = true;
        }
    }

    /// <summary>The generic export products of snapshot's world.</summary>
    public IReadOnlyList<Pol.Resource> Products(GenerationSnapshot snapshot)
    {
        lock (_gate)
        {
            return _snapshot != snapshot ? [] : [.. _sale];
        }
    }

    /// <summary>The gear above demand of snapshot's world; not ok while neither the apparel nor the weapon surplus is read.</summary>
    public (Pol.GearSurplus Surplus, bool Ok) Surplus(GenerationSnapshot snapshot)
    {
        lock (_gate)
        {
            if (_snapshot != snapshot || (!_apparelKnown && !_weaponsKnown))
            {
                return (new Pol.GearSurplus(), false);
            }
            var output = new Pol.GearSurplus();
            if (_apparelKnown)
            {
                output.Apparel = [.. _apparel];
            }
            if (_weaponsKnown)
            {
                output.Weapons = new Dictionary<Pol.Resource, int>(_weapons!);
            }
            return (output, true);
        }
    }

    /// <summary>Whether both halves of the gear surplus are read.</summary>
    public bool Known(GenerationSnapshot snapshot)
    {
        lock (_gate)
        {
            return _snapshot == snapshot && _apparelKnown && _weaponsKnown;
        }
    }
}

/// <summary>The native surface the export declaration reads beyond the review's projection.</summary>
public interface IExportSource : Observation.IDefinitionSource
{
    (ObservationsPb.TradeAcquisition Acquisition, BridgeResult Result) ReadTradeAcquisition(CancellationToken ct, CommonPb.Identity identity, OperationsPb.FormCaravanIntent? intent);

    (Bridge.WorldProgressionRead Read, BridgeResult Result) ReadWorldProgression(CancellationToken ct, CommonPb.Identity identity, bool full);

    (Bridge.WorldRead Read, BridgeResult Result) ReadWorld(CancellationToken ct, CommonPb.Identity identity, int tile, double radius);
}

public sealed partial class Rounder
{
    internal ExportMemory Exports { get; } = new();
}

/// <summary>
/// MaintainTrade's OrderDeclarer. The export batches are the ledger's: DeclareOrders declares them and no bill
/// method is committed here. What stays is the game time a batch takes, which a placed bill does not ask the
/// clock for (PurposeReporter).
/// </summary>
internal sealed class RoundsTradeExportPlanner : IPurposeReporter
{
    private readonly Rounder _reviewer;
    private readonly object _gate = new();

    // Whether the latest declaration carries an export order.
    private bool _working;

    /// <summary>The window an export batch asks for per step; the review re-reads the bench between windows.</summary>
    internal const uint ExportNativeWorkTicks = 2500;

    /// <summary>Reads every settlement of the world, whatever its distance.</summary>
    internal const double AllSettlementsRadius = 1e9;

    public RoundsTradeExportPlanner(Rounder reviewer)
    {
        if (reviewer is null)
        {
            throw new ControlException("NewRoundsTradeExportPlanner: reviewer == nil");
        }
        _reviewer = reviewer;
    }

    private void SetWorking(bool v)
    {
        lock (_gate)
        {
            _working = v;
        }
    }

    /// <summary>The declaration owned by MaintainTrade.</summary>
    public Declared DeclareOrders(CancellationToken ct, GenerationSnapshot snapshot, Observation.ColonyProjection projection, IReadOnlyList<Pol.GearBench> benches) =>
        DeclareOrdersInner(ct, snapshot, projection, benches).For(MaintainTrade);

    /// <summary>Declares the export batches (OrderDeclarer) and files the sale path's inputs. Any unread input abstains: the ledger removes nothing.</summary>
    private Declared DeclareOrdersInner(CancellationToken ct, GenerationSnapshot snapshot, Observation.ColonyProjection projection, IReadOnlyList<Pol.GearBench> benches)
    {
        SetWorking(false);
        var rd = _reviewer;
        Sto.Rounds review;
        try
        {
            review = rd.Player.Journal.LoadRounds(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return AbstainOnRead(ct, Pol.Cause.UnreadReview, e);
        }
        var f = projection.Facts;
        var (gap, demand) = Pol.PolicyFunctions.RoundsSilverGap(f, rd.Policy, review.Latches.MedicalReserve);
        var request = new Pol.ExportRequest
        {
            Benches = benches,
            Profiles = Unknown<IReadOnlyList<Pol.PawnProfile>>(),
            Items = f.Items,
            Gap = gap,
            Demand = demand,
            Floors = Pol.PolicyFunctions.RoundsTradeFloors(rd.Policy, null),
            Packed = Unknown<long>(),
            Buys = new Dictionary<Pol.Resource, Fact<bool>>(),
            CashCap = Unknown<long>(),
            Sources = new Dictionary<Pol.Resource, IReadOnlyList<Pol.SupplyCandidate>>(),
        };
        if (projection.WorkPawns.TryGetValue(out var pawns))
        {
            request.Profiles = Known(Pol.PolicyFunctions.Profiles(pawns));
        }
        var (stock, stockKnown) = ExportStock(f);
        request.Stock = stock;
        if (review.Enabled && review.Snapshot == snapshot)
        {
            request.Runways = review.ResourceRunwayState();
        }
        var (apparel, apparelKnown) = Pol.PolicyFunctions.ApparelSurplus(new Pol.ClothingDemandInput { Garments = f.Garments, Categories = f.Items.StuffCategories, Gear = f.Gear, Stock = new Pol.StockReader { Resources = f.Resources, Wood = f.Wood } });
        if (apparelKnown)
        {
            request.Surplus.Apparel = apparel;
        }
        var (weapons, weaponsOk) = rd.Exports.Surplus(snapshot);
        if (weaponsOk && rd.Exports.Known(snapshot))
        {
            request.Surplus.Weapons = weapons.Weapons;
            request.SurplusKnown = apparelKnown;
        }
        // The sale path learns the rankable goods even when this Round abstains.
        var ranked = Pol.PolicyFunctions.DeclareExportOrders(request);
        rd.Exports.SetPlan(snapshot, Pol.PolicyFunctions.ExportSaleProducts(f.Items, ranked.Products), apparel, apparelKnown);
        rd.Exports.SetView(snapshot, Pol.PolicyFunctions.NewExportView(ranked, gap));
        if (!stockKnown)
        {
            return Pol.PolicyFunctions.Abstaining(Pol.Cause.UnreadStock);
        }
        if (!gap.TryGetValue(out var g) || !(g > 0))
        {
            return ranked.Declared;
        }
        var native = rd.Native as IExportSource;
        var resource = rd.ResourceNative ?? rd.Native as IRoundsResourceSource;
        if (native is null || resource is null)
        {
            return Pol.PolicyFunctions.Abstaining(Pol.Cause.UnreadNativeRead);
        }
        var identity = BoundaryApi.Identity(snapshot);
        Bridge.DefinitionCatalog? catalog;
        try
        {
            catalog = native.DefinitionCatalog(ct, identity);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return AbstainOnRead(ct, Pol.Cause.UnreadDefinitions, e);
        }
        if (catalog is null)
        {
            return AbstainOnRead(ct, Pol.Cause.UnreadDefinitions);
        }
        var (kinds, known) = TraderKinds(ct, native, snapshot, identity);
        if (!known)
        {
            return AbstainOnRead(ct, Pol.Cause.UnreadBuyers);
        }
        (request.CashCap, request.Buys) = ExportBuyers(catalog, kinds, ranked.Products);
        request.WorkFor = ExportWorkFor(catalog);
        var names = Pol.PolicyFunctions.ExportIngredients(request);
        var strs = names.Select(n => n.Value).ToList();
        if (strs.Count > 0)
        {
            try
            {
                (request.Supply, _) = resource.ReadSupplyStock(ct, identity, strs);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return AbstainOnRead(ct, Pol.Cause.UnreadSupply, e);
            }
        }
        var centered = projection.Center().TryGetValue(out var center);
        projection.Acquisition.TryGetValue(out var acquisition);
        foreach (var name in names)
        {
            IReadOnlyList<Pol.ResourceSource> rows;
            try
            {
                (rows, _, _) = resource.ReadResourceSources(ct, identity, name.Value);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return AbstainOnRead(ct, Pol.Cause.UnreadSources, e);
            }
            var mines = new List<Pol.ResourceSource>();
            foreach (var row in rows)
            {
                if (row.Method == Pol.ResourceSourceMethod.Mine && !row.Taken && Pol.PolicyFunctions.MineSafe(row.Safety))
                {
                    mines.Add(row);
                }
            }
            var sources = Pol.PolicyFunctions.MineCandidates(name, mines, Unknown<long>()).ToList();
            if (centered)
            {
                var hand = new List<Pol.AcquisitionSource>();
                foreach (var s in acquisition ?? [])
                {
                    if (new Pol.Resource(s.Resource) == name)
                    {
                        hand.Add(s);
                    }
                }
                sources.AddRange(Pol.PolicyFunctions.AcquisitionSourceCandidates(name, hand, center, Unknown<long>()));
            }
            request.Sources[name] = sources;
        }
        try
        {
            request.Packed = PackedArt(ct, snapshot, identity, projection);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // An unread packed count leaves the fact unknown, as Go ignores the error.
        }
        var plan = Pol.PolicyFunctions.DeclareExportOrders(request);
        rd.Exports.SetView(snapshot, Pol.PolicyFunctions.NewExportView(plan, gap));
        SetWorking(plan.Declared.Orders.Count > 0);
        return plan.Declared;
    }

    /// <summary>Counts the packed sculptures no owed room reserves.</summary>
    private Fact<long> PackedArt(CancellationToken ct, GenerationSnapshot snapshot, CommonPb.Identity identity, Observation.ColonyProjection projection)
    {
        if (_reviewer.Native is not IPackedSource native)
        {
            return Unknown<long>();
        }
        if (!projection.Rooms.IsKnown)
        {
            return Unknown<long>();
        }
        var sale = SaleSculpturesOf(ct, native, identity, projection);
        return sale is null ? Unknown<long>() : Known((long)sale.Count);
    }

    /// <summary>The trader kinds the colony can reach: the orbital and caravan requests the comms consoles offer and the known settlements'.</summary>
    private (List<string> Kinds, bool Known) TraderKinds(CancellationToken ct, IExportSource native, GenerationSnapshot snapshot, CommonPb.Identity identity)
    {
        ObservationsPb.TradeAcquisition? facts;
        try
        {
            (facts, _) = native.ReadTradeAcquisition(ct, identity, null);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return ([], false);
        }
        if (facts is null)
        {
            return ([], false);
        }
        var seen = new HashSet<string>();
        foreach (var row in facts.Requests)
        {
            if (row.HasEligible && !row.Eligible)
            {
                continue;
            }
            if (row.TraderKind != "")
            {
                seen.Add(row.TraderKind);
            }
        }
        Bridge.WorldProgressionRead progress;
        try
        {
            (progress, _) = native.ReadWorldProgression(ct, identity, false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return ([], false);
        }
        var home = -1;
        foreach (var row in progress.Maps)
        {
            if (row.Home && new MapId(row.Id) == snapshot.Map)
            {
                home = row.Tile;
            }
        }
        if (home >= 0)
        {
            Bridge.WorldRead world;
            try
            {
                (world, _) = native.ReadWorld(ct, identity, home, AllSettlementsRadius);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return ([], false);
            }
            foreach (var s in world.Settlements)
            {
                if (s.TraderKind != "")
                {
                    seen.Add(s.TraderKind);
                }
            }
        }
        var output = seen.ToList();
        output.Sort(StringComparer.Ordinal);
        return (output, true);
    }

    /// <summary>Files under MaintainTrade itself: no other planner serves it.</summary>
    public (ConcernId Key, ConcernId Need) ReportTarget() => (MaintainTrade, MaintainTrade);

    public PurposeReport Report()
    {
        lock (_gate)
        {
            if (!_working)
            {
                return new PurposeReport { Order = BuildingReasonNoDeficit };
            }
            return new PurposeReport { Order = WaitFor(Pol.Cause.ExistingWork, "export"), WorkTicks = ExportNativeWorkTicks };
        }
    }

    private static Func<Pol.Resource, Pol.Resource, (double Work, bool Ok)> ExportWorkFor(Bridge.DefinitionCatalog catalog) =>
        (product, stuff) => catalog.WorkToMake(product.Value, stuff.Value);

    /// <summary>The colony stock by definition from the resource census.</summary>
    internal static (Dictionary<Pol.Resource, long>? Stock, bool Known) ExportStock(Pol.RoundsFacts f)
    {
        if (!f.Resources.TryGetValue(out var rows))
        {
            return (null, false);
        }
        var output = new Dictionary<Pol.Resource, long>();
        foreach (var row in rows)
        {
            output[row.Resource] = output.GetValueOrDefault(row.Resource) + row.Count;
        }
        return (output, true);
    }

    /// <summary>
    /// Evaluates the reachable kinds' buy generators: per product whether any kind buys it, and the cash cap, the
    /// low end of the silver the best kind carries. No reachable kind buys nothing and caps at zero.
    /// </summary>
    internal static (Fact<long> CashCap, Dictionary<Pol.Resource, Fact<bool>> Buys) ExportBuyers(Bridge.DefinitionCatalog catalog, IReadOnlyList<string> kinds, IReadOnlyList<Pol.Resource> products)
    {
        var buys = new Dictionary<Pol.Resource, Fact<bool>>();
        foreach (var product in products)
        {
            var any = false;
            var unknown = false;
            foreach (var kind in kinds)
            {
                if (!catalog.TraderKindBuys(kind, product).TryGetValue(out var v))
                {
                    unknown = true;
                }
                else if (v)
                {
                    any = true;
                }
            }
            buys[product] = any ? Known(true) : unknown ? Unknown<bool>() : Known(false);
        }
        long cap = 0;
        var capUnknown = false;
        foreach (var kind in kinds)
        {
            if (!catalog.TraderKindSilver(kind).TryGetValue(out var silver))
            {
                capUnknown = true;
                continue;
            }
            cap = Math.Max(cap, silver);
        }
        if (cap == 0 && capUnknown)
        {
            return (Unknown<long>(), buys);
        }
        return (Known(cap), buys);
    }
}
