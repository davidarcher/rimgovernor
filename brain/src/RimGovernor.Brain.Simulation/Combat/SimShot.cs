using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Simulation.CombatMetrics;
using FillKind = RimGovernor.Brain.Simulation.Combat.Fill;

namespace RimGovernor.Brain.Simulation.Combat;

/// <summary>A launched shot whose outcome is decided; it lands at tick <see cref="At"/>.</summary>
/// <param name="At">The landing tick.</param>
/// <param name="Shooter">The shooter's pawn index.</param>
/// <param name="Victim">The pawn the projectile strikes.</param>
/// <param name="Intended">Whether the victim is the aimed-at pawn.</param>
/// <param name="Distal">Whether the shot was at least 4.5 cells.</param>
internal readonly record struct Projectile(int At, int Shooter, int Victim, bool Intended, bool Distal);

/// <summary>ProjectileHitFlags: what a flying projectile may strike.</summary>
[Flags]
internal enum HitFlags : byte
{
    None = 0,
    Intended = 1,
    NonTargetPawns = 2,
    NonTargetWorld = 4,
}

/// <summary>Where a projectile ends: a pawn (index), a non-pawn thing, or neither (the ground, or off the map).</summary>
internal readonly record struct Strike(int Pawn, bool Thing)
{
    public static readonly Strike None = new(-1, false);
}

/// <summary>What the projectile was launched at (Projectile.usedTarget).</summary>
internal enum UsedTarget : byte
{
    /// <summary>The aimed-at pawn.</summary>
    Pawn,

    /// <summary>The cover thing a covered shot is sent into.</summary>
    Cover,

    /// <summary>A cell, for a wild shot.</summary>
    Cell,
}

/// <summary>A launched projectile's course: shooter si, intended target ti, the destination cell and its exact point, and the hit flags.</summary>
internal readonly record struct Flight(int Si, int Ti, Cell Dest, double Tx, double Tz, HitFlags Flags)
{
    /// <summary>Ports Projectile.CanHit for pawn j: not the launcher, the aimed target only with the IntendedTarget flag, any other pawn with NonTargetPawns.</summary>
    public bool CanHitPawn(int j)
    {
        if (j == Si)
        {
            return false;
        }
        if (j == Ti)
        {
            return (Flags & HitFlags.Intended) != 0;
        }
        return (Flags & HitFlags.NonTargetPawns) != 0;
    }
}

public sealed partial class Sim
{
    private readonly List<Projectile> _pending = [];

    /// <summary>Ports ShotReport.HitReportFor for the pawn at index si shooting the pawn at ti, from the shooter's own cell. Darkness offsets (Ideology) and gas are zero and one.</summary>
    private ShotReport Report(int si, int ti)
    {
        var a = Pawns[si];
        var b = Pawns[ti];
        double dx = b.Pos.X - a.Pos.X, dz = b.Pos.Z - a.Pos.Z;
        var d = Math.Sqrt((dx * dx) + (dz * dz));
        double fromPosture = 1, fromExecution = 1;

        // HitFactorFromShooter: accuracy^distance scaled by the range-band stat.
        var fromShooter = Math.Max(Math.Pow(a.Sh.Acc, d) * ShotMath.Bands(d, a.Sh.Touch, a.Sh.Short, a.Sh.Medium, a.Sh.Long), ShotMath.MinAim);
        var w = a.Weapon;
        var fromEquipment = Math.Min(Math.Max(ShotMath.Bands(d, w.AccuracyTouch, w.AccuracyShort, w.AccuracyMedium, w.AccuracyLong), 0.01), 1);
        var coverBlock = CoverBlockChance(b.Pos, a.Pos, b.Cover);
        double fromWeather = 1;
        if (!G.Roofed[G.Idx(a.Pos)] || !G.Roofed[G.Idx(b.Pos)])
        {
            fromWeather = _wxAcc;
        }
        var fromTargetSize = Math.Min(Math.Max(b.BodySize, 0.5), 2);
        if (b.Lying)
        {
            if (d >= 4.5)
            {
                fromPosture = 0.5;
            }
            if (d <= 3.9)
            {
                fromExecution = 7.5;
            }
        }
        var aimStandard = Math.Max(fromShooter * fromEquipment * fromWeather * fromExecution, ShotMath.MinAim);
        var aimIgnoringPosture = aimStandard * fromTargetSize;
        var aim = aimIgnoringPosture * fromPosture;
        var passCover = 1 - coverBlock;
        return new ShotReport
        {
            Distance = d,
            FromShooter = fromShooter,
            FromEquipment = fromEquipment,
            FromWeather = fromWeather,
            FromTargetSize = fromTargetSize,
            FromPosture = fromPosture,
            FromExecution = fromExecution,
            CoverBlock = coverBlock,
            AimStandard = aimStandard,
            AimIgnoringPosture = aimIgnoringPosture,
            Aim = aim,
            Total = Math.Min(Math.Max(aim * passCover, 0), 1),
        };
    }

    /// <summary>The hit report for <paramref name="shooter"/> firing at <paramref name="target"/> from where they stand now. It does not check range or line of sight.</summary>
    public ShotReport ShotReportFor(PawnId shooter, PawnId target)
    {
        int si = PawnIndex(shooter), ti = PawnIndex(target);
        if (si < 0)
        {
            throw new UnknownPawnException(shooter);
        }
        if (ti < 0)
        {
            throw new UnknownPawnException(target);
        }
        if (!Pawns[si].Armed || !Pawns[ti].Has)
        {
            throw new DomainException($"combatsim: \"{shooter}\" cannot report on \"{target}\": needs an armed shooter and a combatant target");
        }
        return Report(si, ti);
    }

    /// <summary>Ports Rand.Chance on the physical stream.</summary>
    private bool Chance(double c)
    {
        if (c >= 1)
        {
            return true;
        }
        if (c <= 0)
        {
            return false;
        }
        return RandomStreams.Physical.NextDouble() < c;
    }

    /// <summary>Ports GenMath.RoundRandom.</summary>
    private double RoundRandom(double f)
    {
        var n = Math.Truncate(f);
        if (RandomStreams.Physical.NextDouble() < f - n)
        {
            n++;
        }
        return n;
    }

    /// <summary>
    /// Ports Verb_LaunchProjectile.TryCastShot for a shot from pawn si at ti, drawing the outcome now. A wild
    /// shot is sent to a cell around the target (ShootLine.ChangeDestToMissWild), a covered one at the cover
    /// thing next to it, and every projectile then flies to its destination, where the pawns and things on the
    /// way and at the end are rolled against the shot's hit flags (A7, A9). The interception is decided
    /// against the pawns standing now.
    /// </summary>
    private void Launch(int si, int ti)
    {
        var a = Pawns[si];
        var t = Pawns[ti];
        var r = Report(si, ti);
        var id = _nextId++; // every projectile takes an id, wild or not
        var outcome = ShotOutcome.Flying;
        // ShotReport.GetRandomCoverToMissInto is drawn before the wild test.
        var haveCover = RandomCover(a.Pos, t, out var coverCell);
        var dest = t.Pos;
        var flags = HitFlags.Intended | HitFlags.NonTargetPawns;
        var used = UsedTarget.Pawn;
        if (!Chance(r.AimIgnoringPosture))
        {
            outcome = ShotOutcome.Wild;
            dest = WildDest(a.Pos, t.Pos, r.AimStandard);
            flags = HitFlags.NonTargetWorld;
            used = UsedTarget.Cell;
            if (Chance(0.5))
            {
                flags |= HitFlags.NonTargetPawns;
            }
        }
        else if (t.Cover && haveCover && !Chance(r.PassCover))
        {
            outcome = ShotOutcome.Cover;
            dest = coverCell;
            flags = HitFlags.NonTargetWorld | HitFlags.NonTargetPawns;
            used = UsedTarget.Cover;
        }
        // Projectile.Launch: the destination is the cell centre plus a random horizontal vector of up to 0.3 on
        // each axis, and the projectile is ticked by the TickList on its own thing id's 15-tick phase (A9).
        var off0 = ((RandomStreams.Physical.NextDouble() * 2) - 1) * ShotMath.LaunchScatter;
        var off1 = ((RandomStreams.Physical.NextDouble() * 2) - 1) * ShotMath.LaunchScatter;
        var fl = new Flight(si, ti, dest, dest.X + 0.5 + off0, dest.Z + 0.5 + off1, flags);
        var (end, at, arrived) = Fly(fl, ThingIds.IdPhase(id));
        if (arrived)
        {
            end = ImpactSomething(fl, used);
        }
        Emit(new SimEvent
        {
            Kind = EventKind.Shot,
            Pawn = a.Id,
            Target = t.Id,
            Cell = a.Pos,
            Outcome = outcome,
            ETA = at,
            StopsInThing = end.Thing,
            Dest = dest,
            TargetCell = t.Pos,
        });
        if (end.Pawn < 0)
        {
            return; // it stops in a thing, on the ground or off the map
        }
        var intended = arrived && used == UsedTarget.Pawn;
        _pending.Add(new Projectile(at, si, end.Pawn, intended, r.Distance * r.Distance >= 20.25));
    }

    /// <summary>Ports ShotReport.GetRandomCoverToMissInto: the cover thing next to <paramref name="t"/>, weighted by its block chance.</summary>
    private bool RandomCover(Cell shooter, SimPawn t, out Cell cell)
    {
        cell = default;
        if (!t.Cover)
        {
            return false;
        }
        _givers.Clear();
        CoverGivers(t.Pos, shooter, _givers);
        var total = 0.0;
        foreach (var c in _givers)
        {
            total += c.Chance;
        }
        if (_givers.Count == 0 || total <= 0)
        {
            return false;
        }
        var u = RandomStreams.Physical.NextDouble() * total;
        foreach (var c in _givers)
        {
            u -= c.Chance;
            if (u < 0)
            {
                cell = c.Cell;
                return true;
            }
        }
        cell = _givers[^1].Cell;
        return true;
    }

    /// <summary>
    /// Ports ShootLine.ChangeDestToMissWild for a shot from <paramref name="src"/> at <paramref name="dest"/>: a
    /// random point missDistance cells from the target's cell centre that does not lie behind the shooter,
    /// then, if the shooter cannot see that cell, the first filled cell on the line to it that is not within
    /// 1.5 cells of the shooter. The line starts at the shooter's own cell (A8).
    /// </summary>
    private Cell WildDest(Cell src, Cell dest, double aim)
    {
        var num = ShotMath.MissDistance(aim, RandomStreams.Physical.NextDouble());
        Cell c;
        while (true)
        {
            var ux = RandomStreams.Physical.NextNormal();
            var uz = RandomStreams.Physical.NextNormal();
            var n = ShotMath.Hypot(ux, uz);
            if (n > 0)
            {
                ux /= n;
                uz /= n;
            }
            // Vector3.ToIntVec3 truncates.
            c = new Cell((int)(dest.X + 0.5 + (ux * num)), (int)(dest.Z + 0.5 + (uz * num)));
            var dot = ((dest.X - src.X) * (c.X - src.X)) + ((dest.Z - src.Z) * (c.Z - src.Z));
            if (dot >= 0)
            {
                break;
            }
        }
        if (CellCanSeeCell(src, c))
        {
            return c;
        }
        var result = c;
        Grid.PointsOnLine(src, c, p =>
        {
            result = p;
            if (p == src || !G.InBounds(p) || G.Thing(p).Fillage != FillKind.Full)
            {
                return true;
            }
            double dx = src.X - p.X, dz = src.Z - p.Z;
            if ((dx * dx) + (dz * dz) <= 1.5 * 1.5)
            {
                return true;
            }
            return G.Thing(p).Door && G.DoorOpen[G.Idx(p)];
        });
        return result;
    }

    /// <summary>Projectile.StartingTicksToImpact for a shot from pawn si to the centre of pawn ti's cell shifted by an offset: the 3D origin-to-destination distance over the projectile speed.</summary>
    private double StartingTicks(int si, double tx, double tz)
    {
        var a = Pawns[si].Pos;
        var dx = tx - (a.X + 0.5);
        var dz = tz - (a.Z + 0.5);
        var n = Math.Sqrt((dx * dx) + (dz * dz) + (ShotMath.PawnAltitude * ShotMath.PawnAltitude)) / Pawns[si].Weapon.ProjectileSpeed;
        if (n <= 0)
        {
            return 0.001;
        }
        return n;
    }

    /// <summary>
    /// Ports Projectile.TickInterval and CheckForFreeInterceptBetween over a shot's whole flight. An
    /// off-camera projectile is ticked only on the ticks where (tick + its HashOffset) is a multiple of 15,
    /// each call advancing ticksToImpact by the ticks since the last one, and one interception segment is
    /// tested per call; it lands on the first call that brings ticksToImpact to 0 or below, so at the first
    /// tick of its phase at or after launch + ticks to impact (A9). <paramref name="phase"/> is that tick's
    /// residue mod 15. A shot aimed at a pawn carries only the IntendedTarget and NonTargetPawns hit flags, so
    /// only other pawns can intercept it (walls and cover were settled by line of fire and the cover roll); a
    /// wild or covered shot carries NonTargetWorld, so it can also be stopped by a wall or a sandbag on the
    /// way. It returns where the projectile was stopped, the tick it lands or is destroyed, and whether it
    /// reached its destination (ImpactSomething follows). A projectile whose next position is off the map is
    /// destroyed there.
    /// </summary>
    private (Strike End, int At, bool Arrived) Fly(Flight fl, int phase)
    {
        var a = Pawns[fl.Si];
        double ox = a.Pos.X + 0.5, oz = a.Pos.Z + 0.5;
        var start = StartingTicks(fl.Si, fl.Tx, fl.Tz);
        var total = Math.Max(1, (int)Math.Ceiling(start));
        (double X, double Z) last = (ox, oz);
        // The first call is the first tick of the phase at or after the launch.
        var first = TickCount + ((((phase - TickCount) % ShotMath.OffCameraInterval) + ShotMath.OffCameraInterval) % ShotMath.OffCameraInterval);
        for (var at = first; ; at += ShotMath.OffCameraInterval)
        {
            var left = total - (at - TickCount); // ticksToImpact after this call
            var frac = Math.Min(Math.Max(1 - (left / start), 0), 1);
            (double X, double Z) now = (ox + ((fl.Tx - ox) * frac), oz + ((fl.Tz - oz) * frac));
            if (now.X < 0 || now.Z < 0 || now.X >= G.W || now.Z >= G.H)
            {
                return (Strike.None, at, false);
            }
            var v = InterceptBetween(fl, ox, oz, last, now);
            if (v.Thing || v.Pawn >= 0)
            {
                return (v, at, false);
            }
            if (left <= 0)
            {
                return (Strike.None, at, true);
            }
            last = now;
        }
    }

    private Strike InterceptBetween(Flight fl, double ox, double oz, (double X, double Z) last, (double X, double Z) now)
    {
        if (last == now)
        {
            return Strike.None;
        }
        var from = new Cell((int)last.X, (int)last.Z);
        var to = new Cell((int)now.X, (int)now.Z);
        if (to == from || !G.InBounds(from) || !G.InBounds(to))
        {
            return Strike.None;
        }
        if (CardinalAdjacent(to, from))
        {
            return InterceptAt(fl, to, ox, oz);
        }
        if (ShotMath.InterceptFactor(ox, oz, to) <= 0)
        {
            return Strike.None;
        }
        var vx = now.X - last.X;
        var vz = now.Z - last.Z;
        var mag = ShotMath.Hypot(vx, vz);
        var stepX = vx / mag * 0.2;
        var stepZ = vz / mag * 0.2;
        var n = (int)(mag / 0.2);
        Span<Cell> seen = stackalloc Cell[64];
        var nseen = 0;
        double px = last.X, pz = last.Z;
        for (var steps = 0; ;)
        {
            px += stepX;
            pz += stepZ;
            var c = new Cell((int)px, (int)pz);
            var fresh = true;
            for (var k = 0; k < nseen; k++)
            {
                fresh = fresh && seen[k] != c;
            }
            if (fresh)
            {
                var v = InterceptAt(fl, c, ox, oz);
                if (v.Thing || v.Pawn >= 0)
                {
                    return v;
                }
                if (nseen < seen.Length)
                {
                    seen[nseen] = c;
                    nseen++;
                }
            }
            steps++;
            if (steps > n || c == to)
            {
                return Strike.None;
            }
        }
    }

    /// <summary>
    /// Ports Projectile.CheckForFreeIntercept for the things on <paramref name="c"/>: the building or item
    /// there first, then the pawns in the order they entered it (the cell's thing list).
    /// </summary>
    private Strike InterceptAt(Flight fl, Cell c, double ox, double oz)
    {
        if (c == fl.Dest || !G.InBounds(c))
        {
            return Strike.None;
        }
        var f = ShotMath.InterceptFactor(ox, oz, c);
        if (f <= 0)
        {
            return Strike.None;
        }
        var th = G.Thing(c);
        if ((fl.Flags & HitFlags.NonTargetWorld) != 0 && th.Name.Length != 0)
        {
            // A Full thing stops the projectile unless it is an open door, which only gets the fixed 0.05; any
            // other thing above fill 0.2 gets its fill percent, a full share next to the destination, 0.15 of
            // it elsewhere.
            var openDoor = th.Door && G.DoorOpen[G.Idx(c)];
            if (th.Fillage == FillKind.Full && !openDoor)
            {
                return new Strike(-1, true);
            }
            var chance = 0.0;
            if (th.FillPercent <= 0.2)
            {
                chance = 0;
            }
            else if (openDoor)
            {
                chance = 0.05;
            }
            else if (Math.Max(Grid.Abs32(c.X - fl.Dest.X), Grid.Abs32(c.Z - fl.Dest.Z)) == 1)
            {
                chance = th.FillPercent;
            }
            else
            {
                chance = th.FillPercent * 0.15;
            }
            chance *= f;
            if (chance > 1e-5 && Chance(chance))
            {
                return new Strike(-1, true);
            }
        }
        for (var k = Head[G.Idx(c)]; k != 0; k = Pawns[k - 1].Here)
        {
            var j = k - 1;
            if (!Pawns[j].Has || !fl.CanHitPawn(j))
            {
                continue;
            }
            var v = Pawns[j];
            var chance = 0.4 * Math.Min(Math.Max(v.BodySize, 0.1), 2);
            if (v.Lying)
            {
                chance *= 0.1;
            }
            if (v.Side == Pawns[fl.Si].Side)
            {
                chance *= _ff;
            }
            chance *= f;
            if (chance > 1e-5 && Chance(chance))
            {
                return new Strike(j, false);
            }
        }
        return Strike.None;
    }

    /// <summary>
    /// Ports Projectile.ImpactSomething for a projectile that reached its destination cell. A projectile aimed
    /// at a pawn strikes it; one sent at a cover thing strikes that thing; one sent at a bare cell rolls, in
    /// random order, every pawn and thing standing there that it may hit: a pawn at 0.5 per body size (halved
    /// for a lying pawn from 4.5 cells out, and scaled by the distance factor for a friendly one), a thing at
    /// 1.5 times its fill percent.
    /// </summary>
    private Strike ImpactSomething(Flight fl, UsedTarget used)
    {
        switch (used)
        {
            case UsedTarget.Pawn:
                return new Strike(fl.Ti, false);
            case UsedTarget.Cover:
                return new Strike(-1, true);
            default:
                break;
        }
        var cands = new List<Strike>();
        var th = G.Thing(fl.Dest);
        if ((fl.Flags & HitFlags.NonTargetWorld) != 0 && th.Name.Length != 0)
        {
            cands.Add(new Strike(-1, true));
        }
        for (var k = Head[G.Idx(fl.Dest)]; k != 0; k = Pawns[k - 1].Here)
        {
            var j = k - 1;
            if (fl.CanHitPawn(j))
            {
                cands.Add(new Strike(j, false));
            }
        }
        RandomStreams.Physical.Shuffle(cands.Count, (i, j) => (cands[i], cands[j]) = (cands[j], cands[i]));
        double ox = Pawns[fl.Si].Pos.X + 0.5, oz = Pawns[fl.Si].Pos.Z + 0.5;
        var far = ((fl.Tx - ox) * (fl.Tx - ox)) + ((fl.Tz - oz) * (fl.Tz - oz)) >= 20.25;
        foreach (var c in cands)
        {
            double chance;
            if (c.Thing)
            {
                chance = 1.5 * G.Thing(fl.Dest).FillPercent;
            }
            else
            {
                var v = Pawns[c.Pawn];
                chance = 0.5 * Math.Min(Math.Max(v.BodySize, 0.1), 2);
                if (v.Lying && far)
                {
                    chance *= 0.5;
                }
                if (v.Side == Pawns[fl.Si].Side)
                {
                    chance *= ShotMath.InterceptFactor(ox, oz, fl.Dest);
                }
            }
            if (Chance(chance))
            {
                return c;
            }
        }
        return Strike.None;
    }

    /// <summary>Resolves every projectile due this tick, in launch order.</summary>
    private void LandProjectiles()
    {
        var keep = new List<Projectile>(_pending.Count);
        foreach (var pr in _pending)
        {
            if (pr.At > TickCount)
            {
                keep.Add(pr);
                continue;
            }
            var v = Pawns[pr.Victim];
            if (v.Dead || !v.Has)
            {
                continue;
            }
            // Projectile.ImpactSomething: a lying target is missed half the time from 4.5 cells out. An
            // interception skips that test.
            if (pr.Intended && v.Lying && pr.Distal && !Chance(0.5))
            {
                continue;
            }
            Damage(pr.Shooter, pr.Victim);
        }
        _pending.Clear();
        _pending.AddRange(keep);
    }

    /// <summary>
    /// Lands one projectile of shooter si on victim vi: the part it hits, armor, then the injuries it adds to
    /// the victim's body, then the ledger.
    /// </summary>
    /// <remarks>
    /// It ports DamageWorker_AddInjury.ApplyDamageToPart: the part is drawn first (GetRandomNotMissingPart),
    /// armor then rates the damage, and the injury is added (FinalizeAndAddInjury) with the duplicates on the
    /// outer parts (CheckDuplicateDamageToOuterParts). Every injury runs
    /// Pawn_HealthTracker.CheckForStateChange: dead, else downed (the hit that first makes a non-player pawn
    /// downable rolls the death-on-downed chance, A33), else a pawn that can no longer use its hands drops its
    /// weapon.
    /// </remarks>
    private void Damage(int si, int vi)
    {
        var a = Pawns[si];
        var v = Pawns[vi];
        var b = v.Body!;
        var st = v.Inj;
        var part = b.ChooseHitPart(st, RandomStreams.Physical.NextDouble());
        var dealt = AfterArmor(a.Weapon.DamageAmount, a.Weapon.ArmorPenetration, v.Armor);
        bool downed = v.Downed, dead = v.Dead;
        double landed = 0;
        var healthBefore = b.SummaryHealth(st);
        var bleedBefore = b.BleedRate(st);
        dealt *= b.IncomingDamageFactor;
        if (dealt > 0 && part >= 0)
        {
            DropFilth();
            if (b.PreservesOutside(part))
            {
                dealt = PreserveOutside(b, st, part, dealt);
            }
            landed = AddInjury(v, part, dealt, ref downed, ref dead);
            if (b.HarmAllLayers && b.Parts[part].Inside && !dead)
            {
                // CheckDuplicateDamageToOuterParts: up the tree to the first outside part, each one that is
                // still whole and can be hit.
                for (var p = b.Parts[part].Parent; p >= 0 && !dead; p = b.Parts[p].Parent)
                {
                    if (b.PartHealth(st, p) != 0 && b.Parts[p].Coverage > 0)
                    {
                        landed += AddInjury(v, p, dealt, ref downed, ref dead);
                    }
                    if (!b.Parts[p].Inside)
                    {
                        break;
                    }
                }
            }
        }
        v.Damage += landed;
        Emit(new SimEvent
        {
            Kind = EventKind.Hit,
            Pawn = a.Id,
            Target = v.Id,
            Cell = v.Pos,
            HealthBefore = healthBefore,
            BleedBefore = bleedBefore,
            Amount = landed,
            Health = b.SummaryHealth(st),
            Bleed = b.BleedRate(st),
        });
        _ledger.Add(new MetricsHit(TickCount, v.Id.Value, v.Side, a.Id.Value, a.Side, landed, downed, dead));
        if (downed && !v.Downed)
        {
            v.Downed = true;
            v.Stance = Stance.Idle;
            v.Target = -1;
            v.BurstLeft = 0;
            v.Halt();
            Emit(new SimEvent { Kind = EventKind.Downed, Pawn = v.Id, Target = a.Id, Cell = v.Pos });
        }
        if (dead && !v.Dead)
        {
            v.Dead = true;
            Leave(G.Idx(v.Pos), vi);
            Emit(new SimEvent { Kind = EventKind.Died, Pawn = v.Id, Target = a.Id, Cell = v.Pos });
        }
    }

    /// <summary>The ids a damaging hit's blood filth consumes: at most one per hit, with probability <see cref="ThingIds.FilthIdsPerDamagingHit"/>.</summary>
    private void DropFilth()
    {
        if (RandomStreams.Phase.NextDouble() < ThingIds.FilthIdsPerDamagingHit)
        {
            _nextId++;
        }
    }

    /// <summary>
    /// Runs pawn vi's 60-tick health interval (A2): Pawn_HealthTracker.HealthTickInterval gives the BloodLoss
    /// hediff its bleeding growth (HediffGiver_Bleeding). A change of stage runs CheckForStateChange: dead at
    /// the lethal severity, else a standing pawn whose Consciousness the stage pushed under what keeps it awake
    /// is downed. The check has no DamageInfo and BloodLoss does not canApplyDodChanceForCapacityChanges, so a
    /// raider downed this way does not roll the death-on-downed chance.
    /// </summary>
    private void HealthTick(int vi)
    {
        var v = Pawns[vi];
        if (!v.Has || v.Dead || v.Left || v.Body is null || !v.HashTick(TickCount, Body.BloodLossInterval, 1))
        {
            return;
        }
        var b = v.Body;
        var st = v.Inj;
        if (!(b.BleedFactor > 0) || (st.Blood == 0 && b.BleedRate(st) < Body.BloodLossMinBleed))
        {
            return;
        }
        if (!b.BloodTick(st))
        {
            return;
        }
        var caps = b.Levels(st);
        v.RefreshAccuracy(caps);
        if (b.ShouldBeDead(st, caps))
        {
            v.Dead = true;
            Leave(G.Idx(v.Pos), vi);
            Emit(new SimEvent { Kind = EventKind.Died, Pawn = v.Id, Target = v.Id, Cell = v.Pos });
        }
        else if (!v.Downed && b.ShouldBeDowned(st, caps))
        {
            v.Downed = true;
            v.Stance = Stance.Idle;
            v.Target = -1;
            v.BurstLeft = 0;
            v.Halt();
            Emit(new SimEvent { Kind = EventKind.Downed, Pawn = v.Id, Target = v.Id, Cell = v.Pos });
        }
    }

    /// <summary>
    /// Ports DamageWorker_AddInjury.ReduceDamageToPreserveOutsideParts: a hit on an outside part that would
    /// take more than its health leaves the part at 1 health unless the overkill draws the destruction.
    /// </summary>
    private double PreserveOutside(Body b, InjuryState st, int part, double amount)
    {
        var health = b.PartHealth(st, part);
        if (amount < health)
        {
            return amount;
        }
        if (Chance(b.OverkillChance((amount - health) / b.Parts[part].MaxHealth)))
        {
            return amount;
        }
        return health - 1;
    }

    /// <summary>Adds one injury to the victim's part and runs the state change the game runs after every hediff added (CheckForStateChange). It returns the damage dealt.</summary>
    private double AddInjury(SimPawn v, int part, double severity, ref bool downed, ref bool dead)
    {
        var b = v.Body!;
        var st = v.Inj;
        var dealt = b.AddInjury(st, part, severity);
        var caps = b.Levels(st);
        v.RefreshAccuracy(caps);
        if (b.ShouldBeDead(st, caps))
        {
            downed = true;
            dead = true;
        }
        else if (downed)
        {
            // already down: nothing changes
        }
        else if (b.ShouldBeDowned(st, caps))
        {
            downed = true;
            if (Chance(v.DeathOnDown))
            {
                dead = true;
            }
        }
        else if (!(caps.Manipulation > 0) && v.Armed)
        {
            // A pawn that cannot use its hands drops its weapon.
            v.Armed = false;
            v.Stance = Stance.Idle;
            v.Target = -1;
            v.BurstLeft = 0;
        }
        return dealt;
    }

    /// <summary>Ports ArmorUtility.GetPostArmorDamage for a pawn without body part coverage: each rating, outermost first, either stops the damage, halves it or lets it through.</summary>
    private double AfterArmor(double amount, double penetration, double[] ratings)
    {
        foreach (var rating in ratings)
        {
            var eff = Math.Max(rating - penetration, 0);
            var roll = RandomStreams.Physical.NextDouble();
            if (roll < eff * 0.5)
            {
                amount = 0;
            }
            else if (roll < eff)
            {
                amount = RoundRandom(amount / 2);
            }
            if (amount < 0.001)
            {
                return 0;
            }
        }
        return amount;
    }
}
