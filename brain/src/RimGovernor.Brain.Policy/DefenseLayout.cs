namespace RimGovernor.Brain.Policy;

/// <summary>A defense layout or request the site cannot honour; the Go port returned these as errors.</summary>
public sealed class DefenseException(string message) : Exception(message);

/// <summary>One census row of observations_read_defense_site. Unknown facts never become free space, cover or a route.</summary>
public sealed record DefenseCell
{
    public Cell Cell { get; init; }

    public Fact<bool> Walkable { get; init; }

    public Fact<bool> Passable { get; init; }

    public Fact<bool> PlayerOwned { get; init; }

    public Fact<bool> NaturalRock { get; init; }

    public Fact<bool> EdgeReachable { get; init; }

    public Fact<bool> Door { get; init; }

    /// <summary>The cell under any roof; the mortar tier needs it known false.</summary>
    public Fact<bool> Roofed { get; init; }

    /// <summary>Whether the cell's terrain carries the funnel wall's affordance. Unknown leaves the cell sited; the native preview is the backstop.</summary>
    public Fact<bool> WallSupport { get; init; }

    /// <summary>The same for the spike trap.</summary>
    public Fact<bool> TrapSupport { get; init; }

    public Fact<double> CoverFill { get; init; }

    public string Edifice { get; init; } = "";

    /// <summary>The thing the game's designators could remove that stands on the cell, when the census identified one.</summary>
    public CoverThing Cover { get; init; } = new();
}

/// <summary>
/// Names the native buildings each tier places. Stuff empty requests the native default material. Door is the
/// safe lane's colonist-only passage (wood). Floor is the constructed terrain the firing line lays on each
/// shooter cell. Embrasure, when the game has it, is the cover a firing position gets where its cover cell is
/// the perimeter wall; empty, such positions are skipped. Bait is the cheap furniture the bait tier places.
/// </summary>
public sealed record DefenseDefinitions
{
    public string Sandbag { get; init; } = "";

    public string SandbagStuff { get; init; } = "";

    public string Wall { get; init; } = "";

    public string WallStuff { get; init; } = "";

    public string Fence { get; init; } = "";

    public string FenceStuff { get; init; } = "";

    public string Trap { get; init; } = "";

    public string TrapStuff { get; init; } = "";

    public string Door { get; init; } = "";

    public string DoorStuff { get; init; } = "";

    public string Floor { get; init; } = "";

    public string Embrasure { get; init; } = "";

    public string Bait { get; init; } = "";

    public string BaitStuff { get; init; } = "";
}

public sealed record DefenseRequest
{
    public Bounds Bounds { get; init; } = new();

    public Rectangle Region { get; init; } = new();

    /// <summary>The colony cell the approach path leads to.</summary>
    public Cell Home { get; init; }

    /// <summary>Colony doors that must keep a trap-free route to the map edge.</summary>
    public EquatableArray<Cell> Entrances { get; init; }

    public EquatableArray<DefenseCell> Cells { get; init; }

    /// <summary>Accepted footprints, walkways and doorway approaches.</summary>
    public EquatableArray<Cell> Protected { get; init; }

    public DefenseDefinitions Definitions { get; init; } = new();

    /// <summary>The shortest ranged-weapon range among defenders; unknown forbids the firing line.</summary>
    public Fact<double> MinRange { get; init; }

    public int Defenders { get; init; }

    /// <summary>Maps a definition to its per-placement cost; a missing entry leaves that tier's cost unknown.</summary>
    public IReadOnlyDictionary<string, EquatableArray<Amount>> UnitCosts { get; init; } = new Dictionary<string, EquatableArray<Amount>>();

    public DefenseTurretRequest Turret { get; init; } = new();

    public DefenseMortarRequest Mortar { get; init; } = new();

    /// <summary>Distinct, observed ground-raid arrivals in this census.</summary>
    public EquatableArray<DefenseArrival> Arrivals { get; init; }

    public Tick Tick { get; init; }

    /// <summary>The fill above which a thing counts as raider cover. Unknown disables cover selection.</summary>
    public Fact<double> CoverThreshold { get; init; }

    /// <summary>The layout plan's opening the corridor stands in.</summary>
    public DefenseKillbox Killbox { get; init; } = new();

    /// <summary>The IED traps the approach tier may place, in preference order; empty places none.</summary>
    public EquatableArray<DefenseIED> IEDs { get; init; }

    /// <summary>The threat demand (IEDBudget).</summary>
    public int IEDMax { get; init; }

    /// <summary>The stock census the tier's summed IED cost must fit; unknown places none.</summary>
    public Fact<IReadOnlyDictionary<Resource, long>> IEDStock { get; init; }

    /// <summary>Every storage cell the IED tier's blast must not reach; unknown places no IED.</summary>
    public Fact<EquatableArray<Cell>> FlammableStorage { get; init; }
}

/// <summary>
/// The layout plan's killbox opening: Entry is the centre cell of the opening's outer face, Toward points
/// inward through it, Width is the opening's width. Walled cells are the perimeter tier's wall. Depth is the
/// killbox's rows inward of the ring.
/// </summary>
public sealed record DefenseKillbox
{
    public int Depth { get; init; }

    public Cell Entry { get; init; }

    public Rotation Toward { get; init; }

    public int Width { get; init; }

    public EquatableArray<Cell> Walled { get; init; }
}

/// <summary>The name of a defense tier. Open: perimeter sections add prefixed names.</summary>
public readonly record struct DefenseTierName(string Value)
{
    public override string ToString() => Value;
}

/// <summary>One staged construction: its placements plus cells the tier reserves against later routine construction.</summary>
public sealed record DefenseTier
{
    public DefenseTierName Name { get; init; }

    public EquatableArray<Building> Buildings { get; init; }

    public EquatableArray<Cell> Reserved { get; init; }

    public Fact<EquatableArray<Amount>> Costs { get; init; }

    /// <summary>The gate that stopped the tier short of the positions the layout found; empty when nothing did.</summary>
    public string Gated { get; init; } = "";
}

/// <summary>A tier a gate stopped short of the positions the layout found.</summary>
public sealed record DefenseGated(DefenseTierName Tier, string Reason);

public readonly record struct FiringPosition(Cell Cell, Cell Cover, Cell Retreat);

/// <summary>A deterministic proposal. Native placement previews and the access audit still decide legality.</summary>
public sealed record DefenseLayout
{
    /// <summary>The corridor direction from the map edge toward Home.</summary>
    public Rotation Toward { get; init; }

    /// <summary>The passable width across the chokepoint before construction.</summary>
    public int Width { get; init; }

    /// <summary>The approach cell raiders reach first.</summary>
    public Cell Entry { get; init; }

    /// <summary>The raiders' snake in entry-to-exit order, its last cell the exit into the kill zone.</summary>
    public EquatableArray<Cell> TrapLane { get; init; }

    public EquatableArray<FiringPosition> Firing { get; init; }

    /// <summary>Every turret position with line of sight to the lane.</summary>
    public EquatableArray<TurretPosition> Turrets { get; init; }

    public EquatableArray<DefenseTier> Tiers { get; init; }

    /// <summary>Local routes and cover demand, not admitted orders.</summary>
    public DefenseApproaches Approaches { get; init; } = new();

    /// <summary>The gates that stopped the turret and IED tiers short.</summary>
    public EquatableArray<DefenseGated> Gated { get; init; }

    /// <summary>The cell raiders reach leaving the snake, the one the firing line and turrets must see; Entry for a layout without a trap lane.</summary>
    public Cell KillZone() => TrapLane.Count > 0 ? TrapLane[TrapLane.Count - 1] : Entry;
}

/// <summary>A corridor shape in killbox coordinates (X the column, Z the row).</summary>
internal sealed class Hallway
{
    public List<Cell> Lane { get; } = [];

    public List<Cell> Walls { get; } = [];

    public List<Cell> Pockets { get; } = [];

    public int Rows { get; set; }
}

internal sealed partial class DefenseSite
{
    internal DefenseSite(DefenseRequest r)
    {
        R = r;
    }

    internal DefenseRequest R { get; }

    internal Dictionary<Cell, DefenseCell> Cells { get; } = [];

    internal HashSet<Cell> Protect { get; } = [];

    internal bool InRegion(Cell c)
    {
        var reg = R.Region;
        return c.X >= reg.X && c.Z >= reg.Z && c.X < reg.X + reg.Width && c.Z < reg.Z + reg.Height;
    }

    internal bool Passable(Cell c) => Cells.TryGetValue(c, out var row) && Positive(row.Passable);

    /// <summary>Buildable: walkable, unprotected, no edifice, not a door.</summary>
    internal bool Free(Cell c) =>
        Cells.TryGetValue(c, out var row) && !Protect.Contains(c) && Positive(row.Walkable) && row.Edifice == "" && !Positive(row.Door) && !Positive(row.PlayerOwned);

    /// <summary>A cell known to stop raiders: observed impassable rock or wall.</summary>
    internal bool Blocking(Cell c) => Cells.TryGetValue(c, out var row) && row.Passable.TryGetValue(out var v) && !v;

    /// <summary>A cell inside the site whose passability the census does not know: fogged mountain.</summary>
    internal bool Unseen(Cell c) => Cells.TryGetValue(c, out var row) && !row.Passable.IsKnown;

    internal bool OnBorder(Cell c)
    {
        var reg = R.Region;
        return c.X == reg.X || c.Z == reg.Z || c.X == reg.X + reg.Width - 1 || c.Z == reg.Z + reg.Height - 1;
    }

    internal static DefenseSite Create(DefenseRequest r)
    {
        if (r.Bounds.Width <= 0 || r.Bounds.Height <= 0 || r.Bounds.Width > 4096 || r.Bounds.Height > 4096)
        {
            throw new DefenseException("invalid defense bounds");
        }
        var reg = r.Region;
        if (reg.Width <= 0 || reg.Height <= 0 || reg.X < 0 || reg.Z < 0 || reg.X + reg.Width > r.Bounds.Width || reg.Z + reg.Height > r.Bounds.Height)
        {
            throw new DefenseException("invalid defense region");
        }
        if (r.Defenders < 0 || r.Defenders > DefenseMaxDefenders)
        {
            throw new DefenseException("invalid defender count");
        }
        ValidateDefenseArrivals(r);
        if (r.MinRange.TryGetValue(out var range) && (double.IsNaN(range) || double.IsInfinity(range) || range <= 0))
        {
            throw new DefenseException("invalid minimum range");
        }
        var d = r.Definitions;
        foreach (var name in new[] { d.Sandbag, d.Wall, d.Fence, d.Trap, d.Door, d.Floor })
        {
            if (name == "")
            {
                throw new DefenseException("missing defense definition");
            }
        }
        var s = new DefenseSite(r);
        if (!s.InRegion(r.Home))
        {
            throw new DefenseException("home outside defense region");
        }
        foreach (var c in r.Cells)
        {
            if (!s.InRegion(c.Cell))
            {
                throw new DefenseException("defense cell outside region");
            }
            if (s.Cells.ContainsKey(c.Cell))
            {
                throw new DefenseException("duplicate defense cell");
            }
            if (c.CoverFill.TryGetValue(out var fill) && (double.IsNaN(fill) || fill < 0 || fill > 1))
            {
                throw new DefenseException("invalid cover fill");
            }
            s.Cells[c.Cell] = c;
        }
        foreach (var c in r.Protected)
        {
            s.Protect.Add(c);
        }
        foreach (var c in r.Entrances)
        {
            if (!s.InRegion(c))
            {
                throw new DefenseException("entrance outside defense region");
            }
        }
        return s;
    }
}

public static partial class PolicyFunctions
{
    public static readonly DefenseTierName TierChokepoint = new("chokepoint");
    public static readonly DefenseTierName TierFiringLine = new("firing_line");
    public static readonly DefenseTierName TierFunnel = new("funnel");
    public static readonly DefenseTierName TierTrapCorridor = new("trap_corridor");

    internal const int DefenseMaxWidth = 12;
    internal const int DefenseMaxDefenders = 8;

    // The killbox (following the RimWorld wiki's defense structures), in rows inward from the ring's inner face
    // and columns across the opening (column 0 is the 1-tile entrance). The corridor is a snake of zigzag legs:
    // each leg a hallway KillboxLegWidth wide across the kill zone's width, wall teeth jutting in from
    // alternating sides every KillboxToothPitch columns; legs join by U-turns at alternating ends and are parted
    // by KillboxWallThick rows of wall, as is the last leg from the kill zone, its exit gap at the last leg's
    // end. Below the snake: the kill row, the fence bar, sandbags, shooters, retreat cells and the 2-thick back
    // wall with the defenders' doorway at column 0. No door stands on the raiders' route.
    internal const int KillboxLegWidth = 3;
    internal const int KillboxWallThick = 3;
    internal const int KillboxToothPitch = 3;

    /// <summary>The rows one leg and the wall after it take.</summary>
    internal const int KillboxLegPitch = KillboxLegWidth + KillboxWallThick;

    /// <summary>The kill zone below the snake: kill row, fence bar, sandbags, shooters, retreat cells and the 2-thick back wall.</summary>
    internal const int KillboxZoneRows = 7;

    /// <summary>The narrowest kill zone: a leg long enough for two teeth.</summary>
    internal const int KillboxMinHalf = 4;

    /// <summary>Bounds the legs; the layout plan reserves depth for them.</summary>
    internal const int KillboxMaxLegs = 2;

    /// <summary>The killbox's depth the layout plan reserves.</summary>
    internal const int KillboxRows = (KillboxMaxLegs * KillboxLegPitch) + KillboxZoneRows;

    /// <summary>
    /// The snake of zigzag legs between columns -half and half. Leg 0 holds the entrance and runs toward +half;
    /// each U-turn opens KillboxLegWidth columns of the wall below a leg's end. Within a leg a tooth stands every
    /// KillboxToothPitch columns, two cells long from the top or bottom row in turn. The pockets are the
    /// tooth-side cells beside each tooth and the outer corners of each U-turn.
    /// </summary>
    internal static Hallway SnakeCorridor(int half, int legs)
    {
        var h = new Hallway();
        for (var k = -PerimeterThick; k < 0; k++)
        {
            h.Lane.Add(new Cell(0, k));
        }
        int low = -half, high = half;
        h.Rows = legs * KillboxLegPitch;
        for (var k = 0; k < h.Rows; k++)
        {
            h.Walls.Add(new Cell(low - 1, k));
            h.Walls.Add(new Cell(high + 1, k));
        }
        for (var leg = 0; leg < legs; leg++)
        {
            var top = leg * KillboxLegPitch;
            var bottom = top + KillboxLegWidth - 1;
            // The leg runs from start toward end in steps of dir.
            int start = low, end = high, dir = 1;
            if (leg % 2 == 1)
            {
                start = high;
                end = low;
                dir = -1;
            }
            var tooth = new HashSet<Cell>();
            var fromTop = leg % 2 == 0;
            // Leg 0's teeth stand past the entrance: the cells behind it are a dead end.
            var first = start + (dir * 2);
            if (leg == 0)
            {
                first = 2;
            }
            for (var a = first; dir * (end - a) >= 2; a += dir * KillboxToothPitch)
            {
                int[] rows = fromTop ? [top, top + 1] : [bottom - 1, bottom];
                var side = fromTop ? top : bottom;
                foreach (var k in rows)
                {
                    tooth.Add(new Cell(a, k));
                    h.Walls.Add(new Cell(a, k));
                }
                h.Pockets.Add(new Cell(a - dir, side));
                h.Pockets.Add(new Cell(a + dir, side));
                fromTop = !fromTop;
            }
            for (var i = 0; i <= high - low; i++)
            {
                for (var k = top; k <= bottom; k++)
                {
                    var c = new Cell(start + (dir * i), k);
                    if (!tooth.Contains(c))
                    {
                        h.Lane.Add(c);
                    }
                }
            }
            // The wall below the leg, open under its last KillboxLegWidth columns: the U-turn into the next leg, or the exit.
            var exit = default(Cell);
            for (var k = bottom + 1; k <= bottom + KillboxWallThick; k++)
            {
                for (var i = 0; i <= high - low; i++)
                {
                    var c = new Cell(start + (dir * i), k);
                    if (i < high - low + 1 - KillboxLegWidth)
                    {
                        h.Walls.Add(c);
                    }
                    else if (leg == legs - 1 && k == bottom + KillboxWallThick && i == high - low - 1)
                    {
                        exit = c;
                    }
                    else
                    {
                        h.Lane.Add(c);
                    }
                }
            }
            // The U-turn's outer corners: where the leg meets the far wall, and where the next leg leaves it.
            h.Pockets.Add(new Cell(end, top));
            if (leg == legs - 1)
            {
                h.Lane.Add(exit);
            }
            else
            {
                h.Pockets.Add(new Cell(end, bottom + KillboxLegPitch));
            }
        }
        return h;
    }

    internal static readonly Cell[] Directions = [new(0, 1), new(1, 0), new(0, -1), new(-1, 0)];

    internal static Rotation RotationOf(Cell d)
    {
        if (d == Directions[0])
        {
            return Rotation.North;
        }
        if (d == Directions[1])
        {
            return Rotation.East;
        }
        if (d == Directions[2])
        {
            return Rotation.South;
        }
        return Rotation.West;
    }

    internal static Cell AddCell(Cell a, Cell b) => new(a.X + b.X, a.Z + b.Z);

    internal static Cell Scale(Cell d, int n) => new(d.X * n, d.Z * n);

    internal static Cell Perpendicular(Cell d) => new(-d.Z, d.X);

    /// <summary>Sizes the kill zone from the defenders and turrets, within the plan's killbox.</summary>
    internal static (int Half, int Legs) DefenseShape(DefenseRequest r)
    {
        var half = r.Defenders / 2;
        var q = r.Turret;
        if (q.Definition != "" && q.Max > 0)
        {
            var size = Math.Max(Math.Max(q.Size.Width, q.Size.Height), 1);
            half += (TurretSpacing + size - 1) * ((q.Max + 1) / 2);
        }
        half = Math.Min(Math.Max(half, KillboxMinHalf), KillboxHalf - 1);
        var legs = Math.Min((r.Killbox.Depth - KillboxZoneRows) / KillboxLegPitch, KillboxMaxLegs);
        if (legs < 1)
        {
            throw new DefenseException("killbox too shallow for the corridor");
        }
        return (half, legs);
    }

    /// <summary>
    /// The killbox's role cells for the shared rock step: the raiders' lane and the defenders' ground below it
    /// need a floor, so rock on them is dug before the layout stands; the walls beside the lane and around the
    /// kill zone block, so rock there stays. Cells outside the site, passable cells, and cells the site reads as
    /// a wall or building rather than natural rock are left out; a fogged cell counts as rock.
    /// </summary>
    public static IReadOnlyList<RoleCell> DefenseRockCells(DefenseRequest r)
    {
        var s = DefenseSite.Create(r);
        var kb = r.Killbox;
        if (kb.Width < 1 || kb.Width > DefenseMaxWidth || !s.InRegion(kb.Entry))
        {
            throw new DefenseException("no killbox opening to anchor the corridor on");
        }
        var (half, legs) = DefenseShape(r);
        var d = DirectionOf(kb.Toward);
        var p = Perpendicular(d);
        Cell At(int k, int a) => AddCell(kb.Entry, AddCell(Scale(d, PerimeterThick + k), Scale(p, a)));
        var hall = SnakeCorridor(half, legs);
        var want = new List<RoleCell>();
        foreach (var c in hall.Lane)
        {
            want.Add(new RoleCell(At(c.Z, c.X), RockRole.NeedsFloor));
        }
        foreach (var c in hall.Walls)
        {
            want.Add(new RoleCell(At(c.Z, c.X), RockRole.Blocks));
        }
        var fenceRow = hall.Rows + 1;
        var backWall = fenceRow + 4;
        for (var k = hall.Rows; k < backWall; k++)
        {
            for (var a = -half; a <= half; a++)
            {
                want.Add(new RoleCell(At(k, a), RockRole.NeedsFloor));
            }
            want.Add(new RoleCell(At(k, -half - 1), RockRole.Blocks));
            want.Add(new RoleCell(At(k, half + 1), RockRole.Blocks));
        }
        want.Add(new RoleCell(At(backWall, 0), RockRole.NeedsFloor));
        want.Add(new RoleCell(At(backWall + 1, 0), RockRole.NeedsFloor));
        for (var k = backWall; k < backWall + 2; k++)
        {
            for (var a = -half - 1; a <= half + 1; a++)
            {
                if (a != 0)
                {
                    want.Add(new RoleCell(At(k, a), RockRole.Blocks));
                }
            }
        }
        var output = new List<RoleCell>();
        foreach (var c in want)
        {
            if (!s.Cells.TryGetValue(c.Cell, out var row) || Positive(row.Passable))
            {
                continue;
            }
            if (row.Passable.IsKnown && !Positive(row.NaturalRock))
            {
                continue; // a wall or building, not rock
            }
            output.Add(c);
        }
        return output;
    }

    /// <summary>
    /// Proposes a staged corridor defense in the request's killbox opening. Throws without an opening or when
    /// the corridor cannot stand there, and never places on protected, occupied or unknown cells.
    /// </summary>
    public static DefenseLayout DefenseLayouts(DefenseRequest r)
    {
        var s = DefenseSite.Create(r);
        // The layout anchors on the layout plan's killbox opening: the corridor runs inward from the opening's
        // outer face, across the opening's width, and the wall ring beside it is the perimeter tier's.
        var kb = r.Killbox;
        if (kb.Width < 1 || kb.Width > DefenseMaxWidth || !s.InRegion(kb.Entry))
        {
            throw new DefenseException("no killbox opening to anchor the corridor on");
        }
        var walled = new HashSet<Cell>(kb.Walled);
        var d = DirectionOf(kb.Toward);
        var p = Perpendicular(d);
        var entry = kb.Entry;
        // at is a killbox cell: row k inward from the ring's inner face (the ring rows are -PerimeterThick..-1), column a across the opening.
        Cell At(int k, int a) => AddCell(entry, AddCell(Scale(d, PerimeterThick + k), Scale(p, a)));
        var layout = new DefenseLayout { Toward = RotationOf(d), Width = kb.Width, Entry = entry };
        var (half, legs) = DefenseShape(r);
        int low = -half, high = half;
        var hall = SnakeCorridor(half, legs);
        var trapLane = hall.Lane.Select(c => At(c.Z, c.X)).ToList();
        layout = layout with { TrapLane = EquatableArray.From(trapLane) };
        foreach (var c in trapLane)
        {
            if (!s.Passable(c))
            {
                throw new DefenseException("corridor is not passable end to end");
            }
        }
        var exit = layout.KillZone();
        var lane = new HashSet<Cell>(trapLane);

        var funnel = new List<Building>();
        var corridor = new List<Building>();
        var line = new List<Building>();
        var funnelReserved = new List<Cell>();
        var firingReserved = new List<Cell>();
        var firing = new List<FiringPosition>();
        var costs = new CorridorCosts();
        foreach (var c in walled)
        {
            costs.Closed.Add(c);
        }

        void Build(List<Building> into, string definition, string stuff, Cell c, string what)
        {
            if (!s.Free(c))
            {
                throw new DefenseException($"{what} cell {c} is not buildable");
            }
            into.Add(new Building(definition, c, Rotation.North, stuff));
        }

        var cellsToWall = new List<Cell>();
        // The ring opening narrows to the 1-tile entrance.
        var openLow = -kb.Width / 2;
        for (var a = openLow; a < openLow + kb.Width; a++)
        {
            for (var k = -PerimeterThick; k < 0 && a != 0; k++)
            {
                cellsToWall.Add(At(k, a));
            }
        }
        foreach (var c in hall.Walls)
        {
            cellsToWall.Add(At(c.Z, c.X));
        }
        // The kill zone's sides and its 2-thick back wall.
        int killRow = hall.Rows, fenceRow = hall.Rows + 1;
        var backWall = fenceRow + 4;
        for (var k = killRow; k < backWall; k++)
        {
            cellsToWall.Add(At(k, low - 1));
            cellsToWall.Add(At(k, high + 1));
        }
        for (var k = backWall; k < backWall + 2; k++)
        {
            for (var a = low - 1; a <= high + 1; a++)
            {
                if (a != 0)
                {
                    cellsToWall.Add(At(k, a));
                }
            }
        }
        // A wall-role cell on observed rock stays rock, and so does one in unseen mountain (RockStep leaves it
        // too): the lane cells beside it are observed, so fog here is mass between the legs, not an opening.
        var candidates = new HashSet<Cell>();
        foreach (var c in cellsToWall)
        {
            if (!lane.Contains(c) && !costs.Closed.Contains(c) && !s.Blocking(c) && !s.Unseen(c))
            {
                candidates.Add(c);
            }
        }
        foreach (var c in cellsToWall)
        {
            if (!candidates.Contains(c))
            {
                continue;
            }
            // Terrain that cannot carry the wall is left open, not closed: the raider route check below refuses
            // the layout if the gap lets them round the corridor, instead of the native preview holding it.
            if (s.Cells[c].WallSupport.TryGetValue(out var supported) && !supported)
            {
                continue;
            }
            Build(funnel, r.Definitions.Wall, r.Definitions.WallStuff, c, "wall");
            funnelReserved.Add(c);
            costs.Closed.Add(c);
        }
        // The defenders' doorway through the back wall.
        for (var k = backWall; k < backWall + 2; k++)
        {
            var c = At(k, 0);
            Build(funnel, r.Definitions.Door, r.Definitions.DoorStuff, c, "door");
            funnelReserved.Add(c);
            costs.Cost[c] = PathWoodDoorCost;
            costs.Full.Add(c);
        }
        // Spike traps on the raiders' cheapest line at each turn: hostile pawns do not know the player's traps
        // and walk over them, while colonists price their faction's known traps and take the hallway's
        // trap-free side. Never beside another (PlaceWorker_NeverAdjacentTrap), never where the colonists'
        // cheapest hallway route would cross one.
        var raiderLine = s.CheapestRoutes(RaiderCosts(costs, []), entry, [exit]).Route;
        var sites = new List<(Cell Cell, int Near)>();
        foreach (var c in raiderLine ?? [])
        {
            if (!lane.Contains(c) || c == entry || c == exit)
            {
                continue;
            }
            var near = int.MaxValue;
            foreach (var q in hall.Pockets)
            {
                var t = At(q.Z, q.X);
                near = Math.Min(near, Math.Max(Math.Abs(t.X - c.X), Math.Abs(t.Z - c.Z)));
            }
            if (near <= 2)
            {
                sites.Add((c, near));
            }
        }
        var order = new Dictionary<Cell, int>();
        for (var i = 0; i < trapLane.Count; i++)
        {
            order[trapLane[i]] = i;
        }
        sites = sites.OrderBy(x => x.Near).ThenBy(x => order[x.Cell]).ToList();
        bool unsupportedTrap = false;
        var trapsSited = 0;
        foreach (var st in sites)
        {
            var t = st.Cell;
            // Terrain that cannot carry the trap is skipped for the next corridor cell; native would refuse it every round.
            if (s.Cells[t].TrapSupport.TryGetValue(out var supported) && !supported)
            {
                unsupportedTrap = true;
                continue;
            }
            var beside = costs.Traps.Contains(t);
            foreach (var step in PathSteps)
            {
                beside = beside || costs.Traps.Contains(AddCell(t, step));
            }
            if (beside || !s.Free(t))
            {
                continue;
            }
            costs.Traps.Add(t);
            if (!s.HallwayAvoidsTraps(costs, lane, exit, entry))
            {
                costs.Traps.Remove(t);
                continue;
            }
            Build(corridor, r.Definitions.Trap, r.Definitions.TrapStuff, t, "trap");
            trapsSited++;
        }
        if (unsupportedTrap && trapsSited == 0)
        {
            throw new DefenseException("no corridor cell has terrain that supports a trap");
        }
        // The fence T: a continuous bar across the kill zone and a stem toward the exit, so raiders climb
        // fences under fire. Raider-side shaping is walls and fences only: in 1.6 a pawn stands on a sandbag
        // or barricade cell and still takes its cover, so one there would hand raiders a firing position.
        // Sandbags stand only on the defenders' line.
        var fences = new List<Cell> { At(killRow, hall.Lane[^1].X) };
        for (var a = low; a <= high; a++)
        {
            fences.Add(At(fenceRow, a));
        }
        foreach (var c in fences)
        {
            Build(corridor, r.Definitions.Fence, r.Definitions.FenceStuff, c, "fence");
            costs.Cost[c] = PathFenceCost;
        }
        // Firing line: sandbags behind the fence bar, floored shooter cells behind them and a free retreat cell
        // before the back wall, centred on column 0, each within MinRange of the exit and in the game's line of
        // sight of it over the census.
        if (r.MinRange.TryGetValue(out var minRange) && r.Defenders > 0)
        {
            foreach (var o in new[] { 0, 1, -1, 2, -2, 3, -3, 4, -4, 5, -5, 6, -6 })
            {
                if (firing.Count >= r.Defenders)
                {
                    break;
                }
                if (o < low || o > high)
                {
                    continue;
                }
                Cell cover = At(fenceRow + 1, o), shooter = At(fenceRow + 2, o), retreat = At(fenceRow + 3, o);
                if (!s.Free(cover) || !s.Free(shooter) || !s.Free(retreat))
                {
                    continue;
                }
                if (Math.Sqrt(SquaredDistance(shooter, exit)) > minRange)
                {
                    continue;
                }
                if (!s.LineOfSight(shooter, exit).TryGetValue(out var los) || !los)
                {
                    continue;
                }
                // A cover cell on the perimeter wall is an embrasure in the wall rather than wall plus sandbag.
                var coverDef = r.Definitions.Sandbag;
                var coverStuff = r.Definitions.SandbagStuff;
                if (walled.Contains(cover))
                {
                    if (r.Definitions.Embrasure == "")
                    {
                        continue;
                    }
                    coverDef = r.Definitions.Embrasure;
                    coverStuff = r.Definitions.WallStuff;
                }
                var b = new Building(coverDef, cover, Rotation.North, coverStuff);
                var floor = new Building(r.Definitions.Floor, shooter, Rotation.North, "");
                line.Add(b);
                line.Add(floor);
                firingReserved.Add(cover);
                firingReserved.Add(shooter);
                firingReserved.Add(retreat);
                firing.Add(new FiringPosition(shooter, cover, retreat));
            }
        }
        layout = layout with { Firing = EquatableArray.From(firing) };
        // Every colony entrance and Home keep a route to the map edge once the layout stands, and no cheapest
        // colonist route crosses a trap, nor does one through the hallway from the kill zone to the entrance.
        foreach (var start in new[] { s.R.Home }.Concat(s.R.Entrances))
        {
            if (s.Passable(start) && !s.ColonistRouteAvoidsTraps(costs, start))
            {
                throw new DefenseException("layout would cut an entrance off from the map edge or route colonists over a trap");
            }
        }
        if (!s.HallwayAvoidsTraps(costs, lane, exit, entry))
        {
            throw new DefenseException("colonists would cross a trap walking the hallway");
        }
        // Raiders walk every leg into the kill zone rather than bash through a wall.
        var killCells = new List<Cell>();
        for (var a = low; a <= high; a++)
        {
            killCells.Add(At(killRow, a));
        }
        if (!s.RaiderWalksCorridor(costs, layout, funnelReserved, killCells))
        {
            throw new DefenseException("raiders would bash through the corridor");
        }
        // The chokepoint tier places nothing: it reserves the corridor and the kill row so routine construction never fills them.
        var reservedLanes = new List<Cell>(trapLane);
        foreach (var c in killCells)
        {
            if (!costs.Cost.TryGetValue(c, out var v) || v == 0)
            {
                reservedLanes.Add(c);
            }
        }
        var tiers = new List<DefenseTier>
        {
            new() { Name = TierChokepoint, Reserved = EquatableArray.From(reservedLanes), Costs = Known(EquatableArray.From(new List<Amount>())) },
            new() { Name = TierFiringLine, Buildings = EquatableArray.From(line), Reserved = EquatableArray.From(firingReserved), Costs = TierCosts(r.UnitCosts, line) },
            new() { Name = TierFunnel, Buildings = EquatableArray.From(funnel), Reserved = EquatableArray.From(funnelReserved), Costs = TierCosts(r.UnitCosts, funnel) },
            new() { Name = TierTrapCorridor, Buildings = EquatableArray.From(corridor), Reserved = EquatableArray.From(reservedLanes), Costs = TierCosts(r.UnitCosts, corridor) },
        };
        var gated = new List<DefenseGated>();
        layout = layout with { Tiers = EquatableArray.From(tiers) };
        if (r.Turret.Definition != "")
        {
            var (turrets, turretCandidates) = s.Turrets(layout.Geometry());
            layout = layout with { Turrets = EquatableArray.From(turretCandidates) };
            tiers.Add(turrets);
            layout = layout with { Tiers = EquatableArray.From(tiers) };
            if (turrets.Gated != "")
            {
                gated.Add(new DefenseGated(TierTurrets, turrets.Gated));
            }
        }
        layout = layout with { Approaches = s.ComputeApproaches(layout) };
        var bait = s.BaitTier(layout);
        if (bait.Buildings.Count > 0)
        {
            tiers.Add(bait);
            layout = layout with { Tiers = EquatableArray.From(tiers) };
        }
        var ieds = s.IedTier(layout, costs);
        if (ieds.Buildings.Count > 0)
        {
            tiers.Add(ieds);
            layout = layout with { Tiers = EquatableArray.From(tiers) };
        }
        if (ieds.Gated != "")
        {
            gated.Add(new DefenseGated(TierIEDs, ieds.Gated));
        }
        return layout with { Gated = EquatableArray.From(gated) };
    }

    internal static Fact<EquatableArray<Amount>> TierCosts(IReadOnlyDictionary<string, EquatableArray<Amount>> unit, IEnumerable<Building> buildings)
    {
        var total = new Dictionary<Resource, long>();
        foreach (var b in buildings)
        {
            if (!unit.TryGetValue(b.Definition, out var costs))
            {
                return Unknown<EquatableArray<Amount>>();
            }
            foreach (var a in costs)
            {
                total[a.Resource] = total.GetValueOrDefault(a.Resource) + a.Count;
            }
        }
        var output = total
            .Select(kv => new Amount(kv.Key, kv.Value))
            .OrderBy(a => a.Resource.Value, StringComparer.Ordinal)
            .ToList();
        return Known(EquatableArray.From(output));
    }
}

internal sealed partial class DefenseSite
{
    /// <summary>Reports whether colonists walking the hallway alone from exit to entry have a cheapest route that crosses no trap.</summary>
    internal bool HallwayAvoidsTraps(CorridorCosts k, HashSet<Cell> lane, Cell exit, Cell entry)
    {
        var (route, ok) = CheapestRoutes(k.WithWithin(lane), exit, [entry]);
        return ok && !CrossesAny(route!, k.Traps);
    }
}
