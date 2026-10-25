using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using RimGovernor.Brain.Domain;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools;

/// <summary>
/// The baby fit mechanics the nursery credit prices (#3854, Obs.BabyFitsRead): the baby fit defs, and per baby the fits that can
/// start for it, whether it lies in a nursery and who hears it, with the doors as they are and with its nursery's doors open.
/// Read-only: no mental state, memory or door changes.
/// </summary>
internal static class NativeBabyFits
{
    /// <summary>GenClamor.DoClamor's maxRegions literal (decompiled 1.7; vanilla exposes no symbol for it).</summary>
    private const int ClamorMaxRegions = 15;

    internal static Obs.BabyFitsRead Read(Map map, List<Pawn> people)
    {
        var fits = DefDatabase<MentalFitDef>.AllDefsListForReading.Where(f => f.developmentalStageFilter.Has(DevelopmentalStage.Baby)).ToArray();
        var colonists = new HashSet<Pawn>(people);
        var babies = people.Where(p => fits.Any(f => f.developmentalStageFilter.Has(p.DevelopmentalStage)))
            .OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal)
            .Select(p => Baby(p, map, fits, colonists)).ToArray();
        return new()
        {
            CheckIntervalTicks = Const("CheckInterval"), RecoveryAwakeTicks = Const("MinTicksSinceRecoveryToHaveFit"),
            Fits = EquatableArray.From(fits.Select(f => new Obs.BabyFitRead
            {
                Def = f.defName, DurationTicks = f.mentalState.maxTicksBeforeRecovery, Heard = Thoughts(f) != null,
                MtbDaysByMood = EquatableArray.From((f.mtbDaysMoodCurve ?? throw new InvalidOperationException("mental fit without a mood curve: " + f.defName))
                    .Points.Select(p => new Obs.CurvePointRead(p.x, p.y))),
            })),
            Babies = EquatableArray.From(babies),
        };
    }

    private static Obs.BabyRead Baby(Pawn baby, Map map, MentalFitDef[] fits, HashSet<Pawn> colonists)
    {
        // A fit starts only for a humanlike pawn with a mood (MentalFitGenerator.CanDoRandomMentalFits, MentalFitDef.CalculateMTBDays)
        // whose stage the def admits and whose state its worker lets occur (MentalStateHandler.TryStartMentalState).
        var can = baby.needs?.mood == null || !baby.mindState.mentalFitGenerator.CanDoRandomMentalFits ? []
            : fits.Where(f => f.developmentalStageFilter.Has(baby.DevelopmentalStage) && f.mentalState.Worker.StateCanOccur(baby)).ToArray();
        var room = baby.GetRoom();
        var nursery = room != null && room.Role?.Worker is RoomRoleWorker_Nursery;
        var doors = nursery && room != null ? new HashSet<Building_Door>(room.ContainedAndAdjacentThings.OfType<Building_Door>()) : new HashSet<Building_Door>();
        var now = Reach(baby, map, []);
        var open = nursery ? Reach(baby, map, doors) : now;
        var hearers = now.Concat(open).Distinct().Where(p => p != baby && colonists.Contains(p) && p.needs?.mood != null)
            .OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal)
            .Select(p => new Obs.BabyHearerRead
            {
                Pawn = p.GetUniqueLoadID(), Now = now.Contains(p), NurseryOpen = open.Contains(p),
                Memories = EquatableArray.From(fits.Select(f => Memory(f, baby, p)).OfType<Obs.HeardMemoryRead>()),
            })
            .Where(h => h.Memories.Count != 0).ToArray();
        return new()
        {
            Pawn = baby.GetUniqueLoadID(), Fits = EquatableArray.From(can.Select(f => f.defName)), InNursery = nursery,
            Hearers = EquatableArray.From(hearers),
        };
    }

    // GenClamor.DoClamor from the baby's Position: a breadth-first region walk entering a door region only while its door is open
    // (or, here, is one of openDoors), at most ClamorMaxRegions regions, hearing every pawn within BabyScreamRadius times its
    // Hearing capacity (clamped to 0..1; none at 0).
    private static HashSet<Pawn> Reach(Pawn baby, Map map, HashSet<Building_Door> openDoors)
    {
        var heard = new HashSet<Pawn>();
        var position = baby.Position;
        var region = position.GetRegion(map);
        if (region == null) return heard;
        RegionTraverser.BreadthFirstTraverse(region, (_, r) => r.door == null || r.door.Open || openDoors.Contains(r.door), r =>
        {
            foreach (var thing in r.ListerThings.ThingsInGroup(ThingRequestGroup.Pawn))
            {
                if (thing is not Pawn pawn) continue;
                var hearing = Mathf.Clamp01(pawn.health.capacities.GetLevel(PawnCapacityDefOf.Hearing));
                if (hearing > 0f && pawn.Position.InHorDistOf(position, MentalState_BabyFit.BabyScreamRadius * hearing)) heard.Add(pawn);
            }
            return false;
        }, ClamorMaxRegions);
        return heard;
    }

    // The memory a fit's AuraEffect gives the hearer: the parent's def for the baby's mother or father, else anyone's. None when the
    // fit's state is not one the read knows, or the hearer cannot get the thought (MemoryThoughtHandler.TryGainMemory's first check).
    private static Obs.HeardMemoryRead? Memory(MentalFitDef fit, Pawn baby, Pawn hearer)
    {
        if (Thoughts(fit) is not var (anyone, parent)) return null;
        var def = hearer == baby.GetMother() || hearer == baby.GetFather() ? parent : anyone;
        if (!ThoughtUtility.CanGetThought(hearer, def)) return null;
        var memory = NativeNeedsForecast.Memory(hearer, def);
        return new(fit.defName, def.defName, memory.Offset, ((Obs.MemoryLife.Expires)memory.Life).RemainingTicks, def.stackLimit, def.stackedEffectMultiplier);
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
