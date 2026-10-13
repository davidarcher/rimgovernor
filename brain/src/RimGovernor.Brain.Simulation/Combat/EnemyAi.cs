using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Simulation.CombatMetrics;

namespace RimGovernor.Brain.Simulation.Combat;

// The hostile side's decisions for ranged raiders: a port of the think-tree nodes and the raid lord a
// LordJob_AssaultColony raider runs under. Sources (all checked against AssemblyCSharpSha256, see
// RulesVersion):
//   - Verse.AI.AttackTargetFinder: BestAttackTarget, GetAvailableShootingTargetsByScore,
//     GetShootingTargetScore, FriendlyFireConeTargetScoreOffset, CanSee.
//   - RimWorld.JobGiver_AIFightEnemy: TryGiveJob, UpdateEnemyTarget, ShouldLoseTarget, FindAttackTarget;
//     RimWorld.JobGiver_AIFightEnemies.TryFindShootingPosition; RimWorld.JobGiver_AIGotoNearestHostile.TryGiveJob.
//   - Verse.AI.CastPositionFinder: TryFindCastPosition, EvaluateCell, CastPositionPreference.
//   - Verse.AI.JobDriver_Wait.CheckForAutoAttack (the Wait_Combat auto-attack).
//   - Verse.AI.Pawn_JobTracker (job expiry and the distance interval curve).
//   - RimWorld.LordJob_AssaultColony.CreateGraph, Trigger_FractionColonyDamageTaken, Trigger_TicksPassed.
//   - The AssaultColony DutyDef's JobGiver_AIFightEnemies radii (65 and 72).

/// <summary>What the raid lord needs that the scenario does not carry. The default is a raid that never gives up and never flees.</summary>
public sealed record EnemyConfig
{
    /// <summary>
    /// LordJob_AssaultColony.canTimeoutOrFlee: the raid gives up after AssaultTimeBeforeGiveUp ticks or once
    /// the colony has taken enough damage, and every raider walks to Exit and leaves. The game also needs the
    /// map exitable (TriggerFilter_MapExitable); Exit is that exit.
    /// </summary>
    public bool CanTimeoutOrFlee { get; init; }

    /// <summary>Where fleeing raiders leave the map; required with <see cref="CanTimeoutOrFlee"/>.</summary>
    public Fact<Cell> Exit { get; init; }

    /// <summary>
    /// Lord.initialColonyHealthTotal, the colony's health when the raid arrived; required with
    /// <see cref="CanTimeoutOrFlee"/>. The damage that trips the trigger is the colonists' post-armor damage in
    /// the sim's units.
    /// </summary>
    public Fact<double> ColonyHealthTotal { get; init; }

    /// <summary>AssaultTimeBeforeGiveUp, the inclusive lower bound of the give-up time; required with <see cref="CanTimeoutOrFlee"/>.</summary>
    public int GiveUpMinTicks { get; init; }

    /// <summary>AssaultTimeBeforeGiveUp, the inclusive upper bound of the give-up time; required with <see cref="CanTimeoutOrFlee"/>.</summary>
    public int GiveUpMaxTicks { get; init; }

    /// <summary>
    /// The raid lord's Trigger_FractionPawnsLost fraction (Lord.SetJob adds a LordToil_PanicFlee transition
    /// to every lord of a faction with autoFlee, drawn from attackersDownPercentageRangeForAutoFlee seeded by
    /// the lord's load id, so the caller states it): once the raiders downed or killed reach it times the
    /// raiders ever gained, every standing raider panics and runs for Exit. Unknown: the lord never panics.
    /// Needs Exit.
    /// </summary>
    public Fact<double> AutoFleeFraction { get; init; }

    /// <summary>
    /// Per raider, the pawn its warmup was already aimed at in the checkpoint a resumed run starts from. The
    /// game's Stance_Warmup keeps that target (the pawn is FullBodyBusy, so CheckForAutoAttack does not pick
    /// again) and Pawn_MindState.lastAttackedTarget keeps it sticky (+40) afterwards, so the raider's decision
    /// at tick 0 is that target, not a draw from the scores. Empty: every raider draws.
    /// </summary>
    public IReadOnlyDictionary<PawnId, PawnId> FirstTargets { get; init; } = new Dictionary<PawnId, PawnId>();
}

/// <summary>
/// AttackTargetFinder.BestAttackTarget's scored random pick, the same code for a raider and for a drafted
/// colonist's CheckForAutoAttack (A46). It holds the scratch the pick reuses.
/// </summary>
internal sealed class TargetPicker(Sim sim)
{
    /// <summary>The window of the last-attacked-target bonus, in ticks.</summary>
    private const int RecentTargetTicks = 300;

    private readonly List<double> _weights = [];
    private readonly List<double> _scores = [];
    private readonly List<int> _cand = [];
    private readonly List<Cell> _visited = [];
    private bool[] _seen = [];

    /// <summary>
    /// Lists, in scenario order, the pawns BestAttackTarget's validator lets through for searcher
    /// <paramref name="i"/>: a live standing hostile within radius, farther than minDist and (unless
    /// canTakeCloser) than the weapon's minimum range, with line of sight. A downed pawn is never listed: the
    /// sim cannot shoot one. The returned list is reused by the next call.
    /// </summary>
    public List<int> Valid(int i, double radius, double minDist, bool canTakeCloser)
    {
        var p = sim.Pawns[i];
        _cand.Clear();
        for (var j = 0; j < sim.Pawns.Count; j++)
        {
            var t = sim.Pawns[j];
            if (j == i || !t.Has || t.Side == p.Side || t.Dead || t.Downed)
            {
                continue;
            }
            double d2 = Grid.DistSq(p.Pos, t.Pos);
            if (d2 > radius * radius)
            {
                continue;
            }
            if (minDist > 0 && d2 < minDist * minDist)
            {
                continue;
            }
            if (!canTakeCloser && p.Weapon.MinRange > 0 && d2 < p.Weapon.MinRange * p.Weapon.MinRange)
            {
                continue;
            }
            if (!sim.CellCanSeeCell(p.Pos, t.Pos))
            {
                continue;
            }
            _cand.Add(j);
        }
        return _cand;
    }

    /// <summary>
    /// Ports GetRandomShootingTargetByScore over the pawns that can be shot from i's cell, drawing from
    /// <paramref name="rng"/>. It returns -1 when none can.
    /// </summary>
    public int PickByScore(int i, List<int> cands, GoRandom rng)
    {
        _scores.Clear();
        foreach (var j in cands)
        {
            _scores.Add(sim.CanHitFrom(i, j) ? Score(i, j) : double.NegativeInfinity);
        }
        SelectionWeights(_scores, _weights);
        var total = 0.0;
        foreach (var w in _weights)
        {
            total += w;
        }
        if (total <= 0)
        {
            return -1;
        }
        // TryRandomElementByWeight: walk the list taking weight off a uniform draw.
        var r = rng.NextDouble() * total;
        var pick = -1;
        for (var k = 0; k < _weights.Count; k++)
        {
            var w = _weights[k];
            if (w <= 0)
            {
                continue;
            }
            pick = cands[k];
            r -= w;
            if (r <= 0)
            {
                break;
            }
        }
        return pick;
    }

    /// <summary>
    /// Ports GetAvailableShootingTargetsByScore's weighting. A score of -Inf marks a pawn that cannot be shot.
    /// When the best score is at least 1, every pawn within 30 of it weighs InverseLerp(best-30, best, score);
    /// below 1 the first best pawn weighs 1 and the rest 0.
    /// </summary>
    internal static void SelectionWeights(List<double> scores, List<double> output)
    {
        output.Clear();
        var best = double.NegativeInfinity;
        var bi = -1;
        for (var k = 0; k < scores.Count; k++)
        {
            var sc = scores[k];
            if (!double.IsNegativeInfinity(sc) && (bi < 0 || sc > best))
            {
                best = sc;
                bi = k;
            }
        }
        for (var k = 0; k < scores.Count; k++)
        {
            var sc = scores[k];
            var w = 0.0;
            if (bi < 0 || double.IsNegativeInfinity(sc))
            {
                // cannot be shot
            }
            else if (best < 1)
            {
                if (k == bi)
                {
                    w = 1;
                }
            }
            else if (sc >= best - 30)
            {
                w = (sc - (best - 30)) / 30;
            }
            output.Add(w);
        }
    }

    /// <summary>
    /// Ports GetShootingTargetScore for pawn i choosing target j. Target priority is 1 for pawns; the
    /// non-combatant, juvenile, downed and blast radius terms are 0 or unreachable for this slice
    /// (approximations.md).
    /// </summary>
    private double Score(int i, int j)
    {
        var a = sim.Pawns[i];
        var b = sim.Pawns[j];
        var num = 60.0;
        num -= Math.Min(Math.Sqrt(Grid.DistSq(b.Pos, a.Pos)), 40);
        if (b.Armed && b.Stance != Stance.Idle && b.Cast == i)
        {
            num += 10; // TargetCurrentlyAimingAt: Stance_Warmup or Stance_Cooldown
        }
        if (a.LastAttacked == j && sim.TickCount - a.LastAttackTick <= RecentTargetTicks)
        {
            num += 40;
        }
        num -= sim.CoverBlockChance(b.Pos, a.Pos, true) * 10;
        if (b.Armed)
        {
            num += a.Weapon.TargetHasRangedAttackScoreOffset;
        }
        return num + ConeOffset(i, j);
    }

    /// <summary>
    /// Ports FriendlyFireConeTargetScoreOffset for a humanlike shooter with an ordinary projectile: the cells a
    /// shot at j could strike (a radius of at least 1.5 around the target, each ray cut at the first cell that
    /// blocks sight) weigh every other pawn there by how likely it is to intercept: 18, scaled by distance,
    /// +0.6 of it when the pawn is a hostile of the shooter and -1 of it when a friend.
    /// </summary>
    private double ConeOffset(int i, int j)
    {
        var g = sim.G;
        var a = sim.Pawns[i];
        var b = sim.Pawns[j];
        var radius = Math.Max(AdjustedForcedMiss(a.Weapon.ForcedMissRadius, Grid.DistSq(b.Pos, a.Pos)), 1.5);
        if (_seen.Length != g.W * g.H)
        {
            _seen = new bool[g.W * g.H];
        }
        double ox = a.Pos.X + 0.5, oz = a.Pos.Z + 0.5;
        _visited.Clear();
        var r = (int)Math.Ceiling(radius);
        for (var dz = -r; dz <= r; dz++)
        {
            for (var dx = -r; dx <= r; dx++)
            {
                if ((double)((dx * dx) + (dz * dz)) > radius * radius)
                {
                    continue;
                }
                var dest = new Cell(b.Pos.X + dx, b.Pos.Z + dz);
                if (!g.InBounds(dest))
                {
                    continue;
                }
                var open = true;
                bool Visit(Cell c)
                {
                    open = g.CanBeSeenOver(c);
                    if (!open)
                    {
                        return false;
                    }
                    var ci = g.Idx(c);
                    if (!_seen[ci])
                    {
                        _seen[ci] = true;
                        _visited.Add(c);
                    }
                    return true;
                }

                Grid.PointsOnLine(a.Pos, dest, Visit);
                if (open)
                {
                    Visit(dest);
                }
            }
        }
        var total = 0.0;
        foreach (var c in _visited)
        {
            _seen[g.Idx(c)] = false;
            var f = ShotMath.InterceptFactor(ox, oz, c);
            if (f <= 0)
            {
                continue;
            }
            for (var k = 0; k < sim.Pawns.Count; k++)
            {
                var t = sim.Pawns[k];
                if (k == j || !t.Has || t.Dead || t.Pos != c)
                {
                    continue;
                }
                var v = k == i ? 40.0 : 18.0;
                v *= f;
                if (t.Side != a.Side)
                {
                    v *= 0.6;
                }
                else
                {
                    v = -v;
                }
                total += v;
            }
        }
        return total;
    }

    /// <summary>Ports VerbUtility.CalculateAdjustedForcedMiss for a shot whose squared length is <paramref name="d2"/>.</summary>
    private static double AdjustedForcedMiss(double forcedMiss, int d2)
    {
        if (d2 < 9)
        {
            return 0;
        }
        if (d2 < 25)
        {
            return forcedMiss * 0.5;
        }
        if (d2 < 49)
        {
            return forcedMiss * 0.8;
        }
        return forcedMiss;
    }
}

/// <summary>
/// Drives the hostile ranged pawns of a <see cref="Sim"/>. <see cref="Orders"/> is called once before each
/// Step. All its choices draw from the sim's enemy stream and nothing else, so changing it never shifts the
/// physical-outcome stream.
/// </summary>
public sealed class EnemyAi
{
    // The DutyDef's targetAcquireRadius and targetKeepRadius.
    private const double AcquireRadius = 65.0;
    private const double KeepRadius = 72.0;

    // TicksSinceEngageToLoseTarget.
    private const int EngageTicks = 400;

    // ExpiryInterval_ShooterSucceeded, in ticks (inclusive).
    private const int ShooterExpiryMin = 450;
    private const int ShooterExpiryMax = 550;

    // The damage trigger's fraction range (0.25 to 0.35, so a span of 0.1) and minimum damage.
    private const double FleeFractionMin = 0.25;
    private const double FleeFractionSpan = 0.1;
    private const double FleeMinDamage = 900.0;

    private static double[]? s_decay;

    private enum JobKind : byte
    {
        None,

        /// <summary>Wait_Combat.</summary>
        Wait,

        /// <summary>Goto a shooting position.</summary>
        Goto,

        /// <summary>Goto the nearest hostile (JobGiver_AIGotoNearestHostile).</summary>
        GotoNearest,
    }

    /// <summary>One raider's think state: Pawn_JobTracker's current job and Pawn_MindState's enemyTarget and lastEngageTargetTick.</summary>
    private sealed class EnemyPawn
    {
        public JobKind Job { get; set; }

        /// <summary>Job start tick.</summary>
        public int Start { get; set; }

        /// <summary>Expiry tick (not for GotoNearest).</summary>
        public int End { get; set; }

        /// <summary>Goto's destination.</summary>
        public Cell Goal { get; set; }

        /// <summary>GotoNearest's target pawn.</summary>
        public int Near { get; set; } = -1;

        /// <summary>mindState.enemyTarget, -1 none.</summary>
        public int Enemy { get; set; } = -1;

        public int LastEngage { get; set; } = SimPawn.NoTick;

        public bool WasBusy { get; set; }
    }

    private readonly Sim _sim;
    private readonly EnemyConfig _cfg;
    private readonly EnemyPawn[] _p;
    private readonly TargetPicker _picker;
    private readonly Cell _exit;
    private readonly double _health;
    private readonly int _giveUpAt;
    private readonly double _fraction;

    // The Trigger_FractionPawnsLost fraction (0: none) and the raiders in the lord when it began
    // (Lord.numPawnsEverGained).
    private readonly double _autoFlee;
    private readonly int _gained;
    private bool _panicked;
    private int[] _comp = [];

    /// <summary>
    /// Binds an AI to a Sim that has been Reset, and draws the raid lord's random thresholds from its enemy
    /// stream. It throws <see cref="UnsupportedScenarioException"/> for a raid that can flee without an exit or
    /// a colony health.
    /// </summary>
    public EnemyAi(Sim sim, EnemyConfig cfg)
    {
        _sim = sim;
        _cfg = cfg;
        _picker = new TargetPicker(sim);
        _p = new EnemyPawn[sim.Pawns.Count];
        for (var i = 0; i < _p.Length; i++)
        {
            _p[i] = new EnemyPawn();
        }
        if (cfg.AutoFleeFraction.TryGetValue(out var frac))
        {
            if (!(frac > 0 && frac <= 1))
            {
                throw new UnsupportedScenarioException($"the auto-flee fraction {frac} is outside (0, 1]");
            }
            _autoFlee = frac;
            foreach (var p in sim.Pawns)
            {
                if (p.Has && p.Side == Sides.Hostile)
                {
                    _gained++;
                }
            }
        }
        if (!cfg.CanTimeoutOrFlee && _gained == 0)
        {
            return;
        }
        if (!cfg.Exit.TryGetValue(out _exit) || !sim.G.Standable(_exit))
        {
            throw new UnsupportedScenarioException("a raid that can flee needs a standable exit cell");
        }
        if (!cfg.CanTimeoutOrFlee)
        {
            return;
        }
        if (!cfg.ColonyHealthTotal.TryGetValue(out _health) || !(_health > 0))
        {
            throw new UnsupportedScenarioException("a raid that can flee needs the colony health total");
        }
        if (cfg.GiveUpMinTicks <= 0 || cfg.GiveUpMaxTicks < cfg.GiveUpMinTicks)
        {
            throw new UnsupportedScenarioException("a raid that can flee needs its give-up time range");
        }
        // CreateGraph draws the give-up time, then the damage fraction.
        _giveUpAt = RangeIncl(cfg.GiveUpMinTicks, cfg.GiveUpMaxTicks);
        _fraction = FleeFractionMin + (sim.RandomStreams.Enemy.NextDouble() * FleeFractionSpan);
    }

    /// <summary>Whether the raid lord has moved to the exit toil.</summary>
    public bool Fleeing { get; private set; }

    /// <summary>IntRange.RandomInRange.</summary>
    private int RangeIncl(int lo, int hi) => lo + _sim.RandomStreams.Enemy.NextInt(hi - lo + 1);

    /// <summary>
    /// Runs the lord's PawnLost trigger after a Step: when the raiders incapped or killed
    /// (Lord.numPawnsLostViolently; a downed raider that later dies counts once, it left the lord when it
    /// went down) reach AutoFleeFraction of those ever gained, every raider still standing takes the panic
    /// toil (LordToil_PanicFlee.Init: MentalState_PanicFlee, then ExitMapRandom) and stops fighting. The game
    /// flags them fleeing from that tick.
    /// </summary>
    public void Observe()
    {
        if (_autoFlee == 0 || _panicked)
        {
            return;
        }
        var lost = 0;
        foreach (var p in _sim.Pawns)
        {
            if (p.Has && p.Side == Sides.Hostile && (p.Downed || p.Dead))
            {
                lost++;
            }
        }
        if (lost < _gained * _autoFlee)
        {
            return;
        }
        _panicked = true;
        foreach (var p in _sim.Pawns)
        {
            if (p.Has && p.Side == Sides.Hostile && !p.Downed && !p.Dead)
            {
                p.Panic = true;
            }
        }
    }

    /// <summary>The hostile pawns' orders for the coming Step.</summary>
    public List<Order> Orders()
    {
        var s = _sim;
        if (_cfg.CanTimeoutOrFlee && !Fleeing && ShouldFlee())
        {
            Fleeing = true;
        }
        var output = new List<Order>();
        for (var i = 0; i < s.Pawns.Count; i++)
        {
            var p = s.Pawns[i];
            if (!p.Has || p.Side != Sides.Hostile || p.Dead || p.Downed || !p.Armed)
            {
                continue;
            }
            if (Fleeing || p.Panic)
            {
                Flee(output, i);
                continue;
            }
            var ep = _p[i];
            if (ep.Enemy >= 0 && p.LastAttacked == ep.Enemy)
            {
                // Verb: Notify_EngagedTarget on a shot at the enemy target
                ep.LastEngage = Math.Max(ep.LastEngage, p.LastAttackTick);
            }
            if (JobDone(i))
            {
                Think(output, i);
            }
            var busy = p.Stance != Stance.Idle;
            if (ep.Job == JobKind.Wait && p.Target < 0 && !busy && (ep.WasBusy || s.TickCount == ep.Start || p.CallTick(s.TickCount)))
            {
                AutoAttack(output, i);
            }
            ep.WasBusy = busy;
        }
        return output;
    }

    /// <summary>
    /// The two transitions into LordToil_ExitMap: Trigger_TicksPassed and Trigger_FractionColonyDamageTaken
    /// (colony damage above the larger of the drawn fraction of the initial colony health and 900, counted
    /// from tick 0).
    /// </summary>
    private bool ShouldFlee()
    {
        if (_sim.TickCount >= _giveUpAt)
        {
            return true;
        }
        var dmg = 0.0;
        foreach (var p in _sim.Pawns)
        {
            if (p.Side == Sides.Colonist && (p.Has || p.Left))
            {
                dmg += p.Damage;
            }
        }
        return dmg > Math.Max(_health * _fraction, FleeMinDamage);
    }

    /// <summary>The exit toil: walk to the exit at a jog and leave the map there.</summary>
    private void Flee(List<Order> output, int i)
    {
        var p = _sim.Pawns[i];
        if (p.Pos == _exit)
        {
            output.Add(new Order { Pawn = p.Id, Kind = OrderKind.Leave });
        }
        else if (!p.Moving || p.Dest != _exit)
        {
            output.Add(new Order { Pawn = p.Id, Kind = OrderKind.Move, To = _exit });
        }
    }

    /// <summary>Reports whether pawn i's job has ended and the think tree runs.</summary>
    private bool JobDone(int i)
    {
        var s = _sim;
        var p = s.Pawns[i];
        var ep = _p[i];
        switch (ep.Job)
        {
            case JobKind.Wait:
                return Expired(p, ep.Start, ep.End - ep.Start);
            case JobKind.Goto:
                return !p.Moving || Expired(p, ep.Start, ep.End - ep.Start);
            case JobKind.GotoNearest:
                var t = s.Pawns[ep.Near];
                return !p.Moving || !t.Has || t.Dead || t.Downed || Expired(p, ep.Start, DistanceExpiry(Grid.Manhattan(p.Pos, t.Pos)));
            default:
                // A pawn with no job gets one at its next job tracker tick (JobTrackerTickInterval: curJob ==
                // null, TryFindAndStartJob); the resumed checkpoint's raiders, at tick 0, decide at once.
                return s.TickCount == 0 || p.CallTick(s.TickCount);
        }
    }

    /// <summary>
    /// Ports the job expiry test of Pawn_JobTracker.JobTrackerTickInterval (A41): on a tick the pawn's job
    /// tracker runs (every 15 ticks, CallTick), a job started before this tick whose expiry interval has
    /// passed ends only when pawn.IsHashIntervalTick(expiryInterval, 15) holds as well, that is when
    /// |(tick + HashOffset(thingID)) % interval| &lt; 15. That window opens once per interval, so a job of 450
    /// to 550 ticks lasts until the first window at or after its nominal end: 1.5 times as long on average.
    /// </summary>
    private bool Expired(SimPawn p, int start, int interval)
    {
        var t = _sim.TickCount;
        return interval > 0 && t != start && t >= start + interval && p.CallTick(t) && p.HashTick(t, interval, ShotMath.OffCameraInterval);
    }

    /// <summary>
    /// Pawn_JobTracker.DistanceIntervalCurve: a job scaled by the distance to its target expires after 30
    /// ticks at 0 cells, 120 at 50 and 600 at 100 (linear between, held outside).
    /// </summary>
    internal static int DistanceExpiry(int manhattan)
    {
        double d = manhattan;
        if (d <= 0)
        {
            return 30;
        }
        if (d <= 50)
        {
            return (int)(30 + (90 * d / 50));
        }
        if (d <= 100)
        {
            return (int)(120 + (480 * (d - 50) / 50));
        }
        return 600;
    }

    /// <summary>
    /// The AssaultColony duty's think tree for a ranged raider with no job: JobGiver_AIFightEnemies, then (the
    /// trash-building nodes have nothing to attack here) JobGiver_AIGotoNearestHostile.
    /// </summary>
    private void Think(List<Order> output, int i)
    {
        var ep = _p[i];
        ep.Job = JobKind.None;
        UpdateEnemyTarget(i);
        if (ep.Enemy >= 0 && FightJob(i, output))
        {
            return;
        }
        GotoNearest(output, i);
    }

    /// <summary>
    /// Ports JobGiver_AIFightEnemy.UpdateEnemyTarget: keep the target unless ShouldLoseTarget; look for one
    /// when there is none; when there is one, switch to whatever the finder now returns, and (chaseTarget is
    /// false) drop the target when the finder returns nothing.
    /// </summary>
    private void UpdateEnemyTarget(int i)
    {
        var s = _sim;
        var ep = _p[i];
        var cur = ep.Enemy;
        if (cur >= 0 && ShouldLoseTarget(i))
        {
            cur = -1;
        }
        if (cur < 0)
        {
            cur = FindAttackTarget(i);
            if (cur >= 0)
            {
                ep.LastEngage = s.TickCount; // Notify_EngagedTarget
            }
        }
        else
        {
            var next = FindAttackTarget(i);
            if (next < 0)
            {
                cur = -1;
            }
            else if (next != cur)
            {
                ep.LastEngage = s.TickCount;
                cur = next;
            }
        }
        ep.Enemy = cur;
    }

    /// <summary>
    /// Ports ShouldLoseTarget: the target is gone, was last engaged more than 400 ticks ago, cannot be
    /// reached, is beyond the keep radius or is no longer a threat. A downed pawn is no threat here (see
    /// approximations.md).
    /// </summary>
    private bool ShouldLoseTarget(int i)
    {
        var s = _sim;
        var p = s.Pawns[i];
        var ep = _p[i];
        var t = s.Pawns[ep.Enemy];
        if (!t.Has || t.Dead || t.Downed)
        {
            return true;
        }
        if (s.TickCount - ep.LastEngage > EngageTicks)
        {
            return true;
        }
        if ((double)Grid.DistSq(p.Pos, t.Pos) > KeepRadius * KeepRadius)
        {
            return true;
        }
        return !Flood(p.Pos).Has(s.G.Idx(t.Pos));
    }

    /// <summary>
    /// Ports FindAttackTarget: AttackTargetFinder.BestAttackTarget with the AssaultColony acquire radius, line
    /// of sight to pawns and, when no target can be shot from here, reachability.
    /// </summary>
    private int FindAttackTarget(int i)
    {
        var s = _sim;
        var p = s.Pawns[i];
        var cands = _picker.Valid(i, AcquireRadius, 0, true);
        foreach (var j in cands)
        {
            if (s.CanHitFrom(i, j))
            {
                return _picker.PickByScore(i, cands, s.RandomStreams.Enemy);
            }
        }
        var reach = Flood(p.Pos);
        int best = -1, bd = 0;
        foreach (var j in cands)
        {
            // ClosestThing_Global: nearest, first on ties
            var d = Grid.DistSq(p.Pos, s.Pawns[j].Pos);
            if (reach.Has(s.G.Idx(s.Pawns[j].Pos)) && (best < 0 || d < bd))
            {
                best = j;
                bd = d;
            }
        }
        return best;
    }

    /// <summary>Gives the pawn a Wait_Combat of 450-550 ticks. Pawn.pather.StopDead halts a pawn that was walking.</summary>
    private void StartWait(List<Order> output, int i)
    {
        var s = _sim;
        var p = s.Pawns[i];
        var ep = _p[i];
        ep.Job = JobKind.Wait;
        ep.Start = s.TickCount;
        ep.End = s.TickCount + RangeIncl(ShooterExpiryMin, ShooterExpiryMax);
        if (p.Moving)
        {
            output.Add(new Order { Pawn = p.Id, Kind = OrderKind.Stop });
        }
    }

    /// <summary>
    /// The rest of JobGiver_AIFightEnemy.TryGiveJob once there is an enemy target and a ranged verb: wait where
    /// cover or closeness already allows a shot, else walk to the position CastPositionFinder picks. It returns
    /// false when it finds none, so the think tree falls through.
    /// </summary>
    private bool FightJob(int i, List<Order> output)
    {
        var s = _sim;
        var p = s.Pawns[i];
        var ep = _p[i];
        var t = s.Pawns[ep.Enemy];
        var cover = s.CoverBlockChance(p.Pos, t.Pos, p.Cover) > 0.01;
        var reserve = CanReserve(p.Pos, i);
        var hit = s.CanHitFrom(i, ep.Enemy);
        var near = Grid.DistSq(p.Pos, t.Pos) < 25;
        if ((cover && reserve && hit) || (near && hit))
        {
            StartWait(output, i);
            return true;
        }
        if (!CastPosition(i, ep.Enemy, out var dest))
        {
            return false;
        }
        if (dest == p.Pos)
        {
            StartWait(output, i);
            return true;
        }
        ep.Job = JobKind.Goto;
        ep.Start = s.TickCount;
        ep.End = s.TickCount + RangeIncl(ShooterExpiryMin, ShooterExpiryMax);
        ep.Goal = dest;
        output.Add(new Order { Pawn = p.Id, Kind = OrderKind.Move, To = dest });
        return true;
    }

    /// <summary>Ports JobGiver_AIGotoNearestHostile: walk toward the nearest reachable standing hostile, nothing when already touching it.</summary>
    private void GotoNearest(List<Order> output, int i)
    {
        var s = _sim;
        var p = s.Pawns[i];
        var ep = _p[i];
        var reach = Flood(p.Pos);
        int best = -1, bd = 0;
        for (var j = 0; j < s.Pawns.Count; j++)
        {
            var t = s.Pawns[j];
            if (!t.Has || t.Side == p.Side || t.Dead || t.Downed || !reach.Has(s.G.Idx(t.Pos)))
            {
                continue;
            }
            var d = Grid.DistSq(p.Pos, t.Pos);
            if (best < 0 || d < bd)
            {
                best = j;
                bd = d;
            }
        }
        if (best < 0 || Math.Max(Grid.Abs32(p.Pos.X - s.Pawns[best].Pos.X), Grid.Abs32(p.Pos.Z - s.Pawns[best].Pos.Z)) <= 1)
        {
            return;
        }
        ep.Job = JobKind.GotoNearest;
        ep.Start = s.TickCount;
        ep.Near = best;
        output.Add(new Order { Pawn = p.Id, Kind = OrderKind.Move, To = s.Pawns[best].Pos });
    }

    /// <summary>
    /// JobDriver_Wait.CheckForAutoAttack for a raider on Wait_Combat: start one burst at the scored-random
    /// target among those it can hit from where it stands.
    /// </summary>
    private void AutoAttack(List<Order> output, int i)
    {
        var s = _sim;
        var p = s.Pawns[i];
        var cands = _picker.Valid(i, p.Weapon.Range, p.Weapon.MinRange, false);
        var j = _picker.PickByScore(i, cands, s.RandomStreams.Enemy);
        if (_cfg.FirstTargets.TryGetValue(p.Id, out var t) && s.TickCount == 0)
        {
            var k = s.PawnIndex(t);
            if (k >= 0 && cands.Contains(k))
            {
                j = k;
            }
        }
        if (j >= 0)
        {
            // Auto: the verb starts the moment the raider decides, and its warmup counts down that tick. A
            // decision at tick 0 is the resumed checkpoint's raider already in Wait_Combat: the recorded
            // stance rows show warmup "left 60" at tick 0 and every raider's first shot on tick 60 (A36,
            // #2933).
            output.Add(new Order { Pawn = p.Id, Kind = OrderKind.Attack, Target = s.Pawns[j].Id, Once = true, Auto = true });
        }
    }

    /// <summary>Stands in for PawnDestinationReservationManager.CanReserve: the cell is free of other pawns and is no other raider's destination.</summary>
    private bool CanReserve(Cell c, int i)
    {
        var s = _sim;
        for (var k = 0; k < s.Pawns.Count; k++)
        {
            if (k == i)
            {
                continue;
            }
            var o = s.Pawns[k];
            if ((o.Has || o.Downed) && !o.Dead && o.Pos == c)
            {
                return false;
            }
            var ep = _p[k];
            if (ep.Job == JobKind.Goto && ep.Goal == c)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>The cells one flood reaches: those labelled with the component id.</summary>
    private readonly record struct ReachSet(int[] Comp, int Id)
    {
        public bool Has(int i) => Id > 0 && Comp[i] == Id;
    }

    /// <summary>
    /// The set of cells pawn pathing could reach from <paramref name="from"/>, with the pathfinder's neighbour
    /// rules; pawns do not block it (Reachability ignores them). The map never changes during a fight and the
    /// neighbour rule is symmetric, so reachability is an equivalence: the first call labels every walkable
    /// cell with its connected component and each flood is a lookup.
    /// </summary>
    private ReachSet Flood(Cell from)
    {
        var g = _sim.G;
        if (_comp.Length != g.W * g.H)
        {
            LabelComponents();
        }
        if (!g.InBounds(from))
        {
            return default;
        }
        return new ReachSet(_comp, _comp[g.Idx(from)]);
    }

    /// <summary>Sets the component array to 0 for a cell pawns cannot walk through and otherwise to the number (from 1) of its connected component.</summary>
    private void LabelComponents()
    {
        var g = _sim.G;
        int w = g.W, h = g.H;
        var comp = new int[w * h];
        for (var i = 0; i < comp.Length; i++)
        {
            if (g.CellCostAt(i) < Grid.ImpassableCost)
            {
                comp[i] = -1; // walkable, not yet labelled
            }
        }
        var label = 0;
        var q = new List<int>();
        for (var start = 0; start < comp.Length; start++)
        {
            if (comp[start] != -1)
            {
                continue;
            }
            label++;
            comp[start] = label;
            q.Clear();
            q.Add(start);
            for (var head = 0; head < q.Count; head++)
            {
                var ci = q[head];
                int cx = ci % w, cz = ci / w;
                for (var k = 0; k < Grid.Adjacent.Length; k++)
                {
                    var off = Grid.Adjacent[k];
                    int nx = cx + off.X, nz = cz + off.Z;
                    if (nx < 0 || nz < 0 || nx >= w || nz >= h)
                    {
                        continue;
                    }
                    var ni = (nz * w) + nx;
                    if (comp[ni] == 0)
                    {
                        continue;
                    }
                    if (k >= 4 && (comp[(cz * w) + nx] == 0 || comp[(nz * w) + cx] == 0))
                    {
                        continue;
                    }
                    if (comp[ni] == -1)
                    {
                        comp[ni] = label;
                        q.Add(ni);
                    }
                }
            }
        }
        _comp = comp;
    }

    /// <summary>
    /// Ports CastPositionFinder.TryFindCastPosition for pawn i's attack verb against pawn j
    /// (maxRangeFromTarget = the weapon's range, cover wanted when the range exceeds 5): the best cell within
    /// range of the target from which it can be hit, preferring cover, nearness and about 0.8 of the range.
    /// Cells on the caster's side of the line through the target perpendicular to the caster are tried first,
    /// and the other side only when none beats 0.33. It returns false when no cell qualifies.
    /// </summary>
    private bool CastPosition(int i, int j, out Cell result)
    {
        var s = _sim;
        var g = s.G;
        var p = s.Pawns[i];
        var t = s.Pawns[j];
        var w = p.Weapon;
        var reach = Flood(p.Pos);
        var rng = w.Range;
        var optimal = rng * 0.8 * (rng * 0.8);
        var span = (int)Math.Ceiling(rng);
        int minX = Math.Max(t.Pos.X - span, 0), maxX = Math.Min(t.Pos.X + span, g.W - 1);
        int minZ = Math.Max(t.Pos.Z - span, 0), maxZ = Math.Min(t.Pos.Z + span, g.H - 1);
        var best = default(Cell);
        var found = false;
        var bestPref = 0.001;

        void Eval(Cell c)
        {
            if ((double)Grid.DistSq(c, t.Pos) > rng * rng || !reach.Has(g.Idx(c)))
            {
                return;
            }
            var pref = CastPreference(c, i, j, optimal, rng > 5);
            if (pref < bestPref || !s.CellCanHit(w, c, t.Pos) || !CanReserve(c, i))
            {
                return;
            }
            best = c;
            found = true;
            bestPref = pref;
        }

        Eval(p.Pos);
        if (bestPref >= 1)
        {
            result = p.Pos;
            return true;
        }
        // CellLine.Between(target, caster), then the perpendicular through the target, in single precision as
        // the game computes it (a caster in the target's row makes the slope infinite and every side test
        // false).
        var slope = 1e8f;
        if (t.Pos.X != p.Pos.X)
        {
            slope = (float)(p.Pos.Z - t.Pos.Z) / (float)(p.Pos.X - t.Pos.X);
        }
        var perp = -1 / slope;
        var icept = (float)t.Pos.Z - ((float)t.Pos.X * perp);
        bool Above(Cell c) => (float)c.Z > (perp * (float)c.X) + icept;
        var side = Above(p.Pos);

        void Sweep(bool same)
        {
            for (var z = minZ; z <= maxZ; z++)
            {
                for (var x = minX; x <= maxX; x++)
                {
                    var c = new Cell(x, z);
                    if ((Above(c) == side) == same)
                    {
                        Eval(c);
                    }
                }
            }
        }

        Sweep(true);
        if (found && bestPref > 0.33)
        {
            result = best;
            return true;
        }
        Sweep(false);
        if (found)
        {
            result = best;
            return true;
        }
        result = p.Pos;
        return false;
    }

    /// <summary>
    /// Math.Pow(0.967, sqrt(d2)), the preference's falloff with the distance from the caster, memoised by
    /// squared distance: the entries are the very values Math.Pow returns, computed once and shared by every
    /// fight.
    /// </summary>
    internal static double DistanceDecay(int d2)
    {
        var t = Volatile.Read(ref s_decay);
        if (t is not null && d2 < t.Length)
        {
            return t[d2];
        }
        var old = t?.Length ?? 0;
        var table = new double[Math.Max(Math.Max(d2 + 1, 2 * old), 1024)];
        for (var i = 0; i < table.Length; i++)
        {
            table[i] = Math.Pow(0.967, Math.Sqrt(i));
        }
        Volatile.Write(ref s_decay, table); // a racing larger table is replaced by an equal prefix
        return table[d2];
    }

    /// <summary>
    /// Ports CastPositionFinder.CastPositionPreference for a humanlike caster (aiAvoidCover false) with no
    /// avoid grid. The game also scales by a static field left over from an earlier request (the distance from
    /// the caster to the cell, set only when maxRangeFromCaster is used); AIFightEnemies never sets it, so that
    /// factor is not applied.
    /// </summary>
    private double CastPreference(Cell c, int i, int j, double optimal, bool wantCover)
    {
        var s = _sim;
        var p = s.Pawns[i];
        var t = s.Pawns[j];
        var num = 0.3;
        if (wantCover)
        {
            num += s.CoverBlockChance(c, t.Pos, true) * 0.55;
        }
        num *= DistanceDecay(Grid.DistSq(p.Pos, c));
        double rt = Grid.DistSq(c, t.Pos);
        var f = 0.7 + (0.3 * (1 - (Math.Abs(rt - optimal) / optimal)));
        if (rt < 25)
        {
            f *= 0.5;
        }
        num *= f;
        if (s.G.Thing(c).Passability == Passability.PassThroughOnly)
        {
            num *= 0.4;
        }
        return num;
    }
}
