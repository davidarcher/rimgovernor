namespace RimGovernor.Brain.Policy;

// The containment cell: one planned room holding one holding platform, owed while an entity the game lets the
// colony capture has no platform that can hold it. It is staged like the worship room (worship.go,
// layout_child_rooms.go): the layout review grows a core room sized to the platform, MaintainHousing
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

/// <summary>A standing holding platform's native facts.</summary>
public sealed record BuiltHolder
{
    public double Strength { get; init; }

    /// <summary>Whether the platform can take a pawn now.</summary>
    public bool Available { get; init; }

    /// <summary>The pawn on the platform (empty when none).</summary>
    public string HeldPawn { get; init; } = "";

    /// <summary>The doors of the platform's room.</summary>
    public Fact<EquatableArray<ContainmentDoor>> Doors { get; init; }
}

/// <summary>The projection's containment inputs. Each unknown fact stops the cell with its own reason.</summary>
public sealed record ContainmentPlanning
{
    public Fact<ContainmentDemand> Demand { get; init; }

    /// <summary>The standing platforms; unknown without a building census.</summary>
    public Fact<EquatableArray<BuiltHolder>> Holders { get; init; }

    /// <summary>The prediction's inputs.</summary>
    public Fact<ContainmentDefs> Defs { get; init; }

    /// <summary>Says in plain English why Defs are unknown.</summary>
    public string DefsReason { get; init; } = "";

    /// <summary>The entity pawn rows the capture rule decides; unknown without Anomaly.</summary>
    public Fact<EquatableArray<CapturableEntity>> Entities { get; init; }
}

/// <summary>Whether a cell is owed and, when demand exists but a cell is not owed, why in plain English.</summary>
public sealed record ContainmentVerdict
{
    public bool Owed { get; init; }

    public string Reason { get; init; } = "";
}

public static partial class PolicyFunctions
{
    /// <summary>The defs a planned room's shell is built from (reconcileRoom); the prediction reads their hit points.</summary>
    public const string ShellWallDefinition = "Wall";

    public const string ShellDoorDefinition = "Door";

    /// <summary>The lamp the cell is furnished with: the same standing lamp that stands beside a bed or throne.</summary>
    public static string ContainmentLampDefinition => StandingLampDef;

    /// <summary>
    /// The cell the entities owe: one platform in its own room. No demand, or a standing available platform
    /// whose native strength reaches the demand, owes nothing; an unknown fact, an unavailable platform, or a
    /// design whose predicted strength falls short owes nothing and says why.
    /// </summary>
    public static (ChildRoomNeed? Need, ContainmentVerdict Verdict) ContainmentCellNeed(
        ContainmentPlanning p,
        IReadOnlyList<FurnitureDefinition> furniture,
        FlooringFacts floors)
    {
        static (ChildRoomNeed?, ContainmentVerdict) Refuse(string reason) => (null, new ContainmentVerdict { Reason = reason });
        if (!p.Demand.TryGetValue(out var demand))
        {
            return Refuse("the entities' capture and containment facts are unread");
        }
        if (demand.Entities == 0)
        {
            return (null, new ContainmentVerdict());
        }
        if (!p.Holders.TryGetValue(out var holders))
        {
            return Refuse("the standing holding platforms are unread");
        }
        if (!p.Defs.TryGetValue(out var defs))
        {
            return Refuse("the containment inputs cannot be read from the definitions: " + p.DefsReason);
        }
        // A cell is owed for what the capture rule would take: the need plus its margin, so a cell is never
        // built for an entity that would then be killed.
        double margin;
        try
        {
            margin = CaptureMargin(defs);
        }
        catch (DomainException e)
        {
            return Refuse("the capture margin is unknown: " + e.Message);
        }
        var required = demand.Required + margin;
        foreach (var h in holders)
        {
            if (h.Available && h.Strength >= required)
            {
                return (null, new ContainmentVerdict());
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
        if (!need.TryResolve(furniture, out _))
        {
            return Refuse("the holding platform " + defs.Holder + " is not buildable yet or its size is unknown");
        }
        // The floor term counts the containment floor when the stock pays for every tile of the smallest room
        // that holds the cell; WantedFloors lays it under the same test.
        var floored = false;
        if (need.TryShape(furniture, out var shape))
        {
            var sizes = ChildRoomSizes(shape);
            if (sizes.Count > 0)
            {
                floored = ContainmentFloorLaid(defs.Floor, floors, (int)(sizes[0][0] * sizes[0][1]));
            }
        }
        if (floors.Layout.TryGetValue(out var plan) && plan is not null)
        {
            foreach (var room in plan.Rooms)
            {
                if (room.Role == PlannedRole.ContainmentCell && ContainmentRoomFloorLaid(defs.Floor, floors, room))
                {
                    floored = true;
                }
            }
        }
        double strength;
        try
        {
            strength = defs.Predict(new ContainmentRoom { Floored = floored });
        }
        catch (DomainException e)
        {
            return Refuse("the cell's strength cannot be predicted: " + e.Message);
        }
        if (strength < required)
        {
            return Refuse(FormattableString.Invariant(
                $"a cell of {defs.Holder} with the planned walls and door holds at most {strength:F1} containment strength and an entity needs {demand.Required:F1} plus a margin of {margin:F1}; facilities that add strength are not planned"));
        }
        return (need, new ContainmentVerdict { Owed = true });
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
        var ak = def.Available.TryGetValue(out var available);
        var tk = def.Terrain.TryGetValue(out var terrain);
        return ak && tk && available && terrain && AffordableCells(def, facts.Stock, tiles) >= tiles;
    }

    /// <summary>
    /// Credits observed terrain and outstanding floor orders once each, then prices only the room's remaining
    /// tiles against accessible stock. Unknown observations cannot supply credit; an unrelated room supplies none.
    /// </summary>
    private static bool ContainmentRoomFloorLaid(ContainmentFloor floor, FlooringFacts facts, PlannedRoom room)
    {
        if (floor.Def.Length == 0)
        {
            return false;
        }
        var remaining = new HashSet<Cell>(RectCells(room.Region.Bounds()));
        if (remaining.Count == 0)
        {
            return false;
        }
        if (facts.Observation.TryGetValue(out var observed))
        {
            foreach (var r in observed.Rooms)
            {
                foreach (var cell in r.Cells)
                {
                    if (cell.Pending == floor.Def || (cell.Pending.Length == 0 && cell.Terrain == floor.Def))
                    {
                        remaining.Remove(cell.Cell);
                    }
                }
            }
        }
        return remaining.Count == 0 || ContainmentFloorLaid(floor, facts, remaining.Count);
    }
}
