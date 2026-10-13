using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Simulation.Combat;

public sealed partial class Sim
{
    private const int MaxTicksPerMove = 450;
    private const double DiagonalFactor = 1.41421;

    /// <summary>The ticks between StartPath and the first cost payment: the request is queued on tick N, claimed in PatherTick on N+1 and paid on N+2 by the recorded movers (arrivals 29/29/57 at 14.0 per cell).</summary>
    private const int PathLatency = 1;

    // Pawn collision, from the pinned assembly (approximations.md A35). The pather holds a pawn back only when
    // PawnUtility.PawnBlockedBy says the pawn on its next cell blocks it: both must pass
    // ShouldCollideWithPawns (up, alive, with a hostile near: mindState.anyCloseHostilesRecently), the sizes
    // must not let them share, and the other must be hostile (a friendly blocks only when both jobs set
    // collideWithPawns, which a walking pawn's Goto never does).

    /// <summary>PathFinderJob's Cost_Blocked: a cell holding a blocking pawn costs 175 more to path through.</summary>
    private const int PathBlockedCost = 175;

    /// <summary>Pawn_PathFollower's 15 remaining nodes under which a pawn blocked by the same hostile twice gives up its path.</summary>
    private const int BlockedNodesLeft = 15;

    /// <summary>AttackBlockingHostilePawnAfterTicks.</summary>
    private const int BlockedGiveUpTicks = 180;

    /// <summary>CheckNeedNewPathIntervalTicks.</summary>
    private const int RepathInterval = 30;

    /// <summary>PathGridDoorsBlockedJob's 32 cells of Manhattan distance from the path's ends inside which a pawn can block a path.</summary>
    private const int PathBlockerRange = 32;

    /// <summary>
    /// Ports Pawn.TicksPerMove for a pawn standing on <paramref name="from"/>: 60 over MoveSpeed, divided by the
    /// weather multiplier on an unroofed cell, times sqrt(2) for a diagonal, clamped to [1, 450]; a zero speed
    /// costs 450.
    /// </summary>
    internal double TicksPerMove(double speed, Cell from, bool diagonal)
    {
        var v = speed / 60;
        if (v == 0)
        {
            return MaxTicksPerMove;
        }
        var t = 1 / v;
        if (!G.Roofed[G.Idx(from)])
        {
            t /= Weather;
        }
        if (diagonal)
        {
            t *= DiagonalFactor;
        }
        return Math.Min(Math.Max(t, 1), MaxTicksPerMove);
    }

    /// <summary>
    /// Ports Pawn_PathFollower.CostToMoveIntoCell for a pawn on <paramref name="from"/> entering the walkable
    /// neighbour <paramref name="c"/>, without the locomotion urgency (which scales the whole path and is
    /// applied by <see cref="UrgentCost"/>).
    /// </summary>
    internal double StepCost(double speed, Cell from, Cell c)
    {
        var diagonal = c.X != from.X && c.Z != from.Z;
        var cost = TicksPerMove(speed, from, diagonal) + G.CellCost(c);
        return Math.Min(cost, MaxTicksPerMove);
    }

    /// <summary>Applies the job's locomotion urgency and the one-tick floor.</summary>
    internal static double UrgentCost(double cost, Urgency u)
    {
        switch (u)
        {
            case Urgency.Amble:
                cost = Math.Max(cost * 3, 60);
                break;
            case Urgency.Walk:
                cost = Math.Max(cost * 2, 50);
                break;
            case Urgency.Sprint:
                cost = Math.Round(cost * 0.75, MidpointRounding.ToEven);
                break;
            default:
                break;
        }
        return Math.Max(cost, 1);
    }

    private void ApplyOrder(Order o)
    {
        var i = PawnIndex(o.Pawn);
        if (i < 0)
        {
            throw new UnknownPawnException(o.Pawn);
        }
        var p = Pawns[i];
        if (p.Dead || p.Downed)
        {
            return;
        }
        switch (o.Kind)
        {
            case OrderKind.Attack:
                StartAttack(i, o);
                break;
            case OrderKind.Leave:
                LeaveMap(i);
                break;
            case OrderKind.Stop:
                p.CancelAttack();
                if (p.Moving)
                {
                    p.Halt();
                    Emit(new SimEvent { Kind = EventKind.Stopped, Pawn = p.Id, Cell = p.Pos });
                }
                break;
            case OrderKind.Move:
                p.CancelAttack();
                StartMove(i, o);
                break;
            default:
                break;
        }
    }

    /// <summary>
    /// Finds a path and begins walking it. A pawn already walking to the same destination at the same urgency
    /// keeps its place (Pawn_PathFollower StartPath returns early).
    /// </summary>
    private void StartMove(int i, Order o)
    {
        var p = Pawns[i];
        if (p.Moving && p.Dest == o.To && p.Urgency == o.Urgency)
        {
            return;
        }
        p.Halt();
        if (!G.Standable(o.To))
        {
            Emit(new SimEvent { Kind = EventKind.PathFailed, Pawn = p.Id, Cell = o.To });
            return;
        }
        if (o.To == p.Pos)
        {
            p.Dest = o.To;
            Emit(new SimEvent { Kind = EventKind.Arrived, Pawn = p.Id, Cell = p.Pos });
            return;
        }
        var path = FindPath(p, o.To);
        if (path is null)
        {
            Emit(new SimEvent { Kind = EventKind.PathFailed, Pawn = p.Id, Cell = o.To });
            return;
        }
        p.Path.Clear();
        p.Path.AddRange(path);
        p.PathAt = 0;
        p.Moving = true;
        p.Dest = o.To;
        p.Urgency = o.Urgency;
        p.LastMoved = TickCount;
        p.PathPending = true;
        SetupNext(p);
        Emit(new SimEvent { Kind = EventKind.MoveStarted, Pawn = p.Id, Cell = o.To, ETA = TickCount + PathLatency + ArrivalTicks(p) });
    }

    /// <summary>Ports SetupMoveIntoNextCell: choose the next cell, price it, carry any overshoot, and open a closed door being approached.</summary>
    private void SetupNext(SimPawn p)
    {
        p.Next = G.CellAt(p.Path[p.PathAt]);
        var cost = UrgentCost(StepCost(p.Speed, p.Pos, p.Next), p.Urgency);
        p.CostLeft = Math.Max(cost + Math.Min(p.CostLeft, 0), 1);
        p.BlockedSent = false;
        var i = G.Idx(p.Next);
        if (G.Things[G.ThingI[i]].Door && !G.DoorOpen[i])
        {
            G.DoorOpen[i] = true;
            Emit(new SimEvent { Kind = EventKind.DoorOpened, Pawn = p.Id, Cell = p.Next });
        }
    }

    /// <summary>Dry-runs the pathing recurrence for a just-started pawn: the ticks until it enters its destination if nothing blocks it.</summary>
    private int ArrivalTicks(SimPawn p)
    {
        var left = 0.0;
        var from = p.Pos;
        var ticks = 0;
        for (var k = p.PathAt; k < p.Path.Count; k++)
        {
            var c = G.CellAt(p.Path[k]);
            var cost = UrgentCost(StepCost(p.Speed, from, c), p.Urgency);
            left = Math.Max(cost + Math.Min(left, 0), 1);
            while (left > 0)
            {
                left--;
                ticks++;
            }
            from = c;
        }
        return ticks;
    }

    /// <summary>
    /// Advances one pawn one game tick (PatherTick): pay a tick of the next cell's cost, and enter the cell
    /// when it is paid. A hostile on the next cell holds the pawn back before any cost is paid.
    /// </summary>
    private void PawnTick(SimPawn p, int self)
    {
        if (!p.Moving || p.Dead || p.Downed || p.Stance != Stance.Idle)
        {
            return;
        }
        if (p.PathPending)
        {
            // The path request is claimed one tick after StartPath queued it (A37): nothing is paid on the first tick.
            p.PathPending = false;
            return;
        }
        var ni = G.Idx(p.Next);
        // WillCollideWithPawnAt is checked before any cost is paid: a pawn facing a blocker stands still.
        var b = BlockerAt(p, ni);
        if (b >= 0)
        {
            if (!p.BlockedSent)
            {
                p.BlockedSent = true;
                Emit(new SimEvent { Kind = EventKind.Blocked, Pawn = p.Id, Cell = p.Next });
            }
            Blocked(p, self, b);
            return;
        }
        p.LastBlocker = 0;
        p.LastMoved = TickCount;
        if (p.CostLeft > 0)
        {
            p.CostLeft--;
        }
        if (p.CostLeft > 0)
        {
            return;
        }
        Leave(G.Idx(p.Pos), self);
        Enter(ni, self);
        p.Pos = p.Next;
        p.PathAt++;
        Emit(new SimEvent { Kind = EventKind.Moved, Pawn = p.Id, Cell = p.Pos });
        if (p.PathAt >= p.Path.Count)
        {
            p.Moving = false;
            p.CostLeft = 0;
            p.Arrived = true;
            Emit(new SimEvent { Kind = EventKind.Arrived, Pawn = p.Id, Cell = p.Pos });
            return;
        }
        SetupNext(p);
    }

    /// <summary>
    /// Takes pawn i off the map: it frees its cell, stops any attack and movement, ends other pawns' attacks
    /// on it and stops it being a combatant. Metrics still counts it, as a hostile that was neither downed nor
    /// killed.
    /// </summary>
    private void LeaveMap(int i)
    {
        var p = Pawns[i];
        if (!p.Has || p.Dead)
        {
            return;
        }
        p.Halt();
        p.CancelAttack();
        p.Stance = Stance.Idle;
        p.BurstLeft = 0;
        Leave(G.Idx(p.Pos), i);
        p.Has = false;
        p.Left = true;
        for (var j = 0; j < Pawns.Count; j++)
        {
            var o = Pawns[j];
            if (o.Has && o.Target == i)
            {
                Emit(new SimEvent { Kind = EventKind.AttackEnded, Pawn = o.Id, Target = p.Id });
                o.Target = -1;
                o.Once = false;
            }
        }
        Emit(new SimEvent { Kind = EventKind.LeftMap, Pawn = p.Id, Cell = p.Pos });
    }

    // Cell occupancy: the pawns on each cell, in the order they entered it (the game's per-cell thing list).
    // Pawns share cells (approximations.md A35): Head holds a pawn index + 1 per cell, SimPawn.Here chains the
    // rest.

    /// <summary>Appends pawn i to the cell's list.</summary>
    private void Enter(int ci, int i)
    {
        Pawns[i].Here = 0;
        if (Head[ci] == 0)
        {
            Head[ci] = i + 1;
            return;
        }
        var k = Pawns[Head[ci] - 1];
        while (k.Here != 0)
        {
            k = Pawns[k.Here - 1];
        }
        k.Here = i + 1;
    }

    /// <summary>Removes pawn i from the cell's list; absent is a no-op.</summary>
    private void Leave(int ci, int i)
    {
        if (Head[ci] == 0)
        {
            return;
        }
        if (Head[ci] - 1 == i)
        {
            Head[ci] = Pawns[i].Here;
            Pawns[i].Here = 0;
            return;
        }
        var prev = Pawns[Head[ci] - 1];
        while (prev.Here != 0)
        {
            if (prev.Here - 1 == i)
            {
                prev.Here = Pawns[i].Here;
                Pawns[i].Here = 0;
                return;
            }
            prev = Pawns[prev.Here - 1];
        }
    }

    /// <summary>
    /// The index of the pawn on cell <paramref name="ci"/> that blocks <paramref name="p"/> from entering it, -1
    /// when none does. Hostility is the sides differing; with both pawns alive and up, "a hostile is near"
    /// holds for each by the other.
    /// </summary>
    internal int BlockerAt(SimPawn p, int ci)
    {
        var k = Head[ci];
        if (k == 0 || p.Dead || p.Downed)
        {
            return -1;
        }
        for (; k != 0; k = Pawns[k - 1].Here)
        {
            var o = Pawns[k - 1];
            if (o.Side != p.Side && o.Has && !o.Dead && !o.Downed && !SharesBySize(o, p))
            {
                return k - 1;
            }
        }
        return -1;
    }

    /// <summary>
    /// Ports PawnUtility.PawnsCanShareCellBecauseOfBodySize: two pawns under 1.5 body size whose sizes differ
    /// by more than 3.57x never block each other. Unknown sizes share nothing.
    /// </summary>
    private static bool SharesBySize(SimPawn a, SimPawn b)
    {
        double x = a.BodySize, y = b.BodySize;
        if (!(x > 0 && y > 0) || x >= 1.5 || y >= 1.5)
        {
            return false;
        }
        var r = x / y;
        if (r < 1)
        {
            r = 1 / r;
        }
        return r > 3.57;
    }

    /// <summary>
    /// A tick of Pawn_PathFollower.PatherTick facing a blocker b. Past 180 ticks without moving, or when the
    /// same blocker held it the tick before and fewer than 15 nodes remain, the pawn attacks the hostile or
    /// ends its job; the sim has no melee, so it ends the move. Otherwise NeedNewPath (every 30 ticks)
    /// re-paths from the pawn's cell, now pricing the blocker.
    /// </summary>
    private void Blocked(SimPawn p, int self, int b)
    {
        var left = p.Path.Count - p.PathAt;
        if (TickCount - p.LastMoved >= BlockedGiveUpTicks || (p.LastBlocker == b + 1 && left < BlockedNodesLeft))
        {
            p.Halt();
            Emit(new SimEvent { Kind = EventKind.Stopped, Pawn = p.Id, Cell = p.Pos });
            return;
        }
        p.LastBlocker = b + 1;
        if ((TickCount + self) % RepathInterval == 0)
        {
            var path = FindPath(p, p.Dest);
            if (path is not null)
            {
                p.Path.Clear();
                p.Path.AddRange(path);
                p.PathAt = 0;
                p.CostLeft = 0;
                p.LastMoved = TickCount;
                SetupNext(p);
            }
        }
    }

    /// <summary>
    /// The pathfinder's extra cost for entering cell <paramref name="ci"/> with a blocking pawn on it, when
    /// <paramref name="p"/> is itself one that collides. A pawn only counts within 32 cells (Manhattan) of the
    /// path's start or destination.
    /// </summary>
    internal double PathPenalty(SimPawn p, int ci, Cell to)
    {
        if (Head[ci] == 0 || BlockerAt(p, ci) < 0)
        {
            return 0;
        }
        var c = G.CellAt(ci);
        if (Math.Min(Grid.Manhattan(c, p.Pos), Grid.Manhattan(c, to)) > PathBlockerRange)
        {
            return 0;
        }
        return PathBlockedCost;
    }
}
