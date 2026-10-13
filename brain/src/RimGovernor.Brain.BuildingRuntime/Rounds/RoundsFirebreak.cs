using System.Globalization;

namespace RimGovernor.Brain.BuildingRuntime;

public interface IRoundsFirebreakSource
{
    BridgeDefenseSite ReadDefenseSite(C.Identity identity, BridgeCellRect rect, CancellationToken ct);
}

/// <summary>Remembers the firebreak review of the tick so the step can act on it.</summary>
internal sealed class FirebreakMemory
{
    private readonly object _mu = new();
    private string _world = "";
    private Dictionary<Cell, Tick>? _dwell;
    private Tick _tick;
    private FirebreakWork _work = new();
    private Dictionary<Cell, CoverClearance>? _ruins;
    private List<Cell>? _pave;

    internal FirebreakMemory(IRoundsFirebreakSource native)
    {
        Native = native;
    }

    internal IRoundsFirebreakSource Native { get; }

    private void Enter(string world)
    {
        if (_world != world)
        {
            _world = world;
            _dwell = null;
            _work = new FirebreakWork();
            _ruins = null;
            _pave = null;
        }
    }

    internal List<Cell> Pave()
    {
        lock (_mu)
        {
            return _pave?.ToList() ?? [];
        }
    }

    internal (FirebreakWork Work, Dictionary<Cell, CoverClearance>? Ruins, bool Ok) Take(string world, Tick tick)
    {
        lock (_mu)
        {
            if (_world != world || _tick != tick)
            {
                return (new FirebreakWork(), null, false);
            }
            return (_work, _ruins, true);
        }
    }

    internal Fact<bool> Review(CancellationToken ct, C.Identity identity, GenerationSnapshot current, ColonyProjection projection, ColonyStage stage, IReadOnlySet<Cell> busy)
    {
        if (stage == ColonyStage.Foothold)
        {
            return Known(false);
        }
        var world = StockpileWorld(current);
        var tick = projection.Identity.Tick;
        var request = FirebreakRequest(projection);
        var ring = PolicyApi.FirebreakRing(request);
        if (!ring.TryGetValue(out var cells))
        {
            return Unknown<bool>();
        }
        var flooringKnown = projection.Facts.Upkeep.Flooring.TryGetValue(out var flooring);
        if (!flooringKnown)
        {
            stage = (ColonyStage)Math.Min((int)stage, (int)ColonyStage.Stable);
        }
        request.Stage = Known(stage);
        var style = FloorStyle(projection);
        if (style is not null)
        {
            request.Floor = style(RoomRole.None).Floor;
        }
        request.Stock = projection.Resources;
        request.Claimed = Known(FirebreakClaimed(projection));
        request.Floors = new Dictionary<string, FloorDefinition>(StringComparer.Ordinal);
        foreach (var d in projection.Definitions)
        {
            request.Floors[d.Name] = new FloorDefinition
            {
                Available = d.Available,
                Terrain = d.Terrain,
                Cleanliness = d.Cleanliness,
                Beauty = d.Beauty,
                Flammability = d.Flammability,
                PathCost = d.PathCost,
                Costs = d.Costs,
                WorkToBuild = d.WorkToBuild,
            };
        }
        request.Ground = new Dictionary<Cell, Fact<FirebreakGround>>();
        var site = new Dictionary<Cell, BridgeDefenseCell>();
        foreach (var tile in FirebreakTiles(cells))
        {
            var rect = tile with
            {
                Max = new Cell(Math.Min(tile.Min.X + BuildingRuntimePackage.FirebreakTile, projection.Bounds.Width) - 1, Math.Min(tile.Min.Z + BuildingRuntimePackage.FirebreakTile, projection.Bounds.Height) - 1),
            };
            var read = Native.ReadDefenseSite(identity, rect, ct);
            try
            {
                BoundaryApi.Context(read.Context, current);
            }
            catch (ControlException)
            {
                throw new ControlException("firebreak review: defense site context");
            }
            if (new Tick(read.Context.Tick) < tick)
            {
                throw new ControlException("firebreak review: defense site context");
            }
            foreach (var cell in read.Cells)
            {
                site[cell.Cell] = cell;
                request.Ground[cell.Cell] = Known(FirebreakGroundOf(cell));
            }
        }
        Dictionary<Cell, Tick>? dwell;
        lock (_mu)
        {
            Enter(world);
            dwell = _dwell;
        }
        var (fact, next) = PolicyApi.PlanFirebreak(request, dwell);
        if (!fact.TryGetValue(out var plan))
        {
            return Unknown<bool>();
        }
        var cut = plan.Cells.Where(c => c.Treatment == FirebreakTreatment.Cut).Select(c => c.Cell).ToList();
        var standing = StandingPlants(projection.Cells, cut);
        var open = new HashSet<Cell>();
        var ruins = new Dictionary<Cell, CoverClearance>();
        foreach (var cell in plan.Deconstruct)
        {
            var cover = site.TryGetValue(cell, out var sc) ? sc.Cover : null;
            if (cover is null || cover.Designated || cover.Designation() != CoverClearanceKind.Deconstruct)
            {
                continue;
            }
            var clearance = DomainApi.NewCoverClearance(cover.ThingId, cover.DefName, cover.Designation(), cell);
            open.Add(cell);
            ruins[cell] = clearance;
        }
        var work = PolicyApi.FirebreakOwed(plan, standing, open);
        var pave = new List<Cell>();
        if (flooringKnown)
        {
            foreach (var cell in plan.Cells)
            {
                if (cell.Treatment == FirebreakTreatment.Pave
                    && site.TryGetValue(cell.Cell, out var sc)
                    && flooring.Terrains.TryGetValue(sc.Terrain, out var terrain) && terrain.Natural
                    && !busy.Contains(cell.Cell))
                {
                    pave.Add(cell.Cell);
                }
            }
        }
        var dir = Environment.GetEnvironmentVariable(SnapshotApi.DirEnv);
        if (!string.IsNullOrEmpty(dir))
        {
            var f = SnapshotApi.NewFirebreak(request, dwell, MapCells(standing), MapCells(open));
            f.Recorded = string.Create(CultureInfo.InvariantCulture, $"colony {current.Colony} load {current.Load} map {current.Map} tick {tick}");
            f.Snapshot = current;
            try
            {
                SnapshotApi.RecordFirebreak(dir, f);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                ClockEvent(ct, "firebreak", "snapshot", "firebreak snapshot not recorded: " + e.Message);
            }
        }
        lock (_mu)
        {
            Enter(world);
            _dwell = next;
            _tick = tick;
            _work = work;
            _ruins = ruins;
            _pave = pave;
        }
        return Known(work.Owed());
    }

    private static HashSet<Cell> StandingPlants(IReadOnlyList<SiteCell> cells, IReadOnlyList<Cell> cut)
    {
        var want = new HashSet<Cell>(cut);
        var standing = new HashSet<Cell>();
        foreach (var cell in cells)
        {
            if (!want.Contains(cell.Cell))
            {
                continue;
            }
            foreach (var thing in cell.Things)
            {
                if (thing.Category == ThingCategory.Plant && !thing.Has(ThingFlag.Designated))
                {
                    standing.Add(cell.Cell);
                    break;
                }
            }
        }
        return standing;
    }

    private static List<Cell> MapCells(IEnumerable<Cell> set) => set.ToList();

    private static IEnumerable<BridgeCellRect> FirebreakTiles(IEnumerable<Cell> cells)
    {
        var held = new HashSet<Cell>();
        foreach (var cell in cells)
        {
            var key = new Cell(cell.X / BuildingRuntimePackage.FirebreakTile, cell.Z / BuildingRuntimePackage.FirebreakTile);
            if (!held.Add(key))
            {
                continue;
            }
            yield return new BridgeCellRect { Min = new Cell(key.X * BuildingRuntimePackage.FirebreakTile, key.Z * BuildingRuntimePackage.FirebreakTile) };
        }
    }

    private static Dictionary<Resource, long> FirebreakClaimed(ColonyProjection projection)
    {
        var claimed = new Dictionary<Resource, long>();
        projection.Facts.ConstructionClaims.TryGetValue(out var claims);
        foreach (var claim in claims ?? [])
        {
            if (!projection.Definition(claim.Building.Definition, out var d))
            {
                continue;
            }
            IReadOnlyList<Amount> costs;
            if (!d.CostsMadeOf(claim.Building.Stuff, out costs))
            {
                try
                {
                    costs = d.UnstockedStuffChoice(ObservationApi.LowestFlammability).Costs;
                }
                catch (DomainException)
                {
                    continue;
                }
            }
            foreach (var a in costs)
            {
                claimed[a.Resource] = claimed.GetValueOrDefault(a.Resource) + a.Count;
            }
        }
        return claimed;
    }

    private static FirebreakGround FirebreakGroundOf(BridgeDefenseCell cell)
    {
        if (cell.Fogged || cell.NaturalRock)
        {
            return FirebreakGround.NaturalRock;
        }
        if (cell.Terrain.Contains("Water", StringComparison.Ordinal))
        {
            return FirebreakGround.Water;
        }
        if (cell.EdificeDefName == "")
        {
            return FirebreakGround.Open;
        }
        if (cell.PlayerOwned)
        {
            return FirebreakGround.PlayerEdifice;
        }
        if (cell.EdificeStuff == "WoodLog")
        {
            return FirebreakGround.WoodenRuin;
        }
        return FirebreakGround.StoneRuin;
    }

    private static FirebreakRequest FirebreakRequest(ColonyProjection projection) => BuildingRuntimePackage.FirebreakRequest(projection);

    private static string StockpileWorld(GenerationSnapshot current) => BuildingRuntimePackage.StockpileWorld(current);

    private static Func<RoomRole, (FloorChoice Floor, bool Ok)>? FloorStyle(ColonyProjection projection) => BuildingRuntimePackage.FloorStyle(projection);
}

public sealed class RoundsFirebreakResult
{
    public Verdict Verdict { get; init; }

    public PlanId Plan { get; init; }
}

/// <summary>Cuts the plants and clears the ruins of the firebreak ring around the colony.</summary>
public sealed class RoundsFirebreakPlanner
{
    private readonly Rounder _reviewer;
    private readonly FirebreakMemory _memory;

    public RoundsFirebreakPlanner(Rounder reviewer, IRoundsFirebreakSource native)
    {
        if (reviewer is null || native is null || !reviewer.MethodEnabled(PolicyApi.MaintainFirebreak))
        {
            throw new ControlException("NewRoundsFirebreakPlanner: reviewer == nil || native == nil || !reviewer.methodEnabled(policy.MaintainFirebreak)");
        }
        _reviewer = reviewer;
        _memory = new FirebreakMemory(native);
        reviewer.Firebreak = _memory;
    }

    public List<Cell> Pave() => _memory.Pave();

    internal RoundsFirebreakResult Step(CancellationToken call, CancellationToken epoch, StepArbiter arbiter)
    {
        var p = _reviewer.Player;
        var state = p.Session.State();
        if (!state.Enabled)
        {
            return new RoundsFirebreakResult { Verdict = BuildingReasonDisabled };
        }
        if (!state.ObservationKnown || !state.Snapshot.IsValid)
        {
            throw new ControlException("step: !state.ObservationKnown || state.Snapshot.Validate() != nil");
        }
        var review = p.Journal.LoadRounds(call);
        if (!review.Enabled || review.Snapshot != state.Snapshot)
        {
            return new RoundsFirebreakResult { Verdict = BuildingReasonNoReview };
        }
        var (goal, workable) = p.Journal.Workable(review, PolicyApi.MaintainFirebreak, call);
        if (!workable)
        {
            return new RoundsFirebreakResult { Verdict = BuildingReasonNoDeficit };
        }
        foreach (var method in goal.Methods)
        {
            var plan0 = p.Journal.LoadPlan(method.Plan, call);
            if (StoreApi.PlanOpen(plan0))
            {
                return new RoundsFirebreakResult { Verdict = BuildingReasonExistingWork };
            }
        }
        var (work, ruins, ok) = _memory.Take(StockpileWorld(state.Snapshot), review.Tick);
        if (!ok)
        {
            return new RoundsFirebreakResult { Verdict = BuildingReasonNoReview };
        }
        if (!work.Owed())
        {
            return new RoundsFirebreakResult { Verdict = WaitFor(PolicyApi.CauseMethodUsed, "firebreak_work") };
        }
        var (verdict, admitted) = AdmitSubject(call, p.Journal, FirebreakPrefix, StandardMethodPlans(goal.History, goal.Standard.Episode, FirebreakPrefix), state.Snapshot);
        if (!admitted)
        {
            return new RoundsFirebreakResult { Verdict = verdict };
        }
        var actions = FirebreakActions(PlanIds.MintPlanId(), work, ruins ?? []);
        var plan = DomainApi.NewPlan(actions.Id, 1, actions.Actions);
        p.Current(call, epoch);
        if (p.Session.State() != state)
        {
            throw new ControlException("step: p.session.State() != state");
        }
        var methodId = NextMethodId(FirebreakPrefix, HistoryMethodIds(goal.History, goal.Standard.Episode));
        p.Journal.CommitMethodReason(goal.Standard.Id, goal.Revision, methodId, string.Create(CultureInfo.InvariantCulture, $"firebreak cut {actions.Cut} cells, deconstruct {actions.Ruins} ruins"), plan, call);
        return new RoundsFirebreakResult { Verdict = BuildingReasonAdmitted, Plan = actions.Id };
    }
}

internal sealed class FirebreakPlan
{
    internal PlanId Id { get; init; }

    internal List<DomainAction> Actions { get; } = [];

    internal int Cut { get; set; }

    internal int Ruins { get; set; }
}

internal static partial class BuildingRuntimePackage
{
    internal const string FirebreakPrefix = "firebreak-";
    internal const int FirebreakTile = 32;

    internal static FirebreakRequest FirebreakRequest(ColonyProjection projection)
    {
        var farms = new HashSet<string>(projection.Farms.Select(f => f.Id), StringComparer.Ordinal);
        var zones = new List<Cell>();
        foreach (var cell in projection.Cells)
        {
            if (cell.ZoneId.TryGetValue(out var id) && farms.Contains(id))
            {
                zones.Add(cell.Cell);
            }
        }
        var f = projection.Facts;
        var r = new FirebreakRequest
        {
            Bounds = Known(projection.Bounds),
            Construction = f.CurrentConstruction,
            Claims = f.ConstructionClaims,
            Home = f.HomeCoverage,
            GrowingZones = Known<IReadOnlyList<Cell>>(zones),
            Now = projection.Identity.Tick,
        };
        IReadOnlyList<Cell> planned = [];
        if (projection.LayoutPlan.TryGetValue(out var plan))
        {
            planned = LayoutFootprint(plan);
        }
        r.Planned = Known(planned);
        return r;
    }

    internal static List<Cell> LayoutFootprint(LayoutPlan plan)
    {
        var output = new List<Cell>();
        foreach (var room in plan.Rooms)
        {
            var inner = room.Region.Bounds();
            for (var x = inner.X - 1; x <= inner.X + inner.Width; x++)
            {
                for (var z = inner.Z - 1; z <= inner.Z + inner.Height; z++)
                {
                    output.Add(new Cell(x, z));
                }
            }
        }
        return output;
    }

    internal static HashSet<Cell> FirebreakBusy(IReadOnlyList<PlanState> plans, GenerationSnapshot current, IReadOnlyDictionary<PlanId, ulong> player)
    {
        var busy = new HashSet<Cell>();
        RoundsOpenActions(plans, current, player, a =>
        {
            if (a.Building(out var b))
            {
                busy.Add(b.Cell);
            }
        });
        return busy;
    }

    internal static FirebreakPlan FirebreakActions(PlanId id, FirebreakWork work, IReadOnlyDictionary<Cell, CoverClearance> ruins)
    {
        var output = new FirebreakPlan { Id = id };
        if (work.Cut.Count > 0)
        {
            var cut = DomainApi.NewAreaPlantCut(work.Cut);
            output.Actions.Add(DomainAction.NewAreaPlantCutAction(new ActionId($"{id}-cut"), cut));
            output.Cut = work.Cut.Count;
        }
        for (var i = 0; i < work.Deconstruct.Count; i++)
        {
            if (output.Ruins == MaxDefenseCoverBatch)
            {
                break;
            }
            if (!ruins.TryGetValue(work.Deconstruct[i], out var clearance))
            {
                continue;
            }
            output.Actions.Add(DomainAction.NewCoverClearanceAction(new ActionId(string.Create(CultureInfo.InvariantCulture, $"{id}-ruin-{i}")), clearance));
            output.Ruins++;
        }
        return output;
    }
}
