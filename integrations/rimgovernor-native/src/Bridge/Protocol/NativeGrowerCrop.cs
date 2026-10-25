using System;
using System.IO;
using System.Linq;
using System.Text;
using RimWorld;
using Verse;

namespace RimGovernor.Native
{
    // BuildingPatchIntent's plant_def arm: the crop one exact
    // Building_PlantGrower sows (SetPlantDefToGrow), under the game's own
    // set-plant gizmo rules -- sowable, carrying the grower's sow tag
    // (PlantUtility.CanSowOnGrower) and with its sow research finished.
    internal static class NativeGrowerCrop
    {


        internal static bool Eligible(Thing thing) => thing is Building_PlantGrower grower && !grower.Destroyed && grower.Spawned
            && GameThread.IsLoaded(grower.Map);

        internal static bool Sowable(ThingDef? plant, Building_PlantGrower grower) => plant?.plant != null && plant.plant.Sowable
            && PlantUtility.CanSowOnGrower(plant, grower)
            && (plant.plant.sowResearchPrerequisites == null || plant.plant.sowResearchPrerequisites.All(r => r.IsFinished));

        // Control carries the grower's current crop on the building listing row.
        internal static RimGovernor.Brain.GameFacts.Observations.BuildingControl.Grower Control(Building_PlantGrower grower) =>
            new(grower.GetPlantDefToGrow()?.defName);

        private static RimGovernor.Brain.GameFacts.Common.Failure? Resolve(RimGovernor.Brain.GameOrders.Operations.BuildingPatchIntent intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context, out Building_PlantGrower? grower, out ThingDef? plant)
        {
            grower = null; plant = null;
            if (!GameThread.IsIdentifier(intent.ThingId) || string.IsNullOrEmpty(intent.Change?.PlantDef)) return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "A grower patch requires an exact grower and a plant definition.");
            var thing = RefIndex.Thing(GameThread.LoadedMap(context), intent.ThingId);
            if (thing == null || !Eligible(thing)) return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.NotFound, "Exact plant grower is unavailable.");
            grower = (Building_PlantGrower)thing;
            plant = DefDatabase<ThingDef>.GetNamedSilentFail(intent.Change?.PlantDef);
            if (!Sowable(plant, grower)) return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Plant definition cannot be sown on this grower: it must be sowable, carry the grower's sow tag and have its sow research finished.");
            return null;
        }

        private static RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Evidence() =>
            new RimGovernor.Brain.GameFacts.Receipts.EffectEvidence { Effect = new RimGovernor.Brain.GameFacts.Receipts.SettingsEffect {
                Fields = [new RimGovernor.Brain.GameFacts.Receipts.FieldResult { Field = RimGovernor.Brain.GameFacts.Receipts.SettingsField.GrowerCrop, Outcome = RimGovernor.Brain.GameFacts.Receipts.FieldOutcome.Applied }] } };

        internal static RimGovernor.Brain.GameFacts.Common.Failure? Validate(RimGovernor.Brain.GameOrders.Operations.BuildingPatchIntent intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context) => Resolve(intent, context, out _, out _);

        internal static RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Apply(RimGovernor.Brain.GameOrders.Operations.BuildingPatchIntent intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var failure = Resolve(intent, context, out var grower, out var plant);
            if (failure != null) throw new InvalidOperationException(failure.Detail);
            (grower ?? throw new System.InvalidOperationException("Required native value is unavailable: grower.")).SetPlantDefToGrow((plant ?? throw new System.InvalidOperationException("Required native value is unavailable: plant.")));
            if (grower.GetPlantDefToGrow()?.defName != intent.Change?.PlantDef) throw new InvalidOperationException("Native grower crop requires readback.");
            return Evidence();
        }
    }
}
