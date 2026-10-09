#nullable enable
using System;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // The blight responder's native half: the CutPlant designation on
    // one exact blighted plant (the census is the cell mirror's plant state;
    // DesignateIntent with THING_DESIGNATION_CUT_PLANT). The
    // designation is the whole write; ordinary plant-cutting work cuts the
    // plant afterwards, and the census emptying is what settles the concern.
    internal static class NativeCutPlant
    {
        internal const string Kind = "Cut plant";
        internal const string DesignationDef = "CutPlant";

        // Census membership: a blighted plant on colony ground (a growing
        // zone or the home area). Blight on wild plants outside both is the
        // storyteller's, not the colony's, and is left alone.
        internal static bool InColony(Plant plant, Map map) => map.zoneManager.ZoneAt(plant.Position) is Zone_Growing || map.areaManager.Home[plant.Position];
        internal static bool Eligible(Plant plant) => !plant.Destroyed && plant.Spawned && ProtoBoundary.IsLoaded(plant.Map)
            && plant.Blighted && !plant.Position.Fogged(plant.Map) && InColony(plant, plant.Map);
        internal static bool Designated(Plant plant) => plant.Map.designationManager.DesignationOn(plant, DesignationDefOf.CutPlant) != null
            || plant.Map.designationManager.DesignationOn(plant, DesignationDefOf.HarvestPlant) != null;

        // Cutter is the same colonist rule plant acquisition applies: someone
        // with plant cutting enabled must be able to do the work now.
        private static bool Cutter(Pawn p, Plant plant) => p.workSettings?.Initialized == true
            && p.workSettings.GetPriority(WorkTypeDefOf.PlantCutting) > 0 && !p.WorkTypeIsDisabled(WorkTypeDefOf.PlantCutting)
            && !p.Downed && !p.Drafted && !p.InMentalState && !plant.IsForbidden(p)
            && p.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)
            && p.CanReach(plant, PathEndMode.Touch, Danger.None);

        // The apply-time precondition list for CutPlant (action-contracts.md),
        // one rule at a time so a refusal names the fact that moved; a plant
        // already designated applies again.
        private static ApplyPreconditions Rules(Operations.DesignateIntent intent, Map map, out Plant? plant)
        {
            Plant? found = null;
            var rules = new ApplyPreconditions(Kind)
                .Require(() => intent.HasThingId && ProtoBoundary.IsIdentifier(intent.ThingId), "CutPlant requires an exact blighted plant")
                .Present(() => (found = RefIndex.Thing<Plant>(map, intent.ThingId)) != null && !found.Destroyed && found.Spawned && ProtoBoundary.IsLoaded(found.Map), "the exact plant is no longer spawned on this map")
                .Require(() => found!.Blighted, "the plant is not blighted")
                .Require(() => !found!.Position.Fogged(map), "the plant's cell is fogged")
                .Require(() => InColony(found!, map), "the plant stands outside the colony's growing zones and home area")
                .Require(() => !found!.IsForbidden(Faction.OfPlayer), "the plant is forbidden")
                .Require(() => Designated(found!) || new Designator_PlantsCut().CanDesignateThing(found!).Accepted, "the native cut designator refuses the plant")
                .Require(() => map.mapPawns.FreeColonistsSpawned.Any(p => Cutter(p, found!)), "no free colonist with plant cutting enabled can reach the plant");
            plant = found;
            return rules;
        }

        internal static Common.Failure? Validate(Operations.DesignateIntent intent, Common.ObservationContext context)
        {
            var rules = Rules(intent, ProtoBoundary.LoadedMap(context), out _);
            return rules.Holds ? null : rules.Failure();
        }

        internal static Receipts.EffectEvidence Apply(Operations.DesignateIntent intent, Common.ObservationContext context)
        {
            var rules = Rules(intent, ProtoBoundary.LoadedMap(context), out var plant);
            if (!rules.Holds) throw new InvalidOperationException("Cut plant prerequisites changed before apply: " + rules.Reason);
            if (!Designated(plant!)) new Designator_PlantsCut().DesignateThing(plant);
            if (!Designated(plant!)) throw new InvalidOperationException("Native CutPlant designation was not observed.");
            return new Receipts.EffectEvidence { Designation = new Receipts.DesignationEffect { ThingId = plant!.GetUniqueLoadID(), DesignationDef = DesignationDef,
                Present = true, ResourceDef = plant.def.defName, Cell = new Common.Cell { X = plant.Position.x, Z = plant.Position.z } } };
        }
    }

    // DesignateIntent dispatches by designation: CUT_PLANT to the blight
    // responder, STRIP to NativeStrip, ALLOW/FORBID to the supply census's item rules.
    internal sealed class DesignateActionHandler : IActionHandler
    {
        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context)
        {
            var intent = action.Designate;
            if (NativeDesignate.Generic(intent)) return NativeDesignate.Validate(intent!, context);
            if (intent != null && intent.HasDesignation && intent.Designation == Operations.ThingDesignation.CutPlant) return NativeCutPlant.Validate(intent, context);
            if (intent != null && intent.HasDesignation && intent.Designation == Operations.ThingDesignation.Strip) return NativeStrip.Validate(intent, context);
            if (NativeSupplyAllow.Wants(intent)) return NativeSupplyAllow.Validate(intent!, context);
            return ProtoBoundary.Fail(Common.FailureCode.Unsupported, "Designate supports only CutPlant, Strip, Allow and Forbid.");
        }
        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context) =>
            NativeDesignate.Generic(action.Designate) ? NativeDesignate.Apply(action.Designate, context)
            : action.Designate.Designation == Operations.ThingDesignation.CutPlant ? NativeCutPlant.Apply(action.Designate, context)
            : action.Designate.Designation == Operations.ThingDesignation.Strip ? NativeStrip.Apply(action.Designate, context)
            : NativeSupplyAllow.Apply(action.Designate, context);
    }
}
