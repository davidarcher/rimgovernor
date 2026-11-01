using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using RimGovernor.Brain.GameAccess;
using DomainCell = RimGovernor.Brain.Domain.Cell;
using Facts = RimGovernor.Brain.GameFacts.Observations;
using Orders = RimGovernor.Brain.GameOrders.Observations;

namespace RimGovernor.Native;

/// <summary>
/// The defense alternatives' batched probe (RG-HABITAT-009, task 7.2): the game's own shooting geometry under each
/// candidate's planned pieces, and the shooters, turrets and shells the candidates are staffed, armed and powered from.
/// A pure observation: nothing is placed, reserved or simulated, and no raid is predicted.
/// </summary>
internal static class NativeDefenseProbe
{
    internal static Facts.DefenseProbeSnapshot HandleRead(Orders.DefenseProbeRequest request)
    {
        if (!Validate(request, out var invalid)) throw NativeFaults.Refused(invalid);
        if (!GameThread.ValidateIdentity(request.Scope.ExpectedIdentity, out var map, out var context, out var failure))
            throw NativeFaults.Refused(failure);
        // A piece is a ThingDef the read stands up, or a TerrainDef (a floor, a bridge) that gives no cover and blocks no sight.
        var defs = new Dictionary<string, ThingDef?>(StringComparer.Ordinal);
        foreach (var piece in request.Candidates.SelectMany(c => c.Pieces))
        {
            if (defs.ContainsKey(piece.Definition)) continue;
            defs[piece.Definition] = DefDatabase<ThingDef>.GetNamedSilentFail(piece.Definition)
                ?? (DefDatabase<TerrainDef>.GetNamedSilentFail(piece.Definition) != null ? null
                    : throw NativeFaults.Refused(GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Unknown piece definition: " + piece.Definition)));
        }
        if (request.Candidates.SelectMany(c => c.Firing.Concat(c.Approach).Concat(c.Pieces.Select(p => p.Cell))).Any(c => !Native(c).InBounds(map)))
            throw NativeFaults.Refused(GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Cell is outside the current map."));
        try { return Read(map, request, defs, context); }
        catch (Exception error)
        {
            throw NativeFaults.Unavailable(new RimGovernor.Brain.GameFacts.Common.Unavailable { Reason = RimGovernor.Brain.GameFacts.Common.UnavailableReason.ReadFailed,
                Detail = "Native defense probe could not be read completely: " + error.GetType().Name + ": " + error.Message });
        }
    }

    internal static bool Validate(Orders.DefenseProbeRequest request, out RimGovernor.Brain.GameFacts.Common.Failure failure)
    {
        failure = GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest,
            "Valid identity scope and at most 16 uniquely identified candidates of bounded unique firing, approach and piece cells are required.");
        if (request?.Scope?.ExpectedIdentity == null || request.Candidates.Count > Orders.DefenseProbeRequest.MaxCandidates) return false;
        if (request.Candidates.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != request.Candidates.Count) return false;
        foreach (var c in request.Candidates)
        {
            if (string.IsNullOrEmpty(c.Id)) return false;
            if (c.Firing.Count > Orders.DefenseProbeRequest.MaxFiring || c.Approach.Count > Orders.DefenseProbeRequest.MaxApproach
                || c.Pieces.Count > Orders.DefenseProbeRequest.MaxPieces) return false;
            if (c.Firing.Distinct().Count() != c.Firing.Count || c.Approach.Distinct().Count() != c.Approach.Count
                || c.Pieces.Select(p => p.Cell).Distinct().Count() != c.Pieces.Count) return false;
        }
        return true;
    }

    private static IntVec3 Native(DomainCell cell) => new(cell.X, 0, cell.Z);

    private static DomainCell Cell(IntVec3 cell) => new(cell.x, cell.z);

    private static Facts.DefenseProbeSnapshot Read(Map map, Orders.DefenseProbeRequest request, Dictionary<string, ThingDef?> defs, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
    {
        var standing = map.listerBuildings.allBuildingsColonist.OfType<Building_TurretGun>().OrderBy(t => t.GetUniqueLoadID(), StringComparer.Ordinal).ToList();
        var turrets = standing.Select(t => Standing(map, t)).ToList();
        var answers = new List<Facts.DefenseProbeAnswer>(request.Candidates.Count);
        foreach (var c in request.Candidates)
        {
            var planned = c.Pieces.Where(p => defs[p.Definition] != null).ToDictionary(p => Native(p.Cell), p => defs[p.Definition]!);
            var plannedTurrets = c.Pieces.Where(p => defs[p.Definition]?.building?.IsTurret == true).ToList();
            turrets.AddRange(plannedTurrets.Select(p => Planned(map, c.Id, p, defs[p.Definition]!)));
            var firing = c.Firing.Select(Native).Concat(standing.Select(t => t.Position)).Concat(plannedTurrets.Select(p => Native(p.Cell))).Distinct().ToList();
            var lines = new List<Facts.DefenseSightLine>(firing.Count * c.Approach.Count);
            foreach (var from in firing)
            {
                foreach (var to in c.Approach.Select(Native))
                {
                    var distance = Finite((from - to).LengthHorizontal);
                    if (from.Fogged(map) || to.Fogged(map))
                    {
                        lines.Add(new Facts.DefenseSightLine.Fogged { From = Cell(from), To = Cell(to), Distance = distance });
                        continue;
                    }
                    // A planned full-fill piece blocks the line as a standing wall would (GenGrid.CanBeSeenOverFast).
                    var sight = GenSight.LineOfSight(from, to, map, skipFirstCell: true, validator: x => !(planned.TryGetValue(x, out var d) && d.Fillage == FillCategory.Full));
                    lines.Add(new Facts.DefenseSightLine.Traced { From = Cell(from), To = Cell(to), Distance = distance, LineOfSight = sight, Cover = Finite(BlockChance(from, to, map, planned)) });
                }
            }
            answers.Add(new Facts.DefenseProbeAnswer { Id = c.Id, Lines = [.. lines] });
        }
        var player = Faction.OfPlayer;
        var shells = map.listerThings.AllThings.Where(t => t.def.projectileWhenLoaded != null && !t.IsForbidden(player)).Sum(t => t.stackCount);
        return new Facts.DefenseProbeSnapshot
        {
            Context = context,
            Candidates = [.. answers],
            Shooters = [.. map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).Select(Shooter)],
            Turrets = [.. turrets],
            Shells = shells,
        };
    }

    // CoverUtility.CalculateOverallBlockChance(target cell, shooterLoc, map) with the candidate's planned pieces standing as
    // cover (TryFindAdjustedCoverInCell is private, so its angle and distance factors are restated here): each of the eight
    // neighbours but the shooter's own cell gives its cover's BaseBlockChance, scaled by the angle off the shot (diagonal
    // neighbours count the angle 1.75 times) and by the shooter's distance to it, the chances compounding.
    private static double BlockChance(IntVec3 cell, IntVec3 shooter, Map map, Dictionary<IntVec3, ThingDef> planned)
    {
        if (shooter == cell) return 0;
        var total = 0f;
        var shot = (shooter - cell).AngleFlat;
        for (var i = 0; i < 8; i++)
        {
            var adj = cell + GenAdj.AdjacentCells[i];
            if (!adj.InBounds(map) || adj == shooter) continue;
            float chance;
            if (planned.TryGetValue(adj, out var def) && def.BaseBlockChance() > 0f) chance = def.BaseBlockChance();
            else if (adj.GetCover(map) is { } cover) chance = cover.BaseBlockChance();
            else continue;
            var angle = GenGeo.AngleDifferenceBetween((adj - cell).AngleFlat, shot);
            if (!cell.AdjacentToCardinal(adj)) angle *= 1.75f;
            if (angle < 15f) chance *= 1f;
            else if (angle < 27f) chance *= 0.8f;
            else if (angle < 40f) chance *= 0.6f;
            else if (angle < 52f) chance *= 0.4f;
            else if (angle < 65f) chance *= 0.2f;
            else continue;
            var reach = (shooter - adj).LengthHorizontal;
            if (reach < 1.9f) chance *= 0.3333f;
            else if (reach < 2.9f) chance *= 0.66666f;
            total += (1f - total) * chance;
        }
        return total;
    }

    private static Facts.DefenseShooter Shooter(Pawn pawn)
    {
        var verb = pawn.equipment?.PrimaryEq?.PrimaryVerb;
        var skill = pawn.skills?.GetSkill(SkillDefOf.Shooting) ?? throw new InvalidOperationException("colonist has no shooting skill: " + pawn.GetUniqueLoadID());
        return new Facts.DefenseShooter
        {
            Pawn = new RimGovernor.Brain.Domain.PawnId(pawn.GetUniqueLoadID()),
            Verb = verb == null || verb.verbProps.IsMeleeAttack ? null : VerbOf(verb.verbProps, verb.verbProps.AdjustedCooldown(verb, pawn)),
            Shooting = skill.Level,
            ViolentDisabled = pawn.WorkTagIsDisabled(WorkTags.Violent),
            Downed = pawn.Downed,
        };
    }

    private static Facts.DefenseVerb VerbOf(VerbProperties v, float cooldown) => new()
    {
        Range = Finite(v.range),
        MinRange = Finite(v.minRange),
        Warmup = Finite(v.warmupTime),
        Cooldown = Finite(cooldown),
        BurstShots = v.burstShotCount,
        BurstSeconds = Finite(((v.burstShotCount - 1) * v.ticksBetweenBurstShots).TicksToSeconds()),
        LineOfSight = v.requireLineOfSight,
    };

    // Building_TurretGun.BurstCooldownTime (protected): the def's turretBurstCooldownTime when set, else the gun verb's default.
    private static float BurstCooldown(ThingDef turret, VerbProperties verb) =>
        turret.building.turretBurstCooldownTime >= 0f ? turret.building.turretBurstCooldownTime : verb.defaultCooldownTime;

    private static Facts.DefenseTurretProbe Standing(Map map, Building_TurretGun t)
    {
        var verb = t.AttackVerb?.verbProps ?? throw new InvalidOperationException("turret has no attack verb: " + t.GetUniqueLoadID());
        return new Facts.DefenseTurretProbe
        {
            Id = t.GetUniqueLoadID(),
            Cell = Cell(t.Position),
            Planned = false,
            Verb = VerbOf(verb, BurstCooldown(t.def, verb)),
            BurstWarmup = Finite(t.def.building.turretBurstWarmupTime.Average),
            Power = NativeRoomPrediction.Power(map, t.def, t.Position, t),
            Mannable = t.IsMannable,
            Ammo = t.gun?.TryGetComp<CompChangeableProjectile>() is { } shell
                ? new Facts.DefenseTurretAmmo.Shells { Loaded = shell.LoadedShell?.defName }
                : new Facts.DefenseTurretAmmo.None(),
        };
    }

    private static Facts.DefenseTurretProbe Planned(Map map, string candidate, Orders.DefenseProbePiece piece, ThingDef def)
    {
        var gun = def.building.turretGunDef;
        var verb = gun.Verbs.FirstOrDefault(v => !v.IsMeleeAttack) ?? throw new InvalidOperationException("turret gun has no ranged verb: " + def.defName);
        return new Facts.DefenseTurretProbe
        {
            Id = candidate + "/" + def.defName + "@" + piece.Cell.X.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," + piece.Cell.Z.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Cell = piece.Cell,
            Planned = true,
            Verb = VerbOf(verb, BurstCooldown(def, verb)),
            BurstWarmup = Finite(def.building.turretBurstWarmupTime.Average),
            Power = NativeRoomPrediction.Power(map, def, Native(piece.Cell), null),
            Mannable = def.HasComp(typeof(CompMannable)),
            Ammo = gun.HasComp(typeof(CompChangeableProjectile)) ? new Facts.DefenseTurretAmmo.Shells { Loaded = null } : new Facts.DefenseTurretAmmo.None(),
        };
    }

    private static double Finite(double value) => double.IsNaN(value) || double.IsInfinity(value) ? throw new InvalidOperationException("Nonfinite native fact.") : value;
}
