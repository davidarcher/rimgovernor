#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // The blight responder's native half (#245): the crop-blight census in
    // the colony read (Read) and the CutPlant designation on one exact
    // blighted plant (DesignateThing with THING_DESIGNATION_CUT_PLANT). The
    // designation is the whole write; ordinary plant-cutting work cuts the
    // plant afterwards, and the census emptying is what settles the goal.
    internal static class NativeCutPlant
    {
        internal const string Kind = "Cut plant";
        internal const string DesignationDef = "CutPlant";
        internal const int Limit = 64;

        internal static bool Valid(Operations.DesignateThing? command) => command != null
            && NativeDraftProtocol.ValidEntity(command.Target) && command.HasDesignation
            && command.Designation == Operations.ThingDesignation.CutPlant;

        internal static bool IsCutPlant(Operations.DesignateThing? command) => command != null && command.HasDesignation
            && command.Designation == Operations.ThingDesignation.CutPlant;

        // Census membership: a blighted plant on colony ground (a growing
        // zone or the home area). Blight on wild plants outside both is the
        // storyteller's, not the colony's, and is left alone.
        internal static bool InColony(Plant plant, Map map) => map.zoneManager.ZoneAt(plant.Position) is Zone_Growing || map.areaManager.Home[plant.Position];
        internal static bool Eligible(Plant plant) => !plant.Destroyed && plant.Spawned && ProtoBoundary.IsLoaded(plant.Map)
            && plant.Blighted && !plant.Position.Fogged(plant.Map) && InColony(plant, plant.Map);
        internal static bool Designated(Plant plant) => plant.Map.designationManager.DesignationOn(plant, DesignationDefOf.CutPlant) != null
            || plant.Map.designationManager.DesignationOn(plant, DesignationDefOf.HarvestPlant) != null;

        internal static string Token(Common.Identity identity, string id, string definition, int x, int z, bool blighted, bool designated)
        {
            using (var bytes = new MemoryStream())
            {
                using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
                {
                    writer.Write(identity.ColonyId); writer.Write(identity.LoadToken); writer.Write(identity.MapId);
                    writer.Write(id); writer.Write(definition); writer.Write(x); writer.Write(z); writer.Write(blighted); writer.Write(designated);
                }
                using (var hash = SHA256.Create())
                    return "cut-" + BitConverter.ToString(hash.ComputeHash(bytes.ToArray())).Replace("-", "").ToLowerInvariant();
            }
        }

        internal static Obs.SnapshotRef Snapshot(Plant plant, Common.ObservationContext context) => new Obs.SnapshotRef {
            Context = context.Clone(), EntityId = plant.GetUniqueLoadID(),
            Token = Token(context.Identity, plant.GetUniqueLoadID(), plant.def.defName, plant.Position.x, plant.Position.z, plant.Blighted, Designated(plant)) };

        // Read fills ColonyFactsSnapshot.blighted_plants: every eligible
        // blighted plant nearest the colony first, bounded to Limit rows.
        internal static void Read(Obs.ColonyFactsSnapshot result, Map map, IntVec3 center)
        {
            var plants = map.listerThings.AllThings.OfType<Plant>().Where(Eligible)
                .OrderBy(p => p.Position.DistanceToSquared(center)).ThenBy(p => p.thingIDNumber).Take(Limit).ToList();
            foreach (var plant in plants)
            {
                var row = new Obs.BlightedPlant {
                    Plant = new Obs.EntityRef { Id = plant.GetUniqueLoadID(), DefName = plant.def.defName, MapId = map.uniqueID,
                        Position = new Common.Cell { X = plant.Position.x, Z = plant.Position.z }, Snapshot = Snapshot(plant, result.Context) },
                    Designated = Designated(plant), Growth = plant.Growth };
                if (map.zoneManager.ZoneAt(plant.Position) is Zone_Growing zone) row.ZoneId = zone.ID.ToString(System.Globalization.CultureInfo.InvariantCulture);
                result.BlightedPlants.Add(row);
            }
        }

        // Cutter is the same colonist rule plant acquisition applies: someone
        // with plant cutting enabled must be able to do the work now.
        private static bool Cutter(Pawn p, Plant plant) => p.workSettings?.Initialized == true
            && p.workSettings.GetPriority(WorkTypeDefOf.PlantCutting) > 0 && !p.WorkTypeIsDisabled(WorkTypeDefOf.PlantCutting)
            && !p.Downed && !p.Drafted && !p.InMentalState && !plant.IsForbidden(p)
            && p.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)
            && p.CanReach(plant, PathEndMode.Touch, Danger.None);

        // Prepare is the apply-time precondition list for CutPlant
        // (action-contracts.md), one rule at a time so a refusal names the
        // fact that moved; the token comparison closes it.
        private static bool Prepare(Operations.DesignateThing command, Common.ObservationContext context, out Plant? plant, out Common.Failure failure)
        {
            plant = null;
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "CutPlant requires an exact blighted plant snapshot.");
            if (!Valid(command)) return false;
            var map = ProtoBoundary.LoadedMap(context);
            var found = map.listerThings.AllThings.OfType<Plant>().SingleOrDefault(p => p.GetUniqueLoadID() == command.Target.EntityId);
            var rules = new ApplyPreconditions(Kind)
                .Present(() => found != null && !found.Destroyed && found.Spawned && ProtoBoundary.IsLoaded(found.Map), "the exact plant is no longer spawned on this map")
                .Require(() => found!.Blighted, "the plant is not blighted")
                .Require(() => !found!.Position.Fogged(map), "the plant's cell is fogged")
                .Require(() => InColony(found!, map), "the plant stands outside the colony's growing zones and home area")
                .Require(() => !found!.IsForbidden(Faction.OfPlayer), "the plant is forbidden")
                .Require(() => !Designated(found!), "the plant is already designated")
                .Require(() => new Designator_PlantsCut().CanDesignateThing(found!).Accepted, "the native cut designator refuses the plant")
                .Require(() => map.mapPawns.FreeColonistsSpawned.Any(p => Cutter(p, found!)), "no free colonist with plant cutting enabled can reach the plant")
                .Token(() => Snapshot(found!, context).Token == command.Target.ExpectedSnapshotToken, "the plant snapshot changed since it was read");
            if (!rules.Holds) { failure = rules.Failure(); return false; }
            plant = found;
            return true;
        }

        private static Receipts.EffectEvidence Evidence(Plant plant) => new Receipts.EffectEvidence {
            Designation = new Receipts.DesignationEffect { ThingId = plant.GetUniqueLoadID(), DesignationDef = DesignationDef,
                Present = plant.Map.designationManager.DesignationOn(plant, DesignationDefOf.CutPlant) != null,
                ResourceDef = plant.def.defName, Cell = new Common.Cell { X = plant.Position.x, Z = plant.Position.z } } };

        internal static Operations.PreviewReply Preview(Operations.DesignateThing command, Common.ObservationContext context)
        {
            try
            {
                if (!Prepare(command, context, out var plant, out var failure)) return new Operations.PreviewReply { Failure = failure };
                // Proposed=true is never a claim that an effect already happened.
                var proposed = Evidence(plant!); proposed.Designation.Present = true;
                return NativeOperationEnvelope.Preview(new Operations.PreviewReply { Evaluated = new Operations.PreviewEvaluation {
                    Context = context.Clone(), Accepted = true, Projected = proposed } });
            }
            catch (Exception error) { return new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Cut plant preview failed: " + error.GetType().Name) }; }
        }

        internal static Operations.ExecuteReply Execute(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context)
        {
            NativeAttemptLedger.Admission? handle = null;
            Receipts.EffectEvidence? evidence = null;
            var pre = request.Precondition;
            try
            {
                if (!Prepare(request.Operation.DesignateThing, context, out var plant, out var failure)) return new Operations.ExecuteReply { Failure = failure };
                if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null)
                    return new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.AuthorityRequired, "Native authority is required.") };
                var guard = authority.Check(pre.ExpectedGeneration);
                context.NativeGeneration = guard.Snapshot.Generation;
                if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) };
                var admitted = state.Ledger.Admit("rimgovernor.operations.v1.Operations/Execute", request, context);
                if (admitted.Kind != NativeAttemptLedger.DecisionKind.Admitted) return admitted.DecidedReply;
                handle = admitted.AdmittedHandle;
                using (authority.Owned())
                {
                    var current = authority.Check(pre.ExpectedGeneration);
                    if (!current.Success || !Prepare(request.Operation.DesignateThing, context, out var checkedPlant, out failure)
                        || !ReferenceEquals(checkedPlant, plant)) throw new InvalidOperationException("Cut plant admission changed before effect.");
                    new Designator_PlantsCut().DesignateThing(plant);
                    evidence = Evidence(plant!);
                    if (!evidence.Designation.Present) throw new InvalidOperationException("Native CutPlant designation was not observed.");
                    state.CutPlants.Add(pre.Attempt.Clone(), evidence.Designation.Clone());
                }
                return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, evidence) };
            }
            catch (Exception error)
            {
                return handle == null
                    ? new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Cut plant admission failed: " + error.GetType().Name) }
                    : new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, evidence, "Admitted CutPlant requires observation: " + error.GetType().Name) };
            }
        }

        // Observe: the plant gone from the map is the designation's ordinary
        // outcome (cut, or otherwise removed) and completes the attempt; a
        // plant still standing with its designation is pending; a standing
        // plant whose designation was removed is unsuccessful (the player
        // cancelled it), never re-designated here.
        internal static Receipts.Progress Observe(Common.AttemptKey attempt, Common.ObservationContext context, Receipts.DesignationEffect original)
        {
            var result = new Receipts.Progress { Attempt = attempt.Clone(), Context = context.Clone(), CompleteInspection = true };
            try
            {
                var map = ProtoBoundary.LoadedMap(context);
                var plant = map.listerThings.AllThings.OfType<Plant>().SingleOrDefault(p => p.GetUniqueLoadID() == original.ThingId);
                if (plant == null || plant.Destroyed || !plant.Spawned)
                {
                    result.Completed = new Receipts.CompletedEffect { Evidence = new Receipts.EffectEvidence { Designation = original.Clone() } };
                    return result;
                }
                var evidence = Evidence(plant);
                if (evidence.Designation.Present) result.Pending = new Receipts.PendingEffect { Evidence = evidence };
                else result.Unsuccessful = new Receipts.UnsuccessfulEffect { Reason = Receipts.UnsuccessfulReason.OutcomeNotAchieved,
                    Evidence = evidence, Detail = "The plant stands without its CutPlant designation; a cancelled designation is not repeated." };
                return result;
            }
            catch (Exception)
            {
                result.CompleteInspection = false;
                result.Unknown = new Receipts.UnknownEffect { Reason = "The exact plant could not be inspected; absence does not prove the cut." };
                return result;
            }
        }
    }
}
