using System.Text.Json.Nodes;
using Common = RimGovernor.Contracts.Wire.Common;
using Mirror = RimGovernor.Contracts.Wire.Mirror;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>combatlab/mirror</c> (#851, #858, #871, #881): the combat frame sections and combat_geometry on lab-ranged plus a
/// choke room. Geometry reads the staged layout (cover, line of fire, colonist in path, the caps' timing, the
/// cover_behind_line, firing_cells and adjacent_to_choke proposals); a frame lists every rifleman and raider with a rifle;
/// and the firefight's first downing or death arrives as a combat_events row with the pawn's row agreeing.
/// </summary>
internal static class CombatLabMirror
{
    /// <summary>Bounds the lab-ranged firefight until a pawn goes down.</summary>
    private const int FightTicks = 4800 + CombatLabKit.ProbeTicks;

    /// <summary>The choke room's centre offset from the lab centre: east of the firing line and south of the sandbags.</summary>
    private const int ChokeDx = 25;

    private const int ChokeDz = -20;

    /// <summary>Timing samples per read at the caps: the worst one counts.</summary>
    private const int Samples = 5;

    // lab-ranged plus lab-choke's granite room (a ChokeHalf ring whose one opening is the middle of its north wall) off to the side.
    private static readonly LabFixture Fixture = CombatLabKit.Fixture("lab-ranged", (f, cx, cz) =>
    {
        var (rx, rz) = (cx + ChokeDx, cz + ChokeDz);
        for (var x = rx - CombatFixtures.ChokeHalf; x <= rx + CombatFixtures.ChokeHalf; x++)
        {
            for (var z = rz - CombatFixtures.ChokeHalf; z <= rz + CombatFixtures.ChokeHalf; z++)
            {
                var edge = x == rx - CombatFixtures.ChokeHalf || x == rx + CombatFixtures.ChokeHalf || z == rz - CombatFixtures.ChokeHalf || z == rz + CombatFixtures.ChokeHalf;
                if (edge && !(x == rx && z == rz + CombatFixtures.ChokeHalf))
                {
                    f.Things.Add(new FixtureThing { Def = "Wall", Stuff = "BlocksGranite", X = x, Z = z });
                }
            }
        }
    });

    internal static TypedCase Case() => new()
    {
        Name = "combatlab/mirror",
        Scope = "Combat frame sections (#851, #858) and combat_geometry on lab-ranged plus a lab-choke room: on the staged layout combat_geometry "
            + "gives a sandbagged rifleman cover against the raider north of him, an open cell none, line of fire to the raider, a colonist in the "
            + "path from the cell behind a rifleman; a read at 64 cells x 8 pawns projects under 50 ms of main thread "
            + "at the caps; cover_behind_line on the sandbags proposes every cell behind them, a firing_cells proposal at the radius cap fills 64 cells "
            + "within budget (#871), and adjacent_to_choke on the room's gap proposes exactly its three inner cells, the one straight behind first "
            + "(#881). A frame's combat_pawns lists the 4 riflemen and 4 raiders with rifles, and the firefight's first downing or death arrives as a "
            + "combat_events row after shot_fired rows, with the pawn's row downed (changed at or after the event) or gone in the same frame.",
        Ticks = CombatLabKit.ProbeTicks + FightTicks,
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = Fixture.Colonists },
        Stage = CombatLabKit.Spec(Fixture),
        QuietWorld = true,
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var lab = new StagedLab(Fixture, s.Staged!);

        // Geometry reads the staged layout before any tick: the undrafted colonists wander off their cells once the clock runs.
        await GeometryAsync(s, lab, cancellationToken).ConfigureAwait(false);
        await ProposeAsync(s, lab, cancellationToken).ConfigureAwait(false);
        await ChokeAsync(s, lab, cancellationToken).ConfigureAwait(false);

        var frames = new LabFrames(s);
        var first = await frames.CombatAsync("rows", cancellationToken).ConfigureAwait(false);
        foreach (var id in lab.Colonists().Concat(lab.Hostiles()))
        {
            if (LabFrames.Row(first, id) is not { Weapon: CombatFixtures.Rifle })
            {
                throw new AcceptanceException($"combat_pawns row for {id}: {LabFrames.Row(first, id)} (frame {first.CombatPawns.Count} rows)");
            }
        }

        s.Record("frame_rows", first.CombatPawns.Count);
        await FightAsync(s, frames, cancellationToken).ConfigureAwait(false);
    }

    // The lab-ranged answers on the rifleman's, an open and a behind cell, then a read at the caps timed.
    private static async Task GeometryAsync(TypedSession s, StagedLab lab, CancellationToken cancellationToken)
    {
        // Spec order: colonist i then its raider straight north.
        var rifleman = Fixture.Pawns.First(p => p.Side == CombatFixtures.Colonist);
        var at = lab.At(rifleman.X, rifleman.Z);
        var hostiles = lab.Hostiles();
        var colonists = lab.Colonists();
        var g = await CombatLabKit.GeometryAsync(s, "geometry", new Mirror.CombatGeometryRequest
        {
            Identity = s.Identity,
            Cells = [at, Offset(at, -12, 0), Offset(at, 0, -2)],
            HostileIds = CombatLabKit.PawnIds([hostiles[0]]),
        }, cancellationToken).ConfigureAwait(false);
        var (sandbagged, bare, back) = (g.Cells[0].Lines[0], g.Cells[1].Lines[0], g.Cells[2].Lines[0]);
        if (sandbagged is not { Cover: > 0, LineOfFire: true } || sandbagged.ColonistInPath == true
            || bare is not { Cover: 0, LineOfFire: true } || back.ColonistInPath != true)
        {
            throw new AcceptanceException($"geometry answers off lab-ranged's layout: sandbagged {sandbagged}, open {bare}, behind {back}");
        }

        // The timing: every staged pawn as a target (the read does not care which side a target is on) against the
        // cells cap of cells around the line.
        var targets = hostiles.Concat(colonists).ToList();
        var cells = new List<Common.Cell>();
        for (var dz = 0; cells.Count < CombatGeometryContract.MaxCells; dz++)
        {
            for (var dx = -8; dx < 8 && cells.Count < CombatGeometryContract.MaxCells; dx++)
            {
                cells.Add(Offset(at, 3 + dx, -2 - dz));
            }
        }

        var worst = await WorstMsAsync(s, "geometry-cap", new Mirror.CombatGeometryRequest { Identity = s.Identity, Cells = [.. cells], HostileIds = CombatLabKit.PawnIds(targets) }, _ => { }, cancellationToken).ConfigureAwait(false);
        var projected = worst / (cells.Count * targets.Count) * CombatGeometryContract.MaxCells * CombatGeometryContract.MaxHostiles;
        s.Record("geometry_timing", CombatLabKit.Obj(("cells", cells.Count), ("targets", targets.Count), ("worst_ms", worst), ("projected_at_caps_ms", projected)));
        if (projected > CombatGeometryContract.BudgetMs)
        {
            throw new AcceptanceException($"geometry at the caps projects {projected:0.0} ms (worst {worst:0.0} ms at {cells.Count}x{targets.Count}), over {CombatGeometryContract.BudgetMs} ms");
        }
    }

    // cover_behind_line on the sandbag line proposes every cell behind it (the riflemen's row), and a firing_cells
    // proposal at the radius cap, 64 cells scored against every staged pawn, stays in budget.
    private static async Task ProposeAsync(TypedSession s, StagedLab lab, CancellationToken cancellationToken)
    {
        var line = Fixture.Things.Where(t => t.Def == "Sandbags").Select(t => lab.At(t.X, t.Z)).ToList();
        var cover = await CombatLabKit.GeometryAsync(s, "propose-cover", new Mirror.CombatGeometryRequest
        {
            Identity = s.Identity,
            Cells = [],
            HostileIds = CombatLabKit.PawnIds(lab.Hostiles()),
            Propose = new Mirror.CombatGeometryPropose.CoverBehindLine { Line = [.. line] },
        }, cancellationToken).ConfigureAwait(false);
        var proposed = CombatGeometryContract.Cells(cover.Proposed);
        var missed = line.Select(c => (c.X, c.Z - 1)).Where(c => !proposed.Contains(c)).ToList();
        if (missed.Count != 0)
        {
            throw new AcceptanceException($"cover_behind_line on the sandbags missed [{string.Join(" ", missed)}]; proposed [{string.Join(" ", proposed)}]");
        }

        var rifleman = Fixture.Pawns.First(p => p.Side == CombatFixtures.Colonist);
        var pawns = lab.Hostiles().Concat(lab.Colonists()).ToList();
        var firing = new Mirror.CombatGeometryRequest
        {
            Identity = s.Identity,
            Cells = [],
            HostileIds = CombatLabKit.PawnIds(pawns),
            Propose = new Mirror.CombatGeometryPropose.FiringCells
            {
                Targets = [.. Fixture.Pawns.Select(p => lab.At(p.X, p.Z))],
                From = lab.At(rifleman.X, rifleman.Z - 3),
                Radius = CombatGeometryContract.MaxRadius,
            },
        };
        var worst = await WorstMsAsync(s, "propose-cap", firing, g =>
        {
            if (g.Proposed.Count != CombatGeometryContract.MaxCells)
            {
                throw new AcceptanceException($"propose firing_cells at radius {CombatGeometryContract.MaxRadius} gave {g.Proposed.Count} cells, want {CombatGeometryContract.MaxCells}");
            }
        }, cancellationToken).ConfigureAwait(false);

        // Scan and scoring are linear in targets and hostiles: 8 each here, the caps are 16.
        var projected = worst * CombatGeometryContract.MaxHostiles / pawns.Count;
        s.Record("propose_timing", CombatLabKit.Obj(("hostiles", pawns.Count), ("worst_ms", worst), ("projected_at_caps_ms", projected)));
        if (projected > CombatGeometryContract.BudgetMs)
        {
            throw new AcceptanceException($"propose at the caps projects {projected:0.0} ms (worst {worst:0.0} ms), over {CombatGeometryContract.BudgetMs} ms");
        }
    }

    // adjacent_to_choke on the room's gap, our side three cells inside, proposes exactly the three floor cells inside the
    // gap, the one straight behind it first; the walls either side and the cells outside are not proposed.
    private static async Task ChokeAsync(TypedSession s, StagedLab lab, CancellationToken cancellationToken)
    {
        var gap = lab.Cell(ChokeDx, ChokeDz + CombatFixtures.ChokeHalf);
        var g = await CombatLabKit.GeometryAsync(s, "propose-choke", new Mirror.CombatGeometryRequest
        {
            Identity = s.Identity,
            Cells = [],
            HostileIds = CombatLabKit.PawnIds(lab.Hostiles()),
            Propose = new Mirror.CombatGeometryPropose.AdjacentToChoke { Choke = gap, OurSide = Offset(gap, 0, -3) },
        }, cancellationToken).ConfigureAwait(false);
        var inside = Offset(gap, 0, -1);
        var want = new HashSet<(int, int)> { CombatGeometryContract.Key(inside), CombatGeometryContract.Key(Offset(inside, -1, 0)), CombatGeometryContract.Key(Offset(inside, 1, 0)) };
        var proposed = g.Proposed.Select(r => CombatGeometryContract.Key(r.Cell)).ToList();
        if (proposed.Count != want.Count || proposed[0] != CombatGeometryContract.Key(inside) || !proposed.All(want.Contains))
        {
            throw new AcceptanceException($"adjacent_to_choke on the gap at {CombatLabKit.Show(gap)} proposed [{string.Join(" ", proposed)}], want the three cells at z={inside.Z}, x={inside.X} first");
        }
    }

    // Reads request Samples times, each checked by check, and returns the worst main-thread time.
    private static async Task<double> WorstMsAsync(TypedSession s, string label, Mirror.CombatGeometryRequest request, Action<Mirror.CombatGeometry> check, CancellationToken cancellationToken)
    {
        var worst = 0.0;
        for (var i = 0; i < Samples; i++)
        {
            var g = await CombatLabKit.GeometryAsync(s, $"{label}-{i}", request, cancellationToken).ConfigureAwait(false);
            check(g);
            worst = Math.Max(worst, g.MainThreadMs);
        }

        return worst;
    }

    // Runs the lab until a pawn goes down or dies: the frame that first carries the event carries the pawn's row
    // downed (changed at or after the event) or, for a death or a downing then death between frames, no row.
    private static async Task FightAsync(TypedSession s, LabFrames frames, CancellationToken cancellationToken)
    {
        Mirror.CombatEventRow? down = null;
        var frame = await frames.UntilAsync("fight", FightTicks, 0, f =>
        {
            down = frames.Events.FirstOrDefault(e => e.Kind is Mirror.CombatLogKind.Downed or Mirror.CombatLogKind.Killed);
            return down is not null;
        }, cancellationToken).ConfigureAwait(false);
        var kinds = frames.Events.GroupBy(e => e.Kind?.ToString() ?? "").ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        s.Record("event_kinds", new JsonObject([.. kinds.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => new KeyValuePair<string, JsonNode?>(k.Key, k.Value))]));
        s.RecordWire("down", down);
        var row = LabFrames.Row(frame, down!.ThingId ?? "");
        s.RecordWire("down_row", row);
        if (down.Kind == Mirror.CombatLogKind.Killed && row is not null)
        {
            throw new AcceptanceException($"death {down} with its pawn row still in the same frame: {row}");
        }

        if (row is not null && (row.Downed != true || row.Changed?.Tick < down.At?.Tick))
        {
            throw new AcceptanceException($"downing {down} without its pawn row downed at or after the event: {row}");
        }

        if (!kinds.ContainsKey(nameof(Mirror.CombatLogKind.ShotFired)))
        {
            throw new AcceptanceException($"a pawn went down but no shot_fired event was recorded: {string.Join(", ", kinds)}");
        }
    }

    private static Common.Cell Offset(Common.Cell c, int dx, int dz) => new() { X = c.X + dx, Z = c.Z + dz };
}
