#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;

namespace HomeBridge.BridgeTools;

public sealed partial class PawnControlFixture
{
    [Tool("test/conditional_mood_mechanics", Description="UNSAFE FOR MODEL EXECUTION. Prepared local-time and asleep-state probes of installed conditional thought and mood response mechanics; no world ticks elapse.")]
    public async Task<object> ConditionalMoodMechanics(IRimBridgeContext ctx, CancellationToken ct)
        => await ctx.MainThread.InvokeAsync<object>(() =>
        {
            var pawn = worker ?? throw new InvalidOperationException("Prepare pawn control first.");
            var mood = pawn.needs.mood;
            var trait = DefDatabase<TraitDef>.GetNamed("NightOwl");
            var originalTrait = pawn.story.traits.GetTrait(trait);
            var added = originalTrait is null;
            var originalTick = Find.TickManager.TicksGame;
            var driver = pawn.jobs.curDriver ?? throw new InvalidOperationException("Prepared pawn needs an active job driver.");
            var originalSleep = driver.asleep;
            var initialHour = GenLocalDate.HourFloat(pawn);
            var checks = 0;
            try
            {
                if (added) pawn.story.traits.GainTrait(new Trait(trait));
                for (var hour = 0; hour < 24; hour++)
                {
                    Find.TickManager.DebugSetTicksGame(originalTick + (int)Math.Round(((hour + .5 - initialHour + 24) % 24) * GenDate.TicksPerHour));
                    if (GenLocalDate.HourInteger(pawn) != hour) throw new InvalidOperationException("Fixture local-hour setup mismatch.");
                    foreach (var asleep in new[] { false, true })
                    {
                        driver.asleep = asleep;
                        mood.thoughts.situational.Notify_SituationalThoughtsDirty();
                        var read = NativeConditionalMood.Read(pawn, new List<string>());
                        if (!read.ConditionalThoughts.TryGetValue(out var thoughts) || thoughts.Count != 2)
                            throw new InvalidOperationException("Expected both supported native conditional thought windows.");
                        foreach (var thought in thoughts)
                        {
                            var def = DefDatabase<ThoughtDef>.GetNamed(thought.Id);
                            var within = thought.StartHour < thought.EndHour ? hour >= thought.StartHour && hour < thought.EndHour
                                : hour >= thought.StartHour || hour < thought.EndHour;
                            var expected = !asleep && within;
                            if (def.Worker.CurrentState(pawn).Active != expected || Math.Abs(thought.CurrentOffset - (expected ? thought.Offset : 0)) > .00001)
                                throw new InvalidOperationException("Native conditional activation or current contribution mismatch: " + thought.Id);
                            checks++;
                        }
                        var nativeTarget = (pawn.health.hediffSet.OverrideMoodBase ?? mood.def.baseLevel)
                            + (mood.thoughts.TotalMoodOffset() + Find.Storyteller.difficulty.colonistMoodOffset) / 100d;
                        Near(read.MoodUnclampedTarget.Value, nativeTarget, "Unclamped target did not preserve current contributions.");
                        // Detached probe executes the real inherited Need_Seeker body without changing live pawn mood.
                        var probe = new MoodResponseProbe(pawn) { def = mood.def, Target = .8f, CurLevel = .4f };
                        probe.NeedInterval();
                        Near(probe.CurLevel, asleep && mood.def.freezeWhileSleeping ? .4 : .4 + read.MoodRisePerTick.Value * read.MoodIntervalTicks.Value, "Native rise/freeze mismatch.");
                        probe.Target = .2f; probe.CurLevel = .6f; probe.NeedInterval();
                        Near(probe.CurLevel, asleep && mood.def.freezeWhileSleeping ? .6 : .6 - read.MoodFallPerTick.Value * read.MoodIntervalTicks.Value, "Native fall/freeze mismatch.");
                        probe.Target = .40001f; probe.CurLevel = .4f; probe.NeedInterval();
                        Near(probe.CurLevel, asleep && mood.def.freezeWhileSleeping ? .4 : .40001, "Native response overshot target.");
                    }
                }
                pawn.story.traits.RemoveTrait(pawn.story.traits.GetTrait(trait));
                mood.thoughts.situational.Notify_SituationalThoughtsDirty();
                var control = NativeConditionalMood.Read(pawn, new List<string>());
                if (!control.ConditionalThoughts.TryGetValue(out var empty) || empty.Count != 0)
                    throw new InvalidOperationException("Control pawn retained trait-specific effects.");
                if (originalTrait is not null) pawn.story.traits.GainTrait(originalTrait);
                return new { success = true, activationChecks = checks, responseChecks = 144,
                    day = "[11,18)", night = "[23,6)", sleepSuppressesThoughts = true,
                    moodSleepFreeze = mood.def.freezeWhileSleeping, risePerHour = mood.def.seekerRisePerHour,
                    fallPerHour = mood.def.seekerFallPerHour, intervalTicks = 150,
                    limitations = "Prepared state probes; native update phase and live selected sleep/wake outcomes unverified." };
            }
            finally
            {
                Find.TickManager.DebugSetTicksGame(originalTick);
                driver.asleep = originalSleep;
                if (added && pawn.story.traits.GetTrait(trait) is { } gained) pawn.story.traits.RemoveTrait(gained);
                if (originalTrait is not null && !pawn.story.traits.HasTrait(trait)) pawn.story.traits.GainTrait(originalTrait);
                mood.thoughts.situational.Notify_SituationalThoughtsDirty();
            }
        }, ct).ConfigureAwait(false);

    private static void Near(double actual, double expected, string message)
    { if (Math.Abs(actual - expected) > .000001) throw new InvalidOperationException(message + " Actual=" + actual + " expected=" + expected); }
    private sealed class MoodResponseProbe(Pawn pawn) : Need_Seeker(pawn)
    {
        public float Target;
        public override float CurInstantLevel => Target;
    }
}
