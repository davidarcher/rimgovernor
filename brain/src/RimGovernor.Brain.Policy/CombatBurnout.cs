namespace RimGovernor.Brain.Policy;

// Go: combat_burnout.go
//
// The burn-out is the infestation's one heat tactic: it cooks every insect down together in its own sealed,
// roofed room. Every open cell of a square ring around the hive is walled but one, and from that gap a 1-wide
// corridor of BurnCorridorDoors stone doors in series runs toward the colony, walled on both flanks. Wood
// stools stand inside the seal, spread out and away from the hive, as fuel. Once the census
// (CombatView.Burn) shows the room roofed and every seal wall, corridor door and stool standing, one molotov
// lights a stool clear of the insects (a player's flame on an insect may send the hive to assault) and every
// colonist waits past the last door. While the hive reads under BurnTopUpC another molotov tops the fire up;
// none is thrown at or above BurnMaxC, below the 210 C air burn whose memo also turns the hive. Every insect
// breathes the same air, so heat stroke downs them together (about HeatStrokeTicks at 150 C); the retreat
// holds until the frame shows every insect downed, then the fight re-enters and finishes them before the
// stroke kills. The doors hold any straggler.

/// <summary>
/// The combat step's census of a burn-out: how many seal walls, corridor doors and stools it still lacks,
/// whether its sealed room is roofed, and the stools standing.
/// </summary>
public sealed record BurnSite
{
    public int Missing { get; init; }

    public bool Roofed { get; init; }

    public EquatableArray<Cell> Fuel { get; init; }
}

/// <summary>
/// An infestation fight's burn-out: the hive it cooks, the corridor's direction out of the seal (a unit
/// step), the retreat past the last corridor door, the molotov carrier, the stop it was lit (0 while the seal
/// or fuel is short) and the stop of its last molotov. Done once every insect is down, the fire is out, or the
/// room reads unroofed.
/// </summary>
public sealed record CombatBurn
{
    public Cell Hive { get; set; }

    public Cell Exit { get; set; }

    public Rectangle Room { get; set; }

    public PawnId Carrier { get; set; }

    public Tick Lit { get; set; }

    public Tick Thrown { get; set; }

    public bool Done { get; set; }

    /// <summary>The cell k steps from the hive along the exit, side steps across it.</summary>
    internal Cell Step(int k, int side) => new(Hive.X + (Exit.X * k) + (Exit.Z * side), Hive.Z + (Exit.Z * k) + (Exit.X * side));

    /// <summary>The corridor's door cells, from the seal's gap out.</summary>
    public List<Cell> CorridorDoors()
    {
        var output = new List<Cell>(PolicyFunctions.BurnCorridorDoors);
        for (var k = 0; k < PolicyFunctions.BurnCorridorDoors; k++)
        {
            output.Add(Step(PolicyFunctions.BurnSealRadius + k, 0));
        }

        return output;
    }
}

/// <summary>Go's nil-receiver methods of the burn-out pointer.</summary>
public static class CombatBurnExtensions
{
    /// <summary>A burn-out waiting on its seal and fuel: the layout planner's cue.</summary>
    public static bool Fueling(this CombatBurn? b) => b is not null && b.Lit.Value == 0 && !b.Done;

    /// <summary>A burn-out not yet done: the combat step censuses it.</summary>
    public static bool Active(this CombatBurn? b) => b is not null && !b.Done;

    /// <summary>A lit burn-out still holding its retreat.</summary>
    internal static bool Retreating(this CombatBurn? b) => b is not null && b.Lit.Value > 0 && !b.Done;
}

public static partial class PolicyFunctions
{
    /// <summary>The fuel: cheap wood furniture, the bait stool.</summary>
    public const string BurnFuelDef = "Stool";

    public const string BurnFuelStuff = "WoodLog";

    /// <summary>The seal's walls, built in stone: the block ItemFacts.StoneBlock picks.</summary>
    public const string BurnWallDef = "Wall";

    /// <summary>The seal's corridor doors, built in stone.</summary>
    public const string BurnDoorDef = "Door";

    /// <summary>Bounds the fuel around the hive (Chebyshev); the seal ring lies one cell beyond it.</summary>
    public const int BurnFuelRadius = 3;

    /// <summary>No stool stands nearer the hive than this.</summary>
    private const int BurnFuelClear = 2;

    internal const int BurnSealRadius = BurnFuelRadius + 1;

    internal const int BurnCorridorDoors = 3;

    /// <summary>Size the retreat just past the last door.</summary>
    private const int BurnRetreatDepth = 2;

    private const int BurnRetreatWidth = 3;

    private const int BurnFuelStools = 4;

    /// <summary>The least distance from a molotov's target stool to any insect.</summary>
    private const double BurnInsectClear = 2.5;

    /// <summary>How long the carrier keeps a throw before it retreats.</summary>
    private const long BurnThrowTicks = 300;

    /// <summary>The least wait between molotovs, and how long a fire with no fuel left is given before a cool hive reads as out.</summary>
    private const long BurnFireTicks = 1200;

    /// <summary>The hive temperature below which a molotov goes in.</summary>
    private const double BurnTopUpC = 150.0;

    /// <summary>One no molotov is ever thrown at.</summary>
    private const double BurnMaxC = 200.0;

    /// <summary>The hive temperature above which no melee fighter walks in at anything but a downed insect.</summary>
    private const double HeatEntryMaxC = 50.0;

    /// <summary>
    /// How long 150 C air takes to down an insect (HediffGiver_Heat): every insect's comfortable maximum is
    /// 60 C, so heat stroke builds above 70 C, by curve(150-70)*6.45e-5 = 0.00335 per 60-tick interval, to
    /// 0.62 (consciousness 0.1, downed) in about 185 intervals. Death at 1.0 follows some 6.7k ticks later:
    /// the window to finish them.
    /// </summary>
    private const long HeatStrokeTicks = 11100;

    /// <summary>The census rectangle a burn-out reads, as min and max corners: its fuel, seal, corridor and retreat.</summary>
    public static (Cell Min, Cell Max) BurnRegion(Cell hive)
    {
        const int r = BurnSealRadius + BurnCorridorDoors + BurnRetreatDepth;
        return (new Cell(hive.X - r, hive.Z - r), new Cell(hive.X + r, hive.Z + r));
    }

    /// <summary>The unit step from hive toward toward on its dominant axis; ties go to z.</summary>
    private static Cell BurnExit(Cell hive, Cell toward)
    {
        int dx = toward.X - hive.X, dz = toward.Z - hive.Z;
        if (Math.Abs(dx) > Math.Abs(dz) && dx > 0)
        {
            return new Cell(1, 0);
        }

        if (Math.Abs(dx) > Math.Abs(dz))
        {
            return new Cell(-1, 0);
        }

        if (dz < 0)
        {
            return new Cell(0, -1);
        }

        return new Cell(0, 1);
    }

    /// <summary>The rectangle just past the last corridor door.</summary>
    private static Rectangle BurnRetreat(CombatBurn b)
    {
        const int near = BurnSealRadius + BurnCorridorDoors;
        var a = b.Step(near, -BurnRetreatWidth / 2);
        var z = b.Step(near + BurnRetreatDepth - 1, BurnRetreatWidth / 2);
        return new Rectangle(Math.Min(a.X, z.X), Math.Min(a.Z, z.Z), Math.Abs(a.X - z.X) + 1, Math.Abs(a.Z - z.Z) + 1);
    }

    /// <summary>A fuel cell of the hive: inside the seal, clear of the hive.</summary>
    private static bool BurnFuelCell(Cell hive, Cell c)
    {
        var d = Chebyshev(c, hive);
        return d >= BurnFuelClear && d <= BurnFuelRadius;
    }

    /// <summary>The fuel the census shows inside the seal, by cell.</summary>
    public static List<Cell> BurnFuelStanding(Cell hive, IReadOnlyDictionary<Cell, WaitDoorCell> census)
    {
        var output = new List<Cell>();
        foreach (var (c, at) in census)
        {
            if (at.Edifice == BurnFuelDef && BurnFuelCell(hive, c))
            {
                output.Add(c);
            }
        }

        output.Sort(CellOrder);
        return output;
    }

    /// <summary>Orders cells by x, then z.</summary>
    internal static int CellOrder(Cell a, Cell b) => a.X != b.X ? a.X - b.X : a.Z - b.Z;

    /// <summary>Every census cell inside the seal roofed: an open sky vents the heat. A cell the census does not carry is not read.</summary>
    public static bool BurnRoofed(Cell hive, IReadOnlyDictionary<Cell, WaitDoorCell> census)
    {
        foreach (var (c, at) in census)
        {
            if (Chebyshev(c, hive) < BurnSealRadius && !at.Roofed)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The combat step's BurnSite from a census.</summary>
    public static BurnSite BurnSurvey(CombatBurn b, IReadOnlyDictionary<Cell, WaitDoorCell> census, ItemFacts items)
    {
        var stone = items.StoneBlock(null);
        var missing = BurnBuilds(b, census, stone.Value, items);
        return new BurnSite
        {
            Missing = missing.Count,
            Roofed = BurnRoofed(b.Hive, census),
            Fuel = EquatableArray.From(BurnFuelStanding(b.Hive, census)),
        };
    }

    /// <summary>
    /// The seal and corridor still to build: a stone wall on every open (walkable, edifice-free) cell of the
    /// ring BurnSealRadius around the hive except the corridor's gap, a wall on each open flank cell beside
    /// the doors past the gap, and a stone door on each corridor cell not already holding one (a door of other
    /// stuff, per the census's edifice stuff read, is rebuilt in stone). A ring or flank cell the census does
    /// not carry is left alone; a door is always asked for.
    /// </summary>
    public static List<Building> BurnSeal(CombatBurn b, IReadOnlyDictionary<Cell, WaitDoorCell> census, string stone, ItemFacts items)
    {
        var doors = b.CorridorDoors();
        var cells = new List<Cell>();
        for (var x = b.Hive.X - BurnSealRadius; x <= b.Hive.X + BurnSealRadius; x++)
        {
            for (var z = b.Hive.Z - BurnSealRadius; z <= b.Hive.Z + BurnSealRadius; z++)
            {
                var c = new Cell(x, z);
                if (Chebyshev(c, b.Hive) == BurnSealRadius && c != doors[0])
                {
                    cells.Add(c);
                }
            }
        }

        for (var k = 1; k < BurnCorridorDoors; k++)
        {
            cells.Add(b.Step(BurnSealRadius + k, -1));
            cells.Add(b.Step(BurnSealRadius + k, 1));
        }

        var output = new List<Building>();
        foreach (var c in cells)
        {
            if (census.TryGetValue(c, out var at) && at.Edifice.Length == 0 && at.Walkable)
            {
                output.Add(new Building(BurnWallDef, c, Rotation.North, stone));
            }
        }

        foreach (var c in doors)
        {
            if (!census.TryGetValue(c, out var at) || at.Edifice != BurnDoorDef || !items.IsStoneBlocks(new Resource(at.Stuff)))
            {
                output.Add(new Building(BurnDoorDef, c, Rotation.North, stone));
            }
        }

        return output;
    }

    /// <summary>Everything the burn-out still needs standing: its seal and corridor, then its fuel. The fight lights on none missing.</summary>
    public static List<Building> BurnBuilds(CombatBurn b, IReadOnlyDictionary<Cell, WaitDoorCell> census, string stone, ItemFacts items)
    {
        var seal = BurnSeal(b, census, stone, items);
        seal.AddRange(BurnFuel(b.Hive, census));
        return seal;
    }

    /// <summary>
    /// The fuel still to build, enough to bring the standing fuel to BurnFuelStools: stools on free walkable
    /// census cells inside the seal and clear of the hive, spread for an even heat. Each next stool takes the
    /// free cell farthest from every stool standing or chosen (the first, with none, the farthest from the
    /// hive), then the farther from the hive, then the lower x, z.
    /// </summary>
    public static List<Building> BurnFuel(Cell hive, IReadOnlyDictionary<Cell, WaitDoorCell> census)
    {
        var placed = BurnFuelStanding(hive, census);
        var free = new List<Cell>();
        foreach (var (c, at) in census)
        {
            if (at.Edifice.Length == 0 && at.Walkable && BurnFuelCell(hive, c))
            {
                free.Add(c);
            }
        }

        free.Sort(CellOrder);
        var output = new List<Building>();
        while (placed.Count < BurnFuelStools && free.Count > 0)
        {
            (long Near, long Hive) Score(Cell c)
            {
                var near = -1L;
                foreach (var p in placed)
                {
                    var d = Distance2(c, p);
                    if (near < 0 || d < near)
                    {
                        near = d;
                    }
                }

                return (near, Distance2(c, hive));
            }

            var best = 0;
            for (var i = 0; i < free.Count; i++)
            {
                var (n, h) = Score(free[i]);
                var (bn, bh) = Score(free[best]);
                if (n > bn || (n == bn && h > bh))
                {
                    best = i;
                }
            }

            var chosen = free[best];
            free.RemoveAt(best);
            placed.Add(chosen);
            output.Add(new Building(BurnFuelDef, chosen, Rotation.North, BurnFuelStuff));
        }

        return output;
    }

    /// <summary>
    /// The standing stool a molotov lights: the one farthest from the nearest insect, at least
    /// BurnInsectClear from every insect (then the lower x, z); none when no stool is clear.
    /// </summary>
    private static Cell? BurnTarget(IReadOnlyList<Cell> fuel, IReadOnlyList<Cell> insects)
    {
        Cell best = default;
        var bestNear = 0.0;
        var found = false;
        foreach (var c in fuel)
        {
            var near = -1.0;
            foreach (var i in insects)
            {
                var d = Dist(c, i);
                if (near < 0 || d < near)
                {
                    near = d;
                }
            }

            if (near >= 0 && near < BurnInsectClear)
            {
                continue;
            }

            if (near < 0)
            {
                near = 1e9;
            }

            if (!found || near > bestNear)
            {
                best = c;
                bestNear = near;
                found = true;
            }
        }

        return found ? best : null;
    }

    /// <summary>The infestation's live hostile pawns: their cells, and whether every one reads downed.</summary>
    private static (List<Cell> Cells, bool Down) BurnInsects(CombatView view)
    {
        var state = new Dictionary<PawnId, CombatPawnState>();
        foreach (var p in view.Pawns.Items)
        {
            state[p.Id] = p;
        }

        var cells = new List<Cell>();
        var down = true;
        foreach (var t in view.Threats.Items)
        {
            var s = state.GetValueOrDefault(ThreatPawn(t)) ?? new CombatPawnState();
            if (t.Building || Positive(t.Dead) || s.Dead)
            {
                continue;
            }

            if (!Positive(t.Downed) && !s.Downed)
            {
                down = false;
            }

            if (s.Cell.TryGetValue(out var c))
            {
                cells.Add(c);
            }
        }

        return (cells, down);
    }

    /// <summary>
    /// Runs the burn-out on an infestation fight with a live hive. It plans the burn once a molotov carrier
    /// has the hive in reach, its corridor leading out of the seal toward the carrier. While the census is
    /// unread, or any seal wall, corridor door or stool is missing, no molotov is thrown; a room that reads
    /// unroofed ends the burn. With all of it standing the carrier lights a stool clear of the insects,
    /// everyone else takes the retreat past the last door, behind the corridor's closed, forbidden doors
    /// (while waiting), and the carrier follows after BurnThrowTicks. While the hive reads under BurnTopUpC,
    /// at most one molotov per BurnFireTicks tops the fire up. The retreat holds until every insect reads
    /// downed (or a fire with no fuel left has cooled), then the burn is done, the wait releases and every
    /// fighter finishes the downed insects.
    /// </summary>
    private static void BurnOut(CombatView view, CombatMemory m)
    {
        var hiveIndex = view.Structures.Items.ToList().FindIndex(s => s.Hive);
        if (m.Tactic != CombatTactic.Infestation || hiveIndex < 0)
        {
            return;
        }

        var hive = view.Structures.Items[hiveIndex].Cell;
        var state = new Dictionary<PawnId, CombatPawnState>();
        foreach (var p in view.Pawns.Items)
        {
            state[p.Id] = p;
        }

        CombatPawnState StateOf(PawnId id) => state.GetValueOrDefault(id) ?? new CombatPawnState();

        if (m.Burn is null)
        {
            Cell at = default;
            var carrier = m.Roles.FindIndex(r =>
            {
                var s = StateOf(r.Pawn);
                var known = s.Cell.TryGetValue(out var c);
                at = c;
                return s.WeaponFacts.Burner() && known && Dist(c, hive) <= GrenadeReach(s);
            });
            if (carrier < 0)
            {
                return;
            }

            var created = new CombatBurn { Hive = hive, Exit = BurnExit(hive, at), Carrier = m.Roles[carrier].Pawn };
            created.Room = BurnRetreat(created);
            m.Burn = created;
        }

        var b = m.Burn;
        if (b.Done)
        {
            BurnFinish(view, m);
            return;
        }

        foreach (var r in m.Roles)
        {
            if (StateOf(r.Pawn).WeaponFacts.Burner())
            {
                r.Ground = null;
            }
        }

        var sited = view.Burn.TryGetValue(out var site);
        if (sited && !site!.Roofed)
        {
            b.Done = true;
            return;
        }

        var (insects, down) = BurnInsects(view);
        var hot = view.HiveTemperatureC.TryGetValue(out var temp);
        bool doThrow;
        if (b.Lit.Value == 0)
        {
            if (!sited || site!.Missing > 0 || !m.Roles.Any(r => r.Pawn == b.Carrier))
            {
                return;
            }

            doThrow = !hot || temp < BurnTopUpC;
            if (!doThrow)
            {
                return;
            }
        }
        else
        {
            if (down)
            {
                b.Done = true;
                BurnFinish(view, m);
                return;
            }

            if (sited && site!.Fuel.Count == 0 && hot && temp <= HeatEntryMaxC && view.Tick.Value - b.Lit.Value >= BurnFireTicks)
            {
                b.Done = true;
                return;
            }

            doThrow = sited && hot && temp < BurnTopUpC && view.Tick.Value - b.Thrown.Value >= BurnFireTicks;
        }

        Cell aim = default;
        if (doThrow)
        {
            var target = BurnTarget(site!.Fuel.Items, insects);
            doThrow = target is not null;
            if (target is { } t)
            {
                aim = t;
            }

            if (!doThrow && b.Lit.Value == 0)
            {
                return;
            }
        }

        if (doThrow)
        {
            b.Thrown = view.Tick;
            if (b.Lit.Value == 0)
            {
                b.Lit = view.Tick;
            }
        }

        if (!m.Wait)
        {
            m.Wait = true;
            m.WaitSince = view.Tick;
        }

        var had = new List<PodDoor>(m.WaitDoors);
        Shelter(view, m);
        var want = new List<PodDoor>();
        foreach (var d in b.CorridorDoors())
        {
            if (!m.WaitDoors.Any(p => p.Cell == d))
            {
                want.Add(new PodDoor { Cell = d, Mode = DoorMode.Close });
                want.Add(new PodDoor { Cell = d, Mode = DoorMode.Forbid });
            }
        }

        m.WaitDoors = EquatableArray.From(m.WaitDoors.Concat(KeepSent(had, want)));
        var cells = RectCells(b.Room);
        foreach (var r in m.Roles)
        {
            r.Target = default;
            r.Duty = CombatDuty.None;
            r.Cell = null;
            r.Retreat = false;
            r.Ground = null;
            r.Mortar = null;
            r.Aim = null;
            if (r.Pawn == b.Carrier && view.Tick.Value - b.Thrown.Value < BurnThrowTicks)
            {
                if (doThrow)
                {
                    r.Ground = aim;
                }

                continue;
            }

            if (cells.Count == 0)
            {
                continue;
            }

            StateOf(r.Pawn).Cell.TryGetValue(out var at);
            var k = NearestIndex(at, cells);
            var c = cells[k];
            cells.RemoveAt(k);
            r.Cell = c;
            r.Retreat = true;
        }
    }

    /// <summary>Sends every fighter at the nearest insect a finished burn left downed but alive, before heat stroke kills it.</summary>
    private static void BurnFinish(CombatView view, CombatMemory m)
    {
        if (m.Burn!.Lit.Value == 0)
        {
            return;
        }

        var state = new Dictionary<PawnId, CombatPawnState>();
        foreach (var p in view.Pawns.Items)
        {
            state[p.Id] = p;
        }

        var downed = new List<CombatPawnState>();
        foreach (var t in view.Threats.Items)
        {
            if (state.TryGetValue(ThreatPawn(t), out var s) && s.Cell.IsKnown && !t.Building && !Positive(t.Dead) && !s.Dead && (Positive(t.Downed) || s.Downed))
            {
                downed.Add(s);
            }
        }

        if (downed.Count == 0)
        {
            return;
        }

        foreach (var r in m.Roles)
        {
            if (m.Rescue?.Carrying(r.Pawn) == true)
            {
                continue;
            }

            if (!(state.GetValueOrDefault(r.Pawn)?.Cell.TryGetValue(out var at) ?? false))
            {
                continue;
            }

            var best = downed[0];
            foreach (var d in downed.Skip(1))
            {
                d.Cell.TryGetValue(out var c);
                best.Cell.TryGetValue(out var bc);
                if (Dist(at, c) < Dist(at, bc))
                {
                    best = d;
                }
            }

            r.Target = best.Id;
            r.Duty = CombatDuty.None;
            r.Cell = null;
            r.Retreat = false;
            r.Ground = null;
            r.Mortar = null;
            r.Aim = null;
        }
    }

    /// <summary>A lit burn-out holding its retreat: waitTurn keeps the wait on for it.</summary>
    private static bool BurnWaiting(CombatMemory m) => m.Burn.Retreating();
}
