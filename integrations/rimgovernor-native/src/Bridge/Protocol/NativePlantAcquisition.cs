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
        internal static bool Valid(Operations.AcquireResource? command) => command != null && NativeDraftProtocol.ValidEntityTokenOptional(command.Source)
            && command.HasResourceDefName && ProtoBoundary.IsIdentifier(command.ResourceDefName)
            && command.Cell != null && command.Cell.HasX && command.Cell.HasZ && command.Cell.X >= 0 && command.Cell.Z >= 0;
        private static bool Eligible(Plant plant) => ProtoBoundary.IsLoaded(plant.Map) && ResourceAcquisitionTools.Eligible(plant, plant.Map)
            && !(plant.Map.zoneManager.ZoneAt(plant.Position) is Zone_Growing)
            && plant.Map.mapPawns.FreeColonistsSpawned.Any(p => Cutter(p, plant));
        internal static Obs.SnapshotRef Snapshot(Plant plant, Common.ObservationContext context) => new Obs.SnapshotRef {
            Context = context.Clone(), EntityId = plant.GetUniqueLoadID(), Token = NativeAcquisitionToken.Plant(context.Identity, plant.GetUniqueLoadID(),
                plant.def.plant.harvestedThingDef.defName, plant.Position.x, plant.Position.z, plant.HarvestableNow, ResourceAcquisitionTools.Designated(plant)) };
        internal static void Read(Obs.ColonyFactsSnapshot result, Map map, IntVec3 center, Func<ThingDef, bool> humanFood, int limit)
        {
            var plants = map.listerThings.AllThings.OfType<Plant>().Where(p => p.def.plant.harvestedThingDef != null).ToArray();
            // The candidate pool is the nearest bounded set; pending yield below covers all designations.
            // Medicine-yielding wild plants (healroot) join trees and food so MaintainMedicalReserves can harvest.
            // YieldNow rounds randomly, so one sample per plant decides both eligibility and the row: a plant
            // whose sample is zero (below harvest growth, or a fractional yield rounded down) is not a source.
            var selected = plants.Where(p => p.Position.DistanceTo(center) <= 35 && (p.def.plant.IsTree || humanFood(p.def.plant.harvestedThingDef) || p.def.plant.harvestedThingDef.IsMedicine) && Eligible(p))
                .OrderBy(p => p.Position.DistanceToSquared(center)).ThenBy(p => p.thingIDNumber).Take(Math.Max(0, limit - 2))
                .Select(p => (plant: p, yield: p.YieldNow())).Where(s => s.yield > 0).ToArray();
            foreach (var (plant, yield) in selected)
            {
                var resource = plant.def.plant.harvestedThingDef;
                var food = humanFood(resource);
                result.Acquisition.Add(new Obs.AcquisitionFacts {
                    Source = new Obs.EntityRef { Id = plant.GetUniqueLoadID(), DefName = plant.def.defName, MapId = map.uniqueID,
                        Position = new Common.Cell { X = plant.Position.x, Z = plant.Position.z }, Snapshot = Snapshot(plant, result.Context) },
                    Resource = resource.defName, Tree = plant.def.plant.IsTree, Food = food, Yield = yield,
                    NutritionYield = food ? yield * resource.GetStatValueAbstract(StatDefOf.Nutrition) : 0,
                    Designated = ResourceAcquisitionTools.Designated(plant), Hunt = false });
            }
            var pending = plants.Where(ResourceAcquisitionTools.Designated).ToArray();
            result.PendingFoodNutrition = pending.Where(p => humanFood(p.def.plant.harvestedThingDef)).Sum(p => (double)p.YieldNow() * p.def.plant.harvestedThingDef.GetStatValueAbstract(StatDefOf.Nutrition));
            NativeHuntAcquisition.Read(result, map, center, limit);
            result.PendingWoodUnits = pending.Where(p => p.def.plant.harvestedThingDef == ThingDefOf.WoodLog).Sum(p => (double)p.YieldNow());
        }
        internal const string Kind = "Plant acquisition";
        // Cutter is the colonist rule shared by the census (Eligible) and the
        // apply-time check: someone must be able to do the work now.
        internal static bool Cutter(Pawn p, Plant plant) => p.workSettings?.Initialized == true
            && p.workSettings.GetPriority(WorkTypeDefOf.PlantCutting) > 0 && !p.WorkTypeIsDisabled(WorkTypeDefOf.PlantCutting)
            && !p.Downed && !p.Drafted && !p.InMentalState && !plant.IsForbidden(p)
            && p.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation) && p.Position.DistanceTo(plant.Position) <= 50
            && p.CanReach(plant, PathEndMode.Touch, Danger.None);
        // Prepare is the apply-time precondition list for cut/harvest
        // (action-contracts.md): the conjunction is Eligible plus the
        // request's own cell, resource and designation rules, evaluated one
        // rule at a time so a refusal names the fact that moved.
        private static bool Prepare(Operations.AcquireResource command, Common.ObservationContext context, out Plant? plant, out Common.Failure failure)
        {
            plant = null; failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Acquisition requires an exact safe mature wild plant snapshot.");
            if (!Valid(command) || !NativeAcquisitionTracking.Ready) return false;
            var map = ProtoBoundary.LoadedMap(context);
            var found = map.listerThings.AllThings.OfType<Plant>().SingleOrDefault(p => p.GetUniqueLoadID() == command.Source.EntityId);
            var rules = new ApplyPreconditions(Kind)
                .Require(() => !map.AllCells.Any(c => map.roofCollapseBuffer.IsMarkedToCollapse(c)), "a roof collapse is pending on this map")
                .Present(() => found != null && found.Spawned && ProtoBoundary.IsLoaded(found.Map), "the exact plant is no longer spawned on this map")
                .Require(() => found!.Position.x == command.Cell.X && found.Position.z == command.Cell.Z, "the plant is not at the expected cell")
                .Require(() => found!.def.plant.harvestedThingDef?.defName == command.ResourceDefName, "the plant no longer yields the expected resource")
                .Require(() => !found!.Position.Fogged(map), "the plant's cell is fogged")
                .Require(() => !found!.IsForbidden(Faction.OfPlayer), "the plant is forbidden")
                .Require(() => found!.HarvestableNow, "the plant is not harvestable now")
                .Require(() => !(map.zoneManager.ZoneAt(found!.Position) is Zone_Growing), "the plant stands in a growing zone")
                .Require(() => !ResourceAcquisitionTools.Designated(found!), "the plant is already designated")
                .Require(() => ResourceAcquisitionTools.DesignatorFor(found!).CanDesignateThing(found).Accepted, "the native designator refuses the plant")
                .Require(() => map.mapPawns.FreeColonistsSpawned.Any(p => Cutter(p, found!)), "no free colonist with plant cutting enabled can reach the plant")
                .Token(NativeDraftProtocol.TokenSent(command.Source), () => Snapshot(found!, context).Token == command.Source.ExpectedSnapshotToken, "the plant snapshot changed since it was read");
            if (!rules.Holds) { failure = rules.Failure(); return false; }
            plant = found;
            return true;
        }
        internal static Operations.PreviewReply Preview(Operations.AcquireResource command, Common.ObservationContext context)
        {
            try
            {
                if (NativeHuntAcquisition.IsHunt(command, context)) return NativeHuntAcquisition.Preview(command, context);
                if (NativeMineAcquisition.IsMine(command, context)) return NativeMineAcquisition.Preview(command, context);
                if (!Prepare(command, context, out _, out var failure)) return new Operations.PreviewReply { Failure = failure };
                return new Operations.PreviewReply { Evaluated = new Operations.PreviewEvaluation { Context = context.Clone(), Accepted = true } };
            }
            catch (Exception error) { return new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Acquisition preview failed: " + error.GetType().Name) }; }
        }
        internal static bool ValidCancel(Operations.CancelAcquisition? command) => command != null && NativeDraftProtocol.ValidEntityTokenOptional(command.Source)
            && command.HasResourceDefName && ProtoBoundary.IsIdentifier(command.ResourceDefName)
            && command.Cell != null && command.Cell.HasX && command.Cell.HasZ && command.Cell.X >= 0 && command.Cell.Z >= 0;
        // Cancel withdraws the harvest designation of a plant this controller
        // designated and nobody took (#291). It is admitted under its own
        // attempt of the same action; the applied evidence is the record's
        // effect with designated=false, and the record then observes
        // unsuccessful so the journal settles the cancelled action. A plant
        // with no live record (native restarted, harvest finished) or that is
        // already undesignated applies as a no-op on the same evidence.
        internal static Operations.ExecuteReply Cancel(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context)
        {
            var hunt = NativeHuntAcquisition.WithdrawalRecord(state, request.Precondition.Attempt);
            if (hunt != null) return NativeHuntAcquisition.Cancel(state, request, context, hunt);
            NativeAttemptLedger.Admission? handle = null; Receipts.EffectEvidence? evidence = null;
            var pre = request.Precondition; var command = request.Operation.CancelAcquisition;
            try
            {
                if (!ValidCancel(command) || !NativeAcquisitionTracking.Ready)
                    return new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Cancellation requires an exact plant acquisition.") };
                var map = ProtoBoundary.LoadedMap(context);
                var found = map.listerThings.AllThings.OfType<Plant>().SingleOrDefault(p => p.GetUniqueLoadID() == command.Source.EntityId);
                var record = found == null ? null : NativeAcquisitionTracking.Tracked(found);
                if (record == null || record.Resource != command.ResourceDefName || record.Cell.x != command.Cell.X || record.Cell.z != command.Cell.Z)
                    return new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NotFound, "No live acquisition record for the plant.") };
                if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null)
                    return new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.AuthorityRequired, "Native authority is required.") };
                var guard = authority.Check(pre.ExpectedGeneration);
                context.NativeGeneration = guard.Snapshot.Generation;
                if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) };
                var admitted = state.Ledger.Admit("rimgovernor.operations.v1.Operations/Execute", request, context);
                if (admitted.Kind != NativeAttemptLedger.DecisionKind.Admitted) return admitted.DecidedReply;
                handle = admitted.AdmittedHandle;
                state.Acquisition.Add(pre.Attempt.Clone(), record);
                using (authority.Owned())
                {
                    if (!authority.Check(pre.ExpectedGeneration).Success) throw new InvalidOperationException("Authority moved before cancellation.");
                    var manager = map.designationManager;
                    foreach (var def in new[] { DesignationDefOf.HarvestPlant, DesignationDefOf.CutPlant })
                    {
                        var designation = manager.DesignationOn(found!, def);
                        if (designation != null) manager.RemoveDesignation(designation);
                    }
                    evidence = new Receipts.EffectEvidence { Acquisition = record.Evidence() };
                    if (evidence.Acquisition.Designated) throw new InvalidOperationException("Harvest designation survived cancellation.");
                }
                return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, evidence) };
            }
            catch (Exception error)
            {
                return handle == null ? new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Acquisition cancellation failed: " + error.GetType().Name) }
                    : new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, evidence, "Admitted cancellation requires observation: " + error.GetType().Name) };
            }
        }
        internal static Operations.ExecuteReply Execute(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context)
        {
            if (NativeHuntAcquisition.IsHunt(request.Operation.AcquireResource, context)) return NativeHuntAcquisition.Execute(state, request, context);
            if (NativeMineAcquisition.IsMine(request.Operation.AcquireResource, context)) return NativeMineAcquisition.Execute(state, request, context);
            NativeAttemptLedger.Admission? handle = null; Receipts.EffectEvidence? evidence = null;
            var pre = request.Precondition; var command = request.Operation.AcquireResource;
            try
            {
                if (!Prepare(command, context, out var plant, out var failure)) return new Operations.ExecuteReply { Failure = failure };
                if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null)
                    return new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.AuthorityRequired, "Native authority is required.") };
                var guard = authority.Check(pre.ExpectedGeneration);
                context.NativeGeneration = guard.Snapshot.Generation;
                if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) };
                var admitted = state.Ledger.Admit("rimgovernor.operations.v1.Operations/Execute", request, context);
                if (admitted.Kind != NativeAttemptLedger.DecisionKind.Admitted) return admitted.DecidedReply;
                handle = admitted.AdmittedHandle;
                var record = new NativeAcquisitionRecord(plant!);
                state.Acquisition.Add(pre.Attempt.Clone(), record);
                using (authority.Owned())
                {
                    if (!authority.Check(pre.ExpectedGeneration).Success
                        || !Prepare(command, context, out var checkedPlant, out failure) || !ReferenceEquals(plant, checkedPlant)
                        || !NativeAcquisitionTracking.Track(record)) throw new InvalidOperationException("Acquisition changed before designation.");
                    ResourceAcquisitionTools.DesignatorFor(plant!).DesignateThing(plant);
                    evidence = new Receipts.EffectEvidence { Acquisition = record.Evidence() };
                    if (!evidence.Acquisition.Designated) throw new InvalidOperationException("Native acquisition designation was not observed.");
                }
                return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, evidence) };
            }
            catch (Exception error)
            {
                return handle == null ? new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Acquisition admission failed: " + error.GetType().Name) }
                    : new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, evidence, "Admitted acquisition requires observation: " + error.GetType().Name) };
            }
        }
    }
}
