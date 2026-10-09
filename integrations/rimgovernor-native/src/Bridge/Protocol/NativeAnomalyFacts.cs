#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI.Group;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    // Anomaly facts: the pawn, building and thing row blocks (entity state,
    // held state, study state, containment strength). Everything is read from
    // the game objects, never from name lists, and is absent without Anomaly.
    // The static defs are rows of the definition catalog's def mirror.
    internal static class NativeAnomalyFacts
    {
        private static string? Id(string? value) => value != null && ProtoBoundary.IsIdentifier(value) ? value : null;
        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        private static Obs.ReadIssue Failed(string field, Exception ex) => new Obs.ReadIssue { Field = field,
            Unavailable = new Common.Unavailable { Reason = Common.UnavailableReason.ReadFailed, Detail = PlacementPreviewOperation.Diagnostic(ex.Message) } };

        // The study block of a pawn or building with a CompStudiable; null for
        // one without.
        internal static Obs.StudyState? Study(Thing thing)
        {
            if (!ModsConfig.AnomalyActive || (thing as ThingWithComps)?.GetComp<CompStudiable>() is not CompStudiable study) return null;
            var state = new Obs.StudyState { StudyEnabled = study.studyEnabled, Completed = study.Completed, StudyInteractions = study.studyInteractions,
                EverStudiable = study.EverStudiable(), CurrentlyStudiable = study.CurrentlyStudiable() };
            if (Finite(study.ProgressPercent)) state.ProgressPercent = study.ProgressPercent;
            if (Finite(study.studyPoints)) state.StudyPoints = study.studyPoints;
            if (Finite(study.anomalyKnowledgeGained)) state.AnomalyKnowledgeGained = study.anomalyKnowledgeGained;
            if (Id(study.KnowledgeCategory?.defName) is string category) state.KnowledgeCategory = category;
            if (Finite(study.AnomalyKnowledge)) state.AnomalyKnowledge = study.AnomalyKnowledge;
            return state;
        }

        // The pawn row block; null without Anomaly. A sub-read that throws
        // leaves its block absent and adds a ReadIssue.
        internal static Obs.PawnAnomaly? Pawn(Pawn pawn)
        {
            if (!ModsConfig.AnomalyActive) return null;
            var row = new Obs.PawnAnomaly();
            try
            {
                // Read everything before setting anything: a failed read
                // leaves the whole group absent next to its ReadIssue.
                var entity = pawn.IsEntity;
                var mutant = pawn.IsMutant;
                var shambler = pawn.IsShambler;
                var minimum = entity ? pawn.GetStatValue(StatDefOf.MinimumContainmentStrength) : float.NaN;
                row.Entity = entity;
                row.Mutant = mutant;
                row.Shambler = shambler;
                if (entity && Finite(minimum)) row.MinContainmentStrength = minimum;
            }
            catch (Exception ex) { row.Issues.Add(Failed("entity", ex)); }
            try { row.HiddenFromPlayer = InvisibilityUtility.IsHiddenFromPlayer(pawn); }
            catch (Exception ex) { row.Issues.Add(Failed("hidden_from_player", ex)); }
            // The invoker is the pawn whose role in its lord's psychic ritual
            // is the ritual def's invoker role.
            try
            {
                row.PsychicRitualInvoker = pawn.GetLord()?.LordJob is LordJob_PsychicRitual ritual
                    && ritual.def is PsychicRitualDef_InvocationCircle circle
                    && ritual.assignments.RoleForPawn(pawn, true) == circle.InvokerRole;
            }
            catch (Exception ex) { row.Issues.Add(Failed("psychic_ritual_invoker", ex)); }
            try
            {
                row.MeleeOnly = pawn.CurrentEffectiveVerb is Verb verb && verb.IsMeleeAttack
                    && !(pawn.abilities?.AllAbilitiesForReading.Any(a => a.def.ai_IsOffensive) ?? false);
            }
            catch (Exception ex) { row.Issues.Add(Failed("melee_only", ex)); }
            try
            {
                if (pawn.GetComp<CompHoldingPlatformTarget>() is CompHoldingPlatformTarget target)
                {
                    var held = new Obs.HeldState { Held = target.CurrentlyHeldOnPlatform, Mode = NativeEnums.ContainmentMode(target.containmentMode),
                        Escaping = target.isEscaping, ExtractBioferrite = target.extractBioferrite, CanBeCaptured = target.CanBeCaptured,
                        NeedsTend = pawn.health.HasHediffsNeedingTend(false), Bleeding = Finite(pawn.health.hediffSet.BleedRateTotal) && pawn.health.hediffSet.BleedRateTotal > 0 };
                    if (target.HeldPlatform is Building_HoldingPlatform platform)
                    {
                        held.Platform = NativeRef.Thing(platform);
                        held.HarvesterAttached = platform.HasAttachedBioferriteHarvester;
                    }
                    var perDay = CompProducesBioferrite.BioferritePerDay(pawn);
                    if (Finite(perDay)) held.BioferritePerDay = perDay;
                    row.Held = held;
                }
            }
            catch (Exception ex) { row.Issues.Add(Failed("held", ex)); }
            try { row.Study = Study(pawn); }
            catch (Exception ex) { row.Issues.Add(Failed("study", ex)); }
            try { row.Creepjoiner = CreepJoiner(pawn); }
            catch (Exception ex) { row.Issues.Add(Failed("creepjoiner", ex)); }
            return row;
        }

        // The tracker keeps triggeredDownside private: true once the game has
        // fired the downside, which a player sees as its letter or effect.
        // The hidden downside def is never read. A game version without the
        // field fails the read naming it.
        private static class CreepJoinerFields
        {
            private static readonly AccessTools.FieldRef<Pawn_CreepJoinerTracker, bool> Triggered = AccessTools.FieldRefAccess<Pawn_CreepJoinerTracker, bool>("triggeredDownside");
            internal static bool DownsideTriggered(Pawn_CreepJoinerTracker tracker) => Triggered(tracker);
        }

        // The creepjoiner block; null for a pawn with no tracker.
        private static Obs.CreepJoinerState? CreepJoiner(Pawn pawn)
        {
            if (pawn.creepjoiner is not Pawn_CreepJoinerTracker tracker) return null;
            var state = new Obs.CreepJoinerState { DownsideTriggered = CreepJoinerFields.DownsideTriggered(tracker) };
            if (Id(tracker.form?.defName) is string form) state.Form = form;
            if (Id(tracker.benefit?.defName) is string benefit) state.Benefit = benefit;
            return state;
        }

        // The doors the game counts for a holder's room: the Building_Door
        // things among the room's contained and adjacent things, which is
        // what StatWorker_ContainmentStrength.AnyDoorForcedOpen walks. None
        // for a holder in no room or a room that is psychologically outdoors
        // (the game reads no door there either).
        private static IEnumerable<Obs.AnomalyDoor> Doors(Thing holder)
        {
            var room = holder.GetRoom();
            if (room == null || room.PsychologicallyOutdoors) yield break;
            foreach (var door in room.ContainedAndAdjacentThings.OfType<Building_Door>().OrderBy(d => d.Position.z).ThenBy(d => d.Position.x))
                yield return new Obs.AnomalyDoor { Cell = new Common.Cell { X = door.Position.x, Z = door.Position.z }, Open = door.Open, HoldOpen = door.HoldOpen,
                    ContainmentBreached = door.ContainmentBreached, BlockedOpen = door.BlockedOpenMomentary };
        }

        // The building row block; null without Anomaly or when the building
        // is neither an entity holder nor studiable.
        internal static Obs.AnomalyBuilding? Building(Thing thing)
        {
            if (!ModsConfig.AnomalyActive) return null;
            var row = new Obs.AnomalyBuilding();
            try
            {
                if ((thing as ThingWithComps)?.GetComp<CompEntityHolder>() is CompEntityHolder holder)
                {
                    var state = new Obs.EntityHolderState { Available = holder.Available };
                    var strength = holder.ContainmentStrength;
                    if (Finite(strength)) state.ContainmentStrength = strength;
                    if (holder.HeldPawn is Pawn held) state.HeldPawn = NativeRef.Thing(held);
                    row.Holder = state;
                    try { state.Doors.Add(Doors(thing)); }
                    catch (Exception ex) { state.Doors.Clear(); row.Issues.Add(Failed("doors", ex)); }
                }
            }
            catch (Exception ex) { row.Issues.Add(Failed("holder", ex)); }
            try { row.Study = Study(thing); }
            catch (Exception ex) { row.Issues.Add(Failed("study", ex)); }
            return row.Holder == null && row.Study == null && row.Issues.Count == 0 ? null : row;
        }
    }
}
