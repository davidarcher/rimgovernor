using System;
using System.IO;
using System.Linq;
using System.Text;
using RimWorld;
using Verse;

namespace RimGovernor.Native
{
    // BuildingPatchIntent's target_temperature arm: RimWorld.CompTempControl
    // .targetTemperature, a public settable float bounded by the planner
    // (domain.NewBuildingTemperature). The other arms are NativeBedUse,
    // NativeGrowerCrop and NativeClaimBuilding; BuildingPatchActionHandler
    // below routes them.
    internal static class NativeBuildingTemperature
    {



        internal static bool Eligible(Thing thing) => thing != null && !thing.Destroyed
            && thing.Spawned && GameThread.IsLoaded(thing.Map) && thing.TryGetComp<CompTempControl>() != null;

        internal static string Token(RimGovernor.Brain.GameFacts.Common.Identity identity, string id, float target) =>
            NativeSnapshotVersions.Token("temp", identity, id, w => w.Write(target));

        internal static RimGovernor.Brain.GameFacts.Observations.SnapshotRef? Snapshot(Thing thing, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            if (!Eligible(thing)) return null;
            var comp = thing.TryGetComp<CompTempControl>();
            return new RimGovernor.Brain.GameFacts.Observations.SnapshotRef { Context = context, EntityId = thing.GetUniqueLoadID(),
                Token = Token(context.Identity, thing.GetUniqueLoadID(), comp.targetTemperature) };
        }

        private static RimGovernor.Brain.GameFacts.Common.Failure? Resolve(RimGovernor.Brain.GameOrders.Operations.BuildingPatchIntent intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context, out Thing? thing)
        {
            thing = null;
            if (!GameThread.IsIdentifier(intent.ThingId) || intent.Change is not RimGovernor.Brain.GameOrders.Operations.BuildingPatchIntentChange.TargetTemperatureCase { Value: var target } || float.IsNaN(target) || float.IsInfinity(target))
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "A temperature patch requires an exact building and a finite target (domain.NewBuildingTemperature bounds the range).");
            thing = RefIndex.Thing(GameThread.LoadedMap(context), intent.ThingId);
            if (thing == null || !Eligible(thing)) return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.NotFound, "Exact building with CompTempControl is unavailable.");
            return null;
        }

        private static RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Evidence() =>
            new RimGovernor.Brain.GameFacts.Receipts.EffectEvidence { Effect = new RimGovernor.Brain.GameFacts.Receipts.SettingsEffect {
                Fields = [new RimGovernor.Brain.GameFacts.Receipts.FieldResult { Field = RimGovernor.Brain.GameFacts.Receipts.SettingsField.Temperature, Outcome = RimGovernor.Brain.GameFacts.Receipts.FieldOutcome.Applied }] } };

        internal static RimGovernor.Brain.GameFacts.Common.Failure? Validate(RimGovernor.Brain.GameOrders.Operations.BuildingPatchIntent intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context) => Resolve(intent, context, out _);

        internal static RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Apply(RimGovernor.Brain.GameOrders.Operations.BuildingPatchIntent intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var failure = Resolve(intent, context, out var thing);
            if (failure != null) throw new InvalidOperationException(failure.Detail);
            var comp = thing.TryGetComp<CompTempControl>();
            if (intent.Change is not RimGovernor.Brain.GameOrders.Operations.BuildingPatchIntentChange.TargetTemperatureCase { Value: var wanted }) throw new InvalidOperationException("A temperature patch requires a target temperature.");
            comp.targetTemperature = wanted;
            if (Math.Abs(comp.targetTemperature - wanted) >= 0.001f) throw new InvalidOperationException("Native building temperature requires readback.");
            return Evidence();
        }
    }

    // BuildingPatchIntent on Actions/Apply: one settings change on one
    // exact building, routed by the change arm. Native checks the building
    // and the game's own rules live; no snapshot token.
    internal sealed class BuildingPatchActionHandler : IActionHandler
    {
        private static RimGovernor.Brain.GameFacts.Common.Failure Missing() => GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "A building patch requires exactly one change.");

        public RimGovernor.Brain.GameFacts.Common.Failure? Validate(RimGovernor.Brain.GameOrders.Operations.Action action, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var intent = action.Intent?.BuildingPatch;
            if (intent == null) return Missing();
            switch (intent.Change)
            {
                case RimGovernor.Brain.GameOrders.Operations.BuildingPatchIntentChange.TargetTemperatureCase: return NativeBuildingTemperature.Validate(intent, context);
                case RimGovernor.Brain.GameOrders.Operations.BuildingPatchIntentChange.MedicalCase:
                case RimGovernor.Brain.GameOrders.Operations.BuildingPatchIntentChange.ForPrisonersCase:
                case RimGovernor.Brain.GameOrders.Operations.BuildingPatchIntentChange.ForSlavesCase: return NativeBedUse.Validate(intent, context);
                case RimGovernor.Brain.GameOrders.Operations.BuildingPatchIntentChange.PlantDefCase: return NativeGrowerCrop.Validate(intent, context);
                case RimGovernor.Brain.GameOrders.Operations.BuildingPatchIntentChange.ClaimCase: return NativeClaimBuilding.Validate(intent, context);
                case RimGovernor.Brain.GameOrders.Operations.BuildingPatchIntentChange.AutoRefuelCase: return NativeAutoRefuel.Validate(intent, context);
                default: return Missing();
            }
        }

        public RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Apply(RimGovernor.Brain.GameOrders.Operations.Action action, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var intent = action.Intent?.BuildingPatch;
            if (intent == null) throw new InvalidOperationException(Missing().Detail);
            switch (intent.Change)
            {
                case RimGovernor.Brain.GameOrders.Operations.BuildingPatchIntentChange.TargetTemperatureCase: return NativeBuildingTemperature.Apply(intent, context);
                case RimGovernor.Brain.GameOrders.Operations.BuildingPatchIntentChange.MedicalCase:
                case RimGovernor.Brain.GameOrders.Operations.BuildingPatchIntentChange.ForPrisonersCase:
                case RimGovernor.Brain.GameOrders.Operations.BuildingPatchIntentChange.ForSlavesCase: return NativeBedUse.Apply(intent, context);
                case RimGovernor.Brain.GameOrders.Operations.BuildingPatchIntentChange.PlantDefCase: return NativeGrowerCrop.Apply(intent, context);
                case RimGovernor.Brain.GameOrders.Operations.BuildingPatchIntentChange.ClaimCase: return NativeClaimBuilding.Apply(intent, context);
                case RimGovernor.Brain.GameOrders.Operations.BuildingPatchIntentChange.AutoRefuelCase: return NativeAutoRefuel.Apply(intent, context);
                default: throw new InvalidOperationException(Missing().Detail);
            }
        }
    }
}
