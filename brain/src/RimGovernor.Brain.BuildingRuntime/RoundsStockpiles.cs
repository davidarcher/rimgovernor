namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>Remembers, per world, what the reviewer last learned for the planner and layout.</summary>
internal sealed class StockpileMemory
{
    private readonly object _gate = new();

    // The storage planner's latest layout demand for demandWorld: layout reads it to add further rooms.
    private Pol.RoomDemand _demand = new();
    private string _demandWorld = "";

    /// <summary>Records the planner's layout demands for world.</summary>
    public void SetDemand(string world, Pol.RoomDemand demand)
    {
        lock (_gate)
        {
            _demand = demand;
            _demandWorld = world;
        }
    }

    /// <summary>The recorded layout demand; none for another world.</summary>
    public Pol.RoomDemand LayoutDemand(string world)
    {
        lock (_gate)
        {
            return _demandWorld != world ? new Pol.RoomDemand() : _demand;
        }
    }

    // Whether the latest review in activeWorld proposed stockpile work: the admin/stockpiles Need reads it.
    private Fact<bool> _active;
    private GenerationSnapshot _activeWorld;

    /// <summary>Records whether the review in <paramref name="world"/> proposed stockpile work; unknown when its request was incomplete.</summary>
    public void SetActive(GenerationSnapshot world, Fact<bool> active)
    {
        lock (_gate)
        {
            _active = active;
            _activeWorld = world;
        }
    }

    /// <summary>Whether the latest review in <paramref name="world"/> proposed stockpile work; unknown for another world or before a review.</summary>
    public Fact<bool> Active(GenerationSnapshot world)
    {
        lock (_gate)
        {
            return _activeWorld == world ? _active : Unknown<bool>();
        }
    }
}

partial class RoundsStockpilePlanner
{
    internal static string StockpileWorld(GenerationSnapshot s) =>
        string.Create(CultureInfo.InvariantCulture, $"{s.Colony}/{s.Load}/{s.Map}");

    /// <summary>
    /// Builds the stockpile review input from the projection's planning cells (a zone's cells are those naming
    /// it; its stacks count its built shelves' cells too) and the colony's stockpile claims, their
    /// settings superseded by the latest patch of each; the registered roles judge on the projection and benches.
    /// </summary>
    internal static Pol.StockpileRequest StockpileRequest(Observation.ColonyProjection projection, IReadOnlyList<Sto.OwnedZone> owned, IReadOnlyDictionary<string, Sto.AppliedStockpile>? patches, IReadOnlyDictionary<string, List<Pol.ShelfRecord>> shelves, Fact<IReadOnlyDictionary<string, bool>> benches, IReadOnlyList<Pol.BenchInput> inputs, Pol.GearStore? gear, IReadOnlyList<Cell> protectedCells)
    {
        var request = new Pol.StockpileRequest { Cells = projection.Cells, Bounds = projection.Bounds, Protected = protectedCells, Loot = projection.Facts.EventLoot };
        var rooms = new List<Pol.PlannedRole>();
        foreach (var module in new[] { Pol.PlannedRole.Storage })
        {
            var (_, owedRoom) = RoundsBuildingPlanner.PlannedRoomOwed(projection, module);
            if (owedRoom)
            {
                rooms.Add(module);
            }
        }
        request = request with { Rooms = rooms };
        request = request with { Zones = StockpileZones.OwnedStockpileZones(projection, owned, patches, shelves) };
        var storage = StockpileSitedRoles.StoreView(projection, request.Protected);
        storage = storage with { Gear = gear };
        storage = storage with { Zones = request.Zones };
        storage = storage with { BenchInputs = inputs };
        storage = storage with { Benches = benches };
        var (stands, rings) = MilitaryRounds.TrainingDemand(projection);
        storage = storage with { TrainingStands = stands, TrainingRings = rings };
        var declared = Pol.PolicyFunctions.DeclareStores(storage);
        request = request with { Stores = declared.Stores };
        request = request with { RoomDemand = declared.Demand };
        return request;
    }

    /// <summary>
    /// The admin/stockpiles edits commit as one Method per cycle, one action per zone under that zone's
    /// fresh CAS token. A telemetry row (family stockpile) records a stockpile edit proposed, admitted or refused:
    /// target the zone or role, reason the refusal code, attrs the edit kind, plan and detail.
    /// </summary>
    internal static Decision StockpileEditDecision(string verdict, string reason, string target, Dictionary<string, object?> attrs)
    {
        attrs["family"] = "stockpile";
        return new Decision { Kind = TelemetryKinds.LayoutEditKind, Component = "layout", Verdict = verdict, Reason = reason, Target = target, Attrs = attrs };
    }

    /// <summary>
    /// The Shelf's build cost for the storage option report: the stuff an ordinary placement builds it from
    /// (BuildStuff), that stuff's cost list and WorkToBuild; unknown without the def or a cost row for the stuff.
    /// </summary>
    internal static Fact<Pol.StorageBuildCost> ShelfCost(Observation.ColonyProjection projection)
    {
        if (!projection.TryDefinition(ShelfDefinition, out var d))
        {
            return Unknown<Pol.StorageBuildCost>();
        }
        if (d.StuffOptions.Count == 0)
        {
            return !d.Stuffed && d.Costs.TryGetValue(out var costs)
                ? Known(new Pol.StorageBuildCost { Costs = [.. costs], WorkTicks = d.WorkToBuild })
                : Unknown<Pol.StorageBuildCost>();
        }
        var stuff = projection.BuildStuff(ShelfDefinition);
        if (d.StuffOptions.FirstOrDefault(o => o.Stuff == stuff) is not { } option)
        {
            return Unknown<Pol.StorageBuildCost>();
        }
        return Known(new Pol.StorageBuildCost
        {
            Stuff = stuff,
            Costs = option.Costs,
            WorkTicks = option.Stats.TryGetValue(Bridge.Contracts.StatDefNames.StatWorkToBuild, out var work) ? Known(work) : Unknown<double>(),
        });
    }

    /// <summary>
    /// The layout plan's storage rooms and yards for the storage option report, each with the material the plan still
    /// owes for it (PlannedConstructionOf; a yard is unfenced and owes none) and the catalog work its planned sites owe
    /// (PlannedRoomWork over PlannedRoomSitesOf); unknown while the plan is.
    /// </summary>
    internal static Fact<IReadOnlyList<Pol.PlannedStoreRoom>> StoreRooms(Observation.ColonyProjection projection, Pol.FlooringPolicy flooring)
    {
        if (!projection.LayoutPlan.TryGetValue(out var plan))
        {
            return Unknown<IReadOnlyList<Pol.PlannedStoreRoom>>();
        }
        var rooms = plan.Rooms.Where(r => r.Role == Pol.PlannedRole.Storage || r.Role == Pol.PlannedRole.Yard).ToList();
        var owed = rooms.Any(r => !r.Unfenced()) ? Rounder.PlannedConstructionOf(projection, flooring) : default;
        var sites = rooms.Any(r => !r.Unfenced()) ? Rounder.PlannedRoomSitesOf(projection, flooring) : null;
        var floors = sites is null ? null : RoundsBuildingPlanner.FlooringFactsOf(projection);
        var output = new List<Pol.PlannedStoreRoom>(rooms.Count);
        foreach (var room in rooms)
        {
            Fact<IReadOnlyDictionary<Pol.Resource, long>> material = default;
            if (room.Unfenced())
            {
                material = Known<IReadOnlyDictionary<Pol.Resource, long>>(new Dictionary<Pol.Resource, long>());
            }
            else if (owed.TryGetValue(out var byRoom))
            {
                material = Known(byRoom.GetValueOrDefault(room.Id) ?? new Dictionary<Pol.Resource, long>());
            }
            var (work, roof) = room.Unfenced() ? (Known(0.0), false)
                : sites is null || floors is null ? (Unknown<double>(), false)
                : PlannedRoomWork(sites, room.Id, site => Rounder.PlannedSiteWork(projection, floors, site));
            output.Add(new Pol.PlannedStoreRoom
            {
                Room = room.Id, Role = room.Role, Cells = [.. room.Region.Cells()], Outdoor = room.Outdoor, Material = material,
                Work = work, RoofOwed = roof,
            });
        }
        return Known<IReadOnlyList<Pol.PlannedStoreRoom>>(output);
    }

    /// <summary>Whether the census found the Shelf buildable (researched).</summary>
    internal static bool ShelfAvailable(Observation.ColonyProjection projection)
    {
        foreach (var d in projection.Definitions)
        {
            if (d.Name == ShelfDefinition)
            {
                return d.Available.TryGetValue(out var available) && available;
            }
        }
        return false;
    }
}

public sealed partial class Rounder
{
    internal StockpileMemory Stockpiles { get; } = new();

    /// <summary>Files the owned zones per role kind on the projection, whether or not the stockpile method is served.</summary>
    internal Observation.ColonyProjection CensusStockpileZones(GenerationSnapshot snapshot, Observation.ColonyProjection projection, CancellationToken ct)
    {
        projection = projection with { Facts = projection.Facts with { StockpileZones = Unknown<EquatableArray<Pol.StockpileRoleCount>>() } };
        var claims = Player.Journal.ZoneClaims(snapshot, projection.Identity.Tick, ct);
        if (!claims.TryGetValue(out var owned) || projection.Cells.Count == 0)
        {
            return projection;
        }
        return projection with { Facts = projection.Facts with { StockpileZones = Known<EquatableArray<Pol.StockpileRoleCount>>([.. Pol.PolicyFunctions.StockpileRoleCounts(StockpileZones.OwnedStockpileZones(projection, owned, null, ShelfPlans(snapshot, owned, projection, ct).Shelves))]) } };
    }

    /// <summary>
    /// Runs the stockpile review when the admin/stockpiles Need is composed, records whether it proposes work for the
    /// Need's read, and returns the shelf candidates it plans; none otherwise, or while a fact is unread.
    /// </summary>
    internal async Task<IReadOnlyList<Pol.ShelfCandidate>> ReviewStockpilesAsync(GenerationSnapshot snapshot, Observation.ColonyProjection projection, CancellationToken ct)
    {
        if (!Needs.Contains(AdminFamily.Stockpiles))
        {
            return [];
        }
        Stockpiles.SetActive(snapshot, Unknown<bool>());
        var (request, missing) = await StockpileRequestAsync(snapshot, projection, ct).ConfigureAwait(false);
        if (missing != "")
        {
            return [];
        }
        Stockpiles.SetDemand(RoundsStockpilePlanner.StockpileWorld(snapshot), request.RoomDemand);
        var review = Pol.PolicyFunctions.PlanStockpileMaintenance(request);
        Stockpiles.SetActive(snapshot, review.Known ? Known(review.Active) : Unknown<bool>());
        foreach (var e in review.Edits)
        {
            ct.ThrowIfCancellationRequested();
            Tel.Decide(RoundsStockpilePlanner.StockpileEditDecision("proposed", "", e.Zone, new Dictionary<string, object?> { ["kind"] = Wire.Name(e.Kind), ["detail"] = e.Explanation }));
        }
        return review.ShelfCandidates;
    }

    /// <summary>The stacks one Shelf holds: the load catalog's Shelf station item slots; unknown without the catalog or the station.</summary>
    private async Task<Fact<int>> ShelfStacksAsync(GenerationSnapshot snapshot, CancellationToken ct)
    {
        if (Native is not Observation.IDefinitionSource source)
        {
            return Unknown<int>();
        }
        var catalog = await source.DefinitionCatalogAsync(Boundary.Identity(snapshot), ct).ConfigureAwait(false);
        if (catalog is null || !catalog.StationCatalog().Stations.TryGetValue(ShelfDefinition, out var shelf) || shelf.Capacity.Kind != Pol.CapacityKind.Items)
        {
            return Unknown<int>();
        }
        return Known(shelf.Capacity.Slots);
    }

    /// <summary>Assembles the maintenance request; the string names the fact still unread (empty when the request is whole).</summary>
    internal async Task<(Pol.StockpileRequest Request, string Missing)> StockpileRequestAsync(GenerationSnapshot snapshot, Observation.ColonyProjection projection, CancellationToken ct)
    {
        var tick = projection.Identity.Tick;
        var claims = Player.Journal.ZoneClaims(snapshot, tick, ct);
        if (!claims.TryGetValue(out var owned))
        {
            return (new Pol.StockpileRequest(), "zone_claims");
        }
        if (projection.Cells.Count == 0)
        {
            return (new Pol.StockpileRequest(), "site_cells");
        }
        var patches = Player.Journal.StockpilePatches(snapshot, tick, ct);
        var census = await BenchCensusAsync(snapshot, projection.Identity, ct).ConfigureAwait(false);
        var benches = new Dictionary<string, bool>(census.Count);
        foreach (var row in census)
        {
            benches[row.Bench.Id] = true;
        }
        var gear = GearStore(projection);
        var protectedCells = ReservedGround(snapshot, projection, ct);
        var (plans, spent) = ShelfPlans(snapshot, owned, projection, ct);
        var request = RoundsStockpilePlanner.StockpileRequest(projection, owned, patches, plans, Known<IReadOnlyDictionary<string, bool>>(benches), StockpileBenchInputs.BenchInputs(census, projection), gear, protectedCells);
        var canBuild = Native is IRoundsBuildingSource;
        request = request with { ShelfAvailable = canBuild && RoundsStockpilePlanner.ShelfAvailable(projection) };
        request = request with { Walkways = projection.LayoutPlan.TryGetValue(out var layout) ? Pol.PolicyFunctions.PlannedWalkways(layout) : Unknown<EquatableArray<Cell>>() };
        request = request with { ShelfStacks = await ShelfStacksAsync(snapshot, ct).ConfigureAwait(false) };
        var cells = StockpileZones.CensusByCell(projection);
        var shelfSpent = new Dictionary<string, bool>();
        var shelfRecords = new List<Pol.ShelfRecord>();
        var shelfRows = new List<Pol.StockpileShelf>();
        foreach (var z in request.Zones)
        {
            var shelves = plans.GetValueOrDefault(z.Id) ?? [];
            shelfRecords.AddRange(shelves);
            shelfSpent[z.Id] = spent.GetValueOrDefault(z.Id);
            foreach (var s in shelves)
            {
                if (!StockpileZones.BuiltShelf(s))
                {
                    continue;
                }
                var (capacity, used) = Pol.PolicyFunctions.StorageStacks(s.Cells, cells);
                var shelf = new Pol.StockpileShelf { Building = s.Building, Zone = z.Id, Capacity = capacity, Used = used };
                if (patches.TryGetValue(s.Building, out var applied) && applied.Kind == StorageTargetKind.Building)
                {
                    shelf = shelf with { Patched = true };
                    shelf = shelf with { Filter = applied.Filter };
                    shelf = shelf with { Priority = applied.Priority };
                }
                shelfRows.Add(shelf);
            }
        }
        request = request with { ShelfSpent = shelfSpent };
        request = request with { ShelfRecords = shelfRecords };
        request = request with { Shelves = shelfRows };
        request = request with { Occupancy = await StorageOccupancyAsync(request, tick, ct).ConfigureAwait(false) };
        request = request with { RoomDemand = request.RoomDemand with { Yard = Pol.PolicyFunctions.YardDemand(request) } };
        request = request with { ShelfCost = RoundsStockpilePlanner.ShelfCost(projection), StoreRooms = RoundsStockpilePlanner.StoreRooms(projection, Policy.Flooring), Items = projection.Facts.Items };
        return (request, "");
    }

    /// <summary>
    /// The occupancy ring's samples over the inflow window for the request's zones and built shelves; unknown when
    /// the native cannot read it or the read fails (a store's inflow is then unknown, never zero).
    /// </summary>
    private async Task<Fact<Pol.StoreOccupancy>> StorageOccupancyAsync(Pol.StockpileRequest request, Tick tick, CancellationToken ct)
    {
        if (Native is not FacadeNativeClient reader)
        {
            return Unknown<Pol.StoreOccupancy>();
        }
        var groups = new HashSet<string>(StringComparer.Ordinal);
        foreach (var z in request.Zones)
        {
            groups.Add(z.Id);
        }
        foreach (var s in request.Shelves)
        {
            groups.Add(s.Building);
        }
        if (groups.Count == 0)
        {
            return Unknown<Pol.StoreOccupancy>();
        }
        try
        {
            return Known(await reader.ReadStorageOccupancyAsync(groups, Pol.PolicyFunctions.StoreInflowSinceHour(tick), ct).ConfigureAwait(false));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return Unknown<Pol.StoreOccupancy>();
        }
    }

    /// <summary>
    /// The shelf plans of each owned stockpile zone (ZoneShelves under the admin/stockpiles Need in
    /// <paramref name="snapshot"/>'s world), built or in flight, and whether
    /// the zone's shelf attempts are used up.
    /// </summary>
    internal (Dictionary<string, List<Pol.ShelfRecord>> Shelves, Dictionary<string, bool> Spent) ShelfPlans(GenerationSnapshot snapshot, IReadOnlyList<Sto.OwnedZone> owned, Observation.ColonyProjection projection, CancellationToken ct)
    {
        var need = StockpileNeedOwner(snapshot);
        var shelves = new Dictionary<string, List<Pol.ShelfRecord>>();
        var spent = new Dictionary<string, bool>();
        foreach (var zone in owned.Where(z => z.Kind == Domain.ZoneKind.Stockpile).Select(z => z.Id).Distinct())
        {
            var (records, next) = RoundsStockpilePlanner.ZoneShelves(Player.Journal, need, zone, projection.Facts.CurrentConstruction, ct);
            shelves[zone] = records;
            spent[zone] = next >= RoundsStockpilePlanner.MaxShelvesPerZone;
        }
        return (shelves, spent);
    }

    /// <summary>The admin/stockpiles Need's owner key in <paramref name="snapshot"/>'s world, for its Method history reads.</summary>
    internal static Sto.NeedOwner StockpileNeedOwner(GenerationSnapshot snapshot) => new(AdminFamily.Stockpiles, snapshot, []);

    /// <summary>
    /// The ground no stockpile site takes: the footprints of held building reservations and the unroofed floor
    /// inside a shell's wall ring, which belongs to the shell's own furniture until a roof stands. Every sited
    /// role reads it as the planner's protected set.
    /// </summary>
    internal List<Cell> ReservedGround(GenerationSnapshot snapshot, Observation.ColonyProjection projection, CancellationToken ct)
    {
        var held = Player.Journal.BuildingReservations(snapshot, ct);
        var protectedCells = new List<Cell>();
        foreach (var h in held)
        {
            protectedCells.AddRange(h.Footprint);
        }
        var claims = Player.Journal.ConstructionClaims(snapshot, projection.Identity.Tick, ct);
        claims.TryGetValue(out var rows);
        var unroofed = new HashSet<Cell>();
        foreach (var c in projection.Cells)
        {
            if (c.Roofed.TryGetValue(out var roofed) && !roofed)
            {
                unroofed.Add(c.Cell);
            }
        }
        foreach (var cell in RoundsShellInteriors.ShellInteriors([], rows))
        {
            if (unroofed.Contains(cell))
            {
                protectedCells.Add(cell);
            }
        }
        return protectedCells;
    }

    /// <summary>The gear stores' filters from the catalog's armor split; a catalog naming no armor fails with <see cref="Pol.NoArmorDefsException"/>.</summary>
    internal static Pol.GearStore GearStore(Observation.ColonyProjection projection) => Pol.GearStore.NewGearStore(projection.Facts.Items);

    /// <summary>The bench census (bills and recipes per bench).</summary>
    internal async Task<IReadOnlyList<GearBenchRead>> BenchCensusAsync(GenerationSnapshot snapshot, Observation.Identity expected, CancellationToken ct)
    {
        if (Native is not IRoundsWorkBenchSource native)
        {
            throw new ControlException("benchCensus: native lacks the bench census");
        }
        var rows = await BenchSource(native, expected, false).ReadGearBenchesAsync(Boundary.Identity(snapshot), ct).ConfigureAwait(false);
        return rows;
    }
}

/// <summary>
/// The admin/stockpiles step (#3304): commits the stockpile review's edits as one Method, <c>stockpile-edits</c>,
/// one action per zone under that zone's fresh CAS token: zone_cell_edit to grow or shrink, stockpile_patch to
/// retarget a zone or configure a shelf like its zone, zone_delete to delete or merge. Missing fixed-role zones are
/// one zone_create Method (<c>stockpile-create-&lt;roles&gt;</c>) admitted alone once no other edit stands. A
/// zone-edit Method still open holds the next run. Every commit is a claimed Need commit: each edit claims its zone,
/// each created zone its cells.
/// </summary>
internal sealed partial class RoundsStockpilePlanner
{
    /// <summary>The MethodId of a cycle's standing-zone edits: open work continues, a released one is issued again.</summary>
    internal static readonly MethodId EditsMethod = new("stockpile-edits");

    private readonly Rounder _reviewer;
    private readonly FacadeNativeClient _native;
    private readonly StockpileZones _zones;

    // Shells the planned storage rooms and yards; null for a source that cannot preview buildings.
    private readonly RoundsBuildingPlanner? _building;

    public RoundsStockpilePlanner(Rounder reviewer, FacadeNativeClient native)
    {
        if (reviewer is null || native is null || reviewer.Native is null)
        {
            throw new ControlException("RoundsStockpilePlanner: reviewer, native or reviewer.Native is missing");
        }
        _reviewer = reviewer;
        _native = native;
        _zones = new StockpileZones(reviewer, native);
        if (reviewer.Native is IRoundsBuildingSource source)
        {
            _building = new RoundsBuildingPlanner { Reviewer = reviewer, Native = source, Concern = AdminFamily.Stockpiles.FilingKey, Need = AdminFamily.Stockpiles, Definition = ShellWallDefinition };
        }
    }

    /// <summary>
    /// Raises the shell of the planned room module through the planned-room shell path (the storage planner's
    /// rooms, and the materials yard's ring). Handled is true when the shell was admitted or failed; false leaves
    /// the zone edits to go on this step (the room stands, its shell was tried, no space, refused or a fact is
    /// missing). Zoning is instant and needs no builder, so a room waiting on its shell never holds the stores'
    /// zones back.
    /// </summary>
    private async Task<(Verdict Verdict, bool Handled)> RaiseRoomAsync(NeedWork work, Sto.Rounds review, Observation.RoundsReading read, Pol.PlannedRole module, CancellationToken call)
    {
        if (_building is null)
        {
            return (FieldUnavailable("building_source"), false);
        }
        var (room, owed) = RoundsBuildingPlanner.PlannedRoomOwed(read.Projection, module);
        if (!owed)
        {
            return (WaitFor(Pol.Cause.MethodUsed, "stockpile_room_built"), false);
        }
        var result = await _building.ReconcileRoomAsync(work.State, review, work.Owner, read, null, new RoomReconcile { RingOnly = true, Room = room, Name = RoundsBuildingPlanner.PlannedRoomMethod(room).Value, Reason = "storage-planner room" }, call, work.Epoch).ConfigureAwait(false);
        Tel.Decide(StockpileEditDecision("proposed", result.Verdict.ToString() ?? "", Wire.Name(module), new Dictionary<string, object?> { ["kind"] = "room" }));
        return (result.Verdict, result.Verdict == BuildingReasonAdmitted);
    }

    /// <summary>
    /// The ready Need's step: a fresh read of the zones, the review's proposal on it, then the planned room, the
    /// standing zones' edits, the missing zones or the next shelf, in that order.
    /// </summary>
    internal async Task<Verdict> StepAsync(NeedWork work, CancellationToken call)
    {
        var p = _reviewer.Player;
        var state = work.State;
        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || review.Snapshot != state.Snapshot)
        {
            return BuildingReasonNoReview;
        }
        if (StockpileZones.OpenEditPlan(p.Journal, work.Owner, call))
        {
            return BuildingReasonExistingWork;
        }
        var expected = await StepScope.StepScopeAsync(_reviewer.Native, call).ConfigureAwait(false);
        if (!RoundsBuildingPlanner.RoundsBuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("stockpiles: the scope is another world or older than the review");
        }
        var claims = p.Journal.ConstructionClaims(state.Snapshot, expected.Tick, call);
        // The medicine store reads the medical beds, so the census carries them. The planned rooms are shelled from
        // the same reading, so the census carries the wall and door definitions.
        string[] definitions = [ShellWallDefinition, ShellDoorDefinition, PenFenceDefinition, PenGateDefinition, ShelfDefinition];
        var read = _reviewer.RoomsEnabled()
            ? await _reviewer.ObserveRoomsAsync((Observation.IRoundsSource)_reviewer.Native, expected, claims, definitions, call).ConfigureAwait(false)
            : await _reviewer.ObserveOwnedAsync((Observation.IRoundsSource)_reviewer.Native, expected, claims, definitions, call).ConfigureAwait(false);
        var projection = read.Projection;
        var (request, missing) = await _reviewer.StockpileRequestAsync(state.Snapshot, projection, call).ConfigureAwait(false);
        if (missing != "")
        {
            return FieldUnavailable(missing);
        }
        var proposal = Pol.PolicyFunctions.PlanStockpileMaintenance(request);
        if (!proposal.Active)
        {
            return BuildingReasonNoDeficit;
        }
        Verdict waiting = default;
        // A planned room not yet standing is raised first; while its shell waits (already tried, no space,
        // refused) the zone edits go on.
        if (proposal.Rooms.Count > 0)
        {
            var (roomVerdict, handled) = await RaiseRoomAsync(work, review, read, proposal.Rooms[0], call).ConfigureAwait(false);
            if (handled)
            {
                return roomVerdict;
            }
            waiting = roomVerdict;
        }
        if (proposal.Edits.Count == 0)
        {
            if (proposal.Shelf.Kind == Pol.ShelfStepKind.Build)
            {
                return await ShelveAsync(work, review, read, proposal.Shelf, call).ConfigureAwait(false);
            }
            return waiting;
        }
        var id = PlanIds.MintPlanId();
        var identity = Boundary.Identity(state.Snapshot);
        var actions = new List<DomainAction>();
        var creates = new List<Pol.StockpileEdit>();
        foreach (var e in proposal.Edits)
        {
            if (e.Kind == Pol.StockpileEditKind.Create)
            {
                creates.Add(e);
                continue;
            }
            if (!await _zones.EditTargetPresentAsync(identity, e, call).ConfigureAwait(false))
            {
                continue;
            }
            DomainAction action;
            try
            {
                action = StockpileZones.StockpileEditAction(new ActionId(string.Create(CultureInfo.InvariantCulture, $"{id}-{actions.Count}")), e);
            }
            catch (Exception err) when (err is not OperationCanceledException)
            {
                Tel.Decide(StockpileEditDecision("refused", "action_invalid", e.Zone, new Dictionary<string, object?> { ["kind"] = Wire.Name(e.Kind), ["error"] = err }));
                continue;
            }
            actions.Add(action);
        }
        if (actions.Count == 0 && creates.Count > 0)
        {
            // The new zones are admitted together, previewed, once the edits of standing zones are done.
            return await _zones.CreateAsync(state, work.Owner, projection, read.StartedAt, creates, call, work.Epoch).ConfigureAwait(false);
        }
        if (actions.Count == 0)
        {
            return FieldUnavailable("stockpile_edit_targets");
        }
        var plan = new PlanSpec(id, new PlanRevision(1), actions);
        p.Current(call, work.Epoch);
        var now = _reviewer.Clock.Now();
        if (p.Session.State() != state || now < read.StartedAt || now - read.StartedAt > _reviewer.MaxAge)
        {
            throw new ControlException("stockpiles: the session moved or the read took too long");
        }
        var commit = p.Journal.CommitClaimedMethod(work.Need.Id, work.Owner.Snapshot, EditsMethod, "stockpile edits", plan, call);
        if (commit.Outcome != ClaimOutcome.Admitted)
        {
            return AdminRounds.OutcomeVerdict([(commit.Outcome, commit.Conflict)]) ?? BuildingReasonExistingWork;
        }
        foreach (var e in proposal.Edits)
        {
            if (e.Kind == Pol.StockpileEditKind.Create)
            {
                continue;
            }
            Tel.Decide(StockpileEditDecision("admitted", "", e.Zone, new Dictionary<string, object?> { ["kind"] = Wire.Name(e.Kind), ["role"] = e.Role, ["plan"] = id.ToString(), ["detail"] = e.Explanation }));
        }
        return BuildingReasonAdmitted;
    }
}
