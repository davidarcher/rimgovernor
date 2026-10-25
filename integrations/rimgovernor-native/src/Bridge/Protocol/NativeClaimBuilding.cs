using System;
using System.IO;
using System.Linq;
using System.Text;
using RimWorld;
using Verse;

namespace RimGovernor.Native
{
    // BuildingPatchIntent's claim arm: the game's Claim gizmo
    // (Building.ClaimableBy(player) requires def.Claimable, no faction or a
    // non-player one, a spawned building, and refuses a cryptosleep casket
    // that holds anything or is under a raid's spawn lock; then
    // Building.SetFaction(player)). A building already the player's applies
    // again. Nothing here opens a casket.
    internal static class NativeClaimBuilding
    {


        // Eligible is any claimable-by-definition building that is not one
        // of the other settings producers; a casket already the player's
        // stays eligible so the after-token can be read back.
        internal static bool Eligible(Thing thing) => thing is Building building && !building.Destroyed && building.Spawned
            && GameThread.IsLoaded(building.Map) && building.def != null && building.def.Claimable
            && !(thing is Building_Bed) && !(thing is Building_PlantGrower) && thing.TryGetComp<CompTempControl>() == null;

        internal static bool PlayerOwned(Building building) => building.Faction != null && building.Faction == Faction.OfPlayerSilentFail;

        private static RimGovernor.Brain.GameFacts.Common.Failure? Resolve(RimGovernor.Brain.GameOrders.Operations.BuildingPatchIntent intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context, out Building? building)
        {
            building = null;
            if (!GameThread.IsIdentifier(intent.ThingId)) return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "A claim requires an exact building.");
            var thing = RefIndex.Thing(GameThread.LoadedMap(context), intent.ThingId);
            if (thing == null || !Eligible(thing)) return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.NotFound, "Exact claimable building is unavailable.");
            building = (Building)thing;
            var player = Faction.OfPlayerSilentFail;
            if (player == null) return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "No player faction to claim for.");
            if (PlayerOwned(building)) return null;
            if (!building.ClaimableBy(player)) return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "The game refuses the claim (contents, faction or a spawn lock).");
            return null;
        }

        private static RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Evidence() =>
            new RimGovernor.Brain.GameFacts.Receipts.EffectEvidence { Effect = new RimGovernor.Brain.GameFacts.Receipts.SettingsEffect {
                Fields = [new RimGovernor.Brain.GameFacts.Receipts.FieldResult { Field = RimGovernor.Brain.GameFacts.Receipts.SettingsField.Claim, Outcome = RimGovernor.Brain.GameFacts.Receipts.FieldOutcome.Applied }] } };

        internal static RimGovernor.Brain.GameFacts.Common.Failure? Validate(RimGovernor.Brain.GameOrders.Operations.BuildingPatchIntent intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context) => Resolve(intent, context, out _);

        internal static RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Apply(RimGovernor.Brain.GameOrders.Operations.BuildingPatchIntent intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var failure = Resolve(intent, context, out var building);
            if (failure != null || building == null) throw new InvalidOperationException(failure?.Detail);
            if (!PlayerOwned(building)) building.SetFaction(Faction.OfPlayer);
            if (!PlayerOwned(building)) throw new InvalidOperationException("Native claim requires readback.");
            return Evidence();
        }
    }
}
