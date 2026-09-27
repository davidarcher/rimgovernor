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
    // BuildingPatchIntent's claim arm (#459): the game's Claim gizmo
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
            && ProtoBoundary.IsLoaded(building.Map) && building.def != null && building.def.Claimable
            && !(thing is Building_Bed) && !(thing is Building_PlantGrower) && thing.TryGetComp<CompTempControl>() == null;

        internal static bool PlayerOwned(Building building) => building.Faction != null && building.Faction == Faction.OfPlayerSilentFail;

        internal static string Token(Common.Identity identity, Building building)
        {
            using (var bytes = new MemoryStream())
            {
                using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
                {
                    writer.Write(identity.ColonyId); writer.Write(identity.LoadToken); writer.Write(identity.MapId);
                    writer.Write(building.GetUniqueLoadID()); writer.Write(building.Faction?.GetUniqueLoadID() ?? string.Empty);
                    writer.Write(building is Building_Casket casket && casket.HasAnyContents);
                }
                using (var hash = SHA256.Create())
                    return "claim-" + BitConverter.ToString(hash.ComputeHash(bytes.ToArray())).Replace("-", "").ToLowerInvariant();
            }
        }

        internal static Obs.SnapshotRef? Snapshot(Thing thing, Common.ObservationContext context)
        {
            if (!Eligible(thing)) return null;
            return new Obs.SnapshotRef { Context = context.Clone(), EntityId = thing.GetUniqueLoadID(),
                Token = Token(context.Identity, (Building)thing) };
        }

        // Settings carries the faction reading beside its CAS snapshot on
        // the building listing row.
        internal static Obs.BuildingSettings Settings(Building building, Common.ObservationContext context) =>
            new Obs.BuildingSettings { Snapshot = Snapshot(building, context), PlayerOwned = PlayerOwned(building) };

        private static Common.Failure? Resolve(Operations.BuildingPatchIntent intent, Common.ObservationContext context, out Building? building)
        {
            building = null;
            if (!ProtoBoundary.IsIdentifier(intent.ThingId)) return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "A claim requires an exact building.");
            var thing = ProtoBoundary.LoadedMap(context).listerThings.AllThings.SingleOrDefault(t => t.GetUniqueLoadID() == intent.ThingId);
            if (thing == null || !Eligible(thing)) return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact claimable building is unavailable.");
            building = (Building)thing;
            var player = Faction.OfPlayerSilentFail;
            if (player == null) return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "No player faction to claim for.");
            if (PlayerOwned(building)) return null;
            if (!building.ClaimableBy(player)) return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "The game refuses the claim (contents, faction or a spawn lock).");
            return null;
        }

        private static Receipts.EffectEvidence Evidence(string id, string after) =>
            new Receipts.EffectEvidence { Settings = new Receipts.SettingsEffect {
                Snapshot = new Receipts.SnapshotEvidence { EntityId = id, AfterToken = after },
                Fields = { new Receipts.FieldResult { Field = Receipts.SettingsField.Claim, Outcome = Receipts.FieldOutcome.Applied } } } };

        internal static Common.Failure? Validate(Operations.BuildingPatchIntent intent, Common.ObservationContext context) => Resolve(intent, context, out _);

        internal static Receipts.EffectEvidence Apply(Operations.BuildingPatchIntent intent, Common.ObservationContext context)
        {
            var failure = Resolve(intent, context, out var building);
            if (failure != null) throw new InvalidOperationException(failure.Detail);
            if (!PlayerOwned(building!)) building!.SetFaction(Faction.OfPlayer);
            if (!PlayerOwned(building!)) throw new InvalidOperationException("Native claim requires readback.");
            return Evidence(building!.GetUniqueLoadID(), Snapshot(building, context)!.Token);
        }
    }
}
