using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.Policy;
using Region = Verse.Region;

namespace RimGovernor.Native;

/// <summary>
/// The baby fit mechanics the nursery credit prices (#3854, RimGovernor.Brain.GameFacts.Observations.BabyFitsRead): the baby fit defs, and per baby the fits that can
/// start for it, whether it lies in a nursery and who hears it, with the doors as they are and with its nursery's doors open.
/// Read-only: no mental state, memory or door changes.
/// </summary>
internal static class NativeBabyFits
{
    /// <summary>GenClamor.DoClamor's maxRegions literal (decompiled 1.7; vanilla exposes no symbol for it).</summary>
    private const int ClamorMaxRegions = 15;

    internal static RimGovernor.Brain.GameFacts.Observations.BabyFitsRead Read(Map map, List<Pawn> people, List<NeedsForecastPawn> rows)
    {
        var fits = DefDatabase<MentalFitDef>.AllDefsListForReading.Where(f => f.developmentalStageFilter.Has(DevelopmentalStage.Baby)).ToArray();
        var colonists = new HashSet<Pawn>(people);
        var babies = people.Where(p => fits.Any(f => f.developmentalStageFilter.Has(p.DevelopmentalStage)))
            .OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal)
            .Select(p => Baby(p, map, fits, colonists, rows)).ToArray();
        return new()
        {
            Play = NativeBabyPlay.Mechanics(),
            SleepDisturbanceIntervalTicks = BridgeCommon.PrivateStaticField(typeof(Pawn), "SleepDisturbanceMinInterval")?.GetRawConstantValue() is int interval && interval > 0
                ? interval : throw new RequiredFactException("Pawn.SleepDisturbanceMinInterval"),
            CheckIntervalTicks = Const("CheckInterval"), RecoveryAwakeTicks = Const("MinTicksSinceRecoveryToHaveFit"),
            Fits = EquatableArray.From(fits.Select(f => new RimGovernor.Brain.GameFacts.Observations.BabyFitRead
            {
                Def = f.defName, DurationTicks = f.mentalState.maxTicksBeforeRecovery, Heard = Thoughts(f) != null,
                MtbDaysByMood = EquatableArray.From((f.mtbDaysMoodCurve ?? throw new InvalidOperationException("mental fit without a mood curve: " + f.defName))
                    .Points.Select(p => new RimGovernor.Brain.GameFacts.Observations.CurvePointRead(p.x, p.y))),
            })),
            Babies = EquatableArray.From(babies),
        };
    }

    private static RimGovernor.Brain.GameFacts.Observations.BabyRead Baby(Pawn baby, Map map, MentalFitDef[] fits, HashSet<Pawn> colonists, List<NeedsForecastPawn> rows)
    {
        // A fit starts only for a humanlike pawn with a mood (MentalFitGenerator.CanDoRandomMentalFits, MentalFitDef.CalculateMTBDays)
        // whose stage the def admits and whose state its worker lets occur (MentalStateHandler.TryStartMentalState).
        var can = baby.needs?.mood == null || !baby.mindState.mentalFitGenerator.CanDoRandomMentalFits ? []
            : fits.Where(f => f.developmentalStageFilter.Has(baby.DevelopmentalStage) && f.mentalState.Worker.StateCanOccur(baby)).ToArray();
        var room = baby.GetRoom();
        var nursery = room != null && room.Role?.Worker is RoomRoleWorker_Nursery;
        var alternate = NativeNeedsForecast.SleepWithoutNursery(baby, map, nursery ? room : null);
        var alternateBed = alternate.Bed.Length == 0 ? null : map.listerThings.AllThings.OfType<Building_Bed>()
            .FirstOrDefault(b => b.GetUniqueLoadID() == alternate.Bed) ?? throw new RequiredFactException("baby alternate crib:" + baby.GetUniqueLoadID());
        double CribMood(Building_Bed? bed, QualityCategory? quality = null)
        {
            if (quality == null && (bed == null || !bed.def.building.bed_crib)) return 0;
            if (quality == null)
            {
                bed!.TryGetQuality(out var readQuality);
                quality = readQuality;
            }
            // No DefOf field names CribQuality; its worker is the typed route.
            var defs = DefDatabase<ThoughtDef>.AllDefsListForReading.Where(d => d.workerClass == typeof(ThoughtWorker_CribQuality)).ToArray();
            if (defs.Length != 1 || defs[0].stages == null || (int)quality.Value >= defs[0].stages.Count)
                throw new RequiredFactException("baby crib quality thought stages:" + baby.GetUniqueLoadID());
            var def = defs[0];
            if (!ThoughtUtility.CanGetThought(baby, def) || def.stages[(int)quality.Value] == null) return 0;
            var thought = ThoughtMaker.MakeThought(def, (int)quality.Value);
            thought.pawn = baby;
            var offset = thought.MoodOffset();
            if (!double.IsFinite(offset)) throw new RequiredFactException("baby crib quality mood:" + baby.GetUniqueLoadID());
            return offset;
        }
        var doors = nursery && room != null ? new HashSet<Building_Door>(room.ContainedAndAdjacentThings.OfType<Building_Door>()) : new HashSet<Building_Door>();
        var regions = ReachRegions(baby, map, []);
        var openRegions = nursery ? ReachRegions(baby, map, doors) : regions;
        var now = Reach(baby, regions);
        var open = nursery ? Reach(baby, openRegions) : now;
        var sleepers = new List<BabySleepHearerRead>();
        // Pawn.HearClamor / CheckForDisturbedSleep: sleep is projected, every other admission predicate is observed.
        foreach (var pawn in colonists.Where(p => p != baby && !p.Dead && !p.Downed && !p.Deathresting && !p.IsSelfShutdown()
            && p.Faction == Faction.OfPlayer && !p.IsWildMan() && p.needs?.rest != null && p.needs.mood != null
            && !LovePartnerRelationUtility.LovePartnerRelationExists(p, baby) && !(baby.RaceProps.petness > 0f)
            && (baby.relations == null || !baby.relations.DirectRelations.Any(r => r.def == PawnRelationDefOf.Bond))
            && ThoughtUtility.CanGetThought(p, ThoughtDefOf.SleepDisturbed)).OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal))
        {
            var row = rows.SingleOrDefault(r => r.Pawn == pawn.GetUniqueLoadID()) ?? throw new RequiredFactException("sleep hearer needs:" + pawn.GetUniqueLoadID());
            var surface = row.Sleep ?? throw new RequiredFactException("sleep hearer surface:" + row.Pawn);
            var targets = row.Targets.Where(t => t.Kind == "rest").Select(t => (Target: t.Id, Cell: new IntVec3(t.Location.X, 0, t.Location.Z))).ToList();
            if (targets.All(t => t.Target != surface.Bed))
            {
                Building_Bed? bed = surface.Ground ? null : (map.listerThings.AllThings.OfType<Building_Bed>().SingleOrDefault(b => b.GetUniqueLoadID() == surface.Bed)
                    ?? throw new RequiredFactException("sleep hearer bed:" + surface.Bed));
                targets.Add((surface.Bed, bed is null ? pawn.Position : NativeNeedsForecast.SleepingCell(pawn, bed)));
            }
            foreach (var fit in fits.Where(f => typeof(MentalState_BabyCry).IsAssignableFrom(f.mentalState.stateClass)))
            {
                var memory = NativeNeedsForecast.Memory(pawn, ThoughtDefOf.SleepDisturbed);
                if (memory.Life is not MemoryLife.Expires expires) throw new RequiredFactException("SleepDisturbed duration:" + row.Pawn);
                foreach (var target in targets)
                    sleepers.Add(new(row.Pawn, target.Target, Reaches(pawn, target.Cell, baby.Position, map, regions),
                        Reaches(pawn, target.Cell, baby.Position, map, openRegions),
                        new(fit.defName, ThoughtDefOf.SleepDisturbed.defName, memory.Offset, expires.RemainingTicks,
                            ThoughtDefOf.SleepDisturbed.stackLimit, ThoughtDefOf.SleepDisturbed.stackedEffectMultiplier)));
            }
        }
        var hearers = now.Concat(open).Distinct().Where(p => p != baby && colonists.Contains(p) && p.needs?.mood != null)
            .OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal)
            .Select(p => new RimGovernor.Brain.GameFacts.Observations.BabyHearerRead
            {
                Pawn = p.GetUniqueLoadID(), Now = now.Contains(p), NurseryOpen = open.Contains(p),
                Memories = EquatableArray.From(fits.Select(f => Memory(f, baby, p)).OfType<RimGovernor.Brain.GameFacts.Observations.HeardMemoryRead>()),
            })
            .Where(h => h.Memories.Count != 0).ToArray();
        return new()
        {
            Pawn = baby.GetUniqueLoadID(), Fits = EquatableArray.From(can.Select(f => f.defName)), InNursery = nursery,
            CribMood = baby.needs?.mood == null ? null : new(CribMood(baby.CurrentBed()),
                CribMood(alternateBed),
                CribMood(null, QualityCategory.Normal)),
            Play = NativeBabyPlay.Read(baby, map, nursery ? room : null),
            SleepWithoutNursery = alternate,
            Hearers = EquatableArray.From(hearers),
            SleepHearers = EquatableArray.From(sleepers),
        };
    }

    // GenClamor.DoClamor from the baby's Position: a breadth-first region walk entering a door region only while its door is open
    // (or, here, is one of openDoors), at most ClamorMaxRegions regions, hearing every pawn within BabyScreamRadius times its
    // Hearing capacity (clamped to 0..1; none at 0).
    private static HashSet<Pawn> Reach(Pawn baby, HashSet<Region> regions)
    {
        var heard = new HashSet<Pawn>();
        foreach (var r in regions)
        {
            foreach (var thing in r.ListerThings.ThingsInGroup(ThingRequestGroup.Pawn))
            {
                if (thing is not Pawn pawn) continue;
                if (InRange(pawn, pawn.Position, baby.Position)) heard.Add(pawn);
            }
        }
        return heard;
    }

    private static HashSet<Region> ReachRegions(Pawn baby, Map map, HashSet<Building_Door> openDoors)
    {
        var heard = new HashSet<Region>();
        var region = baby.Position.GetRegion(map) ?? throw new RequiredFactException("baby clamor region:" + baby.GetUniqueLoadID());
        RegionTraverser.BreadthFirstTraverse(region, (_, r) => r.door == null || r.door.Open || openDoors.Contains(r.door),
            r => { heard.Add(r); return false; }, ClamorMaxRegions);
        return heard;
    }

    private static bool Reaches(Pawn pawn, IntVec3 cell, IntVec3 source, Map map, HashSet<Region> regions) =>
        regions.Contains(cell.GetRegion(map) ?? throw new RequiredFactException("sleep clamor region:" + pawn.GetUniqueLoadID())) && InRange(pawn, cell, source);

    private static bool InRange(Pawn pawn, IntVec3 cell, IntVec3 source)
    {
        var hearing = Mathf.Clamp01(pawn.health.capacities.GetLevel(PawnCapacityDefOf.Hearing));
        return hearing > 0f && cell.InHorDistOf(source, MentalState_BabyFit.BabyScreamRadius * hearing);
    }

    // The memory a fit's AuraEffect gives the hearer: the parent's def for the baby's mother or father, else anyone's. None when the
    // fit's state is not one the read knows, or the hearer cannot get the thought (MemoryThoughtHandler.TryGainMemory's first check).
    private static RimGovernor.Brain.GameFacts.Observations.HeardMemoryRead? Memory(MentalFitDef fit, Pawn baby, Pawn hearer)
    {
        if (Thoughts(fit) is not var (anyone, parent)) return null;
        var def = hearer == baby.GetMother() || hearer == baby.GetFather() ? parent : anyone;
        if (!ThoughtUtility.CanGetThought(hearer, def)) return null;
        var memory = NativeNeedsForecast.Memory(hearer, def);
        return new(fit.defName, def.defName, memory.Offset, ((RimGovernor.Brain.GameFacts.Observations.MemoryLife.Expires)memory.Life).RemainingTicks, def.stackLimit, def.stackedEffectMultiplier);
    }

    // MentalState_BabyCry.AuraEffect and MentalState_BabyGiggle.AuraEffect: (anyone's, the parent's) memory.
    private static (ThoughtDef Anyone, ThoughtDef Parent)? Thoughts(MentalFitDef fit) =>
        typeof(MentalState_BabyCry).IsAssignableFrom(fit.mentalState.stateClass) ? (ThoughtDefOf.CryingBaby, ThoughtDefOf.MyCryingBaby)
        : typeof(MentalState_BabyGiggle).IsAssignableFrom(fit.mentalState.stateClass) ? (ThoughtDefOf.GigglingBaby, ThoughtDefOf.MyGigglingBaby)
        : null;

    /// <summary>A private const of MentalFitGenerator. No member exposes it; a version that renames it is a broken read.</summary>
    private static double Const(string name) =>
        BridgeCommon.PrivateStaticField(typeof(MentalFitGenerator), name)?.GetRawConstantValue() is int n && n > 0 ? n
            : throw new InvalidOperationException("MentalFitGenerator." + name + " is not readable.");
}
