using System.Security.Cryptography;
using System.Text;

namespace RimGovernor.Brain.Policy;

// MaintainRefrigeration makes the rooms that hold warm, soon-to-rot perishable food cold, where
// MaintainFoodStorage moves such food into better storage. Both read the same FoodStorageObservation: a stock
// is "warm at risk" when it is roofed (so a stockpile exists to cool), perishable, warmer than ChilledMaxC and
// closer than SafeRotDays to rotting. The review latches on warm at-risk nutrition and releases once every
// such room is measured at or below ChilledExitC (or the food is gone); the method then reuses an existing
// cooler before proposing one on a vented wall of the room.

public sealed record RefrigerationReview
{
    public bool Active { get; init; }

    /// <summary>
    /// The total nutrition of warm at-risk roofed stock; unknown when the census or any at-risk stock's
    /// storage facts are unknown, which preserves the previous latch.
    /// </summary>
    public Fact<double> WarmNutrition { get; init; }

    /// <summary>The distinct native room ids holding warm at-risk stock, sorted, and only meaningful while WarmNutrition is known.</summary>
    public EquatableArray<string> Rooms { get; init; }

    /// <summary>The warm standing tombs, morgues and meal closets (WithWarmRooms), also listed in Rooms.</summary>
    public EquatableArray<string> Warm { get; init; }
}

/// <summary>
/// One observed Cooler building: geometry from the typed building read, power state from the same-tick power
/// census. Cold is the cell behind the cooler (Position - facing), Hot the cell in front.
/// </summary>
public sealed record RefrigerationCooler
{
    public string Id { get; init; } = "";

    public Cell Position { get; init; }

    public Rotation Rotation { get; init; }

    public Fact<double> Target { get; init; }

    public Fact<bool> Connected { get; init; }

    public Fact<bool> PowerOn { get; init; }

    /// <summary>The typed thermal-side fact when the reader supplies it; otherwise the hot cell is classified from the site cells and rooms.</summary>
    public Fact<bool> HotIndoors { get; init; }

    public Cell Cold() => PolicyFunctions.Step(Position, PolicyFunctions.Facing(Rotation), -1);

    public Cell Hot() => PolicyFunctions.Step(Position, PolicyFunctions.Facing(Rotation), 1);
}

public sealed record RefrigerationObservation
{
    public EquatableArray<Room> Rooms { get; init; }

    public EquatableArray<RefrigerationCooler> Coolers { get; init; }

    /// <summary>The Cooler planning definition's availability (research AirConditioning); unknown when the definition was not read.</summary>
    public Fact<bool> CoolerAvailable { get; init; }

    public EquatableArray<SiteCell> Cells { get; init; }

    /// <summary>
    /// The power topology's solar-flare read: every cooler is off while it is true, so no cooler method is
    /// proposed. Unknown when the environment census was not read; the method then proceeds as before.
    /// </summary>
    public Fact<bool> Blackout { get; init; }

    /// <summary>
    /// The layout plan's cooler sites; a room whose back wall holds one takes it over any other vented wall
    /// once its hot side is open (the outer face, or a dug exhaust shaft).
    /// </summary>
    public EquatableArray<PlannedCoolerSite> Planned { get; init; }

    public void Validate()
    {
        new RoomObservation { Rooms = Rooms }.Validate();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in Coolers)
        {
            if (!PolicyFunctions.FoodId(c.Id) || !seen.Add(c.Id))
            {
                throw new DomainException("invalid cooler identity");
            }
            if (c.Rotation is not (Rotation.North or Rotation.East or Rotation.South or Rotation.West))
            {
                throw new DomainException("invalid cooler rotation");
            }
            if (c.Target.TryGetValue(out var target) && (double.IsNaN(target) || double.IsInfinity(target)))
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

    [WireName("unknown")]
    Unknown,

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

    public string Room { get; init; } = "";

    /// <summary>Cell and Rotation place a new cooler in the room's wall with its cold side facing the room.</summary>
    public Cell Cell { get; init; }

    public Rotation Rotation { get; init; }

    /// <summary>Cooler and TargetC patch an existing one.</summary>
    public string Cooler { get; init; } = "";

    public double TargetC { get; init; }
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// A stock's contribution to the review: known perishable, unrotted, roofed, with a known room, whose
    /// temperature exceeds limit and whose rot runway is shorter than SafeRotDays. Known is false when a
    /// needed fact is unknown.
    /// </summary>
    internal static (bool Warm, bool Known) WarmAtRisk(this FoodStorageStock s, FoodStoragePolicy p, double limit)
    {
        if (!s.Stock.Perishable.TryGetValue(out var perishable))
        {
            return (false, false);
        }
        // A known non-perishable stock (a survival meal, pemmican) never rots, so native reports no rot runway
        // for it; that is not an unknown fact.
        if (!perishable)
        {
            return (false, true);
        }
        if (!s.Stock.RotTicks.TryGetValue(out var ticks))
        {
            return (false, false);
        }
        if (ticks <= 0)
        {
            return (false, true);
        }
        // Food a pawn holds (carried or in inventory) has no storage cell to cool: it is not at risk here, and
        // its unknown roof/room facts must not blank the review while a cook or hauler walks a stack across
        // the map.
        if (s.Stock.Holder.TryGetValue(out var holder) && !holder.IsEmpty)
        {
            return (false, true);
        }
        if (!s.Stock.Roofed.TryGetValue(out var roofed))
        {
            return (false, false);
        }
        if (!roofed)
        {
            return (false, true);
        }
        var tmk = s.Stock.TemperatureC.TryGetValue(out var temperature);
        var roomKnown = s.Stock.Room.TryGetValue(out _);
        if (!tmk || !roomKnown)
        {
            return (false, false);
        }
        return (temperature > limit && (double)ticks < p.SafeRotDays * GameTime.TicksPerDay, true);
    }

    /// <summary>
    /// Enters on warm at-risk nutrition at or above AtRiskNutritionThreshold and, once active, holds until no
    /// roofed at-risk stock remains warmer than ChilledExitC. Unknown storage facts on any at-risk stock
    /// preserve the previous latch rather than asserting recovery.
    /// </summary>
    public static RefrigerationReview ReviewRefrigeration(FoodStorageObservation fact, bool active, FoodStoragePolicy p)
    {
        if (!p.Valid)
        {
            throw new DomainException("invalid refrigeration policy");
        }
        fact.Validate();
        if (!fact.Stocks.TryGetValue(out var stocks))
        {
            return new RefrigerationReview { Active = active, WarmNutrition = Unknown<double>() };
        }
        if (p.ChilledExitC > fact.ChilledMaxC)
        {
            throw new DomainException("refrigeration exit temperature above the chilled limit");
        }
        var limit = fact.ChilledMaxC;
        if (active)
        {
            limit = p.ChilledExitC;
        }
        var total = 0.0;
        var rooms = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in stocks)
        {
            var (warm, known) = entry.WarmAtRisk(p, limit);
            if (!known)
            {
                return new RefrigerationReview { Active = active, WarmNutrition = Unknown<double>() };
            }
            if (!warm)
            {
                continue;
            }
            if (!entry.Stock.Nutrition.TryGetValue(out var nutrition))
            {
                return new RefrigerationReview { Active = active, WarmNutrition = Unknown<double>() };
            }
            total += nutrition;
            entry.Stock.Room.TryGetValue(out var room);
            rooms.Add(room ?? "");
        }
        var isActive = active ? total > 0 : total >= p.AtRiskNutritionThreshold;
        return new RefrigerationReview
        {
            WarmNutrition = Known(total),
            Active = isActive,
            Rooms = isActive ? EquatableArray.From(rooms.Order(StringComparer.Ordinal)) : default,
        };
    }

    internal static Cell Step(Cell from, Cell direction, int times) =>
        new(from.X + (direction.X * times), from.Z + (direction.Z * times));

    /// <summary>The cell a building's front points at for each rotation; a cooler pushes heat out its front and cools the cell behind it.</summary>
    internal static Cell Facing(Rotation r) => r switch
    {
        Rotation.North => new Cell(0, 1),
        Rotation.South => new Cell(0, -1),
        Rotation.East => new Cell(1, 0),
        _ => new Cell(-1, 0),
    };

    /// <summary>
    /// Serves the lowest-sorted candidate room first. A known solar flare defers every room. Deterministic
    /// order per room: enclosure, then an existing cooler serving the room (power, heat rejection, target,
    /// wait), then one new cooler on a wall cell whose outside is not indoors. A second cooler is proposed
    /// only when allowance is true: the caller's bounded native cooling allowance for the existing one has
    /// already elapsed without recovery.
    /// </summary>
    public static RefrigerationProposal SelectRefrigerationMethod(
        RefrigerationReview review, Fact<RefrigerationObservation> fact, FoodStoragePolicy p, bool allowance)
    {
        if (!review.Active)
        {
            return new RefrigerationProposal { Method = RefrigerationMethod.NoMethod };
        }
        if (!fact.TryGetValue(out var v))
        {
            return new RefrigerationProposal { Method = RefrigerationMethod.Unknown };
        }
        v.Validate();
        // Under a solar flare no cooler runs and none is built for an outage measured in hours; the room keeps
        // its deficit and waits for the power to return (same outcome as PowerWaitBlackout).
        if (v.Blackout.TryGetValue(out var blackout) && blackout)
        {
            return new RefrigerationProposal { Method = RefrigerationMethod.WaitBlackout };
        }
        var rooms = new Dictionary<string, Room>(StringComparer.Ordinal);
        var cellRoom = new Dictionary<Cell, string>();
        foreach (var room in v.Rooms)
        {
            rooms[room.Id] = room;
            foreach (var c in room.Cells)
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
                return new RefrigerationProposal { Method = RefrigerationMethod.Unknown };
            }
            if (!room.Enclosed.TryGetValue(out var enclosed))
            {
                return new RefrigerationProposal { Method = RefrigerationMethod.Unknown };
            }
            if (!enclosed || room.Cells.Count == 0)
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
                var (proposal, method) = ServeRoom(id, serving, rooms, cells, p);
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
            if (!v.CoolerAvailable.TryGetValue(out var available))
            {
                return new RefrigerationProposal { Method = RefrigerationMethod.Unknown };
            }
            if (!available)
            {
                deferred = FirstReason(deferred, RefrigerationMethod.ResearchNeeded);
                continue;
            }
            var taken = new HashSet<Cell>();
            foreach (var cooler in v.Coolers)
            {
                taken.Add(cooler.Position);
            }
            var (cell, rotation, ok) = PlannedWall(room, v.Planned, cells, taken);
            if (!ok)
            {
                (cell, rotation, ok) = VentedWall(room, cellRoom, cells, taken);
            }
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
                Room = id,
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
        IReadOnlyDictionary<string, Room> rooms,
        IReadOnlyDictionary<Cell, SiteCell> cells,
        FoodStoragePolicy p)
    {
        var waiting = RefrigerationMethod.None;
        foreach (var cooler in coolers)
        {
            var ck = cooler.Connected.TryGetValue(out var connected);
            var ok = cooler.PowerOn.TryGetValue(out var on);
            if (!ck || !ok)
            {
                return (new RefrigerationProposal { Method = RefrigerationMethod.Unknown }, RefrigerationMethod.None);
            }
            if (!connected || !on)
            {
                waiting = FirstReason(waiting, RefrigerationMethod.PowerNeeded);
                continue;
            }
            var (blocked, known) = HeatRejectionBlocked(cooler, rooms, cells);
            if (!known)
            {
                return (new RefrigerationProposal { Method = RefrigerationMethod.Unknown }, RefrigerationMethod.None);
            }
            if (blocked)
            {
                waiting = FirstReason(waiting, RefrigerationMethod.HeatRejectionBlocked);
                continue;
            }
            if (!cooler.Target.TryGetValue(out var target))
            {
                return (new RefrigerationProposal { Method = RefrigerationMethod.Unknown }, RefrigerationMethod.None);
            }
            if (target > p.FreezerTargetC)
            {
                var digest = SHA256.HashData(Encoding.UTF8.GetBytes(cooler.Id + "/target"));
                return (
                    new RefrigerationProposal
                    {
                        Method = RefrigerationMethod.SetTarget,
                        Key = new MethodId("refrigeration-" + Convert.ToHexStringLower(digest.AsSpan(0, 12))),
                        Room = room,
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
            if (!(cooler.Connected.TryGetValue(out var connected) && connected))
            {
                continue;
            }
            if (!(cooler.PowerOn.TryGetValue(out var on) && on))
            {
                continue;
            }
            var (blocked, known) = HeatRejectionBlocked(cooler, rooms, cells);
            if (known && !blocked)
            {
                return (new RefrigerationProposal(), RefrigerationMethod.Wait);
            }
        }
        return (new RefrigerationProposal(), waiting);
    }

    /// <summary>
    /// True when the cooler's hot side is impassable, or sits indoors in a room measured no warmer than the
    /// room it cools would be useful at: heat pushed into another enclosed room stops the exchange once that
    /// room saturates. Outdoors or unroofed hot sides are never blocked. Known is false when it cannot be told.
    /// </summary>
    internal static (bool Blocked, bool Known) HeatRejectionBlocked(
        RefrigerationCooler cooler, IReadOnlyDictionary<string, Room> rooms, IReadOnlyDictionary<Cell, SiteCell> cells)
    {
        var hot = cooler.Hot();
        if (cells.TryGetValue(hot, out var c) && c.Walkable.TryGetValue(out var walkable) && !walkable)
        {
            return (true, true);
        }
        if (cooler.HotIndoors.TryGetValue(out var hotIndoors))
        {
            return (hotIndoors, true);
        }
        if (cells.TryGetValue(hot, out var hc))
        {
            if (hc.Indoors.TryGetValue(out var indoors))
            {
                return (indoors, true);
            }
            if (hc.Roofed.TryGetValue(out var roofed) && !roofed)
            {
                return (false, true);
            }
        }
        // Go ranged a map here; the first room holding the hot cell decides. Rooms do not overlap, so the
        // lowest id keeps the result independent of dictionary order.
        foreach (var id in rooms.Keys.Order(StringComparer.Ordinal))
        {
            var room = rooms[id];
            foreach (var cell in room.Cells)
            {
                if (cell == hot)
                {
                    var known = room.Enclosed.TryGetValue(out var enclosed);
                    return (enclosed, known);
                }
            }
        }
        return (false, false);
    }

    /// <summary>The first planned cooler site whose cold side is in room, not taken, standing in a known wall with a known walkable hot side.</summary>
    internal static (Cell Cell, Rotation Rotation, bool Ok) PlannedWall(
        Room room, IReadOnlyList<PlannedCoolerSite> planned, IReadOnlyDictionary<Cell, SiteCell> cells, ISet<Cell> taken)
    {
        var inside = new HashSet<Cell>(room.Cells);
        foreach (var s in planned)
        {
            var cooler = new RefrigerationCooler { Position = s.Cell, Rotation = s.Rotation };
            if (taken.Contains(s.Cell) || !inside.Contains(cooler.Cold()))
            {
                continue;
            }
            if (!cells.TryGetValue(s.Cell, out var w))
            {
                continue;
            }
            if (!w.Walkable.TryGetValue(out var walkable) || walkable)
            {
                continue;
            }
            if (w.NaturalRock())
            {
                // No cooler stands on rock; the exhaust dig mines the cell and places this planned cooler in one plan.
                continue;
            }
            if (!cells.TryGetValue(cooler.Hot(), out var o))
            {
                continue;
            }
            if (!o.Walkable.TryGetValue(out var ow) || !ow)
            {
                continue;
            }
            return (s.Cell, s.Rotation, true);
        }
        return (default, default, false);
    }

    // Go ordered rotations by their wire strings: east, north, south, west.
    private static int RotationWireOrder(Rotation r) => r switch
    {
        Rotation.East => 0,
        Rotation.North => 1,
        Rotation.South => 2,
        _ => 3,
    };

    /// <summary>
    /// Picks the lowest-sorted wall cell of the room whose outward neighbour is a known non-indoor cell, and
    /// the rotation that points the cooler's front outward so its cold side faces the room. Wall cells are
    /// non-walkable site cells adjacent to a room cell along a straight line with the outside: inside -> wall
    /// -> outside.
    /// </summary>
    internal static (Cell Cell, Rotation Rotation, bool Ok) VentedWall(
        Room room, IReadOnlyDictionary<Cell, string> cellRoom, IReadOnlyDictionary<Cell, SiteCell> cells, ISet<Cell> taken)
    {
        var options = new List<(Cell Cell, Rotation Rotation)>();
        foreach (var inside in room.Cells)
        {
            foreach (var rotation in new[] { Rotation.North, Rotation.East, Rotation.South, Rotation.West })
            {
                var wall = Step(inside, Facing(rotation), 1);
                var outside = Step(inside, Facing(rotation), 2);
                if (taken.Contains(wall) || cellRoom.GetValueOrDefault(wall, "") == room.Id || cellRoom.GetValueOrDefault(outside, "") == room.Id)
                {
                    continue;
                }
                if (!cells.TryGetValue(wall, out var w))
                {
                    continue;
                }
                if (!w.Walkable.TryGetValue(out var walkable) || walkable)
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
                var ik = o.Indoors.TryGetValue(out var indoors);
                var rk = o.Roofed.TryGetValue(out var roofed);
                if (!((ik && !indoors) || (rk && !roofed)))
                {
                    continue;
                }
                if (!o.Walkable.TryGetValue(out var ow) || !ow)
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
