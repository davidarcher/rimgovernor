#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text;
using RimWorld;
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;
using Obs = RimGovernor.Contracts.Wire.Observations;
using Operations = RimGovernor.Contracts.Wire.Operations;
using Receipts = RimGovernor.Contracts.Wire.Receipts;

namespace HomeBridge.BridgeTools
{
    // BuildingPatchIntent's plant_def arm: the crop one exact
    // Building_PlantGrower sows (SetPlantDefToGrow), under the game's own
    // set-plant gizmo rules -- sowable, carrying the grower's sow tag
    // (PlantUtility.CanSowOnGrower) and with its sow research finished.
    internal static class NativeGrowerCrop
    {


        internal static bool Eligible(Thing thing) => thing is Building_PlantGrower grower && !grower.Destroyed && grower.Spawned
            && ProtoBoundary.IsLoaded(grower.Map);

        internal static bool Sowable(ThingDef? plant, Building_PlantGrower grower) => plant?.plant != null && plant.plant.Sowable
            && PlantUtility.CanSowOnGrower(plant, grower)
            && (plant.plant.sowResearchPrerequisites == null || plant.plant.sowResearchPrerequisites.All(r => r.IsFinished));

        // Control carries the grower's current crop on the building listing row.
        internal static Obs.BuildingControl.Grower Control(Building_PlantGrower grower) =>
            new(grower.GetPlantDefToGrow()?.defName);

        private static Common.Failure? Resolve(Operations.BuildingPatchIntent intent, Common.ObservationContext context, out Building_PlantGrower? grower, out ThingDef? plant)
        {
            grower = null; plant = null;
            if (!ProtoBoundary.IsIdentifier(intent.ThingId) || string.IsNullOrEmpty(intent.Change?.PlantDef)) return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "A grower patch requires an exact grower and a plant definition.");
            var thing = RefIndex.Thing(ProtoBoundary.LoadedMap(context), intent.ThingId);
            if (thing == null || !Eligible(thing)) return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact plant grower is unavailable.");
            grower = (Building_PlantGrower)thing;
            plant = DefDatabase<ThingDef>.GetNamedSilentFail(intent.Change?.PlantDef);
            if (!Sowable(plant, grower)) return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Plant definition cannot be sown on this grower: it must be sowable, carry the grower's sow tag and have its sow research finished.");
            return null;
        }

        private static Receipts.EffectEvidence Evidence() =>
            new Receipts.EffectEvidence { Effect = new Receipts.SettingsEffect {
                Fields = [new Receipts.FieldResult { Field = Receipts.SettingsField.GrowerCrop, Outcome = Receipts.FieldOutcome.Applied }] } };

        internal static Common.Failure? Validate(Operations.BuildingPatchIntent intent, Common.ObservationContext context) => Resolve(intent, context, out _, out _);

        internal static Receipts.EffectEvidence Apply(Operations.BuildingPatchIntent intent, Common.ObservationContext context)
        {
            var failure = Resolve(intent, context, out var grower, out var plant);
            if (failure != null) throw new InvalidOperationException(failure.Detail);
            grower!.SetPlantDefToGrow(plant!);
            if (grower.GetPlantDefToGrow()?.defName != intent.Change?.PlantDef) throw new InvalidOperationException("Native grower crop requires readback.");
            return Evidence();
        }
    }
}
