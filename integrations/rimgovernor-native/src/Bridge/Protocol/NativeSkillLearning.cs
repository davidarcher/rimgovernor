#nullable enable
using System;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using RimGovernor.Runtime;
using RimGovernor.Brain.Domain;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools;

internal static class NativeSkillLearning
{
    // Pawn_SkillTracker exposes no typed accessor for its serialized reset timestamp. This private field is
    // required to distinguish an already-reset midnight hour from one awaiting its first tracker callback.
    private static readonly AccessTools.FieldRef<Pawn_SkillTracker, int> LastReset =
        AccessTools.FieldRefAccess<Pawn_SkillTracker, int>("lastXpSinceMidnightResetTimestamp");

    // Thing exposes no accessor for the retained interval counter; camera-rate changes can leave it off phase.
    private static readonly AccessTools.FieldRef<Thing, int> TickDelta = AccessTools.FieldRefAccess<Thing, int>("tickDelta");
    internal static int Elapsed(Pawn pawn) => TickDelta(pawn);

    internal static int Cadence(Pawn pawn) => Math.Max(1, Math.Min(15, pawn.UpdateRateTicks));
    internal static int Phase(Pawn pawn) => (Cadence(pawn) - pawn.HashOffsetTicks() % Cadence(pawn)) % Cadence(pawn);

    internal static Obs.SkillLearning Read(Pawn pawn, SkillRecord skill)
    {
        if (pawn.story?.traits == null) throw new InvalidOperationException("skill learning " + pawn.GetUniqueLoadID() + ": the pawn has no traits");
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
        var elapsed = Elapsed(pawn);
        var clock = new TickIntervalClock(cadence, phase, elapsed);
        if (next - interval > -elapsed) next -= interval;
        var reset = LastReset(pawn.skills);
        var now = Find.TickManager.TicksGame;
        int? midnightReset = null;
        for (var hashTick = next; hashTick <= GenDate.TicksPerDay + interval; hashTick += interval)
        {
            var at = clock.AtOrAfter(hashTick);
            if (GenDate.HourInteger(GenTicks.TicksAbs + at, (pawn.Tile.Valid ? Find.WorldGrid.LongLatOf(pawn.Tile).x : 0f)) == 0
                && (reset < 0 || now + at - reset >= GenDate.TicksPerDay / 2))
            { midnightReset = at; break; }
        }
        // The tracker resets once each local day, so a reset always falls within a day and a decay interval.
        if (midnightReset is not { } nextReset)
            throw new InvalidOperationException("skill learning " + pawn.GetUniqueLoadID() + ": no midnight reset within a day");
        var localDay = GenLocalDate.DayPercent(pawn);
        // No extrapolation through either the tracker local midnight or range's global daily reset.
        var localRemaining = GenLocalDate.HourInteger(pawn) == 0 ? 0 : Math.Max(0, (int)((1d - localDay) * GenDate.TicksPerDay) - 1);
        var globalRemaining = GenDate.TicksPerDay - Find.TickManager.TicksGame % GenDate.TicksPerDay;
        return new()
        {
            Disabled = skill.TotallyDisabled, TrackerAllowsLearning = allowed,
            StoredLevel = skill.levelInt, Aptitude = skill.Aptitude, Xp = skill.xpSinceLastLevel,
            XpSinceMidnight = skill.xpSinceMidnight, DirectFactor = skill.LearnRateFactor(true),
            UnsaturatedFactor = skill.LearnRateFactor() / (skill.LearningSaturatedToday ? saturatedFactor : 1),
            SaturationThreshold = SkillRecord.MaxFullRateXpPerDay, SaturatedFactor = saturatedFactor,
            LossThreshold = SkillRecord.MinXPAmount, DecayInterval = interval, NextDecay = next,
            ValidForTicks = Math.Min(localRemaining, globalRemaining), NextMidnightReset = nextReset,
            TrackerIntervalTicks = cadence, TrackerPhase = phase, TrackerElapsedTicks = elapsed, Levels = EquatableArray.From(levels),
            Practice = skill.def == SkillDefOf.Shooting
                ? new Obs.SkillPractice.Range(RangeTraining.CanTrain(pawn, skill.def), RangeTraining.Eligible(pawn, skill.def),
                    RangeTraining.CeilingFor(skill.def), RangeTraining.Remaining(pawn), RangeTraining.DailyXpBudget)
                : skill.def == SkillDefOf.Melee
                    ? new Obs.SkillPractice.Sparring(RangeTraining.CanTrain(pawn, skill.def), SparringBouts.Eligible(pawn), RangeTraining.CeilingFor(skill.def))
                    : new Obs.SkillPractice.None(),
        };
    }
}
