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

namespace RimGovernor.Native;

/// <summary>
/// The baby fit mechanics the nursery credit prices (#3854, RimGovernor.Brain.GameFacts.Observations.BabyFitsRead): the baby fit defs, and per baby the fits that can
/// start for it, its canonical resting nursery, recipient admission and the single map topology.
/// Read-only: no mental state, memory or door changes.
/// </summary>
internal static class NativeBabyFits
{

    internal static RimGovernor.Brain.GameFacts.Observations.BabyFitsRead Read(Map map, List<Pawn> people, IReadOnlyList<NeedsForecastPawn> rows, PrisonRecruitRead prison)
    {
        var fits = DefDatabase<MentalFitDef>.AllDefsListForReading.Where(f => f.developmentalStageFilter.Has(DevelopmentalStage.Baby)).ToArray();
        var recipients = new HashSet<Pawn>(map.mapPawns.AllPawnsSpawned.Where(p => p.needs?.mood != null));
        var babies = people.Where(p => fits.Any(f => f.developmentalStageFilter.Has(p.DevelopmentalStage)))
            .OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal)
            .Select(p => Baby(p, map, fits, recipients, rows.Single(r => r.Pawn == p.GetUniqueLoadID()))).ToArray();
        return new()
        {
            Geometry = babies.Length == 0 ? null : NativeNurseryGeometry.Read(map),
            OtherRecipients = babies.Length == 0 ? [] : EquatableArray.From(recipients.Where(p => !people.Contains(p)).OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).Select(p => Recipient(p, map, prison))),
            Social = NativeBabySocial.Read(map, recipients.Where(p => p.RaceProps.Humanlike).ToList(), people.Where(p => fits.Any(f => f.developmentalStageFilter.Has(p.DevelopmentalStage))).ToArray()),
            Play = NativeBabyPlay.Mechanics(),
            SleepDisturbanceIntervalTicks = BridgeCommon.PrivateStaticField(typeof(Pawn), "SleepDisturbanceMinInterval")?.GetRawConstantValue() is int interval && interval > 0
                ? interval : throw new RequiredFactException("Pawn.SleepDisturbanceMinInterval"),
            CheckIntervalTicks = Const("CheckInterval"), RecoveryAwakeTicks = Const("MinTicksSinceRecoveryToHaveFit"),
            ClamorIntervalTicks = MentalState_BabyFit.ScreamInterval + 1,
            Fits = EquatableArray.From(fits.Select(f => new RimGovernor.Brain.GameFacts.Observations.BabyFitRead
            {
                Def = f.defName, DurationTicks = f.mentalState.maxTicksBeforeRecovery, Heard = Thoughts(f) != null,
                OpinionThought = typeof(MentalState_BabyCry).IsAssignableFrom(f.mentalState.stateClass) ? ThoughtDefOf.BabyCriedSocial.defName
                    : typeof(MentalState_BabyGiggle).IsAssignableFrom(f.mentalState.stateClass) ? ThoughtDefOf.BabyGiggledSocial.defName : "",
                MtbDaysByMood = EquatableArray.From((f.mtbDaysMoodCurve ?? throw new InvalidOperationException("mental fit without a mood curve: " + f.defName))
                    .Points.Select(p => new RimGovernor.Brain.GameFacts.Observations.CurvePointRead(p.x, p.y))),
            })),
            Babies = EquatableArray.From(babies),
        };
    }

    private static RimGovernor.Brain.GameFacts.Observations.BabyRead Baby(Pawn baby, Map map, MentalFitDef[] fits, HashSet<Pawn> recipients, NeedsForecastPawn row)
    {
        // A fit starts only for a humanlike pawn with a mood (MentalFitGenerator.CanDoRandomMentalFits, MentalFitDef.CalculateMTBDays)
        // whose stage the def admits and whose state its worker lets occur (MentalStateHandler.TryStartMentalState).
        var can = baby.needs?.mood == null || !baby.mindState.mentalFitGenerator.CanDoRandomMentalFits ? []
            : fits.Where(f => f.developmentalStageFilter.Has(baby.DevelopmentalStage) && f.mentalState.Worker.StateCanOccur(baby)).ToArray();
        var surface = row.Sleep ?? throw new RequiredFactException("baby resting surface:" + row.Pawn);
        var room = surface.Position is ServicePositionRead.Located { Cell: var location } ? new IntVec3(location.X, 0, location.Z).GetRoom(map) : null;
        var surfaceBed = surface.Bed.Length == 0 ? null : map.listerThings.AllThings.OfType<Building_Bed>()
            .FirstOrDefault(b => b.GetUniqueLoadID() == surface.Bed) ?? throw new RequiredFactException("baby resting crib:" + row.Pawn);
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
        var sleepers = new List<BabySleepHearerRead>();
        // Pawn.HearClamor / CheckForDisturbedSleep: sleep is projected, every other admission predicate is observed.
        foreach (var pawn in recipients.Where(p => p != baby && !p.Dead && !p.Downed && !p.Deathresting && !p.IsSelfShutdown()
            && p.Faction == Faction.OfPlayer && !p.IsWildMan() && p.needs?.rest != null && p.needs.mood != null
            && !LovePartnerRelationUtility.LovePartnerRelationExists(p, baby) && !(baby.RaceProps.petness > 0f)
            && (baby.relations == null || !baby.relations.DirectRelations.Any(r => r.def == PawnRelationDefOf.Bond))
            && ThoughtUtility.CanGetThought(p, ThoughtDefOf.SleepDisturbed)).OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal))
        {
            foreach (var fit in fits.Where(f => typeof(MentalState_BabyCry).IsAssignableFrom(f.mentalState.stateClass)))
            {
                var memory = NativeNeedsForecast.Memory(pawn, ThoughtDefOf.SleepDisturbed);
                if (memory.Life is not MemoryLife.Expires expires) throw new RequiredFactException("SleepDisturbed duration:" + pawn.GetUniqueLoadID());
                sleepers.Add(new(pawn.GetUniqueLoadID(), new(fit.defName, ThoughtDefOf.SleepDisturbed.defName, memory.Offset, expires.RemainingTicks,
                    ThoughtDefOf.SleepDisturbed.stackLimit, ThoughtDefOf.SleepDisturbed.stackedEffectMultiplier)));
            }
        }
        var hearers = recipients.Where(p => p != baby && p.needs?.mood != null)
            .OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal)
            .Select(p => new RimGovernor.Brain.GameFacts.Observations.BabyHearerRead
            {
                Pawn = p.GetUniqueLoadID(), Hearing = Mathf.Clamp01(p.health.capacities.GetLevel(PawnCapacityDefOf.Hearing)),
                Memories = EquatableArray.From(fits.Select(f => Memory(f, baby, p)).OfType<RimGovernor.Brain.GameFacts.Observations.HeardMemoryRead>()),
            })
            .ToArray();
        var play = NativeBabyPlay.Read(baby, map, nursery ? room : null);
        return new()
        {
            NurseryRoom = nursery ? room!.ID : -1,
            Care = NativeBabyCare.Read(baby, map, play),
            Pawn = baby.GetUniqueLoadID(), Fits = EquatableArray.From(can.Select(f => f.defName)), InNursery = nursery,
            CribMood = baby.needs?.mood == null ? null : new(CribMood(surfaceBed),
                CribMood(alternateBed),
                CribMood(null, QualityCategory.Normal)),
            Play = play,
            SleepWithoutNursery = alternate,
            Hearers = EquatableArray.From(hearers),
            SleepHearers = EquatableArray.From(sleepers),
        };
    }

    private static NurseryRecipientRead Recipient(Pawn pawn, Map map, PrisonRecruitRead prison)
    {
        var id = pawn.GetUniqueLoadID();
        var mood = pawn.needs.mood ?? throw new RequiredFactException("nursery recipient mood:" + id);
        var breaker = pawn.mindState?.mentalBreaker ?? throw new RequiredFactException("nursery recipient breaker:" + id);
        var prisoner = prison.Prisoners.SingleOrDefault(p => p.Pawn == id);
        var captured = prisoner is not null ? prisoner.Mood : NativePrisonRecruit.MoodOf(pawn, []);
        if (captured is null) throw new RequiredFactException("nursery recipient mood capture:" + id);
        // Prisoner movement stays in its native prison room; freely moving cohorts have the full map enclosure.
        // No current standing cell is assumed to remain its future destination.
        var room = pawn.GetRoom();
        ServicePositionRead awake = pawn.IsPrisonerOfColony && !pawn.InMentalState && room is { IsPrisonCell: true }
            ? new ServicePositionRead.Possible(EquatableArray.From(room.Cells.Select(c => new Cell(c.x, c.z))))
            : new ServicePositionRead.PassableMap();
        return new(id, pawn.IsPrisonerOfColony ? NurseryRecipientKind.Prisoner : pawn.IsSlaveOfColony ? NurseryRecipientKind.Slave
            : pawn.HostFaction == Faction.OfPlayer ? NurseryRecipientKind.Guest : NurseryRecipientKind.Other,
            captured, mood.CurLevelPercentage, (double)mood.def.seekerRisePerHour / GenDate.TicksPerHour,
            (double)mood.def.seekerFallPerHour / GenDate.TicksPerHour, breaker.BreakThresholdMinor,
            mood.def.freezeWhileSleeping, pawn.Awake(), NativeNeedsForecast.SleepOf(pawn, map), awake) { ObservedSleep = NativeNeedsForecast.LyingInOf(pawn, map)?.Place };
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
