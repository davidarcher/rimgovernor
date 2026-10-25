using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// What the latest declarations of one world derived for the sale path: the generic goods the economy/trade Need ranks,
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
                output = output with { Apparel = [.. _apparel] };
            }
            if (_weaponsKnown)
            {
                output = output with { Weapons = new Dictionary<Pol.Resource, int>(_weapons!) };
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

public sealed partial class Rounder
{
    internal ExportMemory Exports { get; } = new();
}

/// <summary>
/// The economy/trade Need's OrderDeclarer (#3316). The export batches are the ledger's, filed under the Need: DeclareOrders declares them and no bill
/// method is committed here. What stays is the game time a batch takes, which a placed bill does not ask the
/// clock for (PurposeReporter).
/// </summary>
internal sealed class RoundsTradeExportPlanner : IPurposeReporter
{
    private readonly Rounder _reviewer;
    private readonly object _gate = new();

    // Whether the latest declaration carries an export order.
    private bool _working;

    // The silver gap of the latest declaration: the economy/trade Need's measure, unknown before the first.
    private Fact<double> _gap = Unknown<double>();

    // The home map's world tile of _homeSnapshot's world, once read (HomeTileAsync).
    private GenerationSnapshot _homeSnapshot;
    private int _home = -1;
    private bool _homeKnown;

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

    /// <summary>The declaration owned by the economy/trade Need.</summary>
    public async Task<Declared> DeclareOrdersAsync(GenerationSnapshot snapshot, Observation.ColonyProjection projection, IReadOnlyList<Pol.GearBench> benches, CancellationToken ct) =>
        (await DeclareOrdersInnerAsync(snapshot, projection, benches, ct).ConfigureAwait(false)).For(EconomyFamily.Trade.FilingKey.Value);

    /// <summary>Declares the export batches (OrderDeclarer) and files the sale path's inputs. Any unread input abstains: the ledger removes nothing.</summary>
    private async Task<Declared> DeclareOrdersInnerAsync(GenerationSnapshot snapshot, Observation.ColonyProjection projection, IReadOnlyList<Pol.GearBench> benches, CancellationToken ct)
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
            return RoundsGearPlanner.AbstainOnRead(Pol.Cause.UnreadReview, e, ct);
        }
        var f = projection.Facts;
        var (gap, demand) = Pol.SilverGapRules.RoundsSilverGap(f, rd.Policy, review.Latches.MedicalReserve);
        lock (_gate)
        {
            _gap = gap;
        }
        var candidateSources = new Dictionary<Pol.Resource, IReadOnlyList<Pol.SupplyCandidate>>();
        var request = new Pol.ExportRequest
        {
            Benches = benches,
            Profiles = Unknown<EquatableArray<Pol.PawnProfile>>(),
            Items = f.Items,
            Gap = gap,
            Demand = demand,
            Floors = Pol.TradePolicyRules.RoundsTradeFloors(rd.Policy, null),
            Packed = Unknown<long>(),
            Buys = new Dictionary<Pol.Resource, Fact<bool>>(),
            CashCap = Unknown<long>(),
            Sources = candidateSources,
        };
        if (projection.WorkPawns is WorkPawnsRead.Read { Pawns: var pawns })
        {
            request = request with { Profiles = Known<EquatableArray<Pol.PawnProfile>>([.. Pol.PawnRules.Profiles(pawns)]) };
        }
        var (stock, stockKnown) = ExportStock(f);
        request = request with { Stock = stock ?? new Dictionary<Pol.Resource, long>() };
        if (review.Enabled && review.Snapshot == snapshot)
        {
            request = request with { Runways = review.ResourceRunwayState() };
        }
        var (apparel, apparelKnown) = GearSurplusRules.ApparelSurplus(new Pol.ClothingDemandInput { Garments = f.Garments, Items = f.Items, Gear = f.Gear, Stock = new Pol.StockReader { Resources = f.Resources, Wood = f.Wood } });
        if (apparelKnown)
        {
            request = request with { Surplus = request.Surplus with { Apparel = apparel } };
        }
        var (weapons, weaponsOk) = rd.Exports.Surplus(snapshot);
        if (weaponsOk && rd.Exports.Known(snapshot))
        {
            request = request with { Surplus = request.Surplus with { Weapons = weapons.Weapons }, SurplusKnown = apparelKnown };
        }
        // The sale path learns the rankable goods even when this Round abstains.
        var ranked = ExportOrdersRules.DeclareExportOrders(request);
        rd.Exports.SetPlan(snapshot, ExportOrdersRules.ExportSaleProducts(f.Items, ranked.Products), apparel, apparelKnown);
        if (!stockKnown)
        {
            return Pol.WorkRules.Abstaining(Pol.Cause.UnreadStock);
        }
        if (!gap.TryGetValue(out var g) || !(g > 0))
        {
            return ranked.Declared;
        }
        var native = rd.Native as FacadeNativeClient;
        var resource = rd.ResourceNative ?? rd.Native as FacadeNativeClient;
        if (native is null || resource is null)
        {
            return Pol.WorkRules.Abstaining(Pol.Cause.UnreadNativeRead);
        }
        var identity = Boundary.Identity(snapshot);
        Bridge.DefinitionCatalog catalog;
        try
        {
            catalog = await native.DefinitionCatalogAsync(identity, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return RoundsGearPlanner.AbstainOnRead(Pol.Cause.UnreadDefinitions, e, ct);
        }
        var (kinds, known) = await TraderKindsAsync(native, snapshot, identity, ct).ConfigureAwait(false);
        if (!known)
        {
            return RoundsGearPlanner.AbstainOnRead(Pol.Cause.UnreadBuyers, null, ct);
        }
        var (cashCap, buys) = ExportBuyers(catalog, kinds, ranked.Products);
        request = request with { CashCap = cashCap, Buys = buys };
        request = request with { WorkFor = ExportWorkFor(catalog) };
        var names = ExportOrdersRules.ExportIngredients(request);
        var strs = names.Select(n => n.Value).ToList();
        // The supply stock, every ingredient's sources and the packed art read nothing of each other, so their hops
        // share frames (#3566); the results are read in the order the sequential reads had, so the first unread input
        // still names the abstain.
        var supplyRead = strs.Count > 0 ? SharedReads.Attempt(resource.ReadSupplyStockAsync(identity, strs, ct)) : null;
        var sourceReads = new Task<(IReadOnlyList<Pol.ResourceSource> Rows, Exception? Error)>[names.Count];
        for (var i = 0; i < names.Count; i++)
        {
            sourceReads[i] = ReadSourcesAsync(resource, identity, names[i].Value, ct);
        }
        var packedRead = PackedArtAsync(snapshot, identity, projection, ct);
        if (supplyRead is not null)
        {
            var (supply, error) = await supplyRead.ConfigureAwait(false);
            if (error is not null)
            {
                return RoundsGearPlanner.AbstainOnRead(Pol.Cause.UnreadSupply, error, ct);
            }
            request = request with { Supply = supply };
        }
        var centered = projection.Center().TryGetValue(out var center);
        for (var i = 0; i < names.Count; i++)
        {
            var name = names[i];
            var (rows, error) = await sourceReads[i].ConfigureAwait(false);
            if (error is not null)
            {
                return RoundsGearPlanner.AbstainOnRead(Pol.Cause.UnreadSources, error, ct);
            }
            var mines = new List<Pol.ResourceSource>();
            foreach (var row in rows)
            {
                if (row.Method == Pol.ResourceSourceMethod.Mine && !row.Taken && Pol.ResourceRules.MineSafe(row.Safety))
                {
                    mines.Add(row);
                }
            }
            var sources = AcquisitionCatalogRules.MineCandidates(name, mines, new YieldStorage.Unread()).ToList();
            if (centered)
            {
                var hand = new List<Pol.AcquisitionSource>();
                foreach (var s in projection.Acquisition)
                {
                    if (new Pol.Resource(s.Resource) == name)
                    {
                        hand.Add(s);
                    }
                }
                sources.AddRange(AcquisitionCatalogRules.AcquisitionSourceCandidates(name, hand, center, new YieldStorage.Unread()));
            }
            candidateSources[name] = sources;
        }
        request = request with { Packed = await packedRead.ConfigureAwait(false) };
        var plan = ExportOrdersRules.DeclareExportOrders(request);
        SetWorking(plan.Declared.Orders.Count > 0);
        return plan.Declared;
    }

    private static async Task<(IReadOnlyList<Pol.ResourceSource> Rows, Exception? Error)> ReadSourcesAsync(FacadeNativeClient resource, C.Identity identity, string name, CancellationToken ct)
    {
        var (read, error) = await SharedReads.Attempt(resource.ReadResourceSourcesAsync(identity, name, ct)).ConfigureAwait(false);
        return (error is null ? read.Rows : [], error);
    }

    /// <summary>Counts the packed sculptures no owed room reserves; unknown when the count is unread.</summary>
    private async Task<Fact<long>> PackedArtAsync(GenerationSnapshot snapshot, C.Identity identity, Observation.ColonyProjection projection, CancellationToken ct)
    {
        if (_reviewer.Native is not FacadeNativeClient native)
        {
            return Unknown<long>();
        }
        if (!projection.Rooms.IsKnown)
        {
            return Unknown<long>();
        }
        var (sale, error) = await SharedReads.Attempt(RoundsSleepingUpkeepPlanner.SaleSculpturesOfAsync(native, identity, projection, ct)).ConfigureAwait(false);
        return error is not null || sale is null ? Unknown<long>() : Known((long)sale.Count);
    }

    /// <summary>
    /// The trader kinds the colony can reach: the orbital and caravan requests the comms consoles offer and the known
    /// settlements'. The two reads share frames; the home tile the settlements read needs is read once per world
    /// (a map's tile never changes), so after the first review the kinds cost two hops in one frame, not three in three (#3566).
    /// </summary>
    private async Task<(List<string> Kinds, bool Known)> TraderKindsAsync(FacadeNativeClient native, GenerationSnapshot snapshot, C.Identity identity, CancellationToken ct)
    {
        var requestsRead = SharedReads.Attempt(native.ReadTradeAcquisitionAsync(identity, null, ct));
        var settlementsRead = SettlementTraderKindsAsync(native, snapshot, identity, ct);
        var (facts, error) = await requestsRead.ConfigureAwait(false);
        if (error is not null || facts is null)
        {
            return ([], false);
        }
        var seen = new HashSet<string>();
        foreach (var row in facts.Requests)
        {
            if (row.Eligible && row.TraderKind.Length != 0)
            {
                seen.Add(row.TraderKind);
            }
        }
        var settlements = await settlementsRead.ConfigureAwait(false);
        if (settlements is null)
        {
            return ([], false);
        }
        seen.UnionWith(settlements);
        var output = seen.ToList();
        output.Sort(StringComparer.Ordinal);
        return (output, true);
    }

    /// <summary>The known settlements' trader kinds; empty without a home tile, null when a read failed.</summary>
    private async Task<List<string>?> SettlementTraderKindsAsync(FacadeNativeClient native, GenerationSnapshot snapshot, C.Identity identity, CancellationToken ct)
    {
        var home = await HomeTileAsync(native, snapshot, identity, ct).ConfigureAwait(false);
        if (home is null)
        {
            return null;
        }
        if (home < 0)
        {
            return [];
        }
        var (world, error) = await SharedReads.Attempt(native.ReadWorldAsync(identity, home.Value, AllSettlementsRadius, ct)).ConfigureAwait(false);
        if (error is not null)
        {
            return null;
        }
        var kinds = new List<string>();
        foreach (var s in world.Settlements)
        {
            if (s.TraderKind != "")
            {
                kinds.Add(s.TraderKind);
            }
        }
        return kinds;
    }

    /// <summary>The home map's world tile in snapshot's world, read once per world; -1 when the map is not a home, null when the read failed.</summary>
    private async Task<int?> HomeTileAsync(FacadeNativeClient native, GenerationSnapshot snapshot, C.Identity identity, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_homeSnapshot == snapshot && _homeKnown)
            {
                return _home;
            }
        }
        var (progress, error) = await SharedReads.Attempt(native.ReadWorldProgressionAsync(identity, ct)).ConfigureAwait(false);
        if (error is not null)
        {
            return null;
        }
        var home = -1;
        foreach (var row in progress.Maps)
        {
            if (row.Home && new MapId(row.Id) == snapshot.Map)
            {
                home = row.Tile;
            }
        }
        lock (_gate)
        {
            _homeSnapshot = snapshot;
            _home = home;
            _homeKnown = true;
        }
        return home;
    }

    /// <summary>The ledger key the export batches report under, a subject of the economy/trade Need.</summary>
    internal static readonly ConcernId TradeBillsKey = NeedId.Of(EconomyFamily.FamilyId, "trade", "bills").FilingKey;

    /// <summary>Files the export batches under the economy/trade Need.</summary>
    public (ConcernId Key, ConcernId Need) ReportTarget() => (TradeBillsKey, EconomyFamily.Trade.FilingKey);

    public PurposeReport Report()
    {
        lock (_gate)
        {
            var need = EconomyFamily.TradeNeed(new TradeReview { SilverGap = _gap });
            if (!_working)
            {
                return new PurposeReport { Need = need, Order = BuildingReasonNoDeficit };
            }
            return new PurposeReport { Need = need, Order = WaitFor(Pol.Cause.ExistingWork, "export") };
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
