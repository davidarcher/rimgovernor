namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    /// <summary>How far, in cells, a colonist with no room to go to moves away from the nearest hostile.</summary>
    internal const int ShelterStep = 12;

    /// <summary>The shelter tactic's moves; empty with no live hostile position known.</summary>
    internal static List<CombatRole> ShelterRoles(CombatView view, GeometryReply geometry, IReadOnlyList<Cell> unreachable) =>
        ShelterRolesFor(view, geometry, unreachable, d =>
        {
            var (busy, known) = SquadDraftBusy(d);
            return known && !busy;
        });

    /// <summary>The shelter moves of the defenders <paramref name="pick"/> admits.</summary>
    internal static List<CombatRole> ShelterRolesFor(CombatView view, GeometryReply geometry, IReadOnlyList<Cell> unreachable, Func<SquadDefenderFacts, bool> pick)
    {
        var hostiles = new List<Cell>();
        var down = DownPawns(view);
        foreach (var t in view.Positional)
        {
            if (t.Position.TryGetValue(out var c) && !Positive(t.Dead) && !Positive(t.Downed) && !down.Contains(t.Id))
            {
                hostiles.Add(c);
            }
        }
        if (hostiles.Count == 0)
        {
            return [];
        }
        var safeCells = new List<Cell>();
        foreach (var r in view.Rooms)
        {
            if (r.Roofed && !hostiles.Any(r.Contains))
            {
                safeCells.AddRange(RectCells(r.Interior));
            }
        }
        var safe = safeCells
            .OrderByDescending(c => NearestDistance(c, hostiles))
            .ThenBy(c => c, Comparer<Cell>.Create((a, b) => CellLess(a, b) ? -1 : (CellLess(b, a) ? 1 : 0)))
            .ToList();
        if (view.Layout.TryGetValue(out var layout))
        {
            safe = [.. layout.Retreat, .. safe];
        }
        safe = safe.Where(c => geometry.Stands(c) && !unreachable.Contains(c)).ToList();
        var at = new Dictionary<PawnId, Cell>();
        foreach (var p in view.Pawns)
        {
            if (p.Cell.TryGetValue(out var c))
            {
                at[p.Id] = c;
            }
        }
        var roles = new List<CombatRole>();
        var taken = new List<Cell>();
        foreach (var d in view.Defenders)
        {
            if (!at.TryGetValue(d.Id, out var c) || !pick(d) || Positive(d.Dead) || Positive(d.Downed) || Positive(d.MentalState))
            {
                continue;
            }
            Cell cell;
            if (safe.Count > 0)
            {
                cell = safe[0];
                safe.RemoveAt(0);
            }
            else
            {
                var candidates = ShelterChecks(view)
                    .Where(to => geometry.Stands(to) && !unreachable.Contains(to) && !taken.Contains(to) && KeepDefended(view, c, to) == to && NearestDistance(to, hostiles) > NearestDistance(c, hostiles))
                    .OrderByDescending(to => NearestDistance(to, hostiles))
                    .ToList();
                if (candidates.Count == 0)
                {
                    continue;
                }
                cell = candidates[0];
            }
            roles.Add(new CombatRole { Pawn = d.Id, Cell = cell, Retreat = true });
            taken.Add(cell);
        }
        return SortRoles(roles);
    }

    /// <summary>
    /// Identifies evidence for a new formation. Unknown pawn positions and weapon capabilities cannot
    /// establish a replacement. Arrival alone is useful shelter, not completion of the fight.
    /// </summary>
    public static string ShelterReconsider(CombatView view, CombatMemory m)
    {
        if (m.Tactic != CombatTactic.Shelter)
        {
            return "";
        }
        foreach (var role in m.Roles)
        {
            if (role.Cell is { } roleCell && m.Unreachable.Contains(roleCell))
            {
                return "shelter_unreachable";
            }
        }
        var (_, available) = SquadAssignments(view);
        if (available)
        {
            return "defenders_available";
        }
        foreach (var role in m.Roles)
        {
            if (role.Cell is not { } roleCell)
            {
                continue;
            }
            foreach (var pawn in view.Pawns)
            {
                if (pawn.Id != role.Pawn || !pawn.Cell.TryGetValue(out var at) || at != roleCell)
                {
                    continue;
                }
                foreach (var hostile in view.Positional)
                {
                    if (!hostile.Position.TryGetValue(out var cell) || Positive(hostile.Dead) || Positive(hostile.Downed))
                    {
                        continue;
                    }
                    var reach = 1.5;
                    foreach (var state in view.Pawns)
                    {
                        if (state.Id == hostile.Id && state.WeaponRange > reach)
                        {
                            reach = state.WeaponRange;
                        }
                    }
                    double dx = at.X - cell.X;
                    double dz = at.Z - cell.Z;
                    if (Math.Sqrt((dx * dx) + (dz * dz)) <= reach)
                    {
                        return "shelter_exposed";
                    }
                }
            }
        }
        return "";
    }

    /// <summary>Names bounded candidates for the existing geometry read. A proposed vector is never an order until native confirms standability.</summary>
    internal static List<Cell> ShelterChecks(CombatView view)
    {
        var cells = new List<Cell>();
        void Add(Cell c)
        {
            if (c.X >= 0 && c.Z >= 0 && cells.Count < MaxGeometryCells / 2 && !cells.Contains(c))
            {
                cells.Add(c);
            }
        }
        if (view.Layout.TryGetValue(out var layout))
        {
            foreach (var c in layout.Retreat)
            {
                Add(c);
            }
        }
        foreach (var room in view.Rooms)
        {
            if (room.Roofed)
            {
                foreach (var c in RectCells(room.Interior))
                {
                    Add(c);
                }
            }
        }
        foreach (var pawn in view.Pawns)
        {
            if (!pawn.Cell.TryGetValue(out var from))
            {
                continue;
            }
            var hostiles = new List<Cell>();
            foreach (var h in view.Positional)
            {
                if (h.Position.TryGetValue(out var c) && !Positive(h.Dead) && !Positive(h.Downed))
                {
                    hostiles.Add(c);
                }
            }
            if (hostiles.Count == 0)
            {
                continue;
            }
            var near = hostiles[NearestIndex(from, hostiles)];
            double dx = from.X - near.X;
            double dz = from.Z - near.Z;
            var n = Math.Sqrt((dx * dx) + (dz * dz));
            if (n == 0)
            {
                continue;
            }
            var to = KeepDefended(view, from, new Cell(
                Math.Max(0, from.X + (int)Math.Round(dx / n * ShelterStep, MidpointRounding.AwayFromZero)),
                Math.Max(0, from.Z + (int)Math.Round(dz / n * ShelterStep, MidpointRounding.AwayFromZero))));
            Add(to);
            foreach (var offset in new[] { new Cell(1, 0), new Cell(-1, 0), new Cell(0, 1), new Cell(0, -1) })
            {
                Add(KeepDefended(view, from, new Cell(to.X + offset.X, to.Z + offset.Z)));
            }
        }
        return cells;
    }

    /// <summary>
    /// Pulls a fallback move target back onto the defended side: inside the room the colonist stands in, else,
    /// in a layout, no further than the firing line's far side. A colonist already outside both keeps the
    /// unclamped target; there is no defended side to hold.
    /// </summary>
    internal static Cell KeepDefended(CombatView view, Cell from, Cell to)
    {
        foreach (var r in view.Rooms)
        {
            if (!r.Contains(from) || r.Contains(to))
            {
                continue;
            }
            var interior = r.Interior;
            return new Cell(
                Math.Min(Math.Max(to.X, interior.X), interior.X + interior.Width - 1),
                Math.Min(Math.Max(to.Z, interior.Z), interior.Z + interior.Height - 1));
        }
        if (!view.Layout.TryGetValue(out var layout) || !BehindFiringLine(layout.Firing, layout.Toward, from) || BehindFiringLine(layout.Firing, layout.Toward, to))
        {
            return to;
        }
        double dx = to.X - from.X;
        double dz = to.Z - from.Z;
        for (var t = 1 - (1.0 / ShelterStep); t > 0; t -= 1.0 / ShelterStep)
        {
            var c = new Cell(from.X + (int)Math.Round(dx * t, MidpointRounding.AwayFromZero), from.Z + (int)Math.Round(dz * t, MidpointRounding.AwayFromZero));
            if (BehindFiringLine(layout.Firing, layout.Toward, c))
            {
                return c;
            }
        }
        return from;
    }
}
