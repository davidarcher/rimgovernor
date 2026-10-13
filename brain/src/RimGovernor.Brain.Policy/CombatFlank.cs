namespace RimGovernor.Brain.Policy;

// Go: combat_flank.go
//
// Flanking: a hold with at least the flank minimum of defenders and a layout choke detaches flankSize gunners
// to cells beside the approach, just inside the choke, square to the main line (the L). They hold fire while
// the raid walks in, and open fire once the lead hostile has passed them, so the raid takes fire from the line
// and from its side.

/// <summary>
/// The hold's flanking detachment. An empty Pawns means the game proposed no cells: the fight does not ask
/// again until it re-forms.
/// </summary>
public sealed class CombatFlank
{
    public List<PawnId> Pawns { get; set; } = [];

    public List<Cell> Cells { get; set; } = [];

    /// <summary>Set once the lead hostile passed the detachment.</summary>
    public bool Open { get; set; }

    /// <summary>The flankers given hold-fire while waiting.</summary>
    public List<PawnId> Held { get; set; } = [];

    public CombatFlank Clone() => new() { Pawns = [.. Pawns], Cells = [.. Cells], Open = Open, Held = [.. Held] };

    /// <summary>Reports <paramref name="pawn"/> a flanker still holding fire.</summary>
    public bool Waiting(PawnId pawn) => !Open && Pawns.Contains(pawn);
}

public static partial class PolicyFunctions
{
    /// <summary>The hold's size below which no one is detached.</summary>
    internal const int FlankMinDefenders = 5;

    /// <summary>The detachment's gunner count.</summary>
    internal const int FlankSize = 2;

    /// <summary>Places the ask's anchor: cells to the side of the choke.</summary>
    internal const int FlankLateral = 4;

    /// <summary>Places the ask's anchor: cells toward the colony.</summary>
    internal const int FlankDepth = 1;

    /// <summary>
    /// The hold's gunners the detachment takes: the last flank-size ranged line roles in pawn order, or none
    /// when the hold is under the flank minimum or would keep fewer than flank-size on the line.
    /// </summary>
    private static List<PawnId> FlankGunners(EquatableArray<CombatRole> roles, CombatTuning t)
    {
        if (roles.Count < t.FlankMinDefenders)
        {
            return [];
        }

        var gunners = new List<PawnId>();
        foreach (var r in roles)
        {
            if (r.Ranged && r.Duty == CombatDuty.None && !r.Retreat && r.Cell is not null && r.Mortar is null)
            {
                gunners.Add(r.Pawn);
            }
        }

        if (gunners.Count < 2 * t.FlankSize)
        {
            return [];
        }

        return gunners.GetRange(gunners.Count - t.FlankSize, t.FlankSize);
    }

    /// <summary>The firing_cells ask's from cell beside the approach, and the choke the flankers need a line to.</summary>
    private static (Cell From, Cell Choke)? FlankAnchor(CombatView view, CombatTuning t)
    {
        if (!view.Layout.TryGetValue(out var layout))
        {
            return null;
        }

        var ok = layout.Choke.TryGetValue(out var choke);
        var vok = TowardVector(layout.Toward, out var v);
        if (!ok || !vok)
        {
            return null;
        }

        var side = new Cell(v.Z, -v.X);
        foreach (var s in new[] { 1, -1 })
        {
            var a = new Cell(
                choke.X + (s * t.FlankLateral * side.X) + (FlankDepth * v.X),
                choke.Z + (s * t.FlankLateral * side.Z) + (FlankDepth * v.Z)
            );
            if (a.X >= 0 && a.Z >= 0)
            {
                return (a, choke);
            }
        }

        return null;
    }

    /// <summary>
    /// The detachment's firing_cells ask: a hold with enough gunners and a choke, not flanking yet. It names
    /// the shooters and the top hostiles too, so the stop's attacks still get their lines.
    /// </summary>
    private static GeometryRequest? FlankAsk(CombatView view, CombatMemory m)
    {
        if (m.Tactic != CombatTactic.Hold || m.Flank is not null || m.MechLure || FlankGunners(m.Roles, m.EffectiveTuning()).Count == 0)
        {
            return null;
        }

        if (FlankAnchor(view, m.EffectiveTuning()) is not { } anchor)
        {
            return null;
        }

        return FiringCellsAsk(view, anchor.From, anchor.Choke);
    }

    /// <summary>
    /// A firing_cells ask for cells near <paramref name="from"/> with a line to <paramref name="target"/>,
    /// naming the shooters and the top hostiles too, so the stop's attacks still get their lines.
    /// </summary>
    private static GeometryRequest FiringCellsAsk(CombatView view, Cell from, Cell target)
    {
        var ask = new GeometryRequest { Propose = FormationRole.FiringCells, From = from, Targets = [target], Cells = ShooterCells(view) };
        if (ask.Cells.Count > MaxGeometryCells / 2)
        {
            ask.Cells = ask.Cells.GetRange(0, MaxGeometryCells / 2);
        }

        foreach (var h in RankThreats(view))
        {
            if (ask.Hostiles.Count < MaxGeometryHostiles)
            {
                ask.Hostiles.Add(h.Id);
            }
        }

        return ask;
    }

    /// <summary>Takes the answered proposals nearest the anchor, spaced, off the line and every other role's cell, for the flank gunners.</summary>
    private static void FlankDetach(CombatView view, GeometryReply geometry, CombatMemory m)
    {
        m.Flank = new CombatFlank();
        var anchor = FlankAnchor(view, m.EffectiveTuning());
        var gunners = FlankGunners(m.Roles, m.EffectiveTuning());
        if (anchor is not { } a || gunners.Count == 0)
        {
            return;
        }

        var taken = new HashSet<Cell>();
        foreach (var r in m.Roles)
        {
            if (r.Cell is { } cell && !gunners.Contains(r.Pawn))
            {
                taken.Add(cell);
            }
        }

        var cells = NearestFree(geometry, a.From, taken);
        if (cells.Count < gunners.Count)
        {
            return;
        }

        m.Flank.Pawns = gunners;
        m.Flank.Cells = cells.GetRange(0, gunners.Count);
    }

    /// <summary>The answered proposals not taken, nearest <paramref name="from"/> first, spaced a tile apart while they allow it.</summary>
    private static List<Cell> NearestFree(GeometryReply geometry, Cell from, HashSet<Cell> taken)
    {
        var cells = new List<Cell>();
        foreach (var c in geometry.Proposals.Items)
        {
            if (!taken.Contains(c) && !cells.Contains(c))
            {
                cells.Add(c);
            }
        }

        cells = PolicySort.ByLess(cells, (a, b) => Distance2(from, a) < Distance2(from, b));
        return SpaceCells(cells, 1);
    }

    /// <summary>
    /// Moves the detachment to its cells each stop and keeps its targets clear until the lead hostile passes
    /// it: a live hostile at or beyond the detachment's deepest cell along the approach, or one targeting a
    /// flanker. Then it opens for good, and focus fire picks its targets.
    /// </summary>
    private static void Flank(CombatView view, CombatMemory m)
    {
        var f = m.Flank;
        if (f is null || f.Pawns.Count == 0)
        {
            return;
        }

        view.Layout.TryGetValue(out var layout);
        layout ??= new CombatLayout();
        layout.Choke.TryGetValue(out var choke);
        _ = TowardVector(layout.Toward, out var v);
        int Project(Cell c) => ((c.X - choke.X) * v.X) + ((c.Z - choke.Z) * v.Z);
        var deepest = Project(f.Cells[0]);
        foreach (var c in f.Cells.Skip(1))
        {
            deepest = Math.Max(deepest, Project(c));
        }

        foreach (var h in RankThreats(view))
        {
            var ok = h.Cell.TryGetValue(out var c);
            if ((ok && Project(c) >= deepest) || f.Pawns.Contains(h.Target))
            {
                f.Open = true;
            }
        }

        foreach (var r in m.Roles)
        {
            var j = f.Pawns.IndexOf(r.Pawn);
            if (j < 0 || r.Retreat)
            {
                continue;
            }

            r.Cell = f.Cells[j];
            if (!f.Open)
            {
                r.Target = default;
            }
        }
    }

    /// <summary>A hold-fire order for each waiting flanker not held yet.</summary>
    private static List<CombatOrder> FlankHoldFire(CombatView view, HashSet<PawnId> orderable, CombatMemory m)
    {
        var output = new List<CombatOrder>();
        var f = m.Flank;
        if (f is null || f.Open)
        {
            return output;
        }

        var state = new Dictionary<PawnId, CombatPawnState>();
        foreach (var p in view.Pawns.Items)
        {
            state[p.Id] = p;
        }

        foreach (var id in f.Pawns)
        {
            var s = state.GetValueOrDefault(id) ?? new CombatPawnState();
            if (!orderable.Contains(id) || s.Dead || s.Downed || s.FireMode == HoldFire || (s.FireMode.Length == 0 && f.Held.Contains(id)))
            {
                continue;
            }

            output.Add(new CombatOrder { Pawn = id, Kind = CombatOrderKind.FireMode, FireMode = HoldFire, Reason = CombatOrderReason.Flank });
            if (!f.Held.Contains(id))
            {
                f.Held.Add(id);
            }
        }

        return output;
    }
}
