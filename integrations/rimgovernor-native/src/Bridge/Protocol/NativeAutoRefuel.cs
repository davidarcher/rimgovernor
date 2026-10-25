using System;
using System.Linq;
using RimWorld;
using Verse;

namespace RimGovernor.Native
{
    // BuildingPatchIntent's auto_refuel arm: the game's auto-refuel
    // toggle (CompRefuelable.allowAutoRefuel) on one exact player building
    // whose refuelable shows that toggle. A setting that already holds
    // applies again. The colony read reports the flag on a cooking bench
    // (CookingFacts.auto_refuel).
    internal static class NativeAutoRefuel
    {
        internal static CompRefuelable? Comp(Thing thing) =>
            thing is Building building && !building.Destroyed && building.Spawned && GameThread.IsLoaded(building.Map)
                && building.Faction == Faction.OfPlayerSilentFail && building.TryGetComp<CompRefuelable>() is CompRefuelable comp
                && comp.Props.showAllowAutoRefuelToggle ? comp : null;

        private static RimGovernor.Brain.GameFacts.Common.Failure? Resolve(RimGovernor.Brain.GameOrders.Operations.BuildingPatchIntent intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context, out Thing? thing)
        {
            thing = null;
            if (!GameThread.IsIdentifier(intent.ThingId)) return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "An auto-refuel switch requires an exact building.");
            thing = RefIndex.Thing(GameThread.LoadedMap(context), intent.ThingId);
            if (thing == null || Comp(thing) == null) return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.NotFound, "Exact player building with an auto-refuel toggle is unavailable.");
            return null;
        }

        internal static RimGovernor.Brain.GameFacts.Common.Failure? Validate(RimGovernor.Brain.GameOrders.Operations.BuildingPatchIntent intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context) => Resolve(intent, context, out _);

        internal static RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Apply(RimGovernor.Brain.GameOrders.Operations.BuildingPatchIntent intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var failure = Resolve(intent, context, out var thing);
            if (failure != null) throw new InvalidOperationException(failure.Detail);
            var comp = (Comp((thing ?? throw new System.InvalidOperationException("Required native value is unavailable: thing."))) ?? throw new System.InvalidOperationException("Required native value is unavailable: Comp((thing ?? throw new System.InvalidOperationException(\"Required native value is unavailable: thing.\")))."));
            if (intent.Change is not RimGovernor.Brain.GameOrders.Operations.BuildingPatchIntentChange.AutoRefuelCase { Value: var wanted }) throw new InvalidOperationException("Auto-refuel setting missing");
            comp.allowAutoRefuel = wanted;
            if (comp.allowAutoRefuel != wanted) throw new InvalidOperationException("Native auto-refuel requires readback.");
            return new RimGovernor.Brain.GameFacts.Receipts.EffectEvidence { Effect = new RimGovernor.Brain.GameFacts.Receipts.SettingsEffect {
                Fields = [new RimGovernor.Brain.GameFacts.Receipts.FieldResult { Field = RimGovernor.Brain.GameFacts.Receipts.SettingsField.AutoRefuel, Outcome = RimGovernor.Brain.GameFacts.Receipts.FieldOutcome.Applied }] } };
        }
    }
}
