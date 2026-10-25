using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimGovernor.Brain.Domain;
using RimWorld;
using Verse;
using Verse.AI.Group;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools
{
    // Anomaly facts: the pawn and building row blocks (entity state, held
    // state, study state, containment strength). Everything is read from the
    // game objects, never from name lists; the pawn block is inactive and the
    // building block absent without Anomaly. A failed read throws and fails
    // the row's read. The static defs are rows of the definition catalog's
    // def mirror.
    internal static class NativeAnomalyFacts
    {
        private static double Finite(double value, string what) =>
            !double.IsNaN(value) && !double.IsInfinity(value) ? value : throw new InvalidOperationException("a non-finite " + what);

        // The study block of a pawn or building with a CompStudiable; null for
        // one without.
        internal static Obs.StudyState? Study(Thing thing)
        {
            if (!ModsConfig.AnomalyActive || (thing as ThingWithComps)?.GetComp<CompStudiable>() is not CompStudiable study) return null;
            return new Obs.StudyState
            {
                CurrentlyStudiable = study.CurrentlyStudiable(),
                Yield = new Obs.StudyYieldState { Knowledge = Finite(study.AnomalyKnowledge, "study knowledge"), Category = study.KnowledgeCategory?.defName,
                    FrequencyTicks = study.Props.frequencyTicks },
            };
        }

        // The pawn row block; inactive without Anomaly.
        internal static Obs.PawnAnomalyState State(Pawn pawn) =>
            ModsConfig.AnomalyActive ? new Obs.PawnAnomalyState.Active(Pawn(pawn)) : new Obs.PawnAnomalyState.Inactive();

        private static Obs.PawnAnomaly Pawn(Pawn pawn) => new Obs.PawnAnomaly
        {
            Entity = pawn.IsEntity,
            Mutant = pawn.IsMutant,
            MinContainmentStrength = Finite(pawn.GetStatValue(StatDefOf.MinimumContainmentStrength), "minimum containment strength"),
            HiddenFromPlayer = InvisibilityUtility.IsHiddenFromPlayer(pawn),
            // The invoker is the pawn whose role in its lord's psychic ritual
            // is the ritual def's invoker role.
            PsychicRitualInvoker = pawn.GetLord()?.LordJob is LordJob_PsychicRitual ritual
                && ritual.def is PsychicRitualDef_InvocationCircle circle
                && ritual.assignments.RoleForPawn(pawn, true) == circle.InvokerRole,
            MeleeOnly = pawn.CurrentEffectiveVerb is Verb verb && verb.IsMeleeAttack
                && !(pawn.abilities?.AllAbilitiesForReading.Any(a => a.def.ai_IsOffensive) ?? false),
            Held = Held(pawn),
            Study = Study(pawn),
            Creepjoiner = CreepJoiner(pawn),
        };

        // The holding-platform target block; null for a pawn without the comp.
        private static Obs.HeldState? Held(Pawn pawn)
        {
            if (pawn.GetComp<CompHoldingPlatformTarget>() is not CompHoldingPlatformTarget target) return null;
            return new Obs.HeldState
            {
                Held = target.CurrentlyHeldOnPlatform,
                Mode = NativeEnums.ContainmentMode(target.containmentMode),
                Escaping = target.isEscaping,
                ExtractBioferrite = target.extractBioferrite,
                CanBeCaptured = target.CanBeCaptured,
                NeedsTend = pawn.health.HasHediffsNeedingTend(false),
                Bleeding = Finite(pawn.health.hediffSet.BleedRateTotal, "bleed rate") > 0,
                HarvesterAttached = target.HeldPlatform is Building_HoldingPlatform platform && platform.HasAttachedBioferriteHarvester,
                BioferritePerDay = Finite(CompProducesBioferrite.BioferritePerDay(pawn), "bioferrite per day"),
                Escape = Escape(pawn, target),
            };
        }

        // The inputs of ContainmentUtility.InitiateEscapeMtbDays other than the
        // platform's strength, each as that method reads it (#3811); the
        // brain applies the captivity and strength curves. A non-finite
        // factor throws.
        private static Obs.EntityEscapeState Escape(Pawn pawn, CompHoldingPlatformTarget target)
        {
            var moving = 1f / UnityEngine.Mathf.Clamp(pawn.health.capacities.GetLevel(PawnCapacityDefOf.Moving), 0.01f, 1f);
            var cold = ContainmentUtility.TryGetColdContainmentBonus(pawn, out var modifier) ? modifier : 1f;
            return new Obs.EntityEscapeState
            {
                BaseMtbDays = Finite(target.Props.baseEscapeIntervalMtbDays, "escape interval"),
                MovingFactor = Finite(moving, "moving factor"),
                DaysInCaptivity = pawn.mindState.entityTicksInCaptivity / (double)GenDate.TicksPerDay,
                ColdFactor = Finite(cold, "cold factor"),
                CombatPower = Finite(pawn.kindDef.combatPower, "combat power"),
                CanEscape = ContainmentUtility.CanParticipateInEscape(pawn),
            };
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

        // The creepjoiner block; null for a pawn with no tracker. joinedTick is public: the tick the pawn joined (#3844); the
        // combat power is the kind's, the points of the fight an aggressive downside starts.
        private static Obs.CreepJoinerState? CreepJoiner(Pawn pawn) =>
            pawn.creepjoiner is Pawn_CreepJoinerTracker tracker ? new Obs.CreepJoinerState { DownsideTriggered = CreepJoinerFields.DownsideTriggered(tracker), JoinedTick = tracker.joinedTick,
                CombatPower = Finite(pawn.kindDef.combatPower, "creepjoiner combat power") } : null;

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
                yield return new Obs.AnomalyDoor { Cell = new Cell(door.Position.x, door.Position.z), HoldOpen = door.HoldOpen,
                    ContainmentBreached = door.ContainmentBreached, BlockedOpen = door.BlockedOpenMomentary };
        }

        // The building row block; null without Anomaly or when the building
        // is no entity holder.
        internal static Obs.AnomalyBuilding? Building(Thing thing)
        {
            if (!ModsConfig.AnomalyActive || (thing as ThingWithComps)?.GetComp<CompEntityHolder>() is not CompEntityHolder holder) return null;
            return new Obs.AnomalyBuilding
            {
                Holder = new Obs.EntityHolderState
                {
                    ContainmentStrength = Finite(holder.ContainmentStrength, "containment strength"),
                    Available = holder.Available,
                    HeldPawn = holder.HeldPawn is Pawn held ? NativeRef.Thing(held) : null,
                    Doors = [.. Doors(thing)],
                },
            };
        }
    }
}
