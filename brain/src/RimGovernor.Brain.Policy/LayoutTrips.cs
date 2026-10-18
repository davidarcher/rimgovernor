namespace RimGovernor.Brain.Policy;

/// <summary>The pawn traffic between two rooms of a plan: A and B are room ids (A below B) and Weight the trips a day, the station graph's flow between the two rooms' groups. The trips are the edges of the room graph a plan without a spine is scored over.</summary>
public readonly record struct RoomTrip(int A, int B, double Weight);

/// <summary>The pawn traffic between a room and the outside (the graph's PurposeOutside node, which the tiler maps to the plan's Entrances, not to a room): Weight is the trips a day, one way. Only exteriorDoorMovements counts the way back.</summary>
public readonly record struct OutsideTrip(int Room, double Weight);

public static partial class LayoutTripsRules
{
    /// <summary>A suite's plan role.</summary>
    public static PlannedRole PlannedSuite => PlannedRole.Suite;

    /// <summary>The room role a suite is built for. It is a plan role only: RimWorld has no Suite RoomRoleDef, so the native census reports a furnished suite as a Bedroom.</summary>
    public static RoomRole RoomRoleSuite => RoomRole.Suite;

    private const int SuiteMinWidth = 3;
    private const int SuiteMaxWidth = 7;
    private const int SuiteMinDepth = 4;
    private const int SuiteMaxDepth = 8;

    /// <summary>The bedroom template's footprint (bed 2, end table 1, dresser 2, lamp 1), each tile costing the Space stat 0.9.</summary>
    internal const int SuiteFurnitureTiles = 6;

    /// <summary>
    /// The suite interior (width along the corridor, depth away from it) whose space meets an impressiveness target on
    /// its own: the Space stat normalised by 125 reaching target/100, at 1.4 per open tile less 0.9 per furniture tile.
    /// The smallest area within 3-7 x 4-8 wins, then the squarer, then the narrower; a target no suite reaches gets the
    /// largest.
    /// </summary>
    public static (int Width, int Depth) SuiteSize(double target)
    {
        var cells = RoomQualityRules.SuiteCells(target);
        int bw = SuiteMaxWidth, bd = SuiteMaxDepth;
        for (var w = SuiteMinWidth; w <= SuiteMaxWidth; w++)
        {
            for (var d = SuiteMinDepth; d <= SuiteMaxDepth; d++)
            {
                int a = w * d, b = bw * bd;
                if (a < cells || a > b)
                {
                    continue;
                }
                if (a < b || LightingRules.Abs32(w - d) < LightingRules.Abs32(bw - bd))
                {
                    (bw, bd) = (w, d);
                }
            }
        }
        return (bw, bd);
    }

    /// <summary>Why the graph holds the shelter: a fresh plan owes the colonists a roof from day one.</summary>
    public const string ReasonShelterRoom = "shelter_day_one_room";

    /// <summary>Why the graph holds a throne room: a title asks one.</summary>
    public const string ReasonThroneRoom = "throne_room_owed";

    /// <summary>The shelter and throne nodes for s.</summary>
    public static List<RoomNode> SeedRoomNodes(SeedRooms s)
    {
        var nodes = new List<RoomNode>();
        if (s.Shelter)
        {
            var area = LayoutShelterRules.ShelterInteriorArea(s.Colonists, LayoutShelterRules.ShelterCampfires(s.Cold), LayoutShelterRules.ShelterCoolers(s.Hot));
            nodes.Add(new RoomNode { Id = Wire.Name(LayoutSurveyRules.PlannedShelter), Role = LayoutSurveyRules.PlannedShelter, Area = area, Reason = ReasonShelterRoom });
        }
        if (s.ThroneArea > 0)
        {
            nodes.Add(new RoomNode { Id = Wire.Name(LayoutThroneRules.PlannedThrone), Role = LayoutThroneRules.PlannedThrone, Area = s.ThroneArea, Reason = ReasonThroneRoom });
        }
        return nodes;
    }

    private static readonly RoomSizeTable RoomSizeTableValue = new()
    {
        Tomb = [5, 5],
        Morgue = [5, 4],
        Battery = [5, 7],
    };

    /// <summary>The interior (width, depth) of the rooms whose size the tiler does not derive from a station set.</summary>
    public static RoomSizeTable RoomSizes => RoomSizeTableValue;

    /// <summary>The role's interior from the room size table; {0, 0} for a role the table does not size.</summary>
    internal static int[] RoomSize(PlannedRole role)
    {
        var t = RoomSizeTableValue;
        if (role == LayoutSurveyRules.PlannedTomb)
        {
            return t.Tomb;
        }
        if (role == LayoutSurveyRules.PlannedMorgue)
        {
            return t.Morgue;
        }
        if (role == LayoutUtilityRules.PlannedBattery)
        {
            return t.Battery;
        }
        return [0, 0];
    }
}

partial record PlannedRoom
{
    /// <summary>
    /// The score a move of built room r must gain: the replan gain plus what building the room twice costs (the walls,
    /// floor and furniture built again), so a replan never flickers between near-equal layouts.
    /// </summary>
    internal static int SwitchThreshold(PlannedRoom r) => PlanWeights.ReplanGain + PlanWeights.BuiltSwitch + (PlanWeights.BuiltSwitchCell * r.Region.Area());
}

/// <summary>The special rooms a plan is asked to hold beyond its stations.</summary>
public sealed record SeedRooms
{
    /// <summary>Asks for the day-1 shelter for Colonists on a Cold or Hot map.</summary>
    public bool Shelter { get; init; }

    public int Colonists { get; init; }
    public bool Cold { get; init; }
    public bool Hot { get; init; }

    /// <summary>The interior cells of the throne room asked, 0 asks for none.</summary>
    public int ThroneArea { get; init; }
}

/// <summary>The interior (width, depth) of the rooms whose size the tiler does not derive from a station set, each a judgment kept in one registered table.</summary>
public sealed record RoomSizeTable
{
    public int[] Tomb { get; init; } = [0, 0];
    public int[] Morgue { get; init; } = [0, 0];
    public int[] Battery { get; init; } = [0, 0];
}

/// <summary>
/// A splitmix64 stream seeded by a site's seed cell: the seeded searches (the embedder's, the regrouper's, the tiler's
/// siting, the interior search) draw from it, so the same input gives the same result on any box and at any thread count.
/// </summary>
public sealed class SearchRand
{
    private ulong state;

    public SearchRand(Cell seed) => state = (((ulong)(uint)seed.X << 32) | (ulong)(uint)seed.Z) ^ 0x9e3779b97f4a7c15UL;

    private SearchRand(ulong raw) => state = raw;

    /// <summary>A stream whose state starts at raw, for the interior search.</summary>
    public static SearchRand FromState(ulong raw) => new(raw);

    /// <summary>The stream's current state: the siting seed a search derives its own from before any draw.</summary>
    public ulong State => state;

    public ulong Next()
    {
        unchecked
        {
            state += 0x9e3779b97f4a7c15UL;
            var z = state;
            z = (z ^ (z >> 30)) * 0xbf58476d1ce4e5b9UL;
            z = (z ^ (z >> 27)) * 0x94d049bb133111ebUL;
            return z ^ (z >> 31);
        }
    }

    /// <summary>A value in 0..n-1.</summary>
    public int Intn(int n) => (int)(Next() % (ulong)n);
}

public sealed partial record LayoutPlan
{
    /// <summary>The ids of the plan's rooms that another room of the plan replaces.</summary>
    internal HashSet<int> Superseded() => [.. Rooms.Where(r => r.Replaces != 0).Select(r => r.Replaces)];
}

partial record LayoutPlan
{
    /// <summary>A successor whose predecessor is still in the plan: a rebuild in flight.</summary>
    public static bool SuccessorPending(LayoutPlan plan)
    {
        var gone = plan.Superseded();
        return plan.Rooms.Any(r => gone.Contains(r.Id));
    }

    /// <summary>The ids of a room in a rebuild in flight and of its successor: neither moves until the predecessor leaves the plan.</summary>
    internal static HashSet<int> SuccessorPinned(LayoutPlan plan)
    {
        var have = plan.Rooms.Select(r => r.Id).ToHashSet();
        var output = new HashSet<int>();
        foreach (var r in plan.Rooms)
        {
            if (r.Replaces != 0 && have.Contains(r.Replaces))
            {
                output.Add(r.Id);
                output.Add(r.Replaces);
            }
        }
        return output;
    }
}
