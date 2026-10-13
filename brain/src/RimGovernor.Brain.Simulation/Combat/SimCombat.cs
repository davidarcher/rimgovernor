using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Simulation.CombatMetrics;

namespace RimGovernor.Brain.Simulation.Combat;

public sealed partial class Sim
{
    private readonly List<MetricsHit> _ledger = [];

    /// <summary>Reads the scenario-wide combat facts once any combatant exists.</summary>
    private void LoadCombatWorld(Scenario sc)
    {
        if (!Pawns.Any(p => p.Has))
        {
            return;
        }
        if (!sc.WeatherAccuracy.TryGetValue(out _wxAcc) || !(_wxAcc > 0))
        {
            throw new UnsupportedScenarioException("weather accuracy multiplier unknown or not positive");
        }
        if (!sc.FriendlyFireChanceFactor.TryGetValue(out _ff) || _ff < 0)
        {
            throw new UnsupportedScenarioException("friendly fire chance factor unknown or negative");
        }
    }

    internal static int SecondsToTicks(double sec) => (int)Math.Round(sec * 60, MidpointRounding.ToEven);

    internal void StartAttack(int i, Order o)
    {
        var p = Pawns[i];
        var ti = PawnIndex(o.Target);
        if (ti < 0)
        {
            throw new UnknownPawnException(o.Target);
        }
        if (!p.Armed)
        {
            throw new DomainException($"combatsim: pawn \"{p.Id}\" has no ranged weapon to attack with");
        }
        if (!Pawns[ti].Has || Pawns[ti].Side == p.Side)
        {
            throw new DomainException($"combatsim: pawn \"{p.Id}\" cannot attack \"{o.Target}\": not a combatant of the other side");
        }
        if (p.Target != ti)
        {
            p.CancelAttack();
        }
        p.Target = ti;
        p.Once = o.Once;
        if (o.Auto && !p.Dead && !p.Downed)
        {
            StartVerb(i); // Wait_Combat's CheckForAutoAttack: TryStartAttack at once
        }
    }

    /// <summary>Ports Verb.CanHitTargetFrom for a pawn at its own cell: the target within [min range, range] and in line of fire.</summary>
    internal bool CanHitFrom(int si, int ti) => CellCanHit(Pawns[si].Weapon, Pawns[si].Pos, Pawns[ti].Pos);

    /// <summary>Verb.CanHitTargetFrom for a shooter of weapon <paramref name="w"/> standing on <paramref name="from"/> and a target on <paramref name="to"/>.</summary>
    internal bool CellCanHit(WeaponDef w, Cell from, Cell to)
    {
        double dx = to.X - from.X, dz = to.Z - from.Z;
        var d2 = (dx * dx) + (dz * dz);
        if (d2 > w.Range * w.Range || d2 < w.MinRange * w.MinRange)
        {
            return false;
        }
        return CellCanSeeCell(from, to);
    }

    /// <summary>
    /// Runs one pawn's stance, job and verb phases in the order Pawn.Tick does (the pather already ran):
    /// stances, then jobs, then the equipment's verbs.
    /// </summary>
    private void CombatTick(int i)
    {
        var p = Pawns[i];
        if (!p.Armed || !p.Has || p.Dead || p.Downed)
        {
            return;
        }
        // Stance_Warmup.StanceTick and Stance_Cooldown/Stance_Busy.StanceTick.
        switch (p.Stance)
        {
            case Stance.Warmup:
                var t = Pawns[p.Cast];
                if (t.Downed || t.Dead || !t.Has || !CanHitFrom(i, p.Cast))
                {
                    p.Stance = Stance.Idle;
                    if (p.Once)
                    {
                        // TryStartAttack keeps no order: the dropped warmup leaves Wait_Combat to scan again.
                        p.Target = -1;
                        p.Once = false;
                    }
                    break;
                }
                p.StanceLeft--;
                if (p.StanceLeft <= 0)
                {
                    WarmupComplete(i);
                }
                break;
            case Stance.Cooldown:
                p.StanceLeft--;
                if (p.StanceLeft <= 0)
                {
                    p.Stance = Stance.Idle;
                    if (p.Once)
                    {
                        p.Target = -1;
                        p.Once = false;
                    }
                }
                break;
            default:
                break;
        }
        // JobDriver_AttackStatic: start the verb whenever the pawn is not busy.
        StartVerb(i);
        // Verb.VerbTick while Bursting.
        if (p.Stance == Stance.Burst)
        {
            p.NextShot--;
            if (p.NextShot <= 0)
            {
                CastBurstShot(i);
            }
        }
    }

    /// <summary>
    /// Ports Pawn.TryStartAttack / Verb.TryStartCastOn for a pawn with a target and no stance: the warmup
    /// stance starts (its countdown begins on the next stance tick), or an instant weapon fires at once. It is
    /// what the AttackStatic driver does every tick it is idle, and what Wait_Combat's CheckForAutoAttack does
    /// when an Auto order lands.
    /// </summary>
    private void StartVerb(int i)
    {
        var p = Pawns[i];
        if (p.Stance != Stance.Idle || p.Target < 0)
        {
            return;
        }
        var t = Pawns[p.Target];
        if (t.Downed || t.Dead || !t.Has)
        {
            Emit(new SimEvent { Kind = EventKind.AttackEnded, Pawn = p.Id, Target = t.Id });
            p.Target = -1;
        }
        else if (CanHitFrom(i, p.Target))
        {
            p.Cast = p.Target;
            if (p.Weapon.WarmupSeconds > 0)
            {
                p.Stance = Stance.Warmup;
                p.StanceLeft = SecondsToTicks(p.Weapon.WarmupSeconds * p.Sh.Aim);
            }
            else
            {
                WarmupComplete(i);
            }
        }
    }

    /// <summary>Ports Verb.WarmupComplete: start the burst and fire its first shot at once.</summary>
    private void WarmupComplete(int i)
    {
        var p = Pawns[i];
        p.Stance = Stance.Burst;
        p.BurstLeft = p.Weapon.BurstShotCount;
        CastBurstShot(i);
    }

    /// <summary>Ports Verb.TryCastNextBurstShot.</summary>
    private void CastBurstShot(int i)
    {
        var p = Pawns[i];
        var t = Pawns[p.Cast];
        if (!t.Dead && t.Has && CellCanSeeCell(p.Pos, t.Pos))
        {
            // Verb_LaunchProjectile.TryCastShot: stopBurstWithoutLos
            Launch(i, p.Cast);
            p.BurstLeft--;
            p.LastAttacked = p.Cast; // Verb.TryCastNextBurstShot: Notify_AttackedTarget
            p.LastAttackTick = TickCount;
        }
        else
        {
            p.BurstLeft = 0;
        }
        if (p.BurstLeft > 0)
        {
            p.NextShot = p.Weapon.TicksBetweenBurstShots;
            return;
        }
        p.Stance = Stance.Cooldown;
        p.StanceLeft = SecondsToTicks(p.Weapon.CooldownSeconds * p.Sh.Cooldown);
    }

    /// <summary>Reports whether one side has no standing combatant left and which side that leaves standing. Both sides must have had combatants.</summary>
    public (string Winner, bool Resolved) Resolved()
    {
        int colonists = 0, hostiles = 0;
        foreach (var p in Pawns)
        {
            if (!p.Has || p.Downed || p.Dead)
            {
                continue;
            }
            if (p.Side == Sides.Colonist)
            {
                colonists++;
            }
            else
            {
                hostiles++;
            }
        }
        if (hostiles == 0)
        {
            return (Sides.Colonist, true);
        }
        if (colonists == 0)
        {
            return (Sides.Hostile, true);
        }
        return ("", false);
    }

    /// <summary>The damage ledger so far: every landed projectile, in order, as combat metrics rows. The list aliases a buffer the next Step may grow.</summary>
    public IReadOnlyList<MetricsHit> DamageLedger => _ledger;

    /// <summary>
    /// Scores the fight so far with the shared combat metrics definition: the staging read is every combatant
    /// standing at tick 0 and the final read is the current state with the whole ledger.
    /// </summary>
    public CombatMetrics.Metrics Metrics(string fixture)
    {
        var staged = new Dictionary<string, string>();
        var start = new MetricsRead(0);
        var startPawns = new List<MetricsPawn>();
        var endPawns = new List<MetricsPawn>();
        foreach (var p in Pawns)
        {
            if (!p.Has && !p.Left)
            {
                continue;
            }
            staged[p.Id.Value] = p.Side;
            startPawns.Add(new MetricsPawn(p.Id.Value));
            endPawns.Add(new MetricsPawn(p.Id.Value, p.Dead, p.Downed && !p.Dead, p.Left || p.Panic));
        }
        start = start with { Pawns = startPawns };
        var end = new MetricsRead(TickCount) { Pawns = endPawns, Damage = [.. _ledger] };
        return CombatMetrics.Metrics.Aggregate(fixture, staged, [start, end]);
    }
}
