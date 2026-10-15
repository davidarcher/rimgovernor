#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using Verse;
using RimGovernor.Brain.Domain;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools;

/// <summary>Read native conditional mechanics, never choose a sleep schedule. Only the verified native owl workers are supported.</summary>
internal static class NativeConditionalMood
{
    // Installed 1.7 ThoughtWorker_IsDayForNightOwl / IsNightForNightOwl use HourInteger
    // 11..17 and 23..5 respectively, both gated by Awake. Need_Seeker changes by
    // def.seeker{Rise,Fall}PerHour * .06 every 150 ticks and Need.IsFrozen gates sleep.
    internal static Obs.NeedsForecastPawn Read(Pawn pawn, List<string> unsupported)
    {
        var mood = pawn.needs?.mood;
        var row = new Obs.NeedsForecastPawn { LocalHour = K(GenLocalDate.HourFloat(pawn)), Awake = Fact.Known(pawn.Awake()) };
        Fact<double> SleepMechanic(string name) => typeof(RestUtility).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)
            ?.Invoke(null, new object[] { pawn }) is float value ? K(value) : default;
        var sleepBlocked = pawn.health.hediffSet.HasHediffBlocksSleeping() || !pawn.ageTracker.CurLifeStage.canVoluntarilySleep;
        if (sleepBlocked) unsupported.Add("sleep_blocker_recovery_unassessed");
        row = row with { SleepThreshold = SleepMechanic("FallAsleepMaxLevel"), WakeThreshold = SleepMechanic("WakeThreshold"),
            SleepWhileStarving = Fact.Known(pawn.ageTracker.CurLifeStage.canSleepWhenStarving),
            SleepBlockedUntilTicks = sleepBlocked ? default : K(Math.Max(0, 400 - (Find.TickManager.TicksGame - (pawn.mindState?.lastDisturbanceTick ?? -400)))) };
        if (mood == null) return row;
        var total = mood.thoughts.TotalMoodOffset();
        var groups = new List<Thought>();
        mood.thoughts.GetDistinctMoodThoughtGroups(groups);
        var baseline = (pawn.health.hediffSet.OverrideMoodBase ?? mood.def.baseLevel)
            + (total + (pawn.IsColonist || pawn.IsPrisonerOfColony ? Find.Storyteller.difficulty.colonistMoodOffset : 0)) / 100d;
        var conditional = new List<Obs.NeedConditionalThought>();
        var supported = true;
        foreach (var def in DefDatabase<ThoughtDef>.AllDefsListForReading.OrderBy(d => d.defName, StringComparer.Ordinal))
        {
            var day = def.workerClass == typeof(ThoughtWorker_IsDayForNightOwl);
            var night = def.workerClass == typeof(ThoughtWorker_IsNightForNightOwl);
            if (!day && !night)
            {
                if (def.requiredTraits?.Any(t => t.defName == "NightOwl") == true && ThoughtUtility.CanGetThought(pawn, def))
                { supported = false; unsupported.Add("conditional_thought_worker:" + def.defName); }
                continue;
            }
            if (!ThoughtUtility.CanGetThought(pawn, def)) continue;
            if (def.invert || def.stages.Count != 1 || def.ThoughtClass != typeof(Thought_Situational) || def.producesMemoryThought != null)
            { supported = false; unsupported.Add("conditional_thought_definition:" + def.defName); continue; }
            // Unattached stage probe delegates nullification, stat and worker multipliers to native Thought.MoodOffset.
            var probe = new StageProbe { def = def, pawn = pawn };
            var current = groups.Where(t => t.def == def).Sum(t => mood.thoughts.MoodOffsetOfGroup(t));
            conditional.Add(new(def.defName, day ? 11 : 23, day ? 18 : 6, true, probe.MoodOffset(), current,
                def.workerClass.FullName + ";HourInteger;Awake;native_stage_and_multipliers"));
        }
        unsupported.Add("other_conditional_thought_changes");
        unsupported.Add("mood_response_discrete_150_tick_phase_unobserved");
        return row with
        {
            ConditionalThoughts = supported ? Fact.Known(EquatableArray.From(conditional)) : default,
            MoodUnclampedTarget = K(baseline), MoodRisePerTick = K((double)mood.def.seekerRisePerHour / GenDate.TicksPerHour),
            MoodFallPerTick = K((double)mood.def.seekerFallPerHour / GenDate.TicksPerHour), MoodIntervalTicks = K(150),
            MoodFreezesWhileAsleep = Fact.Known(mood.def.freezeWhileSleeping),
        };
    }
    private sealed class StageProbe : Thought { public override int CurStageIndex => 0; }
    private static Fact<double> K(double value) => double.IsFinite(value) ? Fact.Known(value) : default;
}
