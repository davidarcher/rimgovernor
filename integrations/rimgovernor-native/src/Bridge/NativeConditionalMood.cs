using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using Verse;
using RimGovernor.Brain.Domain;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools;

/// <summary>Read native sleep and mood mechanics, never choose a sleep schedule. Only the verified native owl workers are modeled.</summary>
internal static class NativeConditionalMood
{
    /// <summary>The pawn's sleep mechanics (NeedsForecastPawn's sleep members).</summary>
    internal readonly record struct SleepMechanics(double SleepThreshold, double WakeThreshold, bool Blocked, double BlockedUntilTicks, bool WhileStarving);

    /// <summary>RestUtility's private FallAsleepMaxLevel and WakeThreshold, the sleep blockers and the disturbance wait; an unreadable threshold throws.</summary>
    internal static SleepMechanics ReadSleep(Pawn pawn)
    {
        double Mechanic(string name) => typeof(RestUtility).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)
            ?.Invoke(null, new object[] { pawn }) is float value && double.IsFinite(value) ? value
            : throw new InvalidOperationException("RestUtility." + name + " is not readable.");
        var blocked = pawn.health.hediffSet.HasHediffBlocksSleeping() || !pawn.ageTracker.CurLifeStage.canVoluntarilySleep;
        return new(Mechanic("FallAsleepMaxLevel"), Mechanic("WakeThreshold"), blocked,
            Math.Max(0, 400 - (Find.TickManager.TicksGame - (pawn.mindState?.lastDisturbanceTick ?? -400))),
            pawn.ageTracker.CurLifeStage.canSleepWhenStarving);
    }

    // Installed 1.7 ThoughtWorker_IsDayForNightOwl / IsNightForNightOwl use HourInteger
    // 11..17 and 23..5 respectively, both gated by Awake. Need_Seeker changes by
    // def.seeker{Rise,Fall}PerHour * .06 every 150 ticks and Need.IsFrozen gates sleep.
    /// <summary>The pawn's mood need; null without one. A mood need without a mental breaker throws.</summary>
    internal static Obs.NeedMood? ReadMood(Pawn pawn, EquatableArray<Obs.NeedMemory> memories, List<string> unsupported)
    {
        if (pawn.needs?.mood is not { } mood) return null;
        var breaker = pawn.mindState?.mentalBreaker ?? throw new InvalidOperationException("mood need without a mental breaker: " + pawn.GetUniqueLoadID());
        var total = mood.thoughts.TotalMoodOffset();
        var groups = new List<Thought>();
        mood.thoughts.GetDistinctMoodThoughtGroups(groups);
        var baseline = (pawn.health.hediffSet.OverrideMoodBase ?? mood.def.baseLevel)
            + (total + (pawn.IsColonist || pawn.IsPrisonerOfColony ? Find.Storyteller.difficulty.colonistMoodOffset : 0)) / 100d;
        var modeled = new List<Obs.NeedConditionalThought>();
        var unmodeled = new List<string>();
        foreach (var def in DefDatabase<ThoughtDef>.AllDefsListForReading.OrderBy(d => d.defName, StringComparer.Ordinal))
        {
            var day = def.workerClass == typeof(ThoughtWorker_IsDayForNightOwl);
            var night = def.workerClass == typeof(ThoughtWorker_IsNightForNightOwl);
            if (!day && !night)
            {
                if (def.requiredTraits?.Any(t => t.defName == "NightOwl") == true && ThoughtUtility.CanGetThought(pawn, def)) unmodeled.Add(def.defName);
                continue;
            }
            if (!ThoughtUtility.CanGetThought(pawn, def)) continue;
            if (def.invert || def.stages.Count != 1 || def.ThoughtClass != typeof(Thought_Situational) || def.producesMemoryThought != null)
            { unmodeled.Add(def.defName); continue; }
            // Unattached stage probe delegates nullification, stat and worker multipliers to native Thought.MoodOffset.
            var probe = new StageProbe { def = def, pawn = pawn };
            var current = groups.Where(t => t.def == def).Sum(t => mood.thoughts.MoodOffsetOfGroup(t));
            modeled.Add(new(day ? 11 : 23, day ? 18 : 6, true, probe.MoodOffset(), current));
        }
        unsupported.Add("other_conditional_thought_changes");
        return new()
        {
            Level = Finite(mood.CurLevelPercentage, "mood level"), Target = Finite(mood.CurInstantLevel, "mood target"),
            UnclampedTarget = Finite(baseline, "unclamped mood target"),
            RisePerTick = (double)mood.def.seekerRisePerHour / GenDate.TicksPerHour, FallPerTick = (double)mood.def.seekerFallPerHour / GenDate.TicksPerHour,
            FreezesWhileAsleep = mood.def.freezeWhileSleeping, BreakThreshold = breaker.BreakThresholdMinor,
            Conditional = unmodeled.Count == 0 ? new Obs.ConditionalMood.Modeled(EquatableArray.From(modeled))
                : new Obs.ConditionalMood.Unmodeled(EquatableArray.From(unmodeled)),
            Memories = memories,
        };
    }
    private sealed class StageProbe : Thought { public override int CurStageIndex => 0; }
    private static double Finite(double value, string what) => double.IsFinite(value) ? value : throw new InvalidOperationException(what + " is not finite.");
}
