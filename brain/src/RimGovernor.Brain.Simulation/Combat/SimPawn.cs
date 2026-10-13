using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Simulation.CombatMetrics;

namespace RimGovernor.Brain.Simulation.Combat;

internal enum Stance : byte
{
    Idle,

    /// <summary>Stance_Warmup: aiming.</summary>
    Warmup,

    /// <summary>The verb Bursting (with the between-shots Stance_Cooldown).</summary>
    Burst,

    /// <summary>Stance_Cooldown after the burst.</summary>
    Cooldown,
}

/// <summary>The shooter stats a combatant resolved at load.</summary>
internal sealed class ShooterState
{
    public double Acc { get; set; }

    public double Touch { get; set; }

    public double Short { get; set; }

    public double Medium { get; set; }

    public double Long { get; set; }

    public double Aim { get; set; }

    public double Cooldown { get; set; }
}

/// <summary>
/// One pawn of the simulation: the movement state (the port of Pawn_PathFollower's nextCell,
/// nextCellCostLeft and nextCellCostTotal) and its resolved combat state.
/// </summary>
internal sealed class SimPawn
{
    /// <summary>
    /// A tick that never happened (Pawn_MindState starts its tick fields far in the past). Go used MinInt32 in
    /// a 64-bit int; this is far enough back that tick differences cannot overflow an int.
    /// </summary>
    public const int NoTick = -1_000_000_000;

    public PawnId Id { get; init; }

    public Cell Pos { get; set; }

    /// <summary>The MoveSpeed stat, cells per second.</summary>
    public double Speed { get; init; }

    public Cell Dest { get; set; }

    public bool Moving { get; set; }

    public Urgency Urgency { get; set; }

    /// <summary>The cell indices still to enter, in order.</summary>
    public List<int> Path { get; } = [];

    public int PathAt { get; set; }

    public Cell Next { get; set; }

    /// <summary>Counts down by one per tick; at or below zero the pawn enters Next and the overshoot carries into the following cell.</summary>
    public double CostLeft { get; set; }

    public bool BlockedSent { get; set; }

    /// <summary>Set by StartPath until the first tick has passed: the async path request is claimed a tick later (A37).</summary>
    public bool PathPending { get; set; }

    /// <summary>Set by the pather tick that enters the destination and read by the same tick's Step (Sim.OnArrive).</summary>
    public bool Arrived { get; set; }

    /// <summary>The pawn index + 1 that blocked the previous tick's entry, 0 when nothing did (Pawn_PathFollower.lastBlocker).</summary>
    public int LastBlocker { get; set; }

    /// <summary>The last tick the pawn paid movement cost or was given a path (lastMovedTick).</summary>
    public int LastMoved { get; set; }

    /// <summary>The next pawn index + 1 on the same cell (cellPawns), 0 last.</summary>
    public int Here { get; set; }

    public string Side { get; init; } = "";

    // combatant

    public bool Has { get; set; }

    public bool Armed { get; set; }

    public double BodySize { get; set; }

    public bool Cover { get; set; }

    /// <summary>The body and its injuries; KillAt is the body's lethal damage threshold, the scale of the health fraction the policy reads.</summary>
    public Body? Body { get; set; }

    public InjuryState Inj { get; set; } = new(0);

    public double KillAt { get; set; }

    public double DeathOnDown { get; set; }

    public double[] Armor { get; set; } = [];

    public WeaponDef Weapon { get; set; } = new();

    public ShooterState Sh { get; } = new();

    /// <summary>ShootingAccuracyPawn as the native read gave it, for a pawn at full Manipulation and Sight; Sh.Acc follows the injuries (A43).</summary>
    public double AccHealthy { get; set; }

    /// <summary>The damage dealt to the pawn so far: each injury's severity capped at the health of its part.</summary>
    public double Damage { get; set; }

    public bool Downed { get; set; }

    public bool Dead { get; set; }

    /// <summary>The pawn's thingIDNumber, which sets the ticks its IsHashIntervalTick intervals fall on (<see cref="HashTick"/>).</summary>
    public int ThingNum { get; set; }

    public Stance Stance { get; set; }

    /// <summary>Ticks until warmup completes or cooldown ends.</summary>
    public int StanceLeft { get; set; }

    /// <summary>The pawn index the current warmup or burst shoots at.</summary>
    public int Cast { get; set; }

    /// <summary>The pawn index of the attack order, -1 none.</summary>
    public int Target { get; set; } = -1;

    public int BurstLeft { get; set; }

    /// <summary>Ticks until the next burst shot.</summary>
    public int NextShot { get; set; }

    /// <summary>Drops the attack order's target when the cooldown ends (Order.Once).</summary>
    public bool Once { get; set; }

    /// <summary>Pawn_MindState.lastAttackedTarget: the pawn index of the last shot's target.</summary>
    public int LastAttacked { get; set; } = -1;

    /// <summary>Pawn_MindState.lastAttackTargetTick: the tick the last shot was fired.</summary>
    public int LastAttackTick { get; set; } = NoTick;

    /// <summary>Marks a raider the lord sent into PanicFlee (EnemyAi.Observe): it runs for the exit and is out of the fight, though still on the map.</summary>
    public bool Panic { get; set; }

    /// <summary>Marks a pawn that left the map; it no longer has a combat role.</summary>
    public bool Left { get; set; }

    public void CancelAttack()
    {
        Target = -1;
        Once = false;
        if (Stance == Stance.Warmup)
        {
            Stance = Stance.Idle;
        }
    }

    public void Halt()
    {
        Moving = false;
        PathPending = false;
        Path.Clear();
        PathAt = 0;
        CostLeft = 0;
        BlockedSent = false;
        LastBlocker = 0;
        Dest = Pos;
    }

    /// <summary>Reports a pawn not standing: the game's posture for a downed pawn.</summary>
    public bool Lying => Downed || Dead;

    /// <summary>
    /// Ports Verse.Gen.IsHashIntervalTick(Thing, interval, delta): |(TicksGame + HashOffset(thingIDNumber)) %
    /// interval| &lt; delta. With delta 1 it is the plain interval tick.
    /// </summary>
    public bool HashTick(int tick, int interval, int delta)
    {
        var r = (tick + ThingIds.HashOffset(ThingNum)) % interval;
        if (r < 0)
        {
            r = -r;
        }
        return r < delta;
    }

    /// <summary>
    /// A tick on which the pawn's Pawn.TickInterval runs with the off-camera 15-tick delta
    /// (GenTicks.GetCameraUpdateRate): its job tracker then expires and starts jobs and Wait_Combat looks for
    /// a target.
    /// </summary>
    public bool CallTick(int tick) => HashTick(tick, ShotMath.OffCameraInterval, 1);

    /// <summary>The ShootingAccuracyPawn stat from the pawn's current Manipulation and Sight, as the stat is recomputed on every read. A pawn whose capacities are at full health keeps the value it was read with.</summary>
    internal void RefreshAccuracy(Capacities caps)
    {
        var off = Accuracy.CapacityOffset(caps.Sight, 2, 12) + Accuracy.CapacityOffset(caps.Manipulation, 1, 8);
        if (off == 0)
        {
            Sh.Acc = AccHealthy;
            return;
        }
        Sh.Acc = Accuracy.FromRaw(Accuracy.RawFrom(AccHealthy) + off);
    }

    /// <summary>Resolves the combat spec of <paramref name="ps"/> (Go <c>pawn.loadCombat</c>).</summary>
    public void LoadCombat(PawnSpec ps)
    {
        var cs = ps.Combat!;
        var id = ps.Id.Value;
        if (ps.Side != Sides.Colonist && ps.Side != Sides.Hostile)
        {
            throw new UnsupportedScenarioException($"combat pawn \"{id}\" has side \"{ps.Side}\"");
        }
        Has = true;
        Cover = cs.CanBenefitFromCover;
        Target = -1;
        LastAttacked = -1;
        LastAttackTick = NoTick;
        BodySize = Need(cs.BodySize, "BodySize", id);
        if (cs.Body is null)
        {
            throw new UnsupportedScenarioException($"pawn \"{id}\" body unknown");
        }
        var why = cs.Body.Validate();
        if (why.Length != 0)
        {
            throw new UnsupportedScenarioException($"pawn \"{id}\" body {why}");
        }
        Body = cs.Body;
        KillAt = cs.Body.LethalDamage;
        Inj = new InjuryState(cs.Body.Parts.Count);
        ThingNum = ThingIds.ThingNumber(id);
        if (!(cs.DeathOnDownedChance >= 0 && cs.DeathOnDownedChance <= 1))
        {
            throw new UnsupportedScenarioException($"pawn \"{id}\" death-on-downed chance {Format(cs.DeathOnDownedChance)} is outside [0, 1]");
        }
        DeathOnDown = cs.DeathOnDownedChance;
        if (!(BodySize > 0))
        {
            throw new UnsupportedScenarioException($"pawn \"{id}\" body size {Format(BodySize)} is invalid");
        }
        if (!cs.Armor.TryGetValue(out var armor))
        {
            throw new UnsupportedScenarioException($"pawn \"{id}\" armor unknown");
        }
        Armor = [.. armor];
        if (cs.Weapon is null)
        {
            return;
        }
        var w = cs.Weapon;
        if (!(w.Range > 0) || w.MinRange < 0 || w.MinRange > w.Range ||
            w.WarmupSeconds < 0 || w.CooldownSeconds < 0 ||
            w.BurstShotCount < 1 || (w.BurstShotCount > 1 && w.TicksBetweenBurstShots < 1) ||
            !(w.AccuracyTouch > 0) || !(w.AccuracyShort > 0) || !(w.AccuracyMedium > 0) || !(w.AccuracyLong > 0) ||
            !(w.ProjectileSpeed > 0) || w.DamageAmount < 1 || w.ArmorPenetration < 0)
        {
            throw new UnsupportedScenarioException($"pawn \"{id}\" weapon \"{w.Name}\" has a missing or invalid value: {w}");
        }
        if (w.ForcedMissRadius > 0.5)
        {
            throw new UnsupportedScenarioException($"pawn \"{id}\" weapon \"{w.Name}\" has a forced miss radius, which is not ordinary ranged fire");
        }
        Armed = true;
        Weapon = w;
        var st = cs.Shooter;
        Sh.Acc = Need(st.ShootingAccuracyPawn, "ShootingAccuracyPawn", id);
        Sh.Touch = Need(st.AccuracyTouch, "ShootingAccuracyFactor_Touch", id);
        Sh.Short = Need(st.AccuracyShort, "ShootingAccuracyFactor_Short", id);
        Sh.Medium = Need(st.AccuracyMedium, "ShootingAccuracyFactor_Medium", id);
        Sh.Long = Need(st.AccuracyLong, "ShootingAccuracyFactor_Long", id);
        Sh.Aim = Need(st.AimingDelayFactor, "AimingDelayFactor", id);
        Sh.Cooldown = Need(st.RangedCooldownFactor, "RangedCooldownFactor", id);
        AccHealthy = Sh.Acc;
    }

    private static double Need(Fact<double> f, string what, string id)
    {
        if (!f.TryGetValue(out var v))
        {
            throw new UnsupportedScenarioException($"pawn \"{id}\" {what} unknown");
        }
        return v;
    }

    private static string Format(double v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
}
