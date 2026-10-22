#nullable enable
using System;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using RimGovernor.Runtime;
using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Domain.Fact;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools;

internal static class NativeSkillLearning
{
    // Pawn_SkillTracker exposes no typed accessor for its serialized reset timestamp. This private field is
    // required to distinguish an already-reset midnight hour from one awaiting its first tracker callback.
    private static readonly AccessTools.FieldRef<Pawn_SkillTracker, int> LastReset =
        AccessTools.FieldRefAccess<Pawn_SkillTracker, int>("lastXpSinceMidnightResetTimestamp");

    internal static int Cadence(Pawn pawn) => Math.Max(1, Math.Min(15, pawn.UpdateRateTicks));
    internal static int Phase(Pawn pawn) => (Cadence(pawn) - pawn.HashOffsetTicks() % Cadence(pawn)) % Cadence(pawn);

    internal static Obs.SkillLearning Read(Pawn pawn, SkillRecord skill)
    {
        if (pawn.story?.traits == null) return new();
        var allowed = !(ModsConfig.AnomalyActive && pawn.IsMutant && !pawn.mutant.Def.canGainXP);
        var saturatedFactor = DebugSettings.fastLearning ? 1d : SkillRecord.SaturatedLearningFactor;
        var levels = Enumerable.Range(SkillRecord.MinLevel, SkillRecord.MaxLevel + 1).Select(level =>
        {
            // Interval only applies negative Learn. Probe a detached record, never the pawn's skill.
            // This reads native decay (including traits), rather than copying its level/trait table.
            var probe = new SkillRecord(pawn, skill.def) { levelInt = level, xpSinceLastLevel = 0 };
            if (allowed) probe.Interval();
            return new Obs.SkillLevelMechanics(level, SkillRecord.XpRequiredToLevelUpFrom(level), -probe.xpSinceLastLevel);
        });
        var interval = SkillRecord.IntervalTicks;
        var next = interval - ((pawn.HashOffsetTicks() % interval + interval) % interval);
        var cadence = Cadence(pawn);
        var phase = Phase(pawn);
        // A hash boundary since the last tracker callback is still pending, even if it is in the past.
        var elapsed = (cadence - phase) % cadence;
        if (next - interval > -elapsed) next -= interval;
        var reset = LastReset(pawn.skills);
        var now = Find.TickManager.TicksGame;
        var midnightReset = 0;
        for (var hashTick = next; hashTick <= GenDate.TicksPerDay + interval; hashTick += interval)
        {
            var at = hashTick + (phase - hashTick % cadence + cadence) % cadence;
            if (GenDate.HourInteger(GenTicks.TicksAbs + at, (pawn.Tile.Valid ? Find.WorldGrid.LongLatOf(pawn.Tile).x : 0f)) == 0
                && (reset < 0 || now + at - reset >= GenDate.TicksPerDay / 2))
            { midnightReset = at; break; }
        }
        var localDay = GenLocalDate.DayPercent(pawn);
        // No extrapolation through either the tracker local midnight or range's global daily reset.
        var localRemaining = GenLocalDate.HourInteger(pawn) == 0 ? 0 : Math.Max(0, (int)((1d - localDay) * GenDate.TicksPerDay) - 1);
        var globalRemaining = GenDate.TicksPerDay - Find.TickManager.TicksGame % GenDate.TicksPerDay;
        var practice = skill.def == SkillDefOf.Shooting || skill.def == SkillDefOf.Melee;
        return new()
        {
            Disabled = Known(skill.TotallyDisabled), TrackerAllowsLearning = Known(allowed),
            StoredLevel = Known(skill.levelInt), Aptitude = Known(skill.Aptitude), Xp = Known((double)skill.xpSinceLastLevel),
            XpSinceMidnight = Known((double)skill.xpSinceMidnight), DirectFactor = Known((double)skill.LearnRateFactor(true)),
            UnsaturatedFactor = Known(skill.LearnRateFactor() / (skill.LearningSaturatedToday ? saturatedFactor : 1)),
            SaturationThreshold = Known((double)SkillRecord.MaxFullRateXpPerDay), SaturatedFactor = Known(saturatedFactor),
            LossThreshold = Known((double)SkillRecord.MinXPAmount), DecayInterval = Known(interval), NextDecay = Known(next),
            ValidForTicks = Known(Math.Min(localRemaining, globalRemaining)), NextMidnightReset = midnightReset > 0 ? Known(midnightReset) : default,
            TrackerIntervalTicks = cadence, TrackerPhase = phase, Levels = EquatableArray.From(levels),
            PracticeEligible = practice ? Known(skill.def == SkillDefOf.Shooting ? RangeTraining.Eligible(pawn, skill.def) : SparringBouts.Eligible(pawn)) : default,
            PracticeCeiling = practice ? Known(RangeTraining.CeilingFor(skill.def)) : default,
            RangeAppliedBudget = skill.def == SkillDefOf.Shooting ? Known((double)RangeTraining.Remaining(pawn)) : default,
        };
    }
}
