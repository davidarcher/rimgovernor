using System.Globalization;
using System.Text.Json.Nodes;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>environment/wall-layers</c> (#4043, epic #2795): the native probe behind the climate model's wall term. Two staged
/// roofed 3x3 rooms with one door each stand on a map pinned at -30 C, the second with a wall layer two cells out on
/// every outdoor-facing side (corners skipped, since the sampler never visits a corner). Set to 21 C, the double-walled
/// room's wall delta per interval is half the single-walled room's, and the single-walled room's equals
/// <c>count × interval × WallEqualizeFactor × ΔT / cells</c>; the thin-roof delta equals <c>ΔT × ThinRoofEqualizeRate × interval</c>.
/// </summary>
internal static class WallLayersProbe
{
    private const string Tool = "test/wall_layers";
    private const float Outdoor = -30f;
    private const float Target = 21f;

    // The halving and its slack; the slack against each formula, as a fraction.
    private const double Halving = 0.5;
    private const double HalvingTolerance = 0.05;
    private const double FormulaTolerance = 0.1;

    // RoomTempTracker.ThinRoofEqualizeRate (private const; 1.7 compiles 5E-05): the thin-roof delta per interval is
    // ΔT × coverage × rate × interval.
    private const double ThinRoofRate = 5e-5;

    // The site: room A's ring at (1..5, 2..6), its door south at (3,2); room B's ring at (9..13, 2..6), its door south
    // at (11,2), its layer on the 7x7 edge at (8..14, 1..7) less the corners and the cell beyond the door.
    private static readonly Fx.StageRect SingleRing = new(1, 2, 5, 5);
    private static readonly Fx.StageRect DoubleRing = new(9, 2, 5, 5);
    private static readonly Fx.StageCell SingleDoor = new(3, 2);
    private static readonly Fx.StageCell DoubleDoor = new(11, 2);

    private static readonly Fx.StageSpec Spec = new()
    {
        Paused = true,
        World = new Fx.StageWorld { Hour = 12, OutdoorTemperature = Outdoor },
        Site = new Fx.StageSite
        {
            Width = 16,
            Height = 9,
            Near = Fx.StageAnchor.Colonist,
            OffsetX = 3,
            Radius = 40,
            Require = [Fx.StageGround.Standable, Fx.StageGround.Unzoned, Fx.StageGround.Unfogged, Fx.StageGround.Heavy, Fx.StageGround.Unroofed],
        },
        Clear = [new Fx.StageClear { Rect = new Fx.StageRect(0, 0, 16, 9), Kinds = [Fx.StageClearKind.Things, Fx.StageClearKind.Zones, Fx.StageClearKind.Roofs] }],
        Rooms =
        [
            new Fx.StageRoom { Name = "single", Rect = SingleRing, Stuff = "WoodLog", Doors = [SingleDoor], Roof = true },
            new Fx.StageRoom { Name = "double", Rect = DoubleRing, Stuff = "WoodLog", Doors = [DoubleDoor], Roof = true },
        ],
        Buildings = [new Fx.StageBuilding { Name = "layer", Def = "Wall", Stuff = "WoodLog", Cells = Layer(DoubleRing, DoubleDoor) }],
    };

    private sealed record Cells(int SingleX, int SingleZ, int DoubleX, int DoubleZ);

    private static readonly FixtureOp<Cells, Fx.WallLayersRead> Read = new(
        Tool,
        c => ServedOps.Args(("singleX", c.SingleX), ("singleZ", c.SingleZ), ("doubleX", c.DoubleX), ("doubleZ", c.DoubleZ), ("target", Target)),
        ServedOps.Reply<Fx.WallLayersRead>(Tool));

    internal static TypedCase Case() => new()
    {
        Name = "environment/wall-layers",
        Scope = "Issue #4043 (epic #2795): two staged roofed 3x3 rooms with a door each on a map pinned at -30 C, the second "
            + "with a wall layer two cells out on every outdoor-facing side (corners skipped), both set to 21 C: the "
            + "double-walled room's WallEqualizationTempChangePerInterval is 0.5 ± 0.05 of the single-walled room's, the "
            + "single-walled room's is within 10% of count × RoomTempEqualizeInterval × WallEqualizeFactor × ΔT / cells, and "
            + "the thin-roof delta is within 10% of ΔT × ThinRoofEqualizeRate × interval. The constants the climate model keeps.",
        Ticks = 600, // a hang guard: the read is one call
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = 1 },
        Ops = [Tool],
        QuietWorld = true,
        Crew = new Crew { Size = 1 },
        Stage = Spec,
        Run = RunAsync,
    };

    // The layer: the edge of the ring grown by one, less its corners and the cell beyond the door.
    private static List<Fx.StageCell> Layer(Fx.StageRect ring, Fx.StageCell door)
    {
        var x0 = ring.X - 1;
        var z0 = ring.Z - 1;
        var x1 = ring.X + ring.Width;
        var z1 = ring.Z + ring.Height;
        var beyond = new Fx.StageCell(
            door.X + (door.X == ring.X ? -1 : door.X == ring.X + ring.Width - 1 ? 1 : 0),
            door.Z + (door.Z == ring.Z ? -1 : door.Z == ring.Z + ring.Height - 1 ? 1 : 0));
        var cells = new List<Fx.StageCell>();
        for (var x = x0 + 1; x < x1; x++)
        {
            cells.Add(new(x, z0));
            cells.Add(new(x, z1));
        }
        for (var z = z0 + 1; z < z1; z++)
        {
            cells.Add(new(x0, z));
            cells.Add(new(x1, z));
        }
        cells.Remove(beyond);
        return cells;
    }

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var site = s.Staged!.Site ?? throw new AcceptanceException("the stage found no site");
        var cells = new Cells(site.X + SingleRing.X + 2, site.Z + SingleRing.Z + 2, site.X + DoubleRing.X + 2, site.Z + DoubleRing.Z + 2);
        var read = await s.FixtureAsync("read", Read, cells, cancellationToken).ConfigureAwait(false);
        s.Record("read", ServedOps.Json(read));
        if (Math.Abs(read.OutdoorC - Outdoor) > 0.01)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"the lab outdoor pin did not take: outdoor {read.OutdoorC:F2}, wanted {Outdoor:F2}"));
        }
        foreach (var (name, room) in new[] { ("single", read.SingleWalled), ("double", read.DoubleWalled) })
        {
            if (room.Cells != 9 || room.OpenRoofCells != 0)
            {
                throw new AcceptanceException($"the {name} room is not a roofed 3x3 room: cells={room.Cells} openRoof={room.OpenRoofCells}");
            }
        }
        if (read.SingleWalled.EqualizeCells != read.DoubleWalled.EqualizeCells)
        {
            throw new AcceptanceException($"the rooms sample different equalize cells: single {read.SingleWalled.EqualizeCells}, double {read.DoubleWalled.EqualizeCells}");
        }

        var diff = read.TargetC - read.OutdoorC;
        var formula = read.SingleWalled.EqualizeCells * read.IntervalTicks * read.WallEqualizeFactor * -diff / read.SingleWalled.Cells;
        var ratio = read.DoubleWalled.WallDeltaPerInterval / read.SingleWalled.WallDeltaPerInterval;
        var roofFormula = -diff * ThinRoofRate * read.IntervalTicks;
        var derived = new JsonObject();
        derived.Add("wall_single_per_tick_per_degree_per_cell", -read.SingleWalled.WallDeltaPerInterval * read.SingleWalled.Cells / read.IntervalTicks / diff / read.SingleWalled.EqualizeCells);
        derived.Add("wall_double_per_tick_per_degree_per_cell", -read.DoubleWalled.WallDeltaPerInterval * read.DoubleWalled.Cells / read.IntervalTicks / diff / read.DoubleWalled.EqualizeCells);
        derived.Add("thin_roof_per_tick_per_degree_per_floor_cell", -read.SingleWalled.ThinRoofDeltaPerInterval / read.IntervalTicks / diff);
        derived.Add("ratio_double_over_single", ratio);
        derived.Add("single_formula_per_interval", formula);
        derived.Add("thin_roof_formula_per_interval", roofFormula);
        s.Record("derived", derived);

        if (Math.Abs(ratio - Halving) > HalvingTolerance)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"double over single is {ratio:F3}, not {Halving} ± {HalvingTolerance} (single {read.SingleWalled.WallDeltaPerInterval:F4}, double {read.DoubleWalled.WallDeltaPerInterval:F4} per interval)"));
        }
        Within("the single-walled wall delta", read.SingleWalled.WallDeltaPerInterval, formula);
        Within("the single-walled thin-roof delta", read.SingleWalled.ThinRoofDeltaPerInterval, roofFormula);
        Within("the double-walled thin-roof delta", read.DoubleWalled.ThinRoofDeltaPerInterval, roofFormula);
        if (read.SingleWalled.NoRoofDeltaPerInterval != 0 || read.DoubleWalled.NoRoofDeltaPerInterval != 0)
        {
            throw new AcceptanceException("a roofed room reports a no-roof delta");
        }
    }

    private static void Within(string what, double measured, double formula)
    {
        if (Math.Abs(measured - formula) > FormulaTolerance * Math.Abs(formula))
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"{what} is {measured:F4} per interval, the formula gives {formula:F4}"));
        }
    }
}
