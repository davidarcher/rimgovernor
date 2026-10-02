#nullable enable
using System;
using System.Linq;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // BuildingPatchIntent's auto_refuel arm (#1180): the game's auto-refuel
    // toggle (CompRefuelable.allowAutoRefuel) on one exact player building
    // whose refuelable shows that toggle. A setting that already holds
    // applies again. The colony read reports the flag on a cooking bench
    // (CookingFacts.auto_refuel).
    internal static class NativeAutoRefuel
    {
        internal static CompRefuelable? Comp(Thing thing) =>
            thing is Building building && !building.Destroyed && building.Spawned && ProtoBoundary.IsLoaded(building.Map)
                && building.Faction == Faction.OfPlayerSilentFail && building.TryGetComp<CompRefuelable>() is CompRefuelable comp
                && comp.Props.showAllowAutoRefuelToggle ? comp : null;

        private static Common.Failure? Resolve(Operations.BuildingPatchIntent intent, Common.ObservationContext context, out Thing? thing)
        {
            thing = null;
            if (!ProtoBoundary.IsIdentifier(intent.ThingId)) return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "An auto-refuel switch requires an exact building.");
            thing = RefIndex.Thing(ProtoBoundary.LoadedMap(context), intent.ThingId);
            if (thing == null || Comp(thing) == null) return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact player building with an auto-refuel toggle is unavailable.");
            return null;
        }

        internal static Common.Failure? Validate(Operations.BuildingPatchIntent intent, Common.ObservationContext context) => Resolve(intent, context, out _);

        internal static Receipts.EffectEvidence Apply(Operations.BuildingPatchIntent intent, Common.ObservationContext context)
        {
            var failure = Resolve(intent, context, out var thing);
            if (failure != null) throw new InvalidOperationException(failure.Detail);
            var comp = Comp(thing!)!;
            comp.allowAutoRefuel = intent.AutoRefuel;
            if (comp.allowAutoRefuel != intent.AutoRefuel) throw new InvalidOperationException("Native auto-refuel requires readback.");
            return new Receipts.EffectEvidence { Settings = new Receipts.SettingsEffect {
                Snapshot = new Receipts.SnapshotEvidence { EntityId = thing!.GetUniqueLoadID() },
                Fields = { new Receipts.FieldResult { Field = Receipts.SettingsField.AutoRefuel, Outcome = Receipts.FieldOutcome.Applied } } } };
        }
    }
}
