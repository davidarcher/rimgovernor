#nullable enable
using System;
using System.Linq;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;
using Placement = RimGovernor.Protocol.Placement;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // The uninstall half of the move family (#843): Uninstall on one exact
    // installed player building places the game's Uninstall designation, the
    // write Designator_Uninstall makes. Ordinary construction work
    // (WorkGiver_Uninstall) then minifies the piece where it stands; it needs
    // CanReserve on the piece, so a pawn sleeping in a bed is never
    // interrupted and the uninstall waits. Native does not haul: vanilla
    // hauling takes the packed item to storage. An Uninstall designation
    // already on the building is adopted. The effect is the
    // InstallationEffect at the building's current placement.
    internal static class NativeUninstallBuilding
    {
        internal const string Kind = "Uninstall building";

        private static bool Prepare(Operations.Uninstall? command, Common.ObservationContext context, out Building? building, out Common.Failure failure)
        {
            building = null;
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Uninstall requires an exact building.");
            if (command == null || !NativeDraftProtocol.ValidEntityId(command.Target)) return false;
            var map = ProtoBoundary.LoadedMap(context);
            var found = NativeMoveBuilding.Find(map, command.Target.EntityId);
            var rules = new ApplyPreconditions(Kind)
                .Present(() => found != null && !found.Destroyed && found.Spawned && ProtoBoundary.IsLoaded(found.Map), "the exact building is not installed on this map")
                .Require(() => found!.Faction == Faction.OfPlayer, "the building is not the player's")
                .Require(() => found!.def.Minifiable, "the building cannot be uninstalled")
                .Require(() => InstallBlueprintUtility.ExistingBlueprintFor(found!) == null, "the building has a reinstall blueprint")
                .Require(() => map.designationManager.DesignationOn(found!, DesignationDefOf.Deconstruct) == null, "the building is designated for deconstruction")
                .Require(() => map.mapPawns.FreeColonistsSpawned.Any(p => NativeMoveBuilding.Mover(p, found!)), "no free colonist with construction enabled can reach the building");
            if (!rules.Holds) { failure = rules.Failure(); return false; }
            building = found;
            return true;
        }

        private static Receipts.InstallationEffect Effect(Building building, Receipts.InstallationStage stage) =>
            new Receipts.InstallationEffect {
                InnerThingId = building.GetUniqueLoadID(), DefName = building.def.defName, Stuff = building.Stuff?.defName ?? "",
                Cell = new Common.Cell { X = building.Position.x, Z = building.Position.z },
                Rotation = (Placement.Rotation)(building.Rotation.AsInt + 1), Stage = stage };

        internal static Operations.PreviewReply Preview(Operations.Uninstall command, Common.ObservationContext context)
        {
            try
            {
                if (!Prepare(command, context, out var building, out var failure)) return new Operations.PreviewReply { Failure = failure };
                return NativeOperationEnvelope.Preview(new Operations.PreviewReply { Evaluated = new Operations.PreviewEvaluation {
                    Context = context.Clone(), Accepted = true,
                    Projected = new Receipts.EffectEvidence { Installation = Effect(building!, Receipts.InstallationStage.Placeable) } } });
            }
            catch (Exception error) { return new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Uninstall preview failed: " + error.GetType().Name) }; }
        }

        internal static Operations.ExecuteReply Execute(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context)
        {
            NativeAttemptLedger.Admission? handle = null;
            Receipts.EffectEvidence? evidence = null;
            var pre = request.Precondition;
            var command = request.Operation.Uninstall;
            try
            {
                if (!Prepare(command, context, out var building, out var failure)) return new Operations.ExecuteReply { Failure = failure };
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
                    if (!current.Success || !Prepare(command, context, out var checkedBuilding, out failure) || !ReferenceEquals(checkedBuilding, building))
                        throw new InvalidOperationException("Uninstall admission changed before effect.");
                    var designations = building!.Map.designationManager;
                    if (designations.DesignationOn(building, DesignationDefOf.Uninstall) == null)
                        designations.AddDesignation(new Designation(building, DesignationDefOf.Uninstall));
                    if (designations.DesignationOn(building, DesignationDefOf.Uninstall) == null) throw new InvalidOperationException("Native uninstall designation was not observed.");
                    evidence = new Receipts.EffectEvidence { Installation = Effect(building, Receipts.InstallationStage.UninstallQueued) };
                    state.Uninstalls.Add(pre.Attempt.Clone(), evidence.Installation.Clone());
                }
                return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, evidence) };
            }
            catch (Exception error)
            {
                return handle == null
                    ? new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Uninstall admission failed: " + error.GetType().Name) }
                    : new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, evidence, "Admitted Uninstall requires observation: " + error.GetType().Name) };
            }
        }

        // Packed: the MinifiedThing holding the exact building, spawned
        // (dropped or stored) or in a hauler's hands.
        private static MinifiedThing? Packed(Map map, string inner) =>
            map.listerThings.AllThings.OfType<MinifiedThing>().FirstOrDefault(t => t.InnerThing?.GetUniqueLoadID() == inner)
            ?? map.mapPawns.AllPawnsSpawned.Select(p => p.carryTracker?.CarriedThing).OfType<MinifiedThing>().FirstOrDefault(t => t.InnerThing?.GetUniqueLoadID() == inner);

        // Observe: the building packed completes the uninstall; the building
        // standing with the designation is pending (waiting on a free
        // constructor or on whoever uses it); standing without it is
        // unsuccessful (cancelled), and so is the building gone unpacked.
        internal static Receipts.Progress Observe(Common.AttemptKey attempt, Common.ObservationContext context, Receipts.InstallationEffect original)
        {
            var result = new Receipts.Progress { Attempt = attempt.Clone(), Context = context.Clone(), CompleteInspection = true };
            try
            {
                var map = ProtoBoundary.LoadedMap(context);
                var mini = Packed(map, original.InnerThingId);
                if (mini != null)
                {
                    var packed = original.Clone(); packed.Stage = Receipts.InstallationStage.Packed; packed.PackedThingId = mini.GetUniqueLoadID();
                    result.Completed = new Receipts.CompletedEffect { Evidence = new Receipts.EffectEvidence { Installation = packed } };
                    return result;
                }
                var building = NativeMoveBuilding.Find(map, original.InnerThingId);
                if (building != null && building.Spawned && map.designationManager.DesignationOn(building, DesignationDefOf.Uninstall) != null)
                {
                    result.Pending = new Receipts.PendingEffect { Evidence = new Receipts.EffectEvidence { Installation = original.Clone() } };
                    return result;
                }
                var failed = original.Clone();
                failed.Stage = building != null && building.Spawned ? Receipts.InstallationStage.Installed : Receipts.InstallationStage.Unverified;
                result.Unsuccessful = new Receipts.UnsuccessfulEffect { Reason = Receipts.UnsuccessfulReason.OutcomeNotAchieved,
                    Evidence = new Receipts.EffectEvidence { Installation = failed },
                    Detail = "The uninstall designation is gone and the building is not packed; a cancelled uninstall is not repeated." };
                return result;
            }
            catch (Exception)
            {
                result.CompleteInspection = false;
                result.Unknown = new Receipts.UnknownEffect { Reason = "The exact building could not be inspected; absence does not prove the uninstall." };
                return result;
            }
        }
    }
}
