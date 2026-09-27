#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

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

        internal static string Token(Common.Identity identity, Building_PlantGrower grower)
        {
            using (var bytes = new MemoryStream())
            {
                using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
                {
                    writer.Write(identity.ColonyId); writer.Write(identity.LoadToken); writer.Write(identity.MapId);
                    writer.Write(grower.GetUniqueLoadID()); writer.Write(grower.GetPlantDefToGrow()?.defName ?? string.Empty);
                }
                using (var hash = SHA256.Create())
                    return "crop-" + BitConverter.ToString(hash.ComputeHash(bytes.ToArray())).Replace("-", "").ToLowerInvariant();
            }
        }

        internal static Obs.SnapshotRef? Snapshot(Thing thing, Common.ObservationContext context)
        {
            if (!Eligible(thing)) return null;
            return new Obs.SnapshotRef { Context = context.Clone(), EntityId = thing.GetUniqueLoadID(),
                Token = Token(context.Identity, (Building_PlantGrower)thing) };
        }

        // Settings carries the grower's current crop beside its CAS snapshot on
        // the building listing row.
        internal static Obs.BuildingSettings Settings(Building_PlantGrower grower, Common.ObservationContext context)
        {
            var settings = new Obs.BuildingSettings { Snapshot = Snapshot(grower, context) };
            var crop = grower.GetPlantDefToGrow();
            if (crop != null) settings.CropDefName = crop.defName;
            return settings;
        }

        private static Common.Failure? Resolve(Operations.BuildingPatchIntent intent, Common.ObservationContext context, out Building_PlantGrower? grower, out ThingDef? plant)
        {
            grower = null; plant = null;
            if (!ProtoBoundary.IsIdentifier(intent.ThingId) || string.IsNullOrEmpty(intent.PlantDef)) return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "A grower patch requires an exact grower and a plant definition.");
            var thing = ProtoBoundary.LoadedMap(context).listerThings.AllThings.SingleOrDefault(t => t.GetUniqueLoadID() == intent.ThingId);
            if (thing == null || !Eligible(thing)) return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact plant grower is unavailable.");
            grower = (Building_PlantGrower)thing;
            plant = DefDatabase<ThingDef>.GetNamedSilentFail(intent.PlantDef);
            if (!Sowable(plant, grower)) return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Plant definition cannot be sown on this grower: it must be sowable, carry the grower's sow tag and have its sow research finished.");
            return null;
        }

        private static Receipts.EffectEvidence Evidence(string id, string after) =>
            new Receipts.EffectEvidence { Settings = new Receipts.SettingsEffect {
                Snapshot = new Receipts.SnapshotEvidence { EntityId = id, AfterToken = after },
                Fields = { new Receipts.FieldResult { Field = Receipts.SettingsField.GrowerCrop, Outcome = Receipts.FieldOutcome.Applied } } } };

        internal static Common.Failure? Validate(Operations.BuildingPatchIntent intent, Common.ObservationContext context) => Resolve(intent, context, out _, out _);

        internal static Receipts.EffectEvidence Apply(Operations.BuildingPatchIntent intent, Common.ObservationContext context)
        {
            var failure = Resolve(intent, context, out var grower, out var plant);
            if (failure != null) throw new InvalidOperationException(failure.Detail);
            grower!.SetPlantDefToGrow(plant!);
            if (grower.GetPlantDefToGrow()?.defName != intent.PlantDef) throw new InvalidOperationException("Native grower crop requires readback.");
            return Evidence(grower.GetUniqueLoadID(), Snapshot(grower, context)!.Token);
        }
    }
}
