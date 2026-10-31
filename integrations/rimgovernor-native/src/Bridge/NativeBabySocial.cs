using System;
using System.Collections.Generic;
using System.Linq;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.Policy;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimGovernor.Native;

/// <summary>Read-only social consequences of the baby-fit opinion memories. No memory, job, relation or RNG is changed.</summary>
internal static class NativeBabySocial
{
    internal static BabySocialRead Read(Map map, List<Pawn> people, Pawn[] babies)
    {
        var moodDefs = new HashSet<ThoughtDef>();
        foreach (var interaction in DefDatabase<InteractionDef>.AllDefsListForReading)
        {
            var seen = new HashSet<ThoughtDef>();
            for (var def = interaction.recipientThought; def != null; def = def.thoughtToMake)
            {
                if (!seen.Add(def)) throw new RequiredFactException("cyclic interaction thought:" + interaction.defName);
                if (!def.IsSocial) moodDefs.Add(def);
            }
        }
        var recipients = map.mapPawns.AllPawnsSpawned.Where(p => p.RaceProps.Humanlike).ToArray();
        return new()
        {
            CheckIntervalTicks = Constant("RandomInteractCheckInterval"), RetryIntervalTicks = 91,
            MinInteractionTicks = Pawn_InteractionsTracker.RandomInteractIntervalMin,
            QuietMtbTicks = Constant("RandomInteractMTBTicks_Quiet"), NormalMtbTicks = Constant("RandomInteractMTBTicks_Normal"),
            SuperActiveMtbTicks = Constant("RandomInteractMTBTicks_SuperActive"),
            OpinionFactor = EquatableArray.From(Curve("OpinionFactorCurve").Points.Select(p => new CurvePointRead(p.x, p.y))),
            Jobs = EquatableArray.From(Jobs()),
            MoodDefs = EquatableArray.From(moodDefs.Select(d => d.defName)),
            Pawns = EquatableArray.From(people.Where(p => babies.Length != 0 && p.DevelopmentalStage == DevelopmentalStage.Adult && p.needs?.mood != null)
                .OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).Select(p => Adult(p, babies, recipients))),
        };
    }

    private static BabySocialPawnRead Adult(Pawn adult, Pawn[] babies, Pawn[] recipients)
    {
        var tracker = adult.interactions ?? throw new RequiredFactException("adult interaction tracker:" + adult.GetUniqueLoadID());
        var now = Find.TickManager.TicksGame;
        var interval = (int)Constant("RandomInteractCheckInterval");
        var phase = ((now + adult.HashOffsetTicks()) % interval + interval) % interval;
        var retryPhase = ((now + adult.HashOffsetTicks()) % 91 + 91) % 91;
        var maximum = RandomSocialMode.SuperActive;
        if (adult.mindState.duty is { } duty && duty.SocialModeMax < maximum) maximum = duty.SocialModeMax;
        if (adult.Drafted && maximum > RandomSocialMode.Quiet) maximum = RandomSocialMode.Quiet;
        if (adult.InMentalState && adult.MentalState.SocialModeMax() < maximum) maximum = adult.MentalState.SocialModeMax();
        var eligible = SocialInteractionUtility.CanInitiateRandomInteraction(adult);
        return new()
        {
            Pawn = adult.GetUniqueLoadID(), MaximumMode = Mode(maximum),
            CooldownTicks = Math.Max(0, Integer(tracker, "lastInteractionTime") + Pawn_InteractionsTracker.RandomInteractIntervalMin + 1 - now),
            AbsoluteCooldownTicks = Math.Max(0, Integer(tracker, "lastInteractionTime") + Constant("InteractIntervalAbsoluteMin") - now),
            CheckAfterTicks = interval - phase, RetryAfterTicks = 91 - retryPhase,
            WantsInteraction = Boolean(tracker, "wantsRandomInteract"),
            Unprojected = ModsConfig.AnomalyActive && (adult.story.IsDisturbing || adult.story.traits.HasTrait(TraitDefOf.Occultist))
                ? "nursery_opinion_forced_social_branch:" + adult.GetUniqueLoadID() : "",
            Recipients = EquatableArray.From(recipients.Where(p => p != adult && p.Faction == adult.Faction && !adult.HostileTo(p)
                && SocialInteractionUtility.CanReceiveRandomInteraction(p)
                && DefDatabase<InteractionDef>.AllDefsListForReading.Any(d => !adult.IsInteractionBlocked(d, true, false)
                    && !p.IsInteractionBlocked(d, false, false) && d.Worker.RandomSelectionWeight(adult, p) > 0)).Select(p => p.GetUniqueLoadID())),
            Pairs = EquatableArray.From(babies.Select(b => new BabySocialPairRead
            {
                Baby = b.GetUniqueLoadID(), Opinion = NativePrisonRecruit.OpinionPartsOf(adult, b, true),
                Eligible = eligible && b.Faction == adult.Faction && !adult.HostileTo(b) && SocialInteractionUtility.CanReceiveRandomInteraction(b),
                Cry = Memory(adult, ThoughtDefOf.BabyCriedSocial, b), Giggle = Memory(adult, ThoughtDefOf.BabyGiggledSocial, b),
                Interactions = EquatableArray.From(DefDatabase<InteractionDef>.AllDefsListForReading
                    .Where(d => !adult.IsInteractionBlocked(d, true, false) && !b.IsInteractionBlocked(d, false, false))
                    .Select(d => Interaction(d, adult, b)).Where(d => d.Weight > 0)),
            })),
        };
    }

    private static BabyInteractionRead Interaction(InteractionDef def, Pawn adult, Pawn baby)
    {
        var negative = def.Worker is InteractionWorker_Insult or InteractionWorker_Slight;
        double weight;
        if (negative)
        {
            // The base weights are private native consts; no typed member exposes them.
            var field = def.Worker is InteractionWorker_Insult ? typeof(InteractionWorker_Insult) : typeof(InteractionWorker_Slight);
            weight = BridgeCommon.PrivateStaticField(field, "BaseSelectionWeight")?.GetRawConstantValue() is float value ? value
                : throw new RequiredFactException("negative interaction base weight:" + def.defName);
            weight *= adult.story.traits.HasTrait(TraitDefOf.Kind) || adult.IsSlave && !baby.IsSlave ? 0
                : Curve("CompatibilityFactorCurve").Evaluate(adult.relations.CompatibilityWith(baby))
                    * (adult.story.traits.HasTrait(TraitDefOf.Abrasive) ? NegativeInteractionUtility.AbrasiveSelectionChanceFactor : 1);
        }
        else weight = def.Worker.RandomSelectionWeight(adult, baby);
        var moods = new List<SocialMemoryDefRead>();
        var seen = new HashSet<ThoughtDef>();
        var factor = (double)adult.GetStatValue(StatDefOf.SocialImpact);
        for (var thought = def.recipientThought; thought != null; thought = thought.thoughtToMake)
        {
            if (!seen.Add(thought)) throw new RequiredFactException("cyclic interaction thought:" + def.defName);
            if (Memory(baby, thought, adult) is not { } memory) break;
            if (!thought.IsSocial) moods.Add(memory with { Offset = memory.Offset * factor });
            // Recursively constructed thoughtToMake does not inherit SocialImpact from its parent.
            factor = 1;
        }
        return new(def.defName, Finite(weight, "interaction weight"), negative, EquatableArray.From(moods));
    }

    private static SocialMemoryDefRead? Memory(Pawn pawn, ThoughtDef def, Pawn other)
    {
        if (pawn.needs?.mood == null || !ThoughtUtility.CanGetThought(pawn, def)
            || def.IsSocial && !def.socialTargetDevelopmentalStageFilter.Has(other.DevelopmentalStage)) return null;
        var memory = (Thought_Memory)ThoughtMaker.MakeThought(def);
        memory.pawn = pawn; memory.otherPawn = other;
        return Mechanics(pawn, memory);
    }

    private static SocialMemoryDefRead Mechanics(Pawn pawn, Thought_Memory memory)
    {
        var def = memory.def;
        double offset;
        if (memory is Thought_MemorySocial social) offset = ThoughtUtility.ThoughtNullified(pawn, def) ? 0 : social.opinionOffset;
        else
        {
            // Retain the pre-age offset: the cohort applies age fading exactly once. MoodOffset on a live
            // lerping memory has already faded, while the shared response retains its current additive value.
            var fresh = (Thought_Memory)ThoughtMaker.MakeThought(def, memory.CurStageIndex);
            fresh.pawn = pawn; fresh.moodPowerFactor = memory.moodPowerFactor; fresh.moodOffset = memory.moodOffset;
            offset = fresh.MoodOffset();
        }
        return new(def.defName, Finite(offset, "memory offset"), memory.DurationTicks, def.stackLimit, def.stackLimitForSameOtherPawn,
            def.stackedEffectMultiplier, memory is Thought_MemorySocial ? def.lerpOpinionToZeroAfterDurationPct : def.lerpMoodToZero ? 0 : 1)
        {
            Label = memory.LabelCap,
            Unprojected = memory is not Thought_MemorySocial && (memory.GetType() != typeof(Thought_Memory) || def.lerpMoodToZero)
                ? "nursery_opinion_mood_class_unprojected:" + def.defName : "",
        };
    }

    private static IEnumerable<ActivitySocialJobRead> Jobs()
    {
        // Each listed service toil was decompiled in installed 1.7 and uses Toil's Normal mode with
        // no DesiredSocialMode override. Unlisted/variable drivers stay explicitly unprojected.
        var normal = new HashSet<Type>
        {
            typeof(JobDriver_Research), typeof(JobDriver_ConstructFinishFrame), typeof(JobDriver_Mine),
            typeof(JobDriver_PlantCut), typeof(JobDriver_PlantHarvest), typeof(JobDriver_PlantSow),
            typeof(JobDriver_RemoveFloor), typeof(JobDriver_RemoveRoof), typeof(JobDriver_BuildRoof),
            typeof(JobDriver_Deconstruct), typeof(JobDriver_Uninstall),
            typeof(JobDriver_Ingest), typeof(JobDriver_LayDown), typeof(JobDriver_DoBill),
            typeof(JobDriver_GovernorTend),
            typeof(RimGovernor.Runtime.JobDriver_TrainRange), typeof(RimGovernor.Runtime.JobDriver_Spar),
        };
        foreach (var def in DefDatabase<JobDef>.AllDefsListForReading.Where(d => normal.Contains(d.driverClass)))
            yield return new(PawnActivityKind.Work, def.defName, ActivitySocialMode.Normal);
        foreach (var kind in new[] { PawnActivityKind.Eat, PawnActivityKind.Sleep, PawnActivityKind.Recipe, PawnActivityKind.Hold })
            if (NativePawnActivities.ServiceJob(kind) is { } job && normal.Contains(job.driverClass))
                yield return new(kind, "", ActivitySocialMode.Normal);
        yield return new(PawnActivityKind.Work, JobDefOf.TendPatient.defName, ActivitySocialMode.Normal);
    }

    private static ActivitySocialMode Mode(RandomSocialMode mode) => mode switch
    {
        RandomSocialMode.Off => ActivitySocialMode.Off, RandomSocialMode.Quiet => ActivitySocialMode.Quiet,
        RandomSocialMode.Normal => ActivitySocialMode.Normal, RandomSocialMode.SuperActive => ActivitySocialMode.SuperActive,
        _ => throw new RequiredFactException("social mode:" + mode),
    };

    // These native curves and tracker fields/constants have no public typed accessor. A renamed or
    // unreadable member fails the required read; it never supplies zero/default social exposure.
    private static SimpleCurve Curve(string field) => BridgeCommon.PrivateStaticField(typeof(NegativeInteractionUtility), field)?.GetValue(null) as SimpleCurve
        ?? throw new RequiredFactException("negative interaction curve:" + field);
    private static double Constant(string field) => BridgeCommon.PrivateStaticField(typeof(Pawn_InteractionsTracker), field)?.GetRawConstantValue() is int value && value > 0
        ? value : throw new RequiredFactException("interaction constant:" + field);
    private static int Integer(Pawn_InteractionsTracker tracker, string field) => BridgeCommon.PrivateInstanceField(typeof(Pawn_InteractionsTracker), field)?.GetValue(tracker) is int value
        ? value : throw new RequiredFactException("interaction state:" + field);
    private static bool Boolean(Pawn_InteractionsTracker tracker, string field) => BridgeCommon.PrivateInstanceField(typeof(Pawn_InteractionsTracker), field)?.GetValue(tracker) is bool value
        ? value : throw new RequiredFactException("interaction state:" + field);
    private static double Finite(double value, string fact) => double.IsFinite(value) ? value : throw new RequiredFactException(fact);
}
