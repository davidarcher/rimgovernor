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
    // BuildingPatchIntent's target_temperature arm: RimWorld.CompTempControl
    // .targetTemperature, a public settable float clamped by the game's own
    // -273.15..1000 C interface range. The other arms are NativeBedUse,
    // NativeGrowerCrop and NativeClaimBuilding; BuildingPatchActionHandler
    // below routes them.
    internal static class NativeBuildingTemperature
    {
        private const float MinCelsius = -273.15f;
        private const float MaxCelsius = 1000f;



        internal static bool Eligible(Thing thing) => thing != null && !thing.Destroyed
            && thing.Spawned && ProtoBoundary.IsLoaded(thing.Map) && thing.TryGetComp<CompTempControl>() != null;

        internal static string Token(Common.Identity identity, string id, float target)
        {
            using (var bytes = new MemoryStream())
            {
                using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
                {
                    writer.Write(identity.ColonyId); writer.Write(identity.LoadToken); writer.Write(identity.MapId);
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
            return new Obs.SnapshotRef { Context = context.Clone(), EntityId = thing.GetUniqueLoadID(),
                Token = Token(context.Identity, thing.GetUniqueLoadID(), comp.targetTemperature) };
        }

        private static Common.Failure? Resolve(Operations.BuildingPatchIntent intent, Common.ObservationContext context, out Thing? thing)
        {
            thing = null;
            if (!ProtoBoundary.IsIdentifier(intent.ThingId) || float.IsNaN(intent.TargetTemperature) || float.IsInfinity(intent.TargetTemperature)
                || intent.TargetTemperature < MinCelsius || intent.TargetTemperature > MaxCelsius)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "A temperature patch requires an exact building and a target in the game's -273.15 to 1000 C interface range.");
            thing = RefIndex.Thing(ProtoBoundary.LoadedMap(context), intent.ThingId);
            if (thing == null || !Eligible(thing)) return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact building with CompTempControl is unavailable.");
            return null;
        }

        private static Receipts.EffectEvidence Evidence(string id, string after) =>
            new Receipts.EffectEvidence { Settings = new Receipts.SettingsEffect {
                Snapshot = new Receipts.SnapshotEvidence { EntityId = id, AfterToken = after },
                Fields = { new Receipts.FieldResult { Field = Receipts.SettingsField.Temperature, Outcome = Receipts.FieldOutcome.Applied } } } };

        internal static Common.Failure? Validate(Operations.BuildingPatchIntent intent, Common.ObservationContext context) => Resolve(intent, context, out _);

        internal static Receipts.EffectEvidence Apply(Operations.BuildingPatchIntent intent, Common.ObservationContext context)
        {
            var failure = Resolve(intent, context, out var thing);
            if (failure != null) throw new InvalidOperationException(failure.Detail);
            var comp = thing!.TryGetComp<CompTempControl>();
            comp.targetTemperature = intent.TargetTemperature;
            if (Math.Abs(comp.targetTemperature - intent.TargetTemperature) >= 0.001f) throw new InvalidOperationException("Native building temperature requires readback.");
            return Evidence(thing!.GetUniqueLoadID(), Snapshot(thing!, context)!.Token);
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
            var intent = action.BuildingPatch;
            switch (intent?.ChangeCase)
            {
                case Operations.BuildingPatchIntent.ChangeOneofCase.TargetTemperature: return NativeBuildingTemperature.Validate(intent, context);
                case Operations.BuildingPatchIntent.ChangeOneofCase.Medical:
                case Operations.BuildingPatchIntent.ChangeOneofCase.ForPrisoners:
                case Operations.BuildingPatchIntent.ChangeOneofCase.ForSlaves: return NativeBedUse.Validate(intent, context);
                case Operations.BuildingPatchIntent.ChangeOneofCase.PlantDef: return NativeGrowerCrop.Validate(intent, context);
                case Operations.BuildingPatchIntent.ChangeOneofCase.Claim: return NativeClaimBuilding.Validate(intent, context);
                case Operations.BuildingPatchIntent.ChangeOneofCase.AutoRefuel: return NativeAutoRefuel.Validate(intent, context);
                default: return Missing();
            }
        }

        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context)
        {
            var intent = action.BuildingPatch;
            switch (intent?.ChangeCase)
            {
                case Operations.BuildingPatchIntent.ChangeOneofCase.TargetTemperature: return NativeBuildingTemperature.Apply(intent, context);
                case Operations.BuildingPatchIntent.ChangeOneofCase.Medical:
                case Operations.BuildingPatchIntent.ChangeOneofCase.ForPrisoners:
                case Operations.BuildingPatchIntent.ChangeOneofCase.ForSlaves: return NativeBedUse.Apply(intent, context);
                case Operations.BuildingPatchIntent.ChangeOneofCase.PlantDef: return NativeGrowerCrop.Apply(intent, context);
                case Operations.BuildingPatchIntent.ChangeOneofCase.Claim: return NativeClaimBuilding.Apply(intent, context);
                case Operations.BuildingPatchIntent.ChangeOneofCase.AutoRefuel: return NativeAutoRefuel.Apply(intent, context);
                default: throw new InvalidOperationException(Missing().Detail);
            }
        }
    }
}
