#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using RimGovernor.Brain.Domain;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools;

/// <summary>
/// The recruitment mechanics the prison credit prices (#3805, Obs.PrisonRecruitRead): the resistance curves, the wardens among the
/// colonists and every prisoner of the colony on the map, with its mood, its opinion of each warden and what it would be as a
/// colonist. Read-only: no thought is added, recalculated or cached on any pawn.
/// </summary>
internal static class NativePrisonRecruit
{
    internal static Obs.PrisonRecruitRead Read(Map map, List<Pawn> people, Func<NativeRecreation.OutdoorClimate> climate)
    {
        var wardens = people.Where(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Warden)).OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).ToArray();
        var prisoners = map.mapPawns.PrisonersOfColonySpawned.OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal)
            .Select(p => PrisonerOf(p, wardens, map, climate)).ToArray();
        return new()
        {
            MoodCurve = Curve("ResistanceImpactFactorCurve_Mood"), OpinionCurve = Curve("ResistanceImpactFactorCurve_Opinion"),
            MaxInteractionsPerDay = Pawn_GuestTracker.MaxInteractionsPerDay,
            Wardens = EquatableArray.From(wardens.Select(w => new Obs.WardenRead(w.GetUniqueLoadID(), F(w.GetStatValue(StatDefOf.NegotiationAbility), "negotiation ability")))),
            Prisoners = EquatableArray.From(prisoners),
        };
    }

    /// <summary>
    /// A private static curve of InteractionWorker_RecruitAttempt, point by point. The worker exposes them through no member; the
    /// field name is the only route, so a game version that renames one is a broken read.
    /// </summary>
    private static EquatableArray<Obs.CurvePointRead> Curve(string field)
    {
        var curve = BridgeCommon.PrivateStaticField(typeof(InteractionWorker_RecruitAttempt), field)?.GetValue(null) as SimpleCurve
            ?? throw new InvalidOperationException("InteractionWorker_RecruitAttempt." + field + " is not readable.");
        return EquatableArray.From(curve.Points.Select(p => new Obs.CurvePointRead(p.x, p.y)));
    }

    private static Obs.PrisonerRecruitRead PrisonerOf(Pawn pawn, Pawn[] wardens, Map map, Func<NativeRecreation.OutdoorClimate> climate)
    {
        var guest = pawn.guest ?? throw new InvalidOperationException("prisoner without a guest tracker: " + pawn.GetUniqueLoadID());
        return new()
        {
            Pawn = pawn.GetUniqueLoadID(), Recruitable = guest.Recruitable, Resistance = F(guest.Resistance, "resistance"),
            PrisonBed = pawn.ownership?.OwnedBed?.ForPrisoners == true, WildMan = pawn.IsWildMan(), Mood = MoodOf(pawn),
            Opinions = EquatableArray.From(wardens.Select(w => new Obs.WardenOpinionRead(w.GetUniqueLoadID(), OpinionOf(pawn, w)))),
            Joiner = JoinerOf(pawn, map, climate),
        };
    }

    /// <summary>The prison thoughts' worker classes and the stage-row roles they are read by. No ThoughtDefOf field names them; their worker classes are the typed route.</summary>
    private static readonly (Type Worker, string Role)[] CellThoughts =
    [
        (typeof(ThoughtWorker_PrisonCellImpressiveness), "prison_cell"), (typeof(ThoughtWorker_PrisonBarracksImpressiveness), "prison_barracks"),
    ];

    private static Obs.PrisonerMoodRead? MoodOf(Pawn pawn)
    {
        if (pawn.needs?.mood is not { } mood) return null;
        var groups = new List<Thought>();
        mood.thoughts.GetDistinctMoodThoughtGroups(groups);
        var unclamped = (pawn.health.hediffSet.OverrideMoodBase ?? mood.def.baseLevel)
            + (mood.thoughts.TotalMoodOffset() + (pawn.IsColonist || pawn.IsPrisonerOfColony ? Find.Storyteller.difficulty.colonistMoodOffset : 0)) / 100d;
        var cell = groups.Where(t => CellThoughts.Any(c => c.Worker == t.def.workerClass)).Sum(t => (double)mood.thoughts.MoodOffsetOfGroup(t));
        var ascetic = pawn.story?.traits?.HasTrait(TraitDefOf.Ascetic) == true;
        var rows = new List<Obs.NeedThoughtStage>();
        foreach (var (worker, role) in CellThoughts)
            foreach (var def in DefDatabase<ThoughtDef>.AllDefsListForReading.Where(d => d.workerClass == worker && d.stages != null))
            {
                var can = !ascetic && ThoughtUtility.CanGetThought(pawn, def);
                for (var stage = 0; stage < def.stages.Count; stage++)
                {
                    // ThoughtWorker_RoomImpressiveness leaves the thought inactive at a null stage and for an Ascetic pawn: a 0 row.
                    var offset = can && def.stages[stage] != null ? NativeNeedsForecast.StageOffset(pawn, def, stage) : 0;
                    if (!double.IsFinite(offset)) throw new InvalidOperationException(def.defName + " stage " + stage + " offset is not finite.");
                    rows.Add(new(def.defName, role, stage, offset, new Obs.ThoughtLasting.Situational()));
                }
            }
        return new(F(unclamped, "prisoner unclamped mood"), F(cell, "prison cell thought")) { CellStages = EquatableArray.From(rows) };
    }

    /// <summary>
    /// Pawn_RelationsTracker.OpinionOf(other), rebuilt without its thought cache (decompiled 1.7): OpinionOf reaches
    /// SituationalThoughtHandler.CheckRecalculateSocialThoughts, whose RecalculateState can remove memories (Notify_BecameActive)
    /// and records query ticks. Here the relations' opinionOffset, plus the social thoughts' TotalOpinionOffset (the live social
    /// memories of <paramref name="other"/> and each situational social def TryCreateSocialThought would create, staged without
    /// RecalculateState), times the hediffs' opinionOfOthersFactor, rounded; 0 when positive toward a hostile pawn; within ±100.
    /// </summary>
    internal static int OpinionOf(Pawn pawn, Pawn other)
    {
        if (!other.RaceProps.Humanlike || pawn == other || pawn.Dead) return 0;
        var num = 0;
        foreach (var relation in pawn.GetRelations(other)) num += relation.opinionOffset;
        if (pawn.RaceProps.Humanlike && pawn.needs?.mood != null) num += TotalOpinionOffset(pawn, other);
        if (num != 0)
        {
            var factor = 1f;
            foreach (var hediff in pawn.health.hediffSet.hediffs)
                if (hediff.CurStage != null) factor *= hediff.CurStage.opinionOfOthersFactor;
            num = Mathf.RoundToInt(num * factor);
        }
        if (num > 0 && pawn.HostileTo(other)) num = 0;
        return Mathf.Clamp(num, -100, 100);
    }

    private static readonly System.Reflection.FieldInfo? SituationalStage = BridgeCommon.PrivateInstanceField(typeof(Thought_Situational), "curStageIndex");

    // ThoughtHandler.TotalOpinionOffset over the social thoughts SocialThoughts builds: per distinct group (a thought no earlier one
    // GroupsWith), OpinionOffsetOfGroup's stacked sum of its nonzero members, rounded away from 0.
    private static int TotalOpinionOffset(Pawn pawn, Pawn other)
    {
        var thoughts = SocialThoughts(pawn, other);
        var total = 0;
        for (var i = 0; i < thoughts.Count; i++)
        {
            var head = (Thought)thoughts[i];
            var distinct = true;
            for (var j = 0; j < i && distinct; j++) if (((Thought)thoughts[j]).GroupsWith(head)) distinct = false;
            if (!distinct) continue;
            var members = thoughts.Where(t => ((Thought)t).GroupsWith(head) && t.OpinionOffset() != 0f).ToList();
            if (members.Count == 0) continue;
            if (head.def.IsMemory && head.def.stackedEffectMultiplier != 1f) members.Sort((a, b) => ((Thought_Memory)a).age.CompareTo(((Thought_Memory)b).age));
            var sum = 0f;
            var multiplier = 1f;
            foreach (var member in members)
            {
                sum += member.OpinionOffset() * multiplier;
                multiplier *= ((Thought)member).def.stackedEffectMultiplier;
            }
            total += sum == 0f ? 0 : sum > 0f ? Mathf.Max(Mathf.RoundToInt(sum), 1) : Mathf.Min(Mathf.RoundToInt(sum), -1);
        }
        return total;
    }

    // ThoughtHandler.GetSocialThoughts: the live social memories of other, then SituationalThoughtHandler's active social thoughts,
    // each built as TryCreateSocialThought builds it but staged directly (curStageIndex = the state's stage) instead of by
    // RecalculateState. A def whose worker throws is absent, as TryCreateSocialThought logs and leaves it.
    private static List<ISocialThought> SocialThoughts(Pawn pawn, Pawn other)
    {
        var thoughts = new List<ISocialThought>();
        foreach (var memory in pawn.needs.mood.thoughts.memories.Memories)
            if (memory is ISocialThought social && social.OtherPawn() == other) thoughts.Add(social);
        var stage = SituationalStage ?? throw new InvalidOperationException("Thought_Situational.curStageIndex is not readable.");
        foreach (var def in ThoughtUtility.situationalSocialThoughtDefs)
        {
            Thought_SituationalSocial? thought;
            try
            {
                if (!ThoughtUtility.CanGetThought(pawn, def)) continue;
                var state = def.Worker.CurrentSocialState(pawn, other);
                if (!state.ActiveFor(def) || !def.socialTargetDevelopmentalStageFilter.HasAny(other.DevelopmentalStage) || def.ignoreSubhumans && other.IsSubhuman) continue;
                thought = (Thought_SituationalSocial)ThoughtMaker.MakeThought(def);
                thought.pawn = pawn;
                thought.otherPawn = other;
                if (def.Worker is ThoughtWorker_Precept_Social) thought.sourcePrecept = pawn.Ideo?.GetFirstPreceptAllowingSituationalThought(def);
                stage.SetValue(thought, state.StageIndexFor(def));
            }
            catch (Exception)
            {
                continue;
            }
            thoughts.Add(thought);
        }
        return thoughts;
    }

    /// <summary>
    /// The pawn as it would be once it joins (#3805, Obs.JoinerRead): its upkeep (no mood: its mood parts are read apart), its
    /// mood parts and its food, rest and joy needs. A pawn without the joy need now that would have it as a colonist (a prisoner:
    /// the Joy NeedDef is neverOnPrisoner) is read from a detached Need_Joy at its Satisfied threshold, built under its own random
    /// state (the Need constructor's SetInitialLevel draws from Rand).
    /// </summary>
    private static Obs.JoinerRead JoinerOf(Pawn pawn, Map map, Func<NativeRecreation.OutdoorClimate> climate)
    {
        var joy = pawn.needs?.joy ?? DetachedJoy(pawn);
        return new()
        {
            Upkeep = NativeNeedsForecast.UpkeepOf(pawn, null), Mood = JoinerMoodOf(pawn),
            Needs = EquatableArray.From(NativeNeedsForecast.NeedRatesOf(pawn, map, climate, joy)),
        };
    }

    // The Joy NeedDef by its class (no NeedDefOf field names it; the class is the typed route).
    private static NeedDef? joyDef;

    // Pawn_NeedsTracker.ShouldHaveNeed's gates for joy that do not depend on the pawn's faction or guest status.
    private static Need_Joy? DetachedJoy(Pawn pawn)
    {
        joyDef ??= DefDatabase<NeedDef>.AllDefsListForReading.FirstOrDefault(d => d.needClass == typeof(Need_Joy))
            ?? throw new InvalidOperationException("no NeedDef of class Need_Joy");
        var def = joyDef;
        if (def.onlyIfCausedByHediff || def.onlyIfCausedByGene || def.onlyIfCausedByIdeo || def.onlyIfCausedByTrait || def.titleRequiredAny != null)
            throw new InvalidOperationException("the Joy NeedDef is gated by a cause this read does not model");
        if (pawn.RaceProps.intelligence < def.minIntelligence || !def.developmentalStageFilter.Has(pawn.DevelopmentalStage)
            || pawn.health.hediffSet.DisablesNeed(def) || ModsConfig.BiotechActive && pawn.genes != null && pawn.genes.DisablesNeed(def)
            || pawn.story?.traits != null && pawn.story.traits.DisablesNeed(def) || pawn.Ideo != null && pawn.Ideo.DisablesNeed(def)
            || def.neverOnSlave && pawn.IsSlave
            || pawn.IsMutant && pawn.mutant.Def.disableNeeds && (pawn.mutant.Def.needWhitelist == null || !pawn.mutant.Def.needWhitelist.Contains(def)))
            return null;
        Need_Joy joy;
        Rand.PushState();
        try { joy = new Need_Joy(pawn) { def = def }; }
        finally { Rand.PopState(); }
        joy.CurLevel = (float)NativeNeedsForecast.JoyThreshold(joy);
        return joy;
    }

    /// <summary>
    /// The joiner's own mood parts (Obs.JoinerMoodRead); null without a mood need or a mental breaker. A trait thought is a
    /// situational non-social def whose requiredTraits the pawn has (CanGetThought's trait clause) and its traits do not disallow:
    /// an always-active one at its unattached MoodOffset, any other named. The memories are its live memory groups.
    /// </summary>
    private static Obs.JoinerMoodRead? JoinerMoodOf(Pawn pawn)
    {
        if (pawn.needs?.mood is not { } mood || pawn.mindState?.mentalBreaker is not { } breaker) return null;
        var traits = new List<Obs.ThoughtOffsetRead>();
        var situational = new List<string>();
        foreach (var def in DefDatabase<ThoughtDef>.AllDefsListForReading.Where(d => !d.IsMemory && !d.IsSocial && !d.requiredTraits.NullOrEmpty())
            .OrderBy(d => d.defName, StringComparer.Ordinal))
        {
            if (pawn.story?.traits is not { } has || has.IsThoughtDisallowed(def)) continue;
            if (!def.requiredTraits.Any(t => has.HasTrait(t) && (!def.RequiresSpecificTraitsDegree || def.requiredTraitsDegree == has.DegreeOfTrait(t)))) continue;
            if (def.workerClass != typeof(ThoughtWorker_AlwaysActive)) { situational.Add(def.defName); continue; }
            var offset = NativeNeedsForecast.StageOffset(pawn, def, 0);
            if (!double.IsFinite(offset)) throw new InvalidOperationException(def.defName + " trait thought offset is not finite.");
            traits.Add(new(def.defName, offset));
        }
        var memories = new List<Obs.ThoughtOffsetRead>();
        var heads = new List<Thought_Memory>();
        foreach (var memory in mood.thoughts.memories.Memories)
            if (!heads.Any(h => h.GroupsWith(memory))) heads.Add(memory);
        foreach (var head in heads) memories.Add(new(head.def.defName, F(mood.thoughts.MoodOffsetOfGroup(head), "memory group offset")));
        return new(F(pawn.health.hediffSet.OverrideMoodBase ?? mood.def.baseLevel, "mood base level"), Find.Storyteller.difficulty.colonistMoodOffset, breaker.BreakThresholdMinor)
        {
            Traits = EquatableArray.From(traits), SituationalTraits = EquatableArray.From(situational), Memories = EquatableArray.From(memories),
        };
    }

    private static double F(double value, string what) => double.IsFinite(value) ? value : throw new InvalidOperationException(what + " is not finite.");
}
