#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using RimWorld;
using Verse;
using Common = RimGovernor.Contracts.Wire.Common;
using Obs = RimGovernor.Contracts.Wire.Observations;
using Operations = RimGovernor.Contracts.Wire.Operations;
using Receipts = RimGovernor.Contracts.Wire.Receipts;

namespace HomeBridge.BridgeTools
{
    // BuildingPatchIntent's target_temperature arm: RimWorld.CompTempControl
    // .targetTemperature, a public settable float bounded by the Go planner
    // (domain.NewBuildingTemperature). The other arms are NativeBedUse,
    // NativeGrowerCrop and NativeClaimBuilding; BuildingPatchActionHandler
    // below routes them.
    internal static class NativeBuildingTemperature
    {



        internal static bool Eligible(Thing thing) => thing != null && !thing.Destroyed
            && thing.Spawned && ProtoBoundary.IsLoaded(thing.Map) && thing.TryGetComp<CompTempControl>() != null;

        internal static string Token(Common.Identity identity, string id, float target)
        {
            using (var bytes = new MemoryStream())
            {
                using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
                {
                    writer.Write((identity.ColonyId ?? "")); writer.Write((identity.LoadToken ?? "")); writer.Write(identity.MapId.GetValueOrDefault());
                    writer.Write(id); writer.Write(target);
                }
                using (var hash = SHA256.Create())
                    return "temp-" + BitConverter.ToString(hash.ComputeHash(bytes.ToArray())).Replace("-", "").ToLowerInvariant();
            }
        }

        internal static Obs.SnapshotRef? Snapshot(Thing thing, Common.ObservationContext context)
        {
            if (!Eligible(thing)) return null;
            var comp = thing.TryGetComp<CompTempControl>();
            return new Obs.SnapshotRef { Context = context, EntityId = thing.GetUniqueLoadID(),
                Token = Token(context.Identity ?? new Common.Identity(), thing.GetUniqueLoadID(), comp.targetTemperature) };
        }

        private static Common.Failure? Resolve(Operations.BuildingPatchIntent intent, Common.ObservationContext context, out Thing? thing)
        {
            thing = null;
            if (!ProtoBoundary.IsIdentifier(intent.ThingId) || intent.Change?.TargetTemperature is not { } target || float.IsNaN(target) || float.IsInfinity(target))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "A temperature patch requires an exact building and a finite target (domain.NewBuildingTemperature bounds the range).");
            thing = RefIndex.Thing(ProtoBoundary.LoadedMap(context), intent.ThingId);
            if (thing == null || !Eligible(thing)) return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact building with CompTempControl is unavailable.");
            return null;
        }

        private static Receipts.EffectEvidence Evidence(string id, string after) =>
            new Receipts.EffectEvidence { Effect = new Receipts.SettingsEffect {
                Snapshot = new Receipts.SnapshotEvidence { EntityId = id, AfterToken = after },
                Fields = [new Receipts.FieldResult { Field = Receipts.SettingsField.Temperature, Outcome = Receipts.FieldOutcome.Applied }] } };

        internal static Common.Failure? Validate(Operations.BuildingPatchIntent intent, Common.ObservationContext context) => Resolve(intent, context, out _);

        internal static Receipts.EffectEvidence Apply(Operations.BuildingPatchIntent intent, Common.ObservationContext context)
        {
            var failure = Resolve(intent, context, out var thing);
            if (failure != null) throw new InvalidOperationException(failure.Detail);
            var comp = thing!.TryGetComp<CompTempControl>();
            var wanted = intent.Change?.TargetTemperature ?? throw new InvalidOperationException("A temperature patch requires a target temperature.");
            comp.targetTemperature = wanted;
            if (Math.Abs(comp.targetTemperature - wanted) >= 0.001f) throw new InvalidOperationException("Native building temperature requires readback.");
            return Evidence(thing!.GetUniqueLoadID(), (Snapshot(thing!, context)!.Token ?? ""));
        }
    }

    // BuildingPatchIntent on Actions/Apply: one settings change on one
    // exact building, routed by the change arm. Native checks the building
    // and the game's own rules live; no snapshot token.
    internal sealed class BuildingPatchActionHandler : IActionHandler
    {
        private static Common.Failure Missing() => ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "A building patch requires exactly one change.");

        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context)
        {
            var intent = action.Intent?.BuildingPatch;
            if (intent == null) return Missing();
            switch (intent.Change)
            {
                case Operations.BuildingPatchIntentChange.TargetTemperatureCase: return NativeBuildingTemperature.Validate(intent, context);
                case Operations.BuildingPatchIntentChange.MedicalCase:
                case Operations.BuildingPatchIntentChange.ForPrisonersCase:
                case Operations.BuildingPatchIntentChange.ForSlavesCase: return NativeBedUse.Validate(intent, context);
                case Operations.BuildingPatchIntentChange.PlantDefCase: return NativeGrowerCrop.Validate(intent, context);
                case Operations.BuildingPatchIntentChange.ClaimCase: return NativeClaimBuilding.Validate(intent, context);
                case Operations.BuildingPatchIntentChange.AutoRefuelCase: return NativeAutoRefuel.Validate(intent, context);
                default: return Missing();
            }
        }

        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context)
        {
            var intent = action.Intent?.BuildingPatch;
            if (intent == null) throw new InvalidOperationException(Missing().Detail);
            switch (intent.Change)
            {
                case Operations.BuildingPatchIntentChange.TargetTemperatureCase: return NativeBuildingTemperature.Apply(intent, context);
                case Operations.BuildingPatchIntentChange.MedicalCase:
                case Operations.BuildingPatchIntentChange.ForPrisonersCase:
                case Operations.BuildingPatchIntentChange.ForSlavesCase: return NativeBedUse.Apply(intent, context);
                case Operations.BuildingPatchIntentChange.PlantDefCase: return NativeGrowerCrop.Apply(intent, context);
                case Operations.BuildingPatchIntentChange.ClaimCase: return NativeClaimBuilding.Apply(intent, context);
                case Operations.BuildingPatchIntentChange.AutoRefuelCase: return NativeAutoRefuel.Apply(intent, context);
                default: throw new InvalidOperationException(Missing().Detail);
            }
        }
    }
}
