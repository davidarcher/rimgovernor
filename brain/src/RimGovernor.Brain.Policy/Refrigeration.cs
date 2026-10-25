using System.Security.Cryptography;
using System.Text;

namespace RimGovernor.Brain.Policy;

// The food-stores/refrigeration Need makes the rooms that hold warm, soon-to-rot perishable food cold, where
// the food-stores/storage Need moves such food into better storage. Both read the same FoodStorageObservation: a stock
// is "warm at risk" when it is roofed (so a stockpile exists to cool), perishable, warmer than ChilledExitC and
// closer than SafeRotDays to rotting. The review is a plain threshold with no latch (decision 16): active while warm
// at-risk nutrition reaches AtRiskNutritionThreshold; the method then reuses an existing cooler before proposing
// one on a vented wall of the room.

public sealed record RefrigerationReview
{
    public bool Active { get; init; }

    /// <summary>The total nutrition of warm at-risk roofed stock.</summary>
    public double WarmNutrition { get; init; }

    /// <summary>The distinct native room ids holding warm at-risk stock, sorted; empty while the review is inactive.</summary>
    public EquatableArray<string> Rooms { get; init; }

    /// <summary>The warm standing tombs, morgues and meal closets (WithWarmRooms), also listed in Rooms.</summary>
    public EquatableArray<string> Warm { get; init; }
}

/// <summary>
/// One observed Cooler building: geometry and setpoint from the typed building read, power state from the same-tick
/// power census. Cold is the cell behind the cooler (Position - facing), Hot the cell in front.
/// </summary>
public sealed record RefrigerationCooler
{
    public required string Id { get; init; }

    public required Cell Position { get; init; }

    public required Rotation Rotation { get; init; }

    /// <summary>The cooler's CompTempControl target temperature.</summary>
    public required double Target { get; init; }

    public required bool Connected { get; init; }

    public required bool PowerOn { get; init; }

    public Cell Cold() => RefrigerationRules.ColdSide(Position, Rotation);

    public Cell Hot() => RefrigerationRules.HotSide(Position, Rotation);
}

public sealed record RefrigerationObservation
{
    public required RoomObservation Rooms { get; init; }

    public required EquatableArray<RefrigerationCooler> Coolers { get; init; }

    /// <summary>The Cooler planning definition's availability (research AirConditioning).</summary>
    public required bool CoolerAvailable { get; init; }

    public required EquatableArray<SiteCell> Cells { get; init; }

    /// <summary>The power topology's solar-flare read: every cooler is off while it is true, so no cooler method is proposed.</summary>
    public required bool Blackout { get; init; }

    public void Validate()
    {
        Rooms.Validate();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in Coolers)
        {
            if (!FoodForecastRules.FoodId(c.Id) || !seen.Add(c.Id))
            {
                throw new DomainException("invalid cooler identity");
            }
            if (c.Rotation is not (Rotation.North or Rotation.East or Rotation.South or Rotation.West))
            {
                throw new DomainException("invalid cooler rotation");
            }
            if (!double.IsFinite(c.Target))
            {
                throw new DomainException("invalid cooler target");
            }
        }
    }
}

public enum RefrigerationMethod
{
    [WireName("")]
    None = 0,


    [WireName("no_deficit")]
    NoMethod,

    [WireName("enclosed_storage_room_needed")]
    EnclosureNeeded,

    [WireName("cooler_research_needed")]
    ResearchNeeded,

    [WireName("cooler_power_needed")]
    PowerNeeded,

    [WireName("cooler_heat_rejection_blocked")]
    HeatRejectionBlocked,

    [WireName("no_vented_wall_cell")]
    NoWall,

    [WireName("set_cooler_target")]
    SetTarget,

    [WireName("waiting_for_native_cooling")]
    Wait,

    [WireName("solar_flare")]
    WaitBlackout,

    [WireName("Cooler")]
    Build,
}

public sealed record RefrigerationProposal
{
    public RefrigerationMethod Method { get; init; }

    public MethodId Key { get; init; }

    /// <summary>Cell and Rotation place a new cooler in the room's wall with its cold side facing the room.</summary>
    public Cell Cell { get; init; }

    public Rotation Rotation { get; init; }

    /// <summary>Cooler and TargetC patch an existing one.</summary>
    public string Cooler { get; init; } = "";

    public double TargetC { get; init; }
}

public static partial class RefrigerationRules
{
    /// <summary>
    /// A stock's contribution to the review: perishable, unrotted, standing roofed, whose temperature exceeds
    /// limit and whose rot runway is shorter than SafeRotDays.
    /// </summary>
    internal static bool WarmAtRisk(this FoodStorageStock s, FoodStoragePolicy p, double limit) =>
        // A non-perishable stock (a survival meal, pemmican) never rots, and food a pawn carries (in hand or in
        // inventory) has no storage cell to cool: neither is at risk here.
        s.Stock.Rot is FoodRot.Rots { Ticks: > 0 and var ticks }
        && s.Stock.Holder.IsEmpty
        && s.Stock.Place is FoodPlace.Stands { Roofed: true }
        && s.Stock.TemperatureC > limit && (double)ticks < p.SafeRotDays * GameTime.TicksPerDay;

    /// <summary>
    /// Active while the roofed at-risk nutrition warmer than ChilledExitC (the exit bar) reaches
    /// AtRiskNutritionThreshold: a plain threshold, holding nothing from the review before (decision 16).
    /// </summary>
    public static RefrigerationReview ReviewRefrigeration(FoodStorageObservation fact, FoodStoragePolicy p)
    {
        if (!p.Valid)
        {
            throw new DomainException("invalid refrigeration policy");
        }
        fact.Validate();
        if (p.ChilledExitC > fact.ChilledMaxC)
        {
            throw new DomainException("refrigeration exit temperature above the chilled limit");
        }
        var limit = p.ChilledExitC;
        var total = 0.0;
        var rooms = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in fact.Stocks)
        {
            if (!entry.WarmAtRisk(p, limit) || entry.Stock.Place is not FoodPlace.Stands { Room: var room })
            {
                continue;
            }
            total += entry.Stock.Nutrition;
            rooms.Add(room);
        }
        var isActive = total >= p.AtRiskNutritionThreshold;
        return new RefrigerationReview
        {
            WarmNutrition = total,
            Active = isActive,
            Rooms = isActive ? EquatableArray.From(rooms.Order(StringComparer.Ordinal)) : default,
        };
    }

    internal static Cell Step(Cell from, Cell direction, int times) =>
        new(from.X + (direction.X * times), from.Z + (direction.Z * times));

    /// <summary>The cell a cooler at <paramref name="position"/> facing <paramref name="rotation"/> cools: the one behind it.</summary>
    public static Cell ColdSide(Cell position, Rotation rotation) => Step(position, Facing(rotation), -1);

    /// <summary>The cell a cooler at <paramref name="position"/> facing <paramref name="rotation"/> heats: the one in front.</summary>
    public static Cell HotSide(Cell position, Rotation rotation) => Step(position, Facing(rotation), 1);

    /// <summary>The cell a building's front points at for each rotation; a cooler pushes heat out its front and cools the cell behind it.</summary>
    internal static Cell Facing(Rotation r)
    {
        switch (r)
        {
            case Rotation.North:
                return new Cell(0, 1);
            case Rotation.South:
                return new Cell(0, -1);
            case Rotation.East:
                return new Cell(1, 0);
            case Rotation.West:
                return new Cell(-1, 0);
        }
        throw new DomainException($"a cooler faces no cardinal direction for rotation {(int)r}");
    }

    /// <summary>
    /// Serves the lowest-sorted candidate room first. A solar flare defers every room. Deterministic
    /// order per room: enclosure, then an existing cooler serving the room (power, heat rejection, target,
    /// wait), then one new cooler on a wall cell whose outside is not indoors. A second cooler is proposed
    /// only when allowance is true: the caller's bounded native cooling allowance for the existing one has
    /// already elapsed without recovery.
    /// </summary>
    public static RefrigerationProposal SelectRefrigerationMethod(
        RefrigerationReview review, RefrigerationObservation v, FoodStoragePolicy p, bool allowance)
    {
        if (!review.Active)
        {
            return new RefrigerationProposal { Method = RefrigerationMethod.NoMethod };
        }
        v.Validate();
        // Under a solar flare no cooler runs and none is built for an outage measured in hours; the room keeps
        // its deficit and waits for the power to return (same outcome as PowerWaitBlackout).
        if (v.Blackout)
        {
            return new RefrigerationProposal { Method = RefrigerationMethod.WaitBlackout };
        }
        var rooms = new Dictionary<string, RoomState>(StringComparer.Ordinal);
        var cellRoom = new Dictionary<Cell, string>();
        foreach (var room in v.Rooms.Rooms)
        {
            rooms[room.Id] = room;
            foreach (var c in v.Rooms.CellsOf(room))
            {
                cellRoom[c] = room.Id;
            }
        }
        var cells = new Dictionary<Cell, SiteCell>();
        foreach (var c in v.Cells)
        {
            cells[c.Cell] = c;
        }
        var deferred = RefrigerationMethod.None;
        foreach (var id in review.Rooms)
        {
            if (!rooms.TryGetValue(id, out var room))
            {
                throw new RequiredFactException("refrigeration room:" + id);
            }
            var roomCells = v.Rooms.CellsOf(room);
            if (!room.Enclosed() || roomCells.Count == 0)
            {
                deferred = FirstReason(deferred, RefrigerationMethod.EnclosureNeeded);
                continue;
            }
            // Existing coolers whose cold side is inside this room, sorted by id.
            var serving = v.Coolers.Where(cooler => cellRoom.GetValueOrDefault(cooler.Cold(), "") == id)
                .OrderBy(cooler => cooler.Id, StringComparer.Ordinal)
                .ToList();
            if (serving.Count > 0)
            {
                var (proposal, method) = ServeRoom(id, serving, rooms, cellRoom, cells, p);
                if (proposal.Method != RefrigerationMethod.None)
                {
                    return proposal;
                }
                if (method != RefrigerationMethod.Wait || !allowance)
                {
                    deferred = FirstReason(deferred, method);
                    continue;
                }
            }
            if (!v.CoolerAvailable)
            {
                deferred = FirstReason(deferred, RefrigerationMethod.ResearchNeeded);
                continue;
            }
            var taken = new HashSet<Cell>();
            foreach (var cooler in v.Coolers)
            {
                taken.Add(cooler.Position);
            }
            var (cell, rotation, ok) = VentedWall(id, roomCells, cellRoom, cells, taken);
            if (!ok)
            {
                deferred = FirstReason(deferred, RefrigerationMethod.NoWall);
                continue;
            }
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{id}/cooler/{serving.Count}"));
            return new RefrigerationProposal
            {
                Method = RefrigerationMethod.Build,
                Key = new MethodId("refrigeration-" + Convert.ToHexStringLower(digest.AsSpan(0, 12))),
                Cell = cell,
                Rotation = rotation,
            };
        }
        return new RefrigerationProposal { Method = deferred == RefrigerationMethod.None ? RefrigerationMethod.NoMethod : deferred };
    }

    /// <summary>Keeps the first non-actionable outcome so an earlier room's blocker is reported over a later room's.</summary>
    internal static RefrigerationMethod FirstReason(RefrigerationMethod current, RefrigerationMethod next) =>
        current != RefrigerationMethod.None ? current : next;

    /// <summary>
    /// Checks the coolers already serving a room in id order and returns either an actionable proposal (set
    /// target) or the reason the room is waiting on something else: power, heat rejection or native cooling.
    /// </summary>
    internal static (RefrigerationProposal Proposal, RefrigerationMethod Method) ServeRoom(
        string room,
        IReadOnlyList<RefrigerationCooler> coolers,
        IReadOnlyDictionary<string, RoomState> rooms,
        IReadOnlyDictionary<Cell, string> cellRoom,
        IReadOnlyDictionary<Cell, SiteCell> cells,
        FoodStoragePolicy p)
    {
        var waiting = RefrigerationMethod.None;
        foreach (var cooler in coolers)
        {
            if (!cooler.Connected || !cooler.PowerOn)
            {
                waiting = FirstReason(waiting, RefrigerationMethod.PowerNeeded);
                continue;
            }
            var blocked = HeatRejectionBlocked(cooler, rooms, cellRoom, cells);
            if (blocked)
            {
                waiting = FirstReason(waiting, RefrigerationMethod.HeatRejectionBlocked);
                continue;
            }
            if (cooler.Target > p.FreezerTargetC)
            {
                var digest = SHA256.HashData(Encoding.UTF8.GetBytes(cooler.Id + "/target"));
                return (
                    new RefrigerationProposal
                    {
                        Method = RefrigerationMethod.SetTarget,
                        Key = new MethodId("refrigeration-" + Convert.ToHexStringLower(digest.AsSpan(0, 12))),
                        Cooler = cooler.Id,
                        TargetC = p.FreezerTargetC,
                    },
                    RefrigerationMethod.None);
            }
            waiting = FirstReason(waiting, RefrigerationMethod.Wait);
        }
        // A working cooler outranks a blocked one: the room is cooling.
        foreach (var cooler in coolers)
        {
            if (!cooler.Connected || !cooler.PowerOn)
            {
                continue;
            }
            var blocked = HeatRejectionBlocked(cooler, rooms, cellRoom, cells);
            if (!blocked)
            {
                return (new RefrigerationProposal(), RefrigerationMethod.Wait);
            }
        }
        return (new RefrigerationProposal(), waiting);
    }

    /// <summary>
    /// True when the cooler's hot side is impassable, or sits indoors in a room measured no warmer than the
    /// room it cools would be useful at: heat pushed into another enclosed room stops the exchange once that
    /// room saturates. Outdoors or unroofed hot sides are never blocked. Missing hot-side geometry raises RequiredFactException.
    /// </summary>
    internal static bool HeatRejectionBlocked(
        RefrigerationCooler cooler, IReadOnlyDictionary<string, RoomState> rooms, IReadOnlyDictionary<Cell, string> cellRoom, IReadOnlyDictionary<Cell, SiteCell> cells)
    {
        var hot = cooler.Hot();
        if (cells.TryGetValue(hot, out var c))
        {
            return !c.Walkable || c.Indoors;
        }
        // The room holding the hot cell decides; rooms do not overlap.
        if (cellRoom.TryGetValue(hot, out var id) && rooms.TryGetValue(id, out var room))
        {
            return room.Enclosed();
        }
        throw new RequiredFactException("cooler hot-side cell:" + hot);
    }

    // Rotations order by their wire strings: east, north, south, west.
    private static int RotationWireOrder(Rotation r)
    {
        switch (r)
        {
            case Rotation.East:
                return 0;
            case Rotation.North:
                return 1;
            case Rotation.South:
                return 2;
            case Rotation.West:
                return 3;
        }
        throw new DomainException($"a cooler has no wire order for rotation {(int)r}");
    }

    /// <summary>
    /// Picks the lowest-sorted wall cell of the room whose outward neighbour is a known non-indoor cell, and
    /// the rotation that points the cooler's front outward so its cold side faces the room. Wall cells are
    /// non-walkable site cells adjacent to a room cell along a straight line with the outside: inside -> wall
    /// -> outside.
    /// </summary>
    internal static (Cell Cell, Rotation Rotation, bool Ok) VentedWall(
        string id, IReadOnlyList<Cell> room, IReadOnlyDictionary<Cell, string> cellRoom, IReadOnlyDictionary<Cell, SiteCell> cells, ISet<Cell> taken)
    {
        var options = new List<(Cell Cell, Rotation Rotation)>();
        foreach (var inside in room)
        {
            foreach (var rotation in new[] { Rotation.North, Rotation.East, Rotation.South, Rotation.West })
            {
                var wall = Step(inside, Facing(rotation), 1);
                var outside = Step(inside, Facing(rotation), 2);
                if (taken.Contains(wall) || cellRoom.GetValueOrDefault(wall, "") == id || cellRoom.GetValueOrDefault(outside, "") == id)
                {
                    continue;
                }
                if (!cells.TryGetValue(wall, out var w))
                {
                    continue;
                }
                if (w.Walkable)
                {
                    continue;
                }
                if (!w.Occupied())
                {
                    continue;
                }
                if (!cells.TryGetValue(outside, out var o))
                {
                    continue;
                }
                if (o.Indoors && o.Roofed)
                {
                    continue;
                }
                if (!o.Walkable)
                {
                    continue;
                }
                options.Add((wall, rotation));
            }
        }
        if (options.Count == 0)
        {
            return (default, default, false);
        }
        var best = options
            .OrderBy(o => o.Cell.X)
            .ThenBy(o => o.Cell.Z)
            .ThenBy(o => RotationWireOrder(o.Rotation))
            .First();
        return (best.Cell, best.Rotation, true);
    }
}
