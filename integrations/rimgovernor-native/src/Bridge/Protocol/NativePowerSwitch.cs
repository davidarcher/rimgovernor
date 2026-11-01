using System;
using System.Linq;
using RimWorld;
using Verse;

namespace RimGovernor.Native
{
    // BuildingPatchIntent's power_switch arm: the player's power gizmo on
    // one exact player building with a flick switch (CompFlickable). The
    // gizmo's own toggle runs when its wanted state differs from the order,
    // so the game adds the flick designation and a pawn flicks the switch
    // natively; an order the switch already wants applies again. A building
    // without CompFlickable is refused "not flickable". The building read's
    // switch (on, wanted) is the readback.
    internal static class NativePowerSwitch
    {
        private static RimGovernor.Brain.GameFacts.Common.Failure? Resolve(RimGovernor.Brain.GameOrders.Operations.BuildingPatchIntent intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context, out CompFlickable? comp)
        {
            comp = null;
            if (!GameThread.IsIdentifier(intent.ThingId)) return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "A power switch requires an exact building.");
            var thing = RefIndex.Thing(GameThread.LoadedMap(context), intent.ThingId);
            if (thing is not Building building || building.Destroyed || !building.Spawned || building.Faction != Faction.OfPlayerSilentFail)
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.NotFound, "Exact spawned player building is unavailable.");
            comp = building.TryGetComp<CompFlickable>();
            if (comp == null) return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Building is not flickable.");
            return null;
        }

        internal static RimGovernor.Brain.GameFacts.Common.Failure? Validate(RimGovernor.Brain.GameOrders.Operations.BuildingPatchIntent intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context) => Resolve(intent, context, out _);

        internal static RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Apply(RimGovernor.Brain.GameOrders.Operations.BuildingPatchIntent intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var failure = Resolve(intent, context, out var comp);
            if (failure != null || comp == null) throw new InvalidOperationException(failure?.Detail ?? "Power switch unavailable.");
            if (intent.Change is not RimGovernor.Brain.GameOrders.Operations.BuildingPatchIntentChange.PowerSwitchCase { Value: var wanted }) throw new InvalidOperationException("Power switch state missing");
            var toggle = comp.CompGetGizmosExtra().OfType<Command_Toggle>().FirstOrDefault()
                ?? throw new InvalidOperationException("Native power gizmo is unavailable.");
            if (toggle.isActive() != wanted)
            {
                // The gizmo's toggle opens the flick tutorial once; the order is the colony's, so the concept is known.
                PlayerKnowledgeDatabase.KnowledgeDemonstrated(ConceptDefOf.SwitchFlickingDesignation, KnowledgeAmount.Total);
                toggle.toggleAction();
            }
            if (toggle.isActive() != wanted) throw new InvalidOperationException("Native power switch requires readback.");
            return new RimGovernor.Brain.GameFacts.Receipts.EffectEvidence { Effect = new RimGovernor.Brain.GameFacts.Receipts.SettingsEffect {
                Fields = [new RimGovernor.Brain.GameFacts.Receipts.FieldResult { Field = RimGovernor.Brain.GameFacts.Receipts.SettingsField.Power, Outcome = RimGovernor.Brain.GameFacts.Receipts.FieldOutcome.Applied }] } };
        }
    }

    // BuildingPatchIntent's allow arm: the forbid gizmo's unforbid on one
    // exact spawned player building (CompForbiddable). An allowed building
    // applies again; a building that cannot be forbidden is refused.
    internal static class NativeBuildingAllow
    {
        private static RimGovernor.Brain.GameFacts.Common.Failure? Resolve(RimGovernor.Brain.GameOrders.Operations.BuildingPatchIntent intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context, out Building? building)
        {
            building = null;
            if (!GameThread.IsIdentifier(intent.ThingId)) return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "An allow requires an exact building.");
            var thing = RefIndex.Thing(GameThread.LoadedMap(context), intent.ThingId);
            if (thing is not Building b || b.Destroyed || !b.Spawned || b.Faction != Faction.OfPlayerSilentFail)
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.NotFound, "Exact spawned player building is unavailable.");
            if (b.TryGetComp<CompForbiddable>() == null) return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Building is not forbiddable.");
            building = b;
            return null;
        }

        internal static RimGovernor.Brain.GameFacts.Common.Failure? Validate(RimGovernor.Brain.GameOrders.Operations.BuildingPatchIntent intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context) => Resolve(intent, context, out _);

        internal static RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Apply(RimGovernor.Brain.GameOrders.Operations.BuildingPatchIntent intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var failure = Resolve(intent, context, out var building);
            if (failure != null || building == null) throw new InvalidOperationException(failure?.Detail ?? "Building unavailable.");
            if (intent.Change is not RimGovernor.Brain.GameOrders.Operations.BuildingPatchIntentChange.AllowCase) throw new InvalidOperationException("Allow change missing");
            building.SetForbidden(false, warnOnFail: false);
            if (building.IsForbidden(Faction.OfPlayer)) throw new InvalidOperationException("Native allow requires readback.");
            return new RimGovernor.Brain.GameFacts.Receipts.EffectEvidence { Effect = new RimGovernor.Brain.GameFacts.Receipts.SettingsEffect {
                Fields = [new RimGovernor.Brain.GameFacts.Receipts.FieldResult { Field = RimGovernor.Brain.GameFacts.Receipts.SettingsField.Forbidden, Outcome = RimGovernor.Brain.GameFacts.Receipts.FieldOutcome.Applied }] } };
        }
    }
}
