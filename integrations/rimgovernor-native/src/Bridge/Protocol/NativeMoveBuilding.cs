#nullable enable
using System;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;
using Placement = RimGovernor.Protocol.Placement;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // The move family's native half (#808): InstallBuilding on one exact
    // installed player building places the game's reinstall blueprint at the
    // destination through GenConstruct.PlaceBlueprintForReinstall, the write
    // the Reinstall gizmo makes, without its WipeExistingThings (nothing at
    // the destination is destroyed; a blocked cell is a refusal). Ordinary
    // construction work (WorkGiver_ConstructDeliverResourcesToBlueprints'
    // InstallJob) then uninstalls and carries the piece; it needs CanReserve
    // on the piece, so a pawn sleeping in a bed or working a bench is never
    // interrupted and the move waits. The piece keeps its identity, quality
    // and hit points. A packed (minified) item named by its own id or its
    // inner building's (#830) gets the game's install blueprint instead
    // (GenConstruct.PlaceBlueprintForInstall); the effect always names the
    // inner building, so observation is the same for both.
    internal static class NativeMoveBuilding
    {
        internal const string Kind = "Move building";

        private static bool Valid(Operations.InstallBuilding? command) => command != null
            && NativeDraftProtocol.ValidEntityId(command.PackedOrInner) && command.Destination != null
            && command.Destination.HasX && command.Destination.HasZ && command.HasRotation
            && command.Rotation >= Placement.Rotation.North && command.Rotation <= Placement.Rotation.West;

        private static Rot4 Rotation(Operations.InstallBuilding command) => new Rot4((int)command.Rotation - 1);

        internal static Building? Find(Map map, string id) => map.listerThings.AllThings.OfType<Building>()
            .FirstOrDefault(b => b.GetUniqueLoadID() == id);

        // Packed: the exact spawned MinifiedThing named by its own id or its
        // inner building's (#830; the packed half ported from the retired
        // home/install tool).
        private static MinifiedThing? FindPacked(Map map, string id) => map.listerThings.AllThings.OfType<MinifiedThing>()
            .FirstOrDefault(t => t.GetUniqueLoadID() == id || t.InnerThing?.GetUniqueLoadID() == id);

        // Mover: someone with construction enabled must be able to reach
        // the piece now; whether it is free is the game's reservation, later.
        internal static bool Mover(Pawn p, Thing piece) => p.workSettings?.Initialized == true
            && p.workSettings.GetPriority(WorkTypeDefOf.Construction) > 0 && !p.WorkTypeIsDisabled(WorkTypeDefOf.Construction)
            && !p.Downed && !p.InMentalState && p.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)
            && p.CanReach(piece, PathEndMode.ClosestTouch, Danger.None);

        private static bool PreparePacked(Operations.InstallBuilding command, Map map, MinifiedThing mini, out Common.Failure failure)
        {
            var cell = new IntVec3(command.Destination.X, 0, command.Destination.Z);
            var rotation = Rotation(command);
            var inner = mini.InnerThing as Building;
            var rules = new ApplyPreconditions(Kind)
                .Present(() => inner != null && !mini.Destroyed && mini.Spawned && ProtoBoundary.IsLoaded(mini.Map), "the exact packed building is not on this map")
                .Require(() => inner!.Faction == null || inner.Faction == Faction.OfPlayer, "the packed building is not the player's")
                .Require(() => !mini.Position.Fogged(map) && !mini.IsForbidden(Faction.OfPlayer), "the packed building is fogged or forbidden")
                .Require(() => inner!.def.rotatable || rotation == Rot4.North, "the building is not rotatable; only north is valid")
                .Require(() => InstallBlueprintUtility.ExistingBlueprintFor(mini) == null, "the packed building already has an install blueprint")
                .Require(() => cell.InBounds(map) && !cell.Fogged(map), "the destination is out of bounds or fogged")
                .Require(() => GenConstruct.CanPlaceBlueprintAt(inner!.def, cell, rotation, map, false, mini, inner).Accepted, "the game refuses an install blueprint at the destination")
                .Require(() => map.mapPawns.FreeColonistsSpawned.Any(p => Mover(p, mini)), "no free colonist with construction enabled can reach the packed building");
            failure = rules.Holds ? ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "") : rules.Failure();
            return rules.Holds;
        }

        // Prepare is the apply-time precondition list for InstallBuilding
        // (action-contracts.md), one rule at a time so a refusal names the
        // fact that moved. piece is the packed item for a packed install,
        // null for a move of a standing building.
        private static bool Prepare(Operations.InstallBuilding command, Common.ObservationContext context, out Thing? piece, out Building? building, out Common.Failure failure)
        {
            piece = null;
            building = null;
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "InstallBuilding requires an exact building, a destination cell and a cardinal rotation.");
            if (!Valid(command)) return false;
            var map = ProtoBoundary.LoadedMap(context);
            var found = Find(map, command.PackedOrInner.EntityId);
            if (found == null && FindPacked(map, command.PackedOrInner.EntityId) is MinifiedThing mini)
            {
                if (!PreparePacked(command, map, mini, out failure)) return false;
                piece = mini;
                building = (Building)mini.InnerThing;
                return true;
            }
            var cell = new IntVec3(command.Destination.X, 0, command.Destination.Z);
            var rotation = Rotation(command);
            var rules = new ApplyPreconditions(Kind)
                .Present(() => found != null && !found.Destroyed && found.Spawned && ProtoBoundary.IsLoaded(found.Map), "the exact building is not installed on this map")
                .Require(() => found!.Faction == Faction.OfPlayer, "the building is not the player's")
                .Require(() => found!.def.Minifiable, "the building cannot be uninstalled")
                .Require(() => found!.def.rotatable || rotation == Rot4.North, "the building is not rotatable; only north is valid")
                .Require(() => !(found!.Position == cell && found.Rotation == rotation), "the building already stands at the destination")
                .Require(() => InstallBlueprintUtility.ExistingBlueprintFor(found!) == null, "the building already has a reinstall blueprint")
                .Require(() => map.designationManager.DesignationOn(found!, DesignationDefOf.Uninstall) == null
                    && map.designationManager.DesignationOn(found!, DesignationDefOf.Deconstruct) == null, "the building is designated for uninstall or deconstruction")
                .Require(() => cell.InBounds(map) && !cell.Fogged(map), "the destination is out of bounds or fogged")
                .Require(() => GenConstruct.CanPlaceBlueprintAt(found!.def, cell, rotation, map, false, found, found).Accepted, "the game refuses a reinstall blueprint at the destination")
                .Require(() => map.mapPawns.FreeColonistsSpawned.Any(p => Mover(p, found!)), "no free colonist with construction enabled can reach the building");
            if (!rules.Holds) { failure = rules.Failure(); return false; }
            building = found;
            return true;
        }

        private static Receipts.InstallationEffect Effect(Building building, Operations.InstallBuilding command, Receipts.InstallationStage stage, string? blueprint) =>
            new Receipts.InstallationEffect {
                InnerThingId = building.GetUniqueLoadID(), DefName = building.def.defName, Stuff = building.Stuff?.defName ?? "",
                Cell = new Common.Cell { X = command.Destination.X, Z = command.Destination.Z }, Rotation = command.Rotation,
                Stage = stage, BlueprintId = blueprint ?? "" };

        internal static Operations.PreviewReply Preview(Operations.InstallBuilding command, Common.ObservationContext context)
        {
            try
            {
                if (!Prepare(command, context, out var piece, out var building, out var failure)) return new Operations.PreviewReply { Failure = failure };
                return new Operations.PreviewReply { Evaluated = new Operations.PreviewEvaluation {
                    Context = context.Clone(), Accepted = true,
                    Projected = new Receipts.EffectEvidence { Installation = Effect(building!, command, Receipts.InstallationStage.Placeable, null) } } };
            }
            catch (Exception error) { return new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Move building preview failed: " + error.GetType().Name) }; }
        }

        internal static Operations.ExecuteReply Execute(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context)
        {
            NativeAttemptLedger.Admission? handle = null;
            Receipts.EffectEvidence? evidence = null;
            var pre = request.Precondition;
            var command = request.Operation.InstallBuilding;
            try
            {
                if (!Prepare(command, context, out var piece, out var building, out var failure)) return new Operations.ExecuteReply { Failure = failure };
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
                    if (!current.Success || !Prepare(command, context, out var checkedPiece, out var checkedBuilding, out failure)
                        || !ReferenceEquals(checkedBuilding, building) || !ReferenceEquals(checkedPiece, piece)) throw new InvalidOperationException("Move building admission changed before effect.");
                    var cell = new IntVec3(command.Destination.X, 0, command.Destination.Z);
                    // Packed: Designator_Install's placement without its
                    // WipeExistingThings (a blocked cell was a refusal above).
                    Blueprint_Install? blueprint = piece is MinifiedThing mini
                        ? GenConstruct.PlaceBlueprintForInstall(mini, cell, mini.Map, Rotation(command), Faction.OfPlayer)
                        : GenConstruct.PlaceBlueprintForReinstall(building!, cell, building!.Map, Rotation(command), Faction.OfPlayer);
                    if (blueprint == null || !blueprint.Spawned) throw new InvalidOperationException("Native install blueprint was not observed.");
                    evidence = new Receipts.EffectEvidence { Installation = Effect(building!, command, Receipts.InstallationStage.Queued, blueprint.GetUniqueLoadID()) };
                    state.Moves.Add(pre.Attempt.Clone(), evidence.Installation.Clone());
                }
                return new Operations.ExecuteReply { Receipt = state.Ledger.FinishApplied(handle, evidence) };
            }
            catch (Exception error)
            {
                return handle == null
                    ? new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Move building admission failed: " + error.GetType().Name) }
                    : new Operations.ExecuteReply { Receipt = state.Ledger.FinishUncertain(handle, evidence, "Admitted InstallBuilding requires observation: " + error.GetType().Name) };
            }
        }

        // Observe: the exact building installed at the destination and
        // rotation completes the move; the reinstall blueprint still standing
        // is pending (the piece waits for a free hauler, or for whoever is
        // using it); the blueprint gone without the move is unsuccessful
        // (cancelled or blocked), never re-placed here.
        internal static Receipts.Progress Observe(Common.AttemptKey attempt, Common.ObservationContext context, Receipts.InstallationEffect original)
        {
            var result = new Receipts.Progress { Attempt = attempt.Clone(), Context = context.Clone(), CompleteInspection = true };
            try
            {
                var map = ProtoBoundary.LoadedMap(context);
                var building = Find(map, original.InnerThingId);
                var rotation = new Rot4((int)original.Rotation - 1);
                if (building != null && building.Spawned && building.Position.x == original.Cell.X && building.Position.z == original.Cell.Z && building.Rotation == rotation)
                {
                    var installed = original.Clone(); installed.Stage = Receipts.InstallationStage.Installed; installed.BlueprintId = "";
                    result.Completed = new Receipts.CompletedEffect { Evidence = new Receipts.EffectEvidence { Installation = installed } };
                    return result;
                }
                var blueprint = map.listerThings.AllThings.OfType<Blueprint_Install>().FirstOrDefault(b => b.GetUniqueLoadID() == original.BlueprintId);
                if (blueprint != null && blueprint.Spawned)
                {
                    result.Pending = new Receipts.PendingEffect { Evidence = new Receipts.EffectEvidence { Installation = original.Clone() } };
                    return result;
                }
                var failed = original.Clone(); failed.Stage = Receipts.InstallationStage.Unverified;
                result.Unsuccessful = new Receipts.UnsuccessfulEffect { Reason = Receipts.UnsuccessfulReason.OutcomeNotAchieved,
                    Evidence = new Receipts.EffectEvidence { Installation = failed },
                    Detail = "The reinstall blueprint is gone and the building does not stand at the destination; a cancelled move is not repeated." };
                return result;
            }
            catch (Exception)
            {
                result.CompleteInspection = false;
                result.Unknown = new Receipts.UnknownEffect { Reason = "The exact building could not be inspected; absence does not prove the move." };
                return result;
            }
        }
    }
}
