using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using PawnId = RimGovernor.Brain.Domain.PawnId;

namespace RimGovernor.Brain.Simulation.Combat;

public sealed partial class Adapter
{
    // The combat.geometry caps, NativeCombatGeometryTools.MaxCells, MaxHostiles and MaxRadius. The simulator
    // answers under the same caps so a policy that leans on a truncation sees it here too.
    private const int GeometryMaxCells = 64;
    private const int GeometryMaxHostiles = 16;
    private const int GeometryMaxRadius = 12;

    /// <summary>
    /// Answers a GeometryRequest from the sim's current world, as RoundsDefensePlanner.answerGeometry does from
    /// the game's: the ask's named cells scored against its hostiles and the cells proposed for its role. A read
    /// the native tool would refuse (a cell off the map, a hostile that is not on it, more than the caps) is an
    /// answer with no proposals and no lines. Everything is computed from the sim's present pawns and walls;
    /// nothing is kept between calls.
    /// </summary>
    public GeometryReply Geometry(GeometryRequest? ask)
    {
        var empty = new GeometryReply { Answered = true };
        if (ask is not null && ask.Propose == FormationRole.RescuePath)
        {
            return RescuePath(ask);
        }
        if (ask is null || (ask.Hostiles.Count == 0 && (ask.Propose != FormationRole.None || ask.Cells.Count == 0)))
        {
            return empty;
        }
        var line = new List<Cell>(ask.Line);
        if (line.Count >= GeometryMaxCells)
        {
            line.RemoveRange(GeometryMaxCells - 1, line.Count - (GeometryMaxCells - 1));
        }

        // The line and the shooters' cells are named, so they carry the game's cover and lines of fire beside
        // the proposals.
        var named = new List<Cell>(line);
        foreach (var c in ask.Cells)
        {
            if (!named.Contains(c))
            {
                named.Add(c);
            }
        }
        var limit = GeometryMaxCells;
        if (ask.Propose != FormationRole.None)
        {
            limit--;
        }
        if (named.Count > limit)
        {
            named.RemoveRange(limit, named.Count - limit);
        }
        if (ask.Propose == FormationRole.None && named.Count == 0)
        {
            return empty;
        }
        var hostiles = Hostiles(ask.Hostiles);
        if (hostiles is null)
        {
            return empty;
        }
        var g = _sim.G;
        foreach (var c in named)
        {
            if (!g.InBounds(c))
            {
                return empty;
            }
        }
        List<Cell> proposals = [];
        switch (ask.Propose)
        {
            case FormationRole.CoverBehindLine:
                var cover = CoverBehindLine(line, named, hostiles);
                if (cover is null)
                {
                    return empty;
                }
                proposals = cover;
                break;
            case FormationRole.AdjacentToChoke:
                if (!g.InBounds(ask.Choke) || !g.InBounds(ask.OurSide) || ask.Choke == ask.OurSide)
                {
                    return empty;
                }
                proposals = AdjacentToChoke(ask.Choke, ask.OurSide, named);
                break;
            case FormationRole.FiringCells:
                var targets = ask.Targets;
                if (targets.Count > GeometryMaxHostiles)
                {
                    targets = targets.GetRange(0, GeometryMaxHostiles);
                }
                if (!g.InBounds(ask.From) || targets.Count == 0 || targets.Exists(c => !g.InBounds(c)))
                {
                    return empty;
                }
                proposals = FiringCells(ask.From, targets, named, hostiles);
                break;
            default:
                break;
        }
        var room = GeometryMaxCells - named.Count;
        if (proposals.Count > room)
        {
            proposals.RemoveRange(room, proposals.Count - room);
        }
        var colonists = ColonistCells();
        var lines = new List<SightLine>();
        var scored = new List<ScoredCell>();
        var standable = new List<Cell>();
        var accepted = new List<Cell>();

        ScoredCell Score(Cell c)
        {
            var covers = new List<CoverLine>();
            foreach (var h in hostiles)
            {
                var l = new CoverLine
                {
                    Hostile = (Policy.PawnId)h.Id,
                    Cover = _sim.CoverBlockChance(c, h.Pos, true),
                    HostileCover = _sim.CoverBlockChance(h.Pos, c, true),
                    LineOfFire = g.LineOfSight(c, h.Pos, true, 0, 0),
                };
                covers.Add(l);
                lines.Add(new SightLine { Cell = c, Hostile = (Policy.PawnId)h.Id, LineOfFire = l.LineOfFire, ColonistInPath = OnPath(c, h.Pos, colonists) });
            }
            return new ScoredCell { Cell = c, Lines = EquatableArray.From(covers) };
        }

        foreach (var c in named)
        {
            if (g.Standable(c))
            {
                standable.Add(c);
            }
            scored.Add(Score(c));
        }
        foreach (var c in proposals)
        {
            accepted.Add(c);
            standable.Add(c);
            scored.Add(Score(c));
        }
        return new GeometryReply
        {
            Answered = true,
            Role = ask.Propose,
            Proposals = EquatableArray.From(accepted),
            Lines = EquatableArray.From(lines),
            Scored = EquatableArray.From(scored),
            Standable = EquatableArray.From(standable),
        };
    }

    /// <summary>
    /// Resolves the ask's hostile ids to spawned pawns, in ask order. The native tool refuses an id that is not
    /// spawned (a dead pawn is not) and more than the cap, or a repeat. It returns null when it refuses.
    /// </summary>
    private List<SimPawn>? Hostiles(List<Policy.PawnId> ids)
    {
        if (ids.Count > GeometryMaxHostiles)
        {
            return null;
        }
        var output = new List<SimPawn>();
        for (var i = 0; i < ids.Count; i++)
        {
            var j = _sim.PawnIndex((PawnId)ids[i]);
            if (j < 0 || _sim.Pawns[j].Dead)
            {
                return null;
            }
            for (var k = 0; k < i; k++)
            {
                if (ids[k] == ids[i])
                {
                    return null;
                }
            }
            output.Add(_sim.Pawns[j]);
        }
        return output;
    }

    /// <summary>The cells of the colonists on the map: downed ones included, dead ones gone (FreeColonistsSpawned).</summary>
    private HashSet<Cell> ColonistCells()
    {
        var output = new HashSet<Cell>();
        foreach (var p in _sim.Pawns)
        {
            if (Colonist(p) && !p.Dead)
            {
                output.Add(p.Pos);
            }
        }
        return output;
    }

    /// <summary>Ports the colonist-in-path test: a colonist on the line between the cell and the hostile, neither end counting.</summary>
    private static bool OnPath(Cell from, Cell to, HashSet<Cell> colonists)
    {
        var found = false;
        Grid.PointsOnLine(from, to, c =>
        {
            found = c != from && c != to && colonists.Contains(c);
            return !found;
        });
        return found;
    }

    /// <summary>NativeCombatGeometryTools.BestCover: the cell's block chance against the hostile that gets the most of it.</summary>
    private double BestCover(Cell c, List<SimPawn> hostiles)
    {
        var best = 0.0;
        foreach (var h in hostiles)
        {
            best = Math.Max(best, _sim.CoverBlockChance(c, h.Pos, true));
        }
        return best;
    }

    /// <summary>The propose roles' filter: on the map, not named, standable.</summary>
    private bool Candidate(Cell c, List<Cell> named) => _sim.G.InBounds(c) && !named.Contains(c) && _sim.G.Standable(c);

    private static int CompareCells(Cell x, Cell y)
    {
        var c = x.X.CompareTo(y.X);
        return c != 0 ? c : x.Z.CompareTo(y.Z);
    }

    /// <summary>
    /// The cover_behind_line role: the standable cells next to the line, off it, with any cover against the
    /// hostiles, best cover first, then nearest the line, then by (x, z). It returns null when the ask is
    /// refused.
    /// </summary>
    private List<Cell>? CoverBehindLine(List<Cell> line, List<Cell> named, List<SimPawn> hostiles)
    {
        if (line.Count == 0)
        {
            return null;
        }
        foreach (var c in line)
        {
            if (!_sim.G.InBounds(c))
            {
                return null;
            }
        }
        var found = new List<(Cell Cell, double Cover, int Near)>();
        var seen = new HashSet<Cell>();
        foreach (var l in line)
        {
            foreach (var d in Grid.Adjacent)
            {
                var c = Grid.Add(l, d);
                if (seen.Contains(c) || line.Contains(c) || !Candidate(c, named))
                {
                    continue;
                }
                seen.Add(c);
                var cover = BestCover(c, hostiles);
                if (cover > 0)
                {
                    var near = Grid.DistSq(c, line[0]);
                    foreach (var o in line)
                    {
                        near = Math.Min(near, Grid.DistSq(c, o));
                    }
                    found.Add((c, cover, near));
                }
            }
        }
        found.Sort((x, y) =>
        {
            var c = y.Cover.CompareTo(x.Cover);
            if (c != 0)
            {
                return c;
            }
            c = x.Near.CompareTo(y.Near);
            return c != 0 ? c : CompareCells(x.Cell, y.Cell);
        });
        return [.. found.Select(f => f.Cell)];
    }

    /// <summary>The adjacent_to_choke role: the standable cells beside the choke strictly nearer our side than the choke is, nearest first.</summary>
    private List<Cell> AdjacentToChoke(Cell choke, Cell ourSide, List<Cell> named)
    {
        var limit = Grid.DistSq(choke, ourSide);
        var found = new List<Cell>();
        foreach (var d in Grid.Adjacent)
        {
            var c = Grid.Add(choke, d);
            if (Candidate(c, named) && Grid.DistSq(c, ourSide) < limit)
            {
                found.Add(c);
            }
        }
        found.Sort((x, y) =>
        {
            var c = Grid.DistSq(x, ourSide).CompareTo(Grid.DistSq(y, ourSide));
            return c != 0 ? c : CompareCells(x, y);
        });
        return found;
    }

    /// <summary>
    /// The firing_cells role: standable cells within the radius of <paramref name="from"/> that see at least one
    /// target, most targets first, then best cover, then nearest from, then by (x, z).
    /// </summary>
    private List<Cell> FiringCells(Cell from, List<Cell> targets, List<Cell> named, List<SimPawn> hostiles)
    {
        var found = new List<(Cell Cell, int Sight, double Cover)>();
        const int r = GeometryMaxRadius;
        for (var dx = -r; dx <= r; dx++)
        {
            for (var dz = -r; dz <= r; dz++)
            {
                if ((dx * dx) + (dz * dz) > r * r)
                {
                    continue;
                }
                var c = new Cell(from.X + dx, from.Z + dz);
                if (!Candidate(c, named))
                {
                    continue;
                }
                var sight = 0;
                foreach (var t in targets)
                {
                    if (_sim.G.LineOfSight(c, t, true, 0, 0))
                    {
                        sight++;
                    }
                }
                if (sight > 0)
                {
                    found.Add((c, sight, BestCover(c, hostiles)));
                }
            }
        }
        found.Sort((x, y) =>
        {
            var c = y.Sight.CompareTo(x.Sight);
            if (c != 0)
            {
                return c;
            }
            c = y.Cover.CompareTo(x.Cover);
            if (c != 0)
            {
                return c;
            }
            c = Grid.DistSq(x.Cell, from).CompareTo(Grid.DistSq(y.Cell, from));
            return c != 0 ? c : CompareCells(x.Cell, y.Cell);
        });
        return [.. found.Select(f => f.Cell)];
    }

    /// <summary>
    /// Answers a rescue_path ask with the walker's route to the goal, each cell flagged for hostile line of fire
    /// and a door (answerRescuePath). The game paths to touch the goal; here the route is the pawn's path to the
    /// goal cut at the first cell touching it (approximations.md A23).
    /// </summary>
    private GeometryReply RescuePath(GeometryRequest ask)
    {
        var empty = new GeometryReply { Answered = true, Role = FormationRole.RescuePath };
        if (ask.Hostiles.Count == 0 || ask.Pawn == default)
        {
            return empty;
        }
        var hostiles = Hostiles(ask.Hostiles);
        var i = _sim.PawnIndex((PawnId)ask.Pawn);
        var g = _sim.G;
        if (hostiles is null || i < 0 || _sim.Pawns[i].Dead || !g.InBounds(ask.To))
        {
            return empty;
        }
        var walker = _sim.Pawns[i];
        var route = new List<Cell>();
        if (ask.To != walker.Pos)
        {
            var path = _sim.FindPath(walker, ask.To);
            if (path is null)
            {
                return empty;
            }
            foreach (var ci in path)
            {
                var c = g.CellAt(ci);
                route.Add(c);
                if (Math.Max(Grid.Abs32(c.X - ask.To.X), Grid.Abs32(c.Z - ask.To.Z)) <= 1)
                {
                    break;
                }
            }
        }
        var cells = new List<RouteCell>();
        foreach (var c in route.Take(GeometryMaxCells))
        {
            var lineOfFire = false;
            foreach (var h in hostiles)
            {
                lineOfFire = lineOfFire || g.LineOfSight(c, h.Pos, true, 0, 0);
            }
            cells.Add(new RouteCell { Cell = c, Door = g.Thing(c).Door, LineOfFire = lineOfFire });
        }
        return empty with { Route = EquatableArray.From(cells) };
    }
}
