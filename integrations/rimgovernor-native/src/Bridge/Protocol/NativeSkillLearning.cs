#nullable enable
using System;
using System.Linq;
using RimWorld;
using Verse;
using RimGovernor.Runtime;
using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Domain.Fact;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools;

internal static class NativeSkillLearning
{
    internal static Obs.SkillLearning Read(Pawn pawn, SkillRecord skill)
    {
        if (pawn.story?.traits == null) return new() { Tick = Find.TickManager.TicksGame, Provenance = "unsupported_skill_tracker_without_traits" };
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
        var localDay = GenLocalDate.DayPercent(pawn);
        // No extrapolation through either the tracker local midnight or range's global daily reset.
        var localRemaining = GenLocalDate.HourInteger(pawn) == 0 ? 0 : Math.Max(0, (int)((1d - localDay) * GenDate.TicksPerDay) - 1);
        var globalRemaining = GenDate.TicksPerDay - Find.TickManager.TicksGame % GenDate.TicksPerDay;
        var practice = skill.def == SkillDefOf.Shooting || skill.def == SkillDefOf.Melee;
        var verb = pawn.equipment?.PrimaryEq?.PrimaryVerb;
        var sparring = skill.def == SkillDefOf.Melee && pawn.CurJob?.def.defName == "RimGovernor_Spar" && verb is Verb_MeleeAttack;
        return new()
        {
            Tick = Find.TickManager.TicksGame, Provenance = "SkillRecord/Interval/LearnRateFactor; native training rules",
            Disabled = Known(skill.TotallyDisabled), TrackerAllowsLearning = Known(allowed),
            StoredLevel = Known(skill.levelInt), Aptitude = Known(skill.Aptitude), Xp = Known((double)skill.xpSinceLastLevel),
            XpSinceMidnight = Known((double)skill.xpSinceMidnight), DirectFactor = Known((double)skill.LearnRateFactor(true)),
            UnsaturatedFactor = Known(skill.LearnRateFactor() / (skill.LearningSaturatedToday ? saturatedFactor : 1)),
            SaturationThreshold = Known((double)SkillRecord.MaxFullRateXpPerDay), SaturatedFactor = Known(saturatedFactor),
            LossThreshold = Known((double)SkillRecord.MinXPAmount), DecayInterval = Known(interval), NextDecay = Known(next),
            ValidForTicks = Known(Math.Min(localRemaining, globalRemaining)), Levels = EquatableArray.From(levels),
            PracticeEligible = practice ? Known(skill.def == SkillDefOf.Shooting ? RangeTraining.Eligible(pawn, skill.def) : SparringBouts.Eligible(pawn)) : default,
            PracticeCeiling = practice ? Known(RangeTraining.CeilingFor(skill.def)) : default,
            RangeRawXpPerShot = skill.def == SkillDefOf.Shooting ? Known((double)RangeTraining.ShotXp(RangeTraining.UnlockedWeapon())) : default,
            SparringRawXpPerExchange = sparring && verb != null ? Known(200d * verb.verbProps.AdjustedFullCycleTime(verb, pawn)) : default,
            RangeAppliedBudget = skill.def == SkillDefOf.Shooting ? Known((double)RangeTraining.Remaining(pawn)) : default,
        };
    }
}
