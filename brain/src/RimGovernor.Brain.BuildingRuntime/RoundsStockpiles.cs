namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>Remembers, per world, what the reviewer last learned for the planner and layout.</summary>
internal sealed class StockpileMemory
{
    private readonly object _gate = new();

    // The storage planner's latest layout demand for demandWorld: layout reads it to add the armory and wardrobe.
    private Pol.RoomDemand _demand = new();
    private string _demandWorld = "";

    // The last storage-plan site report logged, so a standing failure logs once and again when it changes or clears.
    private string _siteErr = "";

    /// <summary>Records the report and says whether a non-empty one differs from the last logged.</summary>
    public bool SiteErrChanged(Exception? err)
    {
        lock (_gate)
        {
            var text = err?.Message ?? "";
            var changed = text != "" && text != _siteErr;
            _siteErr = text;
            return changed;
        }
    }

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
}

/// <summary>The loose-weapon read the weapons role counts from.</summary>
public interface IWeaponCensus
{
    Task<(EquipRead Read, BridgeResult Result)> ReadEquipWeaponsAsync(C.Identity identity, Cell from, Cell toCell, CancellationToken ct);
}

public sealed class RoundsStockpileResult : IPlannerMethod
{
    public Verdict Verdict { get; init; }
    public PlanId Plan { get; init; }
    public int Edits { get; init; }
}

internal static partial class BuildingRuntimePackage
{
    internal static string StockpileWorld(GenerationSnapshot s) =>
        string.Create(CultureInfo.InvariantCulture, $"{s.Colony}/{s.Load}/{s.Map}");

    /// <summary>
    /// Builds the MaintainStockpiles input from the projection's planning cells (a zone's cells are those naming
    /// it; a cell whose storage-empty flag is false holds things) and the colony's stockpile claims, their
    /// settings superseded by the latest patch of each; the registered roles judge on the projection and benches.
    /// </summary>
    internal static Pol.StockpileRequest StockpileRequest(Observation.ColonyProjection projection, IReadOnlyList<Sto.OwnedZone> owned, IReadOnlyDictionary<string, Sto.AppliedStockpile>? patches, Fact<IReadOnlyDictionary<string, bool>> benches, IReadOnlyList<Pol.BenchInput> inputs, Pol.GearStore? gear, IReadOnlyList<Cell> protectedCells, Pol.AnimalFeedStore? feed)
    {
        var request = new Pol.StockpileRequest { Tick = projection.Identity.Tick, Cells = projection.Cells, Bounds = projection.Bounds, Protected = protectedCells };
        var rooms = new List<Pol.PlannedRole>();
        foreach (var module in new[] { Pol.PlannedRole.Storage, Pol.PlannedRole.Armory, Pol.PlannedRole.Wardrobe })
        {
            var (_, owedRoom) = PlannedRoomOwed(projection, module);
            if (owedRoom)
            {
                rooms.Add(module);
            }
        }
        request = request with { Rooms = rooms };
        request = request with { Zones = OwnedStockpileZones(projection, owned, patches) };
        var storage = StoreView(projection, request.Protected);
        storage = storage with { Gear = gear };
        storage = storage with { Zones = request.Zones };
        storage = storage with { Incinerator = StandingIncinerator(projection) };
        storage = storage with { BenchInputs = inputs };
        storage = storage with { Benches = benches };
        storage = storage with { Burial = BurialCensus(projection) };
        var (stands, rings) = RoundsTrainingPlanner.TrainingDemand(projection);
        storage = storage with { TrainingStands = stands, TrainingRings = rings };
        storage = storage with { AnimalFeed = feed };
        var declared = Pol.PolicyFunctions.DeclareStores(storage);
        request = request with { Stores = declared.Stores };
        request = request with { RoomDemand = declared.Apply(new Pol.RoomDemand()) };
        request = request with { SiteErr = declared.Err };
        return request;
    }

    /// <summary>
    /// The MaintainStockpiles review's edits commit as one plan per cycle, one action per zone under that zone's
    /// fresh CAS token. A telemetry row (family stockpile) records a stockpile edit proposed, admitted or refused:
    /// target the zone or role, reason the refusal code, attrs the edit kind, plan and detail.
    /// </summary>
    internal static Decision StockpileEditDecision(string verdict, string reason, string target, Dictionary<string, object?> attrs)
    {
        attrs["family"] = "stockpile";
        return new Decision { Kind = "layout_edit", Component = "layout", Verdict = verdict, Reason = reason, Target = target, Attrs = attrs };
    }

    /// <summary>The proposal's zone edits alone: the storage shells and the shelf upgrade need builders.</summary>
    internal static Pol.StockpileReview WithoutBuilds(Pol.StockpileReview proposal) =>
        proposal with { Rooms = [], Shelf = new Pol.ShelfStep() };

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
        return projection with { Facts = projection.Facts with { StockpileZones = Known<EquatableArray<Pol.StockpileRoleCount>>([.. Pol.PolicyFunctions.StockpileRoleCounts(OwnedStockpileZones(projection, owned, null))]) } };
    }

    /// <summary>
    /// Serves the MaintainStockpiles review on the projection when the method is served; otherwise the fact stays
    /// unknown and the goal is never assessed active.
    /// </summary>
    internal async Task<Observation.ColonyProjection> ReviewStockpilesAsync(GenerationSnapshot snapshot, Observation.ColonyProjection projection, CancellationToken ct)
    {
        projection = projection with { Facts = projection.Facts with { Stockpiles = Unknown<Pol.StockpileReview>() } };
        if (!MethodEnabled(MaintainStockpiles))
        {
            return projection;
        }
        var (request, missing) = await StockpileRequestAsync(snapshot, projection, ct).ConfigureAwait(false);
        if (missing != "")
        {
            return projection;
        }
        Stockpiles.SetDemand(StockpileWorld(snapshot), request.RoomDemand);
        Stockpiles.SiteErrChanged(request.SiteErr);
        var review = Pol.PolicyFunctions.PlanStockpileMaintenance(request);
        projection = projection with { Facts = projection.Facts with { Stockpiles = Known(review) } };
        foreach (var e in review.Edits)
        {
            ct.ThrowIfCancellationRequested();
            Tel.Decide(StockpileEditDecision("proposed", "", e.Zone, new Dictionary<string, object?> { ["kind"] = Wire.Name(e.Kind), ["detail"] = e.Explanation }));
        }
        return projection;
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
        var zoneGoal = new Dictionary<string, StandardId>();
        foreach (var z in owned)
        {
            zoneGoal[z.Id] = z.Owner;
        }
        var gear = await GearStoreAsync(snapshot, projection, ct).ConfigureAwait(false);
        var protectedCells = ReservedGround(snapshot, projection, ct);
        var request = BuildingRuntimePackage.StockpileRequest(projection, owned, patches, Known<IReadOnlyDictionary<string, bool>>(benches), BenchInputs(census, projection), gear, protectedCells, AnimalFeedStore(projection));
        var canBuild = Native is IRoundsBuildingSource;
        request = request with { ShelfAvailable = canBuild && ShelfAvailable(projection) };
        var shelfSpent = new Dictionary<string, bool>();
        var shelfRecords = new List<Pol.ShelfRecord>();
        var shelfRows = new List<Pol.StockpileShelf>();
        foreach (var z in request.Zones)
        {
            var (shelves, next) = ZoneShelves(Player.Journal, zoneGoal.GetValueOrDefault(z.Id), z.Id, projection.Facts.CurrentConstruction, ct);
            shelfRecords.AddRange(shelves);
            shelfSpent[z.Id] = next >= MaxShelvesPerZone;
            foreach (var s in shelves)
            {
                if (s.Building == "" || s.Open)
                {
                    continue;
                }
                var shelf = new Pol.StockpileShelf { Building = s.Building, Zone = z.Id, Cells = s.Cells.Count };
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
        return (request, "");
    }

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
        foreach (var cell in ShellInteriors([], rows))
        {
            if (unroofed.Contains(cell))
            {
                protectedCells.Add(cell);
            }
        }
        return protectedCells;
    }

    /// <summary>
    /// The serviceable gear the colony holds for the armory and wardrobe: the stored apparel the gear census
    /// counts, split into armor and clothing by the catalog (ItemFacts.Armor), and the weapons lying on the map.
    /// The weapons read is skipped once layout plans the armory, which no longer needs the count. A catalog
    /// naming no armor fails with <see cref="Pol.NoArmorDefsException"/>; an unknown stored census counts nothing.
    /// </summary>
    internal async Task<Pol.GearStore> GearStoreAsync(GenerationSnapshot snapshot, Observation.ColonyProjection projection, CancellationToken ct)
    {
        if (projection.Facts.Items.Armor.Count == 0)
        {
            throw new Pol.NoArmorDefsException();
        }
        var weapons = 0;
        if (!projection.LayoutPlan.TryGetValue(out var plan) || Pol.PolicyFunctions.GearRoomsOwed(plan, new Pol.RoomDemand { Armory = true }).Count > 0)
        {
            weapons = await LooseWeaponsAsync(snapshot, projection.Bounds, ct).ConfigureAwait(false);
        }
        IReadOnlyList<Pol.GearStock> stored = [];
        if (projection.Facts.Gear.TryGetValue(out var gear))
        {
            gear.Stored.TryGetValue(out var known);
            stored = known;
        }
        return Pol.GearStore.NewGearStore(projection.Facts.Items, stored, weapons);
    }

    /// <summary>Counts the unbiocoded weapons by trade lying on the map; a source without the read counts none.</summary>
    internal async Task<int> LooseWeaponsAsync(GenerationSnapshot snapshot, Pol.Bounds bounds, CancellationToken ct)
    {
        if (Native is not IWeaponCensus source || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return 0;
        }
        var (read, _) = await source.ReadEquipWeaponsAsync(Boundary.Identity(snapshot), default, new Cell(bounds.Width - 1, bounds.Height - 1), ct).ConfigureAwait(false);
        try
        {
            Boundary.Context(read.Context, snapshot);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ControlException("looseWeapons: " + e.Message);
        }
        var catalog = await PawnCatalogAsync(Native, Boundary.Identity(snapshot), ct).ConfigureAwait(false);
        var count = 0;
        foreach (var w in read.Targets)
        {
            if (w.Biocoded)
            {
                continue;
            }
            var facts = catalog?.WeaponOf(w.Definition) ?? new Pol.WeaponDef();
            if (Pol.PolicyFunctions.ClassifyWeapon(facts.ByTrade, facts.Ranged, facts.Melee) != Pol.WeaponClass.Makeshift)
            {
                count++;
            }
        }
        return count;
    }

    /// <summary>The bench census (bills and recipes per bench).</summary>
    internal async Task<IReadOnlyList<GearBenchRead>> BenchCensusAsync(GenerationSnapshot snapshot, Observation.Identity expected, CancellationToken ct)
    {
        if (Native is not IRoundsWorkBenchSource native)
        {
            throw new ControlException("benchCensus: native lacks the bench census");
        }
        var (rows, _) = await BenchSource(native, expected, false).ReadGearBenchesAsync(Boundary.Identity(snapshot), ct).ConfigureAwait(false);
        return rows;
    }

    /// <summary>
    /// The herds' feed and the barn's sleeping spot shape the People animal store declares the barn feed
    /// stockpiles from; null while the animal census, the race catalog or the spot's size is unread.
    /// </summary>
    internal static Pol.AnimalFeedStore? AnimalFeedStore(Observation.ColonyProjection projection)
    {
        var upkeep = projection.Facts.AnimalUpkeep;
        if (!upkeep.Animals.TryGetValue(out var animals))
        {
            return null;
        }
        HerdFurniture furniture;
        bool usable;
        try
        {
            (furniture, usable) = HerdFurnitureOf(projection);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
        if (!usable)
        {
            return null;
        }
        var feed = Pol.PolicyFunctions.AnimalFeedFilterOf(animals, upkeep.AnimalRaces);
        if (feed.Count == 0)
        {
            return null;
        }
        return new Pol.AnimalFeedStore { Feed = feed, Spot = furniture.Spot };
    }
}

/// <summary>
/// Commits the MaintainStockpiles review's edits as one plan per cycle, one action per zone under that zone's
/// fresh CAS token: zone_cell_edit to grow or shrink, stockpile_patch to retarget a zone or configure a shelf like
/// its zone, zone_delete to delete or merge. A missing fixed-role zone is a zone_create admitted alone once no
/// other edit stands. A plan still open holds the next cycle.
/// </summary>
public sealed partial class RoundsStockpilePlanner
{
    private readonly Rounder _reviewer;
    private readonly IRoundsStockpileSource _native;
    private readonly StockpileZones _zones;

    // Shells the planned armory and wardrobe; null for a source that cannot preview buildings.
    private readonly RoundsBuildingPlanner? _building;

    public RoundsStockpilePlanner(Rounder reviewer, IRoundsStockpileSource native)
    {
        if (reviewer is null || native is null || reviewer.Native is null)
        {
            throw new ControlException("NewRoundsStockpilePlanner: reviewer == nil || native == nil || reviewer.native == nil");
        }
        _reviewer = reviewer;
        _native = native;
        _zones = new StockpileZones(reviewer, native);
        if (reviewer.Native is IRoundsBuildingSource source)
        {
            _building = new RoundsBuildingPlanner { Reviewer = reviewer, Native = source, Concern = MaintainStockpiles, Definition = ShellWallDefinition };
        }
    }

    /// <summary>
    /// Raises the shell of the planned room module through the planned-room shell path (the storage planner's
    /// rooms, and the materials yard's ring). Handled is true when the shell was admitted or failed; false leaves
    /// the zone edits to go on this step (the room stands, its shell was tried this Episode, no space, refused or
    /// a fact is missing). Zoning is instant and needs no builder, so a room waiting on its shell never holds the
    /// stores' zones back.
    /// </summary>
    private async Task<(RoundsStockpileResult Result, bool Handled)> RaiseRoomAsync(ControlState state, Sto.Rounds review, Sto.StandardState goal, Observation.RoundsReading read, Pol.PlannedRole module, CancellationToken call, CancellationToken epoch)
    {
        if (_building is null)
        {
            return (new RoundsStockpileResult { Verdict = FieldUnavailable("building_source") }, false);
        }
        var (room, owed) = PlannedRoomOwed(read.Projection, module);
        if (!owed)
        {
            return (new RoundsStockpileResult { Verdict = WaitFor(Pol.Cause.MethodUsed, "stockpile_room_built") }, false);
        }
        var result = await _building.ReconcileRoomAsync(state, review, goal, read, null, new RoomReconcile { RingOnly = true, Room = room, Name = PlannedRoomMethod(room).Value, Reason = "storage-planner room" }, call, epoch).ConfigureAwait(false);
        Tel.Decide(StockpileEditDecision("proposed", result.Verdict.ToString() ?? "", Wire.Name(module), new Dictionary<string, object?> { ["kind"] = "room" }));
        return (new RoundsStockpileResult { Verdict = result.Verdict }, result.Verdict == BuildingReasonAdmitted);
    }

    /// <summary>Whether the initial shelter is owed under review, the reading the priority gate holds planners on.</summary>
    private bool ShelterOwedUnder(Sto.Rounds review, CancellationToken call)
    {
        var standards = new List<Sto.StandardState>();
        foreach (var binding in review.Standards)
        {
            if (binding.Concern != MaintainHousing)
            {
                continue;
            }
            standards.Add(_reviewer.Player.Journal.LoadStandard(binding.Standard, call));
        }
        return ClockPlannerCatalog.ShelterOwed(new Sto.RoundsResult { Review = review, Standards = standards });
    }

    internal async Task<RoundsStockpileResult> StepAsync(StepArbiter? _, CancellationToken call, CancellationToken epoch)
    {
        var p = _reviewer.Player;
        var state = p.Session.State();
        if (!state.Enabled)
        {
            return new RoundsStockpileResult { Verdict = BuildingReasonDisabled };
        }
        if (!state.ObservationKnown)
        {
            throw new ControlException("step: !state.ObservationKnown");
        }
        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || review.Snapshot != state.Snapshot)
        {
            return new RoundsStockpileResult { Verdict = BuildingReasonNoReview };
        }
        var (goal, workable) = p.Journal.Workable(review, MaintainStockpiles, call);
        if (!workable || goal is null)
        {
            return new RoundsStockpileResult { Verdict = BuildingReasonNoDeficit };
        }
        if (OpenEditPlan(p.Journal, goal, call))
        {
            return new RoundsStockpileResult { Verdict = BuildingReasonExistingWork };
        }
        var expected = await StepScopeAsync(_reviewer.Native, call).ConfigureAwait(false);
        if (!RoundsBuildingBoundary(expected, state.Snapshot, review.Tick))
        {
            throw new ControlException("step: !roundsBuildingBoundary(expected, state.Snapshot, review.Tick)");
        }
        var claims = p.Journal.ConstructionClaims(state.Snapshot, expected.Tick, call);
        // The medicine store reads the medical beds, so the census carries them. The gear rooms are shelled from
        // the same reading, so the census carries the wall and door definitions.
        string[] definitions = [ShellWallDefinition, ShellDoorDefinition, PenFenceDefinition, PenGateDefinition, ShelfDefinition];
        var read = _reviewer.RoomsEnabled()
            ? await _reviewer.ObserveRoomsAsync((Observation.IRoundsSource)_reviewer.Native, expected, claims, definitions, call).ConfigureAwait(false)
            : await _reviewer.ObserveOwnedAsync((Observation.IRoundsSource)_reviewer.Native, expected, claims, definitions, call).ConfigureAwait(false);
        var projection = read.Projection;
        var (request, missing) = await _reviewer.StockpileRequestAsync(state.Snapshot, projection, call).ConfigureAwait(false);
        if (missing != "")
        {
            return new RoundsStockpileResult { Verdict = FieldUnavailable(missing) };
        }
        var proposal = Pol.PolicyFunctions.PlanStockpileMaintenance(request);
        if (!proposal.Active)
        {
            return new RoundsStockpileResult { Verdict = BuildingReasonNoDeficit };
        }
        // While the initial shelter is owed the builders are its: zone edits are instant and go on, but no storage
        // shell or shelf is raised.
        Verdict waiting = default;
        if (ShelterOwedUnder(review, call))
        {
            proposal = WithoutBuilds(proposal);
            waiting = BuildingReasonShelterPending;
        }
        // A planned room not yet standing is raised first; while its shell waits (already tried, no space,
        // refused) the zone edits go on.
        if (proposal.Rooms.Count > 0)
        {
            var (roomResult, handled) = await RaiseRoomAsync(state, review, goal, read, proposal.Rooms[0], call, epoch).ConfigureAwait(false);
            if (handled)
            {
                return roomResult;
            }
            waiting = roomResult.Verdict;
        }
        if (proposal.Edits.Count == 0)
        {
            if (proposal.Shelf.Kind == Pol.ShelfStepKind.Build)
            {
                return await ShelveAsync(state, review, goal, read, proposal.Shelf, call, epoch).ConfigureAwait(false);
            }
            return new RoundsStockpileResult { Verdict = waiting };
        }
        var tick = projection.Identity.Tick;
        var method = new MethodId(string.Create(CultureInfo.InvariantCulture, $"stockpiles-{tick.Value}"));
        var id = PlanIds.MintPlanId();
        try
        {
            p.Journal.LoadMethod(goal.Standard.Id, goal.Standard.Episode, method, call);
            return new RoundsStockpileResult { Verdict = WaitFor(Pol.Cause.MethodUsed, "stockpile_edits") };
        }
        catch (Sto.NotFoundException)
        {
        }
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
                action = StockpileEditAction(new ActionId(string.Create(CultureInfo.InvariantCulture, $"{id}-{actions.Count}")), e);
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
            return await _zones.CreateAsync(state, goal, projection, read.StartedAt, creates, call, epoch).ConfigureAwait(false);
        }
        if (actions.Count == 0)
        {
            return new RoundsStockpileResult { Verdict = FieldUnavailable("stockpile_edit_targets") };
        }
        var plan = new PlanSpec(id, new PlanRevision(1), actions);
        p.Current(call, epoch);
        var now = _reviewer.Clock.Now();
        if (p.Session.State() != state || now < read.StartedAt || now - read.StartedAt > _reviewer.MaxAge)
        {
            throw new ControlException("step: p.session.State() != state || now.Before(read.StartedAt) || now.Sub(read.StartedAt) > r.reviewer.maxAge");
        }
        p.Journal.CommitMethod(goal.Standard.Id, goal.Revision, method, plan, call);
        foreach (var e in proposal.Edits)
        {
            if (e.Kind == Pol.StockpileEditKind.Create)
            {
                continue;
            }
            Tel.Decide(StockpileEditDecision("admitted", "", e.Zone, new Dictionary<string, object?> { ["kind"] = Wire.Name(e.Kind), ["role"] = e.Role, ["plan"] = id.ToString(), ["detail"] = e.Explanation }));
        }
        return new RoundsStockpileResult { Verdict = BuildingReasonAdmitted, Plan = id, Edits = actions.Count };
    }
}
