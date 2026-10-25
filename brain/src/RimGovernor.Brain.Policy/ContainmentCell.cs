namespace RimGovernor.Brain.Policy;

// The containment cell: one planned room holding one holding platform, owed while an entity the game lets the
// colony capture has no platform that can hold it. It is staged like the worship room: the layout review grows a core room sized to the platform, shelter/sleeping
// reconciles it to its template (the platform and its lamp, NextChildRoomStep). The platform is the catalog's
// own (a ThingDef with an entity-holder platform comp), never a name here. A cell is owed only when its
// predicted containment strength (ContainmentDefs.Predict) reaches what the entity needs; how much margin to
// keep is the capture rule's, and the standing platform's native strength (EntityHolder.ContainmentStrength)
// is the check once it is built. Capture itself is not planned here. Bioferrite plate floor: when the stock
// pays for every tile of the room, the prediction counts the catalog's containment floor in place of the plain
// floor, and the flooring review lays it (WantedFloors).

/// <summary>
/// What the colony's entities ask of a cell: how many living entities the game lets the colony capture that no
/// platform holds (HeldState.can_be_captured, not held) and the highest containment strength any of them needs
/// (its MinimumContainmentStrength).
/// </summary>
public sealed record ContainmentDemand
{
    public int Entities { get; init; }

    public double Required { get; init; }
}

/// <summary>
/// The containment prediction's def inputs: Inactive without the Anomaly expansion (no platform def exists),
/// Unread when a shell def, the shell's shared stuff or a catalog def could not be read (Reason says which), Read
/// with the defs.
/// </summary>
public abstract record ContainmentDefsRead
{
    private protected ContainmentDefsRead() { }

    /// <summary>The Anomaly expansion is not active.</summary>
    public sealed record Inactive : ContainmentDefsRead;

    /// <summary>The defs could not be read; Reason says why.</summary>
    public sealed record Unread(string Reason) : ContainmentDefsRead;

    /// <summary>The defs the prediction reads.</summary>
    public sealed record Read(ContainmentDefs Defs) : ContainmentDefsRead;

    /// <summary>Why <paramref name="defs"/> are not read, in plain English, when they are Inactive or Unread.</summary>
    public static string NotReadWhy(ContainmentDefsRead defs) =>
        defs is Unread { Reason: var reason } ? reason : "the Anomaly expansion is not active";
}

/// <summary>
/// The projection's containment inputs, read whole from the routine frame. Without the Anomaly expansion no pawn is
/// an entity and no building is a platform: the demand is zero, the holder and entity lists are empty and the defs
/// are <see cref="ContainmentDefsRead.Inactive"/>.
/// </summary>
public sealed record ContainmentPlanning
{
    public ContainmentDemand Demand { get; init; } = new();

    /// <summary>The standing platforms.</summary>
    public EquatableArray<RimGovernor.Contracts.Wire.Observations.EntityHolderState> Holders { get; init; } = [];

    /// <summary>The prediction's inputs.</summary>
    public ContainmentDefsRead Defs { get; init; } = new ContainmentDefsRead.Inactive();

    /// <summary>The entity pawn rows the capture rule decides.</summary>
    public EquatableArray<CapturableEntity> Entities { get; init; } = [];

    /// <summary>The held and waiting entities' escape and study facts and the game's containment curves, which the escape term and the cell credit read (#3811).</summary>
    public ContainmentForecast Forecast { get; init; } = new();
}


public static class ContainmentCellRules
{
    /// <summary>The defs a planned room's shell is built from (reconcileRoom); the prediction reads their hit points.</summary>
    public const string ShellWallDefinition = "Wall";

    public const string ShellDoorDefinition = "Door";

    /// <summary>The lamp the cell is furnished with: the same standing lamp that stands beside a bed or throne.</summary>
    public static string ContainmentLampDefinition => InteriorHelpersRules.StandingLampDef;

    /// <summary>
    /// The cell the entities owe: one platform in its own room. No demand, or a standing available platform
    /// whose native strength reaches the demand, owes nothing; so do defs that are not read, an unavailable platform, or a
    /// design whose predicted strength falls short (no facility that adds strength is planned) owes nothing.
    /// </summary>
    public static ChildRoomNeed? ContainmentCellNeed(
        ContainmentPlanning p,
        IReadOnlyList<FurnitureDefinition> furniture,
        FlooringFacts floors)
    {
        if (p.Demand.Entities == 0 || p.Defs is not ContainmentDefsRead.Read { Defs: var defs })
        {
            return null;
        }
        var demand = p.Demand;
        var holders = p.Holders;
        // A cell is owed for what the capture rule would take: the need plus its margin, so a cell is never
        // built for an entity that would then be killed.
        double margin;
        try
        {
            margin = EntityCaptureRules.CaptureMargin(defs);
        }
        catch (DomainException)
        {
            return null;
        }
        var required = demand.Required + margin;
        foreach (var h in holders)
        {
            if (h.Available && h.ContainmentStrength >= required)
            {
                return null;
            }
        }
        // A lamp lights the cell: glow adds ten containment strength per unit of mean glow, and the power
        // planner connects the lamp like any other unpowered consumer. Optional: left out until the lamp is
        // researched, and the predicted strength above counts no glow, so the cell never relies on it.
        var need = new ChildRoomNeed
        {
            Role = RoomRole.ContainmentCell,
            Module = PlannedRole.ContainmentCell,
            Furniture = EquatableArray.From(
            [
                new ChildFurniture { Defs = EquatableArray.From([defs.Holder]), Count = 1 },
                new ChildFurniture { Defs = EquatableArray.From([ContainmentLampDefinition]), Count = 1, Optional = true },
            ]),
        };
        if (need.Resolve(furniture) is null)
        {
            return null;
        }
        // The floor term counts the containment floor when the stock pays for every tile of the smallest room
        // that holds the cell; WantedFloors lays it under the same test.
        var floored = false;
        if (need.Shape(furniture) is { } shape)
        {
            var sizes = LayoutChildRoomsRules.ChildRoomSizes(shape);
            if (sizes.Count > 0)
            {
                floored = ContainmentFloorLaid(defs.Floor, floors, sizes[0].W * sizes[0].D);
            }
        }
        foreach (var room in PlannedCells(floors))
        {
            if (ContainmentRoomFloorLaid(defs.Floor, floors, room))
            {
                floored = true;
            }
        }
        double strength;
        try
        {
            strength = defs.Predict(new ContainmentRoom { Floored = floored });
        }
        catch (DomainException)
        {
            return null;
        }
        return strength < required ? null : need;
    }

    /// <summary>The layout plan's containment cells; none while the plan is unread.</summary>
    public static IEnumerable<PlannedRoom> PlannedCells(FlooringFacts floors) =>
        floors.Layout is { } plan ? plan.Rooms.Where(r => r.Role == PlannedRole.ContainmentCell) : [];

    /// <summary>
    /// The strength the catalog's platform predicts in planned cell <paramref name="room"/> once it is built (#3811): its
    /// floor term the containment floor when the room is laid with it or the stock lays it (<see cref="ContainmentRoomFloorLaid"/>),
    /// with no glow, as <see cref="ContainmentCellNeed"/> predicts. The reason is set, and the strength 0, when the defs are
    /// unread or the prediction cannot be made.
    /// </summary>
    public static (double Strength, string Reason) PlannedCellStrength(ContainmentPlanning p, FlooringFacts floors, PlannedRoom room)
    {
        if (p.Defs is not ContainmentDefsRead.Read { Defs: var defs })
        {
            return (0, ContainmentDefsRead.NotReadWhy(p.Defs));
        }
        try
        {
            return (defs.Predict(new ContainmentRoom { Floored = ContainmentRoomFloorLaid(defs.Floor, floors, room) }), "");
        }
        catch (DomainException e)
        {
            return (0, e.Message);
        }
    }

    /// <summary>
    /// Reports whether a room of tiles can be laid with the containment floor now: a floor def the catalog
    /// names, known available terrain, and a stock that covers its whole cost for every tile. An unread stock
    /// or definition is not enough (unknown stays unknown).
    /// </summary>
    public static bool ContainmentFloorLaid(ContainmentFloor floor, FlooringFacts facts, int tiles)
    {
        if (floor.Def.Length == 0 || !facts.Definitions.TryGetValue(floor.Def, out var def) || tiles <= 0)
        {
            return false;
        }
        var available = def.Available;
        var terrain = def.Terrain;
        return available && terrain && FlooringRules.AffordableCells(def, facts.Stock, tiles) >= tiles;
    }

    /// <summary>
    /// Credits observed terrain and outstanding floor orders once each, then prices only the room's remaining
    /// tiles against accessible stock. Unknown observations cannot supply credit; an unrelated room supplies none.
    /// </summary>
    internal static bool ContainmentRoomFloorLaid(ContainmentFloor floor, FlooringFacts facts, PlannedRoom room)
    {
        if (floor.Def.Length == 0)
        {
            return false;
        }
        var remaining = new HashSet<Cell>(PlannedRules.RectCells(room.Region.Bounds()));
        if (remaining.Count == 0)
        {
            return false;
        }
        if (facts.Observation is { } observed)
        {
            foreach (var r in observed.Rooms)
            {
                foreach (var cell in r.Cells)
                {
                    if (cell.Pending == floor.Def || (cell.Pending is null && cell.Terrain == floor.Def))
                    {
                        remaining.Remove(cell.Cell);
                    }
                }
            }
        }
        return remaining.Count == 0 || ContainmentFloorLaid(floor, facts, remaining.Count);
    }
}
