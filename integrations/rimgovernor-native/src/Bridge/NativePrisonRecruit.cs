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
/// The recruitment mechanics the prison credit prices (#3805, #3899, Obs.PrisonRecruitRead): the resistance curves, the rapport
/// memory a chat gives, the wardens among the colonists with their inspirations, and every prisoner of the colony on the map, with
/// its mood and memory groups, its opinion of each warden in its parts and what it would be as a colonist. Read-only: no thought is
/// added, recalculated or cached on any pawn, and no inspiration is started.
/// </summary>
internal static class NativePrisonRecruit
{
    internal static Obs.PrisonRecruitRead Read(Map map, List<Pawn> people, Func<NativeRecreation.OutdoorClimate> climate)
    {
        var wardens = people.Where(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Warden)).OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).ToArray();
        var rapport = InteractionDefOf.BuildRapport.recipientThought ?? throw new InvalidOperationException("BuildRapport has no recipient thought.");
        // The credit models a plain social memory's fading and stacking (Thought_MemorySocial): another class is a broken read.
        if (rapport.thoughtClass != typeof(Thought_MemorySocial) || rapport.stages.NullOrEmpty())
            throw new InvalidOperationException(rapport.defName + " is not a staged Thought_MemorySocial.");
        var prisoners = map.mapPawns.PrisonersOfColonySpawned.OrderBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal)
            .Select(p => PrisonerOf(p, wardens, rapport, map, climate)).ToArray();
        return new()
        {
            MoodCurve = Points(WorkerCurve("ResistanceImpactFactorCurve_Mood")), OpinionCurve = Points(WorkerCurve("ResistanceImpactFactorCurve_Opinion")),
            MaxInteractionsPerDay = Pawn_GuestTracker.MaxInteractionsPerDay,
            Rapport = new(rapport.defName, rapport.DurationTicks, rapport.stackLimit, rapport.stackLimitForSameOtherPawn, rapport.stackedEffectMultiplier, rapport.lerpOpinionToZeroAfterDurationPct),
            RecruitInspiration = InspirationDefOf.Inspired_Recruitment.defName, TamingInspiration = InspirationDefOf.Inspired_Taming.defName,
            Wardens = EquatableArray.From(wardens.Select(WardenOf)),
            Prisoners = EquatableArray.From(prisoners),
        };
    }

    /// <summary>
    /// A private static curve of InteractionWorker_RecruitAttempt. The worker exposes them through no member; the field name is
    /// the only route, so a game version that renames one is a broken read.
    /// </summary>
    private static SimpleCurve WorkerCurve(string field) =>
        BridgeCommon.PrivateStaticField(typeof(InteractionWorker_RecruitAttempt), field)?.GetValue(null) as SimpleCurve
        ?? throw new InvalidOperationException("InteractionWorker_RecruitAttempt." + field + " is not readable.");

    /// <summary>A private const factor of InteractionWorker_RecruitAttempt's taming chance; the field name is the only route, as for <see cref="WorkerCurve"/>.</summary>
    private static float WorkerFactor(string field) =>
        BridgeCommon.PrivateStaticField(typeof(InteractionWorker_RecruitAttempt), field)?.GetValue(null) is float factor
            ? factor : throw new InvalidOperationException("InteractionWorker_RecruitAttempt." + field + " is not readable.");

    private static EquatableArray<Obs.CurvePointRead> Points(SimpleCurve curve) => EquatableArray.From(curve.Points.Select(p => new Obs.CurvePointRead(p.x, p.y)));

    /// <summary>
    /// A warden (Obs.WardenRead): its NegotiationAbility, its inspiration now, InspirationHandler.BlockedByHediff's clause, and the
    /// defs InspirationHandler.GetRandomAvailableInspirationDef picks among (InspirationCanOccur) at their CommonalityFor weight.
    /// </summary>
    private static Obs.WardenRead WardenOf(Pawn warden)
    {
        var handler = warden.mindState?.inspirationHandler ?? throw new InvalidOperationException("warden without an inspiration handler: " + warden.GetUniqueLoadID());
        return new(warden.GetUniqueLoadID(), F(warden.GetStatValue(StatDefOf.NegotiationAbility), "negotiation ability"))
        {
            Inspired = handler.CurState is { } now ? new(now.def.defName, F(now.AgeDays, "inspiration age"), F(now.def.baseDurationDays, "inspiration duration")) : null,
            InspirationBlocked = warden.health.hediffSet.hediffs.Any(h => h.CurStage is { blocksInspirations: true }),
            Inspirations = EquatableArray.From(DefDatabase<InspirationDef>.AllDefsListForReading.Where(d => d.Worker.InspirationCanOccur(warden))
                .OrderBy(d => d.defName, StringComparer.Ordinal)
                .Select(d => new Obs.InspirationChanceRead(d.defName, F(d.Worker.CommonalityFor(warden), "inspiration commonality"), F(d.baseDurationDays, "inspiration duration")))),
        };
    }

    private static Obs.PrisonerRecruitRead PrisonerOf(Pawn pawn, Pawn[] wardens, ThoughtDef rapport, Map map, Func<NativeRecreation.OutdoorClimate> climate)
    {
        var guest = pawn.guest ?? throw new InvalidOperationException("prisoner without a guest tracker: " + pawn.GetUniqueLoadID());
        return new()
        {
            Pawn = pawn.GetUniqueLoadID(), Recruitable = guest.Recruitable, Resistance = F(guest.Resistance, "resistance"),
            PrisonBed = pawn.ownership?.OwnedBed?.ForPrisoners == true, WildMan = pawn.IsWildMan(), Mood = MoodOf(pawn),
            Opinions = EquatableArray.From(wardens.Select(w => OpinionOf(pawn, w, rapport))),
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
        // ThoughtHandler.GetAllMoodThoughts' memories, grouped as MoodOffsetOfGroup groups them (Thought.GroupsWith).
        var memories = new List<Obs.MemoryGroupRead>();
        foreach (var head in Heads(mood.thoughts.memories.Memories))
            memories.Add(new(head.def.stackedEffectMultiplier)
            {
                Members = EquatableArray.From(mood.thoughts.memories.Memories.Where(m => m.GroupsWith(head)).Select(m => new Obs.NeedMemory(m.def.defName,
                    F(m.MoodOffset(), "memory offset"), m.permanent ? new Obs.MemoryLife.Permanent() : new Obs.MemoryLife.Expires(Math.Max(0, m.DurationTicks - m.age)),
                    m.def.lerpMoodToZero) { StackLimit = m.def.stackLimit })),
            });
        return new(F(unclamped, "prisoner unclamped mood"), F(cell, "prison cell thought")) { CellStages = EquatableArray.From(rows), Memories = EquatableArray.From(memories) };
    }

    // The first memory of each group, in list order (a memory no earlier one GroupsWith).
    private static List<T> Heads<T>(IEnumerable<T> memories) where T : Thought
    {
        var heads = new List<T>();
        foreach (var memory in memories)
            if (!heads.Any(h => h.GroupsWith(memory))) heads.Add(memory);
        return heads;
    }

    /// <summary>
    /// The prisoner's opinion of <paramref name="other"/> in the parts Pawn_RelationsTracker.OpinionOf sums (decompiled 1.7),
    /// rebuilt without its thought cache: OpinionOf reaches SituationalThoughtHandler.CheckRecalculateSocialThoughts, whose
    /// RecalculateState can remove memories (Notify_BecameActive) and records query ticks. The relations' opinionOffset; the
    /// situational social thoughts' OpinionOffsetOfGroup now, each def TryCreateSocialThought would create staged without
    /// RecalculateState; the live social memories of <paramref name="other"/> by group; the hediffs' opinionOfOthersFactor;
    /// HostileTo; the rapport a chat starts (InteractionDefOf.BuildRapport, Pawn_InteractionsTracker.AddInteractionThought);
    /// the ages of the rapport memories of other pawns; and, for a wild man, InteractionWorker_RecruitAttempt's taming chance
    /// in a prison cell (no Inspired_Taming).
    /// </summary>
    private static Obs.WardenOpinionRead OpinionOf(Pawn pawn, Pawn other, ThoughtDef rapport)
    {
        if (!other.RaceProps.Humanlike) throw new InvalidOperationException("warden is not humanlike: " + other.GetUniqueLoadID());
        var relations = 0;
        foreach (var relation in pawn.GetRelations(other)) relations += relation.opinionOffset;
        var factor = 1f;
        foreach (var hediff in pawn.health.hediffSet.hediffs)
            if (hediff.CurStage != null) factor *= hediff.CurStage.opinionOfOthersFactor;
        var situational = 0;
        var groups = new List<Obs.SocialMemoryGroupRead>();
        var others = new List<double>();
        var chat = 0d;
        // OpinionOf counts thoughts only for a humanlike pawn with a mood need.
        if (pawn.RaceProps.Humanlike && pawn.needs?.mood is { } mood)
        {
            foreach (var thought in SituationalSocial(pawn, other))
            {
                var offset = thought.OpinionOffset();
                situational += offset == 0f ? 0 : offset > 0f ? Mathf.Max(Mathf.RoundToInt(offset), 1) : Mathf.Min(Mathf.RoundToInt(offset), -1);
            }
            var social = mood.thoughts.memories.Memories.OfType<Thought_MemorySocial>().Where(m => m.OtherPawn() == other).ToList();
            foreach (var head in Heads(social))
                groups.Add(new(head.def.defName, head.def.stackedEffectMultiplier, head.def.lerpOpinionToZeroAfterDurationPct)
                {
                    Members = EquatableArray.From(social.Where(m => m.GroupsWith(head)).Select(m => MemoryOf(pawn, m))),
                });
            others.AddRange(mood.thoughts.memories.Memories.Where(m => m.def == rapport && m.otherPawn != other).Select(m => (double)m.age));
            if (ThoughtUtility.CanGetThought(pawn, rapport) && rapport.socialTargetDevelopmentalStageFilter.Has(other.DevelopmentalStage) && !ThoughtUtility.ThoughtNullified(pawn, rapport))
                chat = F(rapport.stages[0].baseOpinionOffset * other.GetStatValue(StatDefOf.SocialImpact), "rapport opinion offset");
        }
        return new(other.GetUniqueLoadID(), relations, situational, F(factor, "opinion of others factor"), pawn.HostileTo(other))
        {
            Memories = EquatableArray.From(groups), Rapport = chat, OtherRapport = EquatableArray.From(others.OrderBy(a => a)),
            TameChance = pawn.IsWildMan() ? TameChance(other, pawn) : 0,
        };
    }

    // A social memory as OpinionOffset reads it: the opinionOffset (0 while nullified) and its age, by its class. A subclass the
    // credit does not model is a broken read.
    private static Obs.SocialMemoryRead MemoryOf(Pawn pawn, Thought_MemorySocial memory)
    {
        var offset = ThoughtUtility.ThoughtNullified(pawn, memory.def) ? 0 : F(memory.opinionOffset, "social memory offset");
        return memory switch
        {
            Thought_MemorySocialCumulative => new Obs.SocialMemoryRead.Cumulative(offset, memory.age, memory.def.maxCumulatedOpinionOffset),
            _ when memory.GetType() == typeof(Thought_MemorySocial) => new Obs.SocialMemoryRead.Fading(offset, memory.age, memory.DurationTicks),
            _ => throw new InvalidOperationException("social memory class " + memory.GetType().Name + " of " + memory.def.defName + " is not modelled."),
        };
    }

    // InteractionWorker_RecruitAttempt.Interacted's taming chance of an uninspired tamer on a wild man held in a prison cell.
    private static double TameChance(Pawn tamer, Pawn wild)
    {
        var chance = tamer.GetStatValue(StatDefOf.TameAnimalChance) * WorkerCurve("TameChanceFactorCurve_Wildness").Evaluate(wild.GetStatValue(StatDefOf.Wildness))
            * WorkerFactor("WildmanPrisonerChanceFactor");
        if (tamer.relations?.DirectRelationExists(PawnRelationDefOf.Bond, wild) == true) chance *= WorkerFactor("TameChanceFactor_Bonded");
        if (tamer.Ideo != null && tamer.Ideo.IsVeneratedAnimal(wild)) chance *= WorkerFactor("VeneratedAnimalChanceFactor");
        return F(chance, "tame chance");
    }

    private static readonly System.Reflection.FieldInfo? SituationalStage = BridgeCommon.PrivateInstanceField(typeof(Thought_Situational), "curStageIndex");

    // SituationalThoughtHandler's active social thoughts of other, each built as TryCreateSocialThought builds it but staged directly
    // (curStageIndex = the state's stage) instead of by RecalculateState. A def whose worker throws is absent, as
    // TryCreateSocialThought logs and leaves it. Each is its own group (one thought per def).
    private static List<Thought_SituationalSocial> SituationalSocial(Pawn pawn, Pawn other)
    {
        var thoughts = new List<Thought_SituationalSocial>();
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
    /// an always-active one at its unattached MoodOffset, an <see cref="OwnStateWorkers"/> one at its worker's state now, an
    /// own-bedroom one (NativeNeedsForecast.RoomTraitWorkers) as its stage rows, any other named. The memories are its live memory groups.
    /// </summary>
    private static Obs.JoinerMoodRead? JoinerMoodOf(Pawn pawn)
    {
        if (pawn.needs?.mood is not { } mood || pawn.mindState?.mentalBreaker is not { } breaker) return null;
        var traits = new List<Obs.ThoughtOffsetRead>();
        var room = new List<ThoughtDef>();
        var situational = new List<string>();
        foreach (var def in DefDatabase<ThoughtDef>.AllDefsListForReading.Where(d => !d.IsMemory && !d.IsSocial && !d.requiredTraits.NullOrEmpty())
            .OrderBy(d => d.defName, StringComparer.Ordinal))
        {
            if (pawn.story?.traits is not { } has || has.IsThoughtDisallowed(def)) continue;
            if (!def.requiredTraits.Any(t => has.HasTrait(t) && (!def.RequiresSpecificTraitsDegree || def.requiredTraitsDegree == has.DegreeOfTrait(t)))) continue;
            if (NativeNeedsForecast.RoomTraitWorkers.Any(w => w.Worker == def.workerClass)) { room.Add(def); continue; }
            double offset;
            if (def.workerClass == typeof(ThoughtWorker_AlwaysActive)) offset = NativeNeedsForecast.StageOffset(pawn, def, 0);
            else if (OwnStateWorkers.Contains(def.workerClass))
            {
                // ThoughtWorker.CurrentState is pure: the gates (CanGetThought), the worker's own state and its invert.
                var state = def.Worker.CurrentState(pawn);
                offset = state.ActiveFor(def) ? NativeNeedsForecast.StageOffset(pawn, def, state.StageIndexFor(def)) : 0;
            }
            else { situational.Add(def.defName); continue; }
            if (!double.IsFinite(offset)) throw new InvalidOperationException(def.defName + " trait thought offset is not finite.");
            traits.Add(new(def.defName, offset));
        }
        var memories = new List<Obs.ThoughtOffsetRead>();
        foreach (var head in Heads(mood.thoughts.memories.Memories)) memories.Add(new(head.def.defName, F(mood.thoughts.MoodOffsetOfGroup(head), "memory group offset")));
        return new(F(pawn.health.hediffSet.OverrideMoodBase ?? mood.def.baseLevel, "mood base level"), Find.Storyteller.difficulty.colonistMoodOffset, breaker.BreakThresholdMinor)
        {
            Traits = EquatableArray.From(traits), RoomTraits = EquatableArray.From(NativeNeedsForecast.RoomTraitStages(pawn, room.Contains)),
            SituationalTraits = EquatableArray.From(situational), Memories = EquatableArray.From(memories),
        };
    }

    /// <summary>
    /// The trait thought workers whose state is the pawn's own body or the map's head count, which joining leaves as they are
    /// (#3899): ThoughtWorker_Pain (MasochistPain, the pawn's PainTotal), ThoughtWorker_HasAddedBodyPart (the Prosthophile and
    /// Prosthophobe thoughts, its added and implanted parts) and ThoughtWorker_Recluse (FreeColonistsAndPrisonersSpawnedCount,
    /// which counts a prisoner as it counts a colonist). Their worker classes are the typed route.
    /// </summary>
    private static readonly Type[] OwnStateWorkers = [typeof(ThoughtWorker_Pain), typeof(ThoughtWorker_HasAddedBodyPart), typeof(ThoughtWorker_Recluse)];
    private static double F(double value, string what) => double.IsFinite(value) ? value : throw new InvalidOperationException(what + " is not finite.");
}
