using System.Globalization;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>Remembers the firebreak review of the tick so the step can act on it.</summary>
internal sealed class FirebreakMemory
{
    private readonly object _mu = new();
    private string _world = "";
    private IReadOnlyDictionary<Cell, Tick>? _dwell;
    private Tick _tick;
    private FirebreakWork _work = new();
    private Dictionary<Cell, CoverClearance>? _ruins;
    private List<Cell>? _pave;

    internal FirebreakMemory(FacadeNativeClient native)
    {
        Native = native;
    }

    internal FacadeNativeClient Native { get; }

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

    /// <summary>
    /// Reviews the ring and records the work it leaves. An input the colony has not read leaves the tick unreviewed:
    /// <see cref="Take"/> then reports it and the layout read raises <see cref="RequiredFactException"/>, so the shared
    /// review is not failed by the one planner that needs the ring.
    /// </summary>
    internal async Task ReviewAsync(C.Identity identity, GenerationSnapshot current, ColonyProjection projection, IReadOnlySet<Cell> busy, CancellationToken ct)
    {
        try
        {
            await ReviewRingAsync(identity, current, projection, busy, ct).ConfigureAwait(false);
        }
        catch (RequiredFactException e)
        {
            ClockEvents.Event("firebreak", "review", "firebreak ring unreviewed: " + e.Message);
        }
    }

    private async Task ReviewRingAsync(C.Identity identity, GenerationSnapshot current, ColonyProjection projection, IReadOnlySet<Cell> busy, CancellationToken ct)
    {
        var world = StockpileWorld(current);
        var tick = projection.Identity.Tick;
        var footprint = FirebreakPlan.Footprint(projection);
        var cells = FirebreakRingRules.FirebreakRing(footprint);
        if (!projection.Facts.Upkeep.Flooring.TryGetValue(out var flooring))
        {
            throw new RequiredFactException("flooring census");
        }
        if (!projection.Resources.TryGetValue(out var stock))
        {
            throw new RequiredFactException("colony stock");
        }
        var style = FloorStyle(projection);
        var floorName = style is null ? "" : style(RoomRole.None).Floor;
        var floors = new Dictionary<string, FloorDefinition>(StringComparer.Ordinal);
        foreach (var d in projection.Definitions)
        {
            floors[d.Name] = new FloorDefinition
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

        var ground = new Dictionary<Cell, FirebreakGround>();
        var site = new Dictionary<Cell, O.DefenseCell.Seen>();
        // Every tile is asked in one frame (the game-thread pump drains the queued reads together) instead of one frame each.
        var tiles = FirebreakTiles(cells).Select(tile => tile with
        {
            Max = new Cell(Math.Min(tile.Min.X + FirebreakPlan.FirebreakTile, projection.Bounds.Width) - 1, Math.Min(tile.Min.Z + FirebreakPlan.FirebreakTile, projection.Bounds.Height) - 1),
        }).ToList();
        var reads = await Task.WhenAll(tiles.Select(rect => Native.ReadDefenseSiteAsync(identity, rect, ct))).ConfigureAwait(false);
        foreach (var read in reads)
        {
            try
            {
                Boundary.Context(read.Context, current);
            }
            catch (ControlException)
            {
                throw new ControlException("firebreak review: defense site context");
            }
            if (new Tick(read.Context?.Tick ?? 0) < tick)
            {
                throw new ControlException("firebreak review: defense site context");
            }
            foreach (var cell in read.Cells)
            {
                if (cell is O.DefenseCell.Seen seen)
                {
                    site[cell.Cell] = seen;
                }
                ground[cell.Cell] = FirebreakGroundOf(cell);
            }
        }
        var request = new FirebreakRequest
        {
            Footprint = footprint,
            Planned = FirebreakPlan.PlannedFootprint(projection),
            Ground = ground,
            Now = tick,
            Floors = floors,
            Stock = stock,
            Claimed = FirebreakClaimed(projection, footprint.Claims),
            Floor = floorName.Length == 0 ? null : floorName,
        };
        IReadOnlyDictionary<Cell, Tick> dwell;
        lock (_mu)
        {
            Enter(world);
            dwell = _dwell ?? new Dictionary<Cell, Tick>();
        }
        var (plan, next) = FirebreakRules.PlanFirebreak(request, dwell);
        var cut = plan.Cells.Where(c => c.Treatment == FirebreakTreatment.Cut).Select(c => c.Cell).ToList();
        var standing = StandingPlants(projection.Cells, cut);
        var open = new HashSet<Cell>();
        var ruins = new Dictionary<Cell, CoverClearance>();
        foreach (var cell in plan.Deconstruct)
        {
            var cover = site.GetValueOrDefault(cell)?.Cover;
            if (cover is null || cover.Designated || cover.Designation() != CoverClearanceDesignation.Deconstruct)
            {
                continue;
            }
            var clearance = new CoverClearance(cover.ThingId, cover.DefName, CoverClearanceDesignation.Deconstruct, cell);
            open.Add(cell);
            ruins[cell] = clearance;
        }
        var work = FirebreakConcernRules.FirebreakOwed(plan, standing, open);
        var pave = new List<Cell>();
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
        var dir = Environment.GetEnvironmentVariable(SnapshotRecorder.DirEnv);
        if (!string.IsNullOrEmpty(dir))
        {
            var f = SnapshotFirebreakReplay.NewFirebreak(request, dwell, MapCells(standing), MapCells(open));
            f.Recorded = string.Create(CultureInfo.InvariantCulture, $"colony {current.Colony} load {current.Load} map {current.Map} tick {tick.Value}");
            f.Snapshot = current;
            try
            {
                SnapshotFirebreakReplay.RecordFirebreak(dir, f);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                ClockEvents.Event("firebreak", "snapshot", "firebreak snapshot not recorded: " + e.Message);
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
                if (thing.Category == ThingCategory.Plant && !thing.Has(ThingMarks.Designated))
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
            var key = new Cell(cell.X / FirebreakPlan.FirebreakTile, cell.Z / FirebreakPlan.FirebreakTile);
            if (!held.Add(key))
            {
                continue;
            }
            yield return new BridgeCellRect { Min = new Cell(key.X * FirebreakPlan.FirebreakTile, key.Z * FirebreakPlan.FirebreakTile) };
        }
    }

    private static Dictionary<Resource, long> FirebreakClaimed(ColonyProjection projection, EquatableArray<ConstructionClaim> claims)
    {
        var claimed = new Dictionary<Resource, long>();
        foreach (var claim in claims)
        {
            if (claim.Building is null || !projection.TryDefinition(claim.Building.Definition, out var d))
            {
                continue;
            }
            IReadOnlyList<Amount> costs;
            if (!d.TryCostsMadeOf(claim.Building.Stuff, out costs))
            {
                try
                {
                    costs = d.UnstockedStuffChoice(StuffCriterion.LowestFlammability).Costs;
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

    private static FirebreakGround FirebreakGroundOf(O.DefenseCell cell)
    {
        if (cell is not O.DefenseCell.Seen seen || seen.Edifice is { NaturalRock: true })
        {
            return FirebreakGround.NaturalRock;
        }
        if (seen.Terrain.Contains("Water", StringComparison.Ordinal))
        {
            return FirebreakGround.Water;
        }
        if (seen.Edifice is not { } edifice)
        {
            return FirebreakGround.Open;
        }
        if (edifice.PlayerOwned)
        {
            return FirebreakGround.PlayerEdifice;
        }
        if (edifice.Stuff == "WoodLog")
        {
            return FirebreakGround.WoodenRuin;
        }
        return FirebreakGround.StoneRuin;
    }

    private static string StockpileWorld(GenerationSnapshot current) => RoundsStockpilePlanner.StockpileWorld(current);

    private static Func<RoomRole, (string Floor, bool Ok)>? FloorStyle(ColonyProjection projection) => RoundsTierStyle.FloorStyle(projection);
}

internal sealed partial class FirebreakPlan
{
    internal PlanId Id { get; init; }

    internal List<DomainAction> Actions { get; } = [];

    internal int Cut { get; set; }

    internal int Ruins { get; set; }
}

partial class FirebreakPlan
{
    internal const string FirebreakPrefix = "firebreak-";
    internal const int FirebreakTile = 32;

    /// <summary>The base the ring surrounds; the construction census, the claims and the home coverage must all be read, or <see cref="RequiredFactException"/>.</summary>
    internal static FirebreakFootprint Footprint(ColonyProjection projection)
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
        if (!f.ConstructionClaims.TryGetValue(out var claims))
        {
            throw new RequiredFactException("construction claims");
        }
        return new FirebreakFootprint
        {
            Bounds = projection.Bounds,
            Construction = f.CurrentConstruction ?? throw new RequiredFactException("construction census"),
            Claims = claims,
            Home = f.HomeCoverage ?? throw new RequiredFactException("home coverage"),
            GrowingZones = EquatableArray.From(zones),
        };
    }

    /// <summary>The cells of the accepted layout plan's footprints; none while no plan is derived.</summary>
    internal static EquatableArray<Cell> PlannedFootprint(ColonyProjection projection) =>
        EquatableArray.From(projection.LayoutPlan.TryGetValue(out var plan) ? LayoutFootprint(plan) : []);

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

    internal static HashSet<Cell> FirebreakBusy(IReadOnlyList<PlanState> plans, GenerationSnapshot current)
    {
        var busy = new HashSet<Cell>();
        RoundsProjectBill.RoundsOpenActions(plans, current, a =>
        {
            if (a.Building is { } b)
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
            var cut = new AreaPlantCut(work.Cut);
            output.Actions.Add(DomainAction.NewAreaPlantCutAction(new ActionId($"{id}-cut"), cut));
            output.Cut = work.Cut.Count;
        }
        for (var i = 0; i < work.Deconstruct.Count; i++)
        {
            if (output.Ruins == RoundsDefenseLayoutPlanner.MaxDefenseCoverBatch)
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
