using System;
using System.Linq;
using RimWorld;
using Verse;

namespace RimGovernor.Native
{
    // The uninstall half of RelocateIntent: the game's
    // Uninstall designation on one exact installed player building, the
    // write Designator_Uninstall makes. Ordinary construction work
    // (WorkGiver_Uninstall) then minifies the piece where it stands; it needs
    // CanReserve on the piece, so a pawn sleeping in a bed is never
    // interrupted. Vanilla hauling takes the packed item to storage. A
    // designation already on the building applies again. The effect is the
    // InstallationEffect at the building's current placement.
    internal static class NativeUninstallBuilding
    {
        internal const string Kind = "Uninstall building";

        private static RimGovernor.Brain.GameFacts.Common.Failure? Resolve(RimGovernor.Brain.GameOrders.Operations.RelocateIntent intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context, out Building? building)
        {
            building = null;
            if (!GameThread.IsIdentifier(intent.ThingId)) return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Uninstall requires an exact building.");
            var map = GameThread.LoadedMap(context);
            var found = NativeMoveBuilding.Find(map, (intent.ThingId ?? ""));
            var rules = new ApplyPreconditions(Kind)
                .Present(() => found != null && !found.Destroyed && found.Spawned && GameThread.IsLoaded(found.Map), "the exact building is not installed on this map")
                .Require(() => found != null && (found.Faction == Faction.OfPlayer), "the building is not the player's")
                .Require(() => found != null && (found.def.Minifiable), "the building cannot be uninstalled")
                .Require(() => InstallBlueprintUtility.ExistingBlueprintFor(found) == null, "the building has a reinstall blueprint")
                .Require(() => map.designationManager.DesignationOn(found, DesignationDefOf.Deconstruct) == null, "the building is designated for deconstruction");
            if (!rules.Holds) return rules.Failure();
            building = found;
            return null;
        }

        internal static RimGovernor.Brain.GameFacts.Common.Failure? Validate(RimGovernor.Brain.GameOrders.Operations.RelocateIntent intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context) => Resolve(intent, context, out _);

        internal static RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Apply(RimGovernor.Brain.GameOrders.Operations.RelocateIntent intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var failure = Resolve(intent, context, out var building);
            if (failure != null) throw new InvalidOperationException(failure.Detail);
            var designations = (building ?? throw new System.InvalidOperationException("Required native value is unavailable: building.")).Map.designationManager;
            if (designations.DesignationOn(building, DesignationDefOf.Uninstall) == null)
                designations.AddDesignation(new Designation(building, DesignationDefOf.Uninstall));
            if (designations.DesignationOn(building, DesignationDefOf.Uninstall) == null) throw new InvalidOperationException("Native uninstall designation was not observed.");
            return new RimGovernor.Brain.GameFacts.Receipts.EffectEvidence { Effect = new RimGovernor.Brain.GameFacts.Receipts.EffectEvidenceEffect.InstallationCase() };
        }
    }
}
