using System;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimGovernor.Native
{
    // The blight responder's native half: the CutPlant designation on
    // one exact plant (the brain picks the blighted ones from the cell mirror's plant state;
    // DesignateIntent with THING_DESIGNATION_CUT_PLANT). The
    // designation is the whole write; ordinary plant-cutting work cuts the
    // plant afterwards, and the census emptying is what settles the concern.
    internal static class NativeCutPlant
    {
        internal const string Kind = "Cut plant";
        internal const string DesignationDef = "CutPlant";

        private static bool Designated(Plant plant) => plant.Map.designationManager.DesignationOn(plant, DesignationDefOf.CutPlant) != null
            || plant.Map.designationManager.DesignationOn(plant, DesignationDefOf.HarvestPlant) != null;

        // The apply-time precondition list for CutPlant (action-contracts.md),
        // one rule at a time so a refusal names the fact that moved; a plant
        // already designated applies again.
        private static ApplyPreconditions Rules(RimGovernor.Brain.GameOrders.Operations.DesignateIntent intent, Map map, out Plant? plant)
        {
            Plant? found = null;
            var rules = new ApplyPreconditions(Kind)
                .Require(() => intent.ThingId is not null && GameThread.IsIdentifier(intent.ThingId), "CutPlant requires an exact plant")
                .Present(() => (found = RefIndex.Thing<Plant>(map, intent.ThingId)) != null && !found.Destroyed && found.Spawned && GameThread.IsLoaded(found.Map), "the exact plant is no longer spawned on this map")
                .Require(() => found != null && (Designated(found) || new Designator_PlantsCut().CanDesignateThing(found).Accepted), "the native cut designator refuses the plant");
            plant = found;
            return rules;
        }

        internal static RimGovernor.Brain.GameFacts.Common.Failure? Validate(RimGovernor.Brain.GameOrders.Operations.DesignateIntent intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var rules = Rules(intent, GameThread.LoadedMap(context), out _);
            return rules.Holds ? null : rules.Failure();
        }

        internal static RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Apply(RimGovernor.Brain.GameOrders.Operations.DesignateIntent intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var rules = Rules(intent, GameThread.LoadedMap(context), out var plant);
            if (!rules.Holds || plant == null) throw new InvalidOperationException("Cut plant prerequisites changed before apply: " + rules.Reason);
            if (!Designated(plant)) new Designator_PlantsCut().DesignateThing(plant);
            if (!Designated(plant)) throw new InvalidOperationException("Native CutPlant designation was not observed.");
            return new RimGovernor.Brain.GameFacts.Receipts.EffectEvidence { Effect = new RimGovernor.Brain.GameFacts.Receipts.DesignationEffect { ThingId = plant.GetUniqueLoadID(), DesignationDef = DesignationDef,
                Present = true } };
        }
    }

    // DesignateIntent dispatches by designation: CUT_PLANT to the blight
    // responder, STRIP to NativeStrip, ALLOW/FORBID to the supply census's item rules.
    internal sealed class DesignateActionHandler : IActionHandler
    {
        public RimGovernor.Brain.GameFacts.Common.Failure? Validate(RimGovernor.Brain.GameOrders.Operations.Action action, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var intent = action.Intent?.Designate;
            if (NativeDesignate.Generic(intent)) return NativeDesignate.Validate((intent ?? throw new System.InvalidOperationException("Required native value is unavailable: intent.")), context);
            if (intent != null && intent.Designation == RimGovernor.Brain.GameOrders.Operations.ThingDesignation.CutPlant) return NativeCutPlant.Validate(intent, context);
            if (intent != null && intent.Designation == RimGovernor.Brain.GameOrders.Operations.ThingDesignation.Strip) return NativeStrip.Validate(intent, context);
            if (NativeSupplyAllow.Wants(intent)) return NativeSupplyAllow.Validate((intent ?? throw new System.InvalidOperationException("Required native value is unavailable: intent.")), context);
            return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.Unsupported, "Designate supports only CutPlant, Strip, Allow and Forbid.");
        }
        public RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Apply(RimGovernor.Brain.GameOrders.Operations.Action action, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var intent = action.Intent?.Designate ?? throw new InvalidOperationException("Designate intent is missing");
            return NativeDesignate.Generic(intent) ? NativeDesignate.Apply(intent, context)
                : intent.Designation == RimGovernor.Brain.GameOrders.Operations.ThingDesignation.CutPlant ? NativeCutPlant.Apply(intent, context)
                : intent.Designation == RimGovernor.Brain.GameOrders.Operations.ThingDesignation.Strip ? NativeStrip.Apply(intent, context)
                : NativeSupplyAllow.Apply(intent, context);
        }    }
}
