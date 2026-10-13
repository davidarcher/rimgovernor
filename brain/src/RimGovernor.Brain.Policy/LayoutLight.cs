namespace RimGovernor.Brain.Policy;

// Lamp pricing (#2945). A roofed cell gets no sky light, so a colonist who works or walks on it loses speed to
// darkness until a lamp lights it. The planner weighs the two in sustained watts (the layout's own currency, see
// BuildW): the speed lost against the lamps that would cure it, chosen per room with the room's work cells and
// traffic, and never as a veto.
//
// What darkness costs, from the game (research on #2945): StatPart_Glow scales WorkSpeedGlobal and MoveSpeed by
// 0.80 at glow 0 up to 1.00 at glow 0.3 (LitGlow), for humanlike pawns, every job alike. The other effects are
// not priced here.
//
// The exchange rate is the epic's decision: a colonist's work-day is worth their own food upkeep a day, counted
// in site-type units against the crop's per-cell yield and turned into watts at SiteTypeWeights.Power, the same
// units BuildW turns a build cost into. It is a derived lower bound; no new constant. A lamp costs its draw (the
// def's power comp) plus its build cost amortised at the same weights. A work cell is one colonist at the bench
// for the whole day and a walked cell costs the extra time its passages take at the slower speed.
//
// A lit cell is one within a lamp's reach (GlowReach: the distance its glow holds above LitGlow, solved from
// the def's radius and colour); glow beyond it is not credited. The lamps give no heat.
//
// The wall lamp (#2948) is the second option: it is hung on a wall, standing on a floor cell beside it, with no
// path cost, so it blocks nobody; the neighbour must be a Full-fill building that supports wall attachments (the
// wall and natural rock; not a door or a vent). It costs 15 steel against the standing lamp's 20, draws the same
// 30 W, and glows to radius 11 against 12. A wall lamp is not an edifice (#2951): it shares a cell with a bench,
// a bed or a table, and only a Full-fill piece or a piece of unknown def withholds the cell.
//
// Reach is solved from the game's falloff, not scaled (GlowReach). Choosing between the lamps is the same watt
// price: each step takes the lamp, of either kind at any spot, whose lit dark cost less its own cost is largest.
// A wall cell another wall building holds is not a support, so a wall lamp there is an absent option and the
// standing lamp answers: contention is priced by what the substitute costs, never refused.

/// <summary>
/// The lamp pricing's table, registered in DefTables so a renamed def fails the dangling-reference gate. LampDef
/// is the standing lamp the plan places and WallLampDef the one hung on a wall; DarkSpeed is StatPart_Glow's
/// factor at glow 0 for WorkSpeedGlobal and MoveSpeed. The glow constants are the game's: GroundScale is
/// GlowGrid.GroundGlowAt's 3.6, FalloffBlend ComputeGlowGridsJob.SetGlowFromDist's lerp weight of 1/x^2 against
/// 1-x/radius, and DiagonalCost its 141/100 per diagonal step.
/// </summary>
public sealed record LightTable
{
    public string LampDef { get; init; } = string.Empty;

    public string WallLampDef { get; init; } = string.Empty;

    public double DarkSpeed { get; init; }

    public double GroundScale { get; init; }

    public double FalloffBlend { get; init; }

    public double DiagonalCost { get; init; }
}

public static partial class PolicyConstants
{
    private static readonly LightTable LightTableValue = new()
    {
        LampDef = "StandingLamp",
        WallLampDef = "WallLamp",
        DarkSpeed = 0.8,
        GroundScale = 3.6,
        FalloffBlend = 0.4,
        DiagonalCost = 1.41,
    };

    internal static LightTable LightJudgment => LightTableValue;

    public const string ReasonTileLamps = "tile_lamps";
    public const string ReasonTileLampsNotPaid = "tile_lamps_not_paid";
    public const string ReasonTileLampsSky = "tile_lamps_sky";
    public const string ReasonTileLampsUnpriced = "tile_lamps_unpriced";
}

/// <summary>One lamp's catalog numbers: the power it draws, the market value of its cheapest cost list, its glow radius and the brightest channel of its glow colour. The default value is unknown.</summary>
public readonly record struct LampModel(double PowerW, double Build, double Radius, double Brightness)
{
    internal bool Priced() => PolicyFunctions.FinitePositive(PowerW) && PolicyFunctions.FinitePositive(Build);

    internal double Reach() => PolicyFunctions.GlowReach(Radius, Brightness);
}

/// <summary>The catalog's two lamps; an unknown one is an absent option.</summary>
public sealed record LightModel
{
    public LampModel Standing { get; init; }

    public LampModel Wall { get; init; }

    /// <summary>The standing lamp's lit radius in cells (GlowReach of its catalog glow); zero when the catalog does not know the lamp.</summary>
    public double StandingReach() => Standing.Reach();
}

/// <summary>
/// What darkness and light cost, in sustained watts. WorkDayW is a colonist's work-day, LampW one standing lamp's
/// draw and amortised build, CellWalkDays the work-days a colonist spends walking one cell. LampReach is the
/// standing lamp's lit radius in cells. WallLampW and WallReach are the wall lamp's price and reach; zero while
/// the wall lamp is unknown, which leaves it out of the options.
/// </summary>
public readonly record struct LightPrice(double WorkDayW, double LampW, double CellWalkDays, double LampReach, double WallLampW, double WallReach)
{
    /// <summary>Reports a price with every number known.</summary>
    public bool Valid() =>
        PolicyFunctions.FinitePositive(WorkDayW) && PolicyFunctions.FinitePositive(LampW) && PolicyFunctions.FinitePositive(CellWalkDays) && PolicyFunctions.FinitePositive(LampReach);

    /// <summary>What a dark work cell costs: the work speed lost over one colonist's work-day.</summary>
    public double WorkCellW() => (1 - PolicyFunctions.LightSpeed(0)) * WorkDayW;

    /// <summary>What a dark cell costs the movements a day through it: the extra time each passage takes at the dark speed, as a share of a work-day.</summary>
    public double WalkCellW(double movements) => movements * CellWalkDays * ((1 / PolicyFunctions.LightSpeed(0)) - 1) * WorkDayW;

    /// <summary>The price of the cheapest lamp of either kind.</summary>
    internal double CheapestLampW() => WallLampW > 0 ? Math.Min(LampW, WallLampW) : LampW;

    /// <summary>The standing lamps that could stand on spots.</summary>
    internal List<LampCand> StandingCands(IReadOnlyList<Cell> spots) =>
        spots.Select(s => new LampCand(new PlannedLamp { Def = PolicyConstants.LightJudgment.LampDef, Cell = s }, LampW, LampReach)).ToList();

    /// <summary>
    /// The lamps a room's floor offers: a standing lamp on each cell no piece or worker holds, and a wall lamp
    /// on each cell no Full-fill piece holds (it shares the cell with the furniture and the work cell's standing
    /// spot) that has a plain wall beside it.
    /// </summary>
    internal List<LampCand> RoomCands(IEnumerable<Cell> floor, IReadOnlySet<Cell> held, IReadOnlySet<Cell> work, IReadOnlySet<Cell> full, Func<Cell, bool>? isWall)
    {
        var standing = new List<Cell>();
        var wall = new List<Cell>();
        foreach (var c in floor)
        {
            if (!held.Contains(c) && !work.Contains(c))
            {
                standing.Add(c);
            }
            if (!full.Contains(c))
            {
                wall.Add(c);
            }
        }
        PolicyFunctions.SortCells(standing);
        PolicyFunctions.SortCells(wall);
        var output = StandingCands(standing);
        output.AddRange(PolicyFunctions.WallLampCands(wall, isWall, this));
        return output;
    }
}

/// <summary>What the price reads. UpkeepPerDay is one colonist's nutrition a day and UnitNutrition the nutrition a day of one site-type unit; Climate gives the game's ticks per second.</summary>
public sealed record LightPriceInput
{
    public LightModel Model { get; init; } = new();

    public ClimateModel Climate { get; init; } = new();

    public SiteTypeWeights Weights { get; init; }

    public double SteelValue { get; init; }

    public double UpkeepPerDay { get; init; }

    public double UnitNutrition { get; init; }
}

/// <summary>A cell that costs W watts a day while it stays dark.</summary>
public readonly record struct DarkCell(Cell Cell, double W);

/// <summary>
/// A lamp the plan places: Def the building, Cell the floor cell it stands on and Facing the direction of the
/// wall it hangs on (a wall lamp's rotation); default for a standing lamp.
/// </summary>
public sealed record PlannedLamp
{
    public string Def { get; init; } = string.Empty;

    public Cell Cell { get; init; }

    public Rotation Facing { get; init; }
}

/// <summary>One way to light from a floor cell: a lamp def, its cost in sustained watts and the distance it lights.</summary>
internal readonly record struct LampCand(PlannedLamp Lamp, double W, double Reach)
{
    public Cell Cell => Lamp.Cell;
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// The Euclidean radius (cells) within which a lamp of the given glow radius and brightest colour channel
    /// keeps ground glow above LitGlow; zero when either is unknown or the lamp never lights its own neighbour.
    /// The flood runs over octile path steps, so a cell at Euclidean distance e is at most sqrt(1+k^2)*e path
    /// cells away (k the diagonal's extra cost): dividing by it keeps every cell inside lit.
    /// </summary>
    internal static double GlowReach(double radius, double brightness)
    {
        if (!FinitePositive(radius) || !FinitePositive(brightness))
        {
            return 0;
        }
        var t = PolicyConstants.LightJudgment;
        // The channel survives int(brightness*falloff), and ground glow must pass LitGlow strictly.
        var need = (Math.Floor(DefaultLightingPolicy().LitGlow / t.GroundScale * 255) + 1) / brightness;
        double Falloff(double x) => ((1 - t.FalloffBlend) * (1 - (x / radius))) + (t.FalloffBlend / (x * x));
        if (Falloff(1) < need)
        {
            return 0;
        }
        double lo = 1.0, hi = radius; // the flood stops at the radius
        if (Falloff(hi) >= need)
        {
            lo = hi;
        }
        for (var i = 0; i < 60 && hi - lo > 1e-9; i++)
        {
            var mid = (lo + hi) / 2;
            if (Falloff(mid) >= need)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }
        return (lo - 1) / Math.Sqrt(1 + ((t.DiagonalCost - 1) * (t.DiagonalCost - 1)));
    }

    /// <summary>The buildings the lamp pricing places.</summary>
    public static LightTable LightDefs() => PolicyConstants.LightJudgment;

    /// <summary>The speed factor of a pawn on ground of the given glow (StatPart_Glow): linear from DarkSpeed at 0 to 1 at LitGlow.</summary>
    public static double LightSpeed(double glow)
    {
        var lit = DefaultLightingPolicy().LitGlow;
        var g = Math.Min(Math.Max(glow, 0), lit) / lit;
        var dark = PolicyConstants.LightJudgment.DarkSpeed;
        return dark + ((1 - dark) * g);
    }

    /// <summary>Prices darkness against light; the default (invalid) price while any number is unknown.</summary>
    public static LightPrice NewLightPrice(LightPriceInput input)
    {
        var w = input.Weights;
        if (w == default)
        {
            w = DefaultSiteTypeWeights();
        }
        if (!input.Model.Standing.Priced() || !FinitePositive(input.SteelValue)
            || !FinitePositive(input.UpkeepPerDay) || !FinitePositive(input.UnitNutrition) || !FinitePositive(input.Climate.TicksPerSecond) || !FinitePositive(w.Power))
        {
            return default;
        }
        var p = new LightPrice(
            WorkDayW: UnitsW(w, input.UpkeepPerDay / input.UnitNutrition),
            LampW: input.Model.Standing.PowerW + BuildW(w, input.Model.Standing.Build, input.SteelValue),
            CellWalkDays: input.Climate.TicksPerSecond / ColonistMoveSpeed / GameTime.TicksPerDay,
            LampReach: input.Model.Standing.Reach(),
            WallLampW: 0,
            WallReach: 0);
        // A lamp whose glow is unknown cannot be placed: the standing lamp's leaves the price invalid, the wall
        // lamp's leaves it out.
        if (!p.Valid())
        {
            return default;
        }
        var wall = input.Model.Wall;
        if (wall.Priced() && wall.Reach() > 0)
        {
            p = p with { WallLampW = wall.PowerW + BuildW(w, wall.Build, input.SteelValue), WallReach = wall.Reach() };
        }
        return p;
    }

    /// <summary>
    /// Prices the colony's darkness from its station facts: a colonist's upkeep is the food census's nutrition
    /// over the colonists, a unit the best crop's nutrition a day on normal soil. Invalid while the census, the
    /// colonists, a crop or the lamp is unknown.
    /// </summary>
    internal static LightPrice StationLightPrice(StationFacts f, double steel)
    {
        var (upkeep, unit, ok) = StationUpkeep(f);
        if (!ok)
        {
            return default;
        }
        return NewLightPrice(new LightPriceInput { Model = f.Catalog.Light, Climate = f.Catalog.Climate, SteelValue = steel, UpkeepPerDay = upkeep, UnitNutrition = unit });
    }

    /// <summary>
    /// A colonist's nutrition a day (the food census over the colonists) and a site-type unit's nutrition a day
    /// (the best crop on normal soil); false while the census or the colonists are unknown. A unit of 0 is a
    /// colony with no crop: the caller's price stays invalid.
    /// </summary>
    internal static (double Upkeep, double Unit, bool Ok) StationUpkeep(StationFacts f)
    {
        if (!f.Food.TryGetValue(out var supply) || !f.Colonists.TryGetValue(out var colonists))
        {
            return (0, 0, false);
        }
        var (nutrition, nk) = HumanNutritionPerDay(supply);
        if (colonists <= 0 || !nk)
        {
            return (0, 0, false);
        }
        double unit = 0;
        foreach (var crop in f.Crops)
        {
            unit = Math.Max(unit, CropRate(crop, 1));
        }
        return (nutrition / colonists, unit, true);
    }

    /// <summary>A colonist's work-day in sustained watts, the lamp price's own (NewLightPrice): upkeep over the best crop's unit at the site-type weights. Zero while unknown.</summary>
    internal static double StationWorkDayW(StationFacts f)
    {
        var (upkeep, unit, ok) = StationUpkeep(f);
        if (!ok || !FinitePositive(unit))
        {
            return 0;
        }
        return UnitsW(DefaultSiteTypeWeights(), upkeep / unit);
    }

    /// <summary>
    /// Places lamps greedily from the candidates: each step takes the one whose newly lit dark cost, less its
    /// own cost, is largest, and only while that is positive (ties and losses place none). Of two lamps on one
    /// cell the cheaper cover wins; the candidates' order breaks ties, and a cell holds one lamp. It returns the
    /// lamps in the order placed with their costs and the dark cost left unlit.
    /// </summary>
    internal static (List<LampCand> Lamps, double LeftW) PlanLamps(IReadOnlyList<DarkCell> cells, IReadOnlyList<LampCand> cands)
    {
        var lamps = new List<LampCand>();
        var lit = new bool[cells.Count];
        var taken = new HashSet<Cell>();
        var leftW = cells.Sum(c => c.W);
        while (true)
        {
            var best = -1;
            var bestNet = 0.0;
            for (var i = 0; i < cands.Count; i++)
            {
                var l = cands[i];
                if (taken.Contains(l.Cell))
                {
                    continue;
                }
                var gain = 0.0;
                for (var j = 0; j < cells.Count; j++)
                {
                    if (!lit[j] && Distance(l.Cell, cells[j].Cell) <= l.Reach)
                    {
                        gain += cells[j].W;
                    }
                }
                var net = gain - l.W;
                if (net > bestNet)
                {
                    (best, bestNet) = (i, net);
                }
            }
            if (best < 0)
            {
                return (lamps, leftW);
            }
            var pick = cands[best];
            lamps.Add(pick);
            taken.Add(pick.Cell);
            for (var j = 0; j < cells.Count; j++)
            {
                if (!lit[j] && Distance(pick.Cell, cells[j].Cell) <= pick.Reach)
                {
                    lit[j] = true;
                    leftW -= cells[j].W;
                }
            }
        }
    }

    /// <summary>Reports a room with a roof and no sky: a fenced room that is not an outdoor role. An outdoor room gets the sun's light and no lamp.</summary>
    internal static bool LightRoofed(PlannedRoom r) => !r.Role.IsOutdoor() && !r.Unfenced();

    internal static double LampsW(IEnumerable<LampCand> lamps) => lamps.Sum(l => l.W);

    /// <summary>
    /// The cells of the plan a wall lamp can hang on: the wall ring of every fenced indoor room (a fence, or no
    /// ring at all, supports nothing), less the cells another building holds: the floor of every room and
    /// hallway, the doors, links and entrances of the plan and the vents the tiler placed (#2915). Natural rock
    /// supports a lamp as a wall does: a survey rock cell outside the held cells counts.
    /// </summary>
    internal static Func<Cell, bool> PlainWalls(LayoutPlan p, IReadOnlySet<Cell> rock)
    {
        var held = HeldWallCells(p);
        var ring = new HashSet<Cell>();
        foreach (var r in p.Rooms)
        {
            if (LightRoofed(r))
            {
                foreach (var c in r.Region.Walls().Cells())
                {
                    ring.Add(c);
                }
            }
        }
        return c => !held.Contains(c) && (ring.Contains(c) || rock.Contains(c));
    }

    /// <summary>The survey's natural rock cells.</summary>
    internal static HashSet<Cell> SurveyRock(MapSurvey s)
    {
        var output = new HashSet<Cell>();
        foreach (var c in s.Cells)
        {
            if (c.Rock)
            {
                output.Add(c.Cell);
            }
        }
        return output;
    }

    /// <summary>The plan's cells that are not a bare wall: floor, doors, links, entrances and vents.</summary>
    internal static HashSet<Cell> HeldWallCells(LayoutPlan p)
    {
        var output = new HashSet<Cell>();
        foreach (var r in p.Rooms)
        {
            foreach (var c in r.Region.Cells())
            {
                output.Add(c);
            }
            foreach (var c in p.DoorsOf(r))
            {
                output.Add(c);
            }
        }
        foreach (var c in p.Entrances)
        {
            output.Add(c);
        }
        foreach (var s in p.LiveSites(NodeKind.Vent))
        {
            foreach (var c in s.Footprint.Cells())
            {
                output.Add(c);
            }
        }
        return output;
    }

    /// <summary>
    /// The wall lamps that could hang over floor spots: on a spot with a cardinal neighbour that is a plain
    /// wall, the first in the plan's direction order carrying the lamp. The lamp stands on the floor and takes
    /// no wall cell; it needs only that a wall is there.
    /// </summary>
    internal static List<LampCand> WallLampCands(IReadOnlyList<Cell> spots, Func<Cell, bool>? isWall, LightPrice p)
    {
        var output = new List<LampCand>();
        if (p.WallLampW <= 0 || p.WallReach <= 0 || isWall is null)
        {
            return output;
        }
        foreach (var s in spots)
        {
            foreach (var dir in Directions)
            {
                if (!isWall(AddCell(s, dir)))
                {
                    continue;
                }
                output.Add(new LampCand(new PlannedLamp { Def = PolicyConstants.LightJudgment.WallLampDef, Cell = s, Facing = RotationOf(dir) }, p.WallLampW, p.WallReach));
                break;
            }
        }
        return output;
    }

    /// <summary>
    /// Lights the roofed rooms where the lamps pay. A room's priced cells are its work cells (a worker station's
    /// interaction cell, a full work-day of speed) and every cell the plan's trips walk; a standing lamp stands
    /// on a floor cell no station or worker holds, a wall lamp hangs on a plain wall beside any floor cell a
    /// Full-fill station does not hold, whichever lights more dark for its cost. Lamps are placed when lighting
    /// the dark costs less than leaving it, never vetoed either way; a room under the sky gets none.
    /// </summary>
    internal static void PlaceLamps(this Tiler t)
    {
        var p = t.Out.Plan;
        Dictionary<Cell, double>? moves = null;
        Func<Cell, bool>? isWall = null;
        for (var i = 0; i < t.Rooms.Count; i++)
        {
            var room = p.Rooms[i];
            var g = t.In.Groups[t.Rooms[i].Group];
            if (!LightRoofed(room))
            {
                t.NoteLamps(g.Id, PolicyConstants.ReasonTileLampsSky, TileVerdict.Place, 0, 0);
                continue;
            }
            var work = new HashSet<Cell>();
            var taken = new HashSet<Cell>();
            var filled = new HashSet<Cell>(); // cells a wall lamp cannot share
            foreach (var piece in t.Out.Pieces.GetValueOrDefault(room.Id) ?? [])
            {
                var (s, known) = t.In.Catalog.Station(piece.Def);
                foreach (var c in RectCells(piece.Rect))
                {
                    taken.Add(c);
                    if (!known || s.FillsCell)
                    {
                        filled.Add(c);
                    }
                }
                if (known && s.Capacity.Kind == CapacityKind.Workers)
                {
                    var (c, ok) = piece.Interaction();
                    if (ok && room.Region.Contains(c))
                    {
                        work.Add(c);
                    }
                }
            }
            if (!t.In.Light.Valid())
            {
                if (work.Count > 0)
                {
                    t.NoteLamps(g.Id, PolicyConstants.ReasonTileLampsUnpriced, TileVerdict.Unsatisfied, 0, 0);
                }
                continue;
            }
            if (moves is null)
            {
                moves = TripCellMovements(p);
                isWall = PlainWalls(p, SurveyRock(t.In.Survey));
            }
            var cells = new List<DarkCell>();
            foreach (var c in room.Region.Cells())
            {
                var w = t.In.Light.WalkCellW(moves.GetValueOrDefault(c));
                if (work.Contains(c))
                {
                    w += t.In.Light.WorkCellW();
                }
                if (w > 0)
                {
                    cells.Add(new DarkCell(c, w));
                }
            }
            var (lamps, left) = PlanLamps(cells, t.In.Light.RoomCands(room.Region.Cells(), taken, work, filled, isWall));
            var total = cells.Sum(c => c.W);
            if (lamps.Count == 0)
            {
                if (total > 0)
                {
                    t.NoteLamps(g.Id, PolicyConstants.ReasonTileLampsNotPaid, TileVerdict.Unsatisfied, total, t.In.Light.CheapestLampW());
                }
                continue;
            }
            t.Out.Lamps ??= [];
            if (!t.Out.Lamps.TryGetValue(room.Id, out var list))
            {
                list = [];
                t.Out.Lamps[room.Id] = list;
            }
            list.AddRange(lamps.Select(l => l.Lamp));
            t.NoteLamps(g.Id, PolicyConstants.ReasonTileLamps, TileVerdict.Place, left, LampsW(lamps));
        }
    }

    internal static void NoteLamps(this Tiler t, int group, string reason, TileVerdict verdict, double darkW, double lampW) =>
        t.Out.Decisions.Add(new TileDecision { Group = group, Other = -1, Verdict = verdict, Reason = reason, DarkW = darkW, LampW = lampW });

    /// <summary>
    /// The lamps the plan places in the room whose floor holds c, in the order the plan placed them (the most
    /// valuable first); empty when c is in no room or its room was planned no lamp.
    /// </summary>
    public static IReadOnlyList<PlannedLamp> PlannedLampsAt(this LayoutPlan p, Cell c)
    {
        foreach (var r in p.Rooms)
        {
            if (r.Region.Contains(c))
            {
                return p.Lamps.GetValueOrDefault(r.Id) ?? [];
            }
        }
        return [];
    }
}
