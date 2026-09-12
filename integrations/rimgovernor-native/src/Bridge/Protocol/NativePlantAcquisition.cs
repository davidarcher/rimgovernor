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
using Authority = RimGovernor.Protocol.Authority;
using Obs = RimGovernor.Protocol.Observations;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    internal static class NativePlantAcquisition
    {
        internal static bool Valid(Operations.AcquireResource? command) => command != null && NativeDraftProtocol.ValidEntity(command.Source)
            && command.HasResourceDefName && ProtoBoundary.IsIdentifier(command.ResourceDefName)
            && command.Cell != null && command.Cell.HasX && command.Cell.HasZ && command.Cell.X >= 0 && command.Cell.Z >= 0;
        private static bool Eligible(Plant plant) => plant.Map == Find.CurrentMap && ResourceAcquisitionTools.Eligible(plant, plant.Map)
            && !(plant.Map.zoneManager.ZoneAt(plant.Position) is Zone_Growing)
            && plant.Map.mapPawns.FreeColonistsSpawned.Any(p => p.workSettings?.Initialized == true
                && p.workSettings.GetPriority(WorkTypeDefOf.PlantCutting) > 0 && !p.WorkTypeIsDisabled(WorkTypeDefOf.PlantCutting)
                && !p.Downed && !p.Drafted && !p.InMentalState && !plant.IsForbidden(p)
                && p.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation) && p.Position.DistanceTo(plant.Position) <= 50
                && p.CanReach(plant, PathEndMode.Touch, Danger.None));
        internal static string Token(Common.Identity identity, string id, string resource, int x, int z, float growth, int yield, bool designated)
        {
            using (var bytes = new MemoryStream())
            {
                using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
                { writer.Write(identity.ColonyId); writer.Write(identity.LoadToken); writer.Write(identity.MapId); writer.Write(id); writer.Write(resource); writer.Write(x); writer.Write(z); writer.Write(growth); writer.Write(yield); writer.Write(designated); }
                using (var hash = SHA256.Create()) return "plant-" + BitConverter.ToString(hash.ComputeHash(bytes.ToArray())).Replace("-", "").ToLowerInvariant();
            }
        }
        private static Obs.SnapshotRef Snapshot(Plant plant, Common.ObservationContext context) => new Obs.SnapshotRef {
            Context = context.Clone(), EntityId = plant.GetUniqueLoadID(), Token = Token(context.Identity, plant.GetUniqueLoadID(),
                plant.def.plant.harvestedThingDef.defName, plant.Position.x, plant.Position.z, plant.Growth, plant.YieldNow(), ResourceAcquisitionTools.Designated(plant)) };
        internal static void Read(Obs.ColonyFactsSnapshot result, Map map, IntVec3 center, Func<ThingDef, bool> humanFood, int limit)
        {
            var plants = map.listerThings.AllThings.OfType<Plant>().Where(p => p.def.plant.harvestedThingDef != null).ToArray();
            // The candidate pool is the nearest bounded set; pending yield below covers all designations.
            var selected = plants.Where(p => p.Position.DistanceTo(center) <= 35 && (p.def.plant.IsTree || humanFood(p.def.plant.harvestedThingDef)) && Eligible(p))
                .OrderBy(p => p.Position.DistanceToSquared(center)).ThenBy(p => p.thingIDNumber).Take(limit).ToArray();
            foreach (var plant in selected)
            {
                var resource = plant.def.plant.harvestedThingDef;
                var food = humanFood(resource);
                result.Acquisition.Add(new Obs.AcquisitionFacts {
                    Source = new Obs.EntityRef { Id = plant.GetUniqueLoadID(), DefName = plant.def.defName, MapId = map.uniqueID,
                        Position = new Common.Cell { X = plant.Position.x, Z = plant.Position.z }, Snapshot = Snapshot(plant, result.Context) },
                    Resource = resource.defName, Tree = plant.def.plant.IsTree, Food = food, Yield = plant.YieldNow(),
                    NutritionYield = food ? plant.YieldNow() * resource.GetStatValueAbstract(StatDefOf.Nutrition) : 0,
                    Designated = ResourceAcquisitionTools.Designated(plant) });
            }
            var pending = plants.Where(ResourceAcquisitionTools.Designated).ToArray();
            result.PendingFoodNutrition = pending.Where(p => humanFood(p.def.plant.harvestedThingDef)).Sum(p => (double)p.YieldNow() * p.def.plant.harvestedThingDef.GetStatValueAbstract(StatDefOf.Nutrition));
            result.PendingWoodUnits = pending.Where(p => p.def.plant.harvestedThingDef == ThingDefOf.WoodLog).Sum(p => (double)p.YieldNow());
        }
        private static bool Prepare(Operations.AcquireResource command, Common.ObservationContext context, out Plant? plant, out Common.Failure failure)
        {
            plant = null; failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Acquisition requires an exact safe mature wild plant snapshot.");
            if (!Valid(command) || !NativeAcquisitionTracking.Ready) return false;
            var map = Find.CurrentMap;
            if (map.AllCells.Any(c => map.roofCollapseBuffer.IsMarkedToCollapse(c))) return false;
            plant = map.listerThings.AllThings.OfType<Plant>().SingleOrDefault(p => p.GetUniqueLoadID() == command.Source.EntityId);
            return plant != null && Eligible(plant) && plant.Position.x == command.Cell.X && plant.Position.z == command.Cell.Z
                && plant.def.plant.harvestedThingDef.defName == command.ResourceDefName && Snapshot(plant, context).Token == command.Source.ExpectedSnapshotToken
                && !ResourceAcquisitionTools.Designated(plant) && ResourceAcquisitionTools.DesignatorFor(plant).CanDesignateThing(plant).Accepted;
        }
        internal static Operations.PreviewReply Preview(Operations.AcquireResource command, Common.ObservationContext context)
        {
            try
            {
                if (!Prepare(command, context, out _, out var failure)) return new Operations.PreviewReply { Failure = failure };
                return new Operations.PreviewReply { Evaluated = new Operations.PreviewEvaluation { Context = context.Clone(), Accepted = true } };
            }
            catch (Exception error) { return new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Acquisition preview failed: " + error.GetType().Name) }; }
        }
        internal static Operations.ExecuteReply Execute(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context)
        {
            NativeAttemptLedger.Admission? handle = null; Authority.Owner? owner = null; Receipts.EffectEvidence? evidence = null;
            var pre = request.Precondition; var command = request.Operation.AcquireResource;
            try
            {
                if (!Prepare(command, context, out var plant, out var failure)) return new Operations.ExecuteReply { Failure = failure };
                if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null)
                    return new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.AuthorityRequired, "Native authority is required.") };
                var guard = authority.Check(pre.ExpectedGeneration, pre.LeaseId, pre.Attempt.ControllerSessionId);
                context.NativeGeneration = guard.Snapshot.Generation;
                if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) };
                owner = new Authority.Owner { ControllerSessionId = guard.Snapshot.Lease!.ControllerSessionId, PlayerDirection = guard.Snapshot.Lease.PlayerDirection };
                var admitted = state.Ledger.Admit("rimgovernor.operations.v1.Operations/Execute", request, context, owner);
                if (admitted.Kind != NativeAttemptLedger.DecisionKind.Admitted) return admitted.Reply!;
                handle = admitted.Handle;
                var record = new NativeAcquisitionRecord(plant!);
                state.Acquisition.Add(pre.Attempt.Clone(), record);
                using (authority.Owned())
                {
                    if (!authority.Check(pre.ExpectedGeneration, pre.LeaseId, pre.Attempt.ControllerSessionId).Success
                        || !Prepare(command, context, out var checkedPlant, out failure) || !ReferenceEquals(plant, checkedPlant)
                        || !NativeAcquisitionTracking.Track(record)) throw new InvalidOperationException("Acquisition changed before designation.");
                    ResourceAcquisitionTools.DesignatorFor(plant!).DesignateThing(plant);
                    evidence = new Receipts.EffectEvidence { Acquisition = record.Evidence() };
                    if (!evidence.Acquisition.Designated) throw new InvalidOperationException("Native acquisition designation was not observed.");
                }
                return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, owner, evidence) };
            }
            catch (Exception error)
            {
                return handle == null ? new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Acquisition admission failed: " + error.GetType().Name) }
                    : new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, owner!, evidence!, "Admitted acquisition requires observation: " + error.GetType().Name) };
            }
        }
    }
}
