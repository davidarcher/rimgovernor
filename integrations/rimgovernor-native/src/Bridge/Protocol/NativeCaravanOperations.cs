#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Authority = RimGovernor.Protocol.Authority;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // Typed dispatch for FormCaravan: ports legacy CaravanTools.Execute's
    // "form" action (home/caravan) behind the typed boundary. Formation and
    // departure happen in the same native call (TryFormAndSendCaravan), so
    // Execute's effect evidence and this record's later Observe both describe
    // "did native form and start this caravan", not ongoing travel/arrival --
    // that remains future scope (see G01.07f).
    internal sealed class NativeCaravanRecord
    {
        internal readonly string? CaravanId;
        internal readonly int DestinationTile;
        internal readonly string[] PawnIds;
        internal NativeCaravanRecord(string? caravanId, int destinationTile, string[] pawnIds)
        { CaravanId = caravanId; DestinationTile = destinationTile; PawnIds = pawnIds; }
    }

    internal static class NativeCaravanOperations
    {
        private static bool Valid(Operations.FormCaravan? command) => command != null
            && command.HasExpectedCatalogToken && ProtoBoundary.IsIdentifier(command.ExpectedCatalogToken)
            && command.PawnIds.Count > 0 && command.PawnIds.Count <= 64
            && command.PawnIds.All(ProtoBoundary.IsIdentifier)
            && command.PawnIds.Distinct().Count() == command.PawnIds.Count
            && command.Cargo.Count <= 256
            && command.Cargo.All(c => c.HasGroupId && ProtoBoundary.IsIdentifier(c.GroupId) && c.HasCount && c.Count > 0)
            && command.Cargo.Select(c => c.GroupId).Distinct().Count() == command.Cargo.Count
            && command.HasDestinationTile && command.DestinationTile >= 0;

        // Recomputes the native catalog dialog and checks it against the
        // command's snapshot, then resolves and applies the exact requested
        // crew/cargo selection and destination. FormCaravan carries no
        // per-pawn precondition (unlike ImproveGear's EntityPrecondition);
        // freshness is enforced once here through the catalog token, which
        // covers both cargo availability and home-colonist eligibility.
        // Mirrors legacy CaravanTools.Execute's "form" branch exactly.
        private static bool Prepare(Operations.FormCaravan command, Common.ObservationContext context,
            out Map? map, out Dialog_FormCaravan? dialog, out List<Pawn>? pawns, out Common.Failure failure)
        {
            map = Find.CurrentMap; dialog = null; pawns = null;
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest,
                "Formation requires an exact current catalog snapshot, unique crew and cargo, and a valid destination.");
            if (!Valid(command)) return false;
            if (map == null || !Find.TickManager.Paused)
            { failure = ProtoBoundary.Fail(Common.FailureCode.Unavailable, "A loaded paused map is required."); return false; }
            dialog = NativeCaravanCatalog.BuildDialog(map);
            var pawnRows = NativeCaravanCatalog.PawnRows(map, context);
            var cargoRows = NativeCaravanCatalog.CargoGroups(dialog);
            if (NativeCaravanCatalog.Token(context, cargoRows, pawnRows) != command.ExpectedCatalogToken)
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Caravan catalog changed; observe before new admission."); return false; }
            var selected = new List<Pawn>();
            foreach (var id in command.PawnIds)
            {
                var group = dialog.transferables.SingleOrDefault(g => g.AnyThing is Pawn p && p.GetUniqueLoadID() == id);
                var pawn = group?.AnyThing as Pawn;
                if (pawn == null || !NativeCaravanCatalog.PawnEligible(pawn))
                { failure = ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact eligible home colonist is unavailable: " + id); return false; }
                selected.Add(pawn);
                group!.ForceToDestination(1);
            }
            if (map.mapPawns.FreeColonistsSpawned.Count <= selected.Count)
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "At least one colonist must remain home."); return false; }
            foreach (var item in command.Cargo)
            {
                var group = dialog.transferables.SingleOrDefault(g => !(g.AnyThing is Pawn) && NativeCaravanCatalog.GroupId(g) == item.GroupId);
                if (group == null || item.Count > group.MaxCount)
                { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Cargo group changed or requested quantity is unavailable: " + item.GroupId); return false; }
                group.ForceToDestination(item.Count);
            }
            var tile = new PlanetTile(command.DestinationTile);
            if (!tile.Valid || tile.tileId >= Find.WorldGrid.TilesCount || tile == map.Tile)
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Choose a different valid observed surface tile."); return false; }
            var exit = CaravanExitMapUtility.BestExitTileToGoTo(tile, map);
            if (!exit.Valid)
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "No native caravan exit route."); return false; }
            NativeCaravanCatalog.Set(dialog, "destinationTile", tile);
            NativeCaravanCatalog.Set(dialog, "startingTile", exit);
            NativeCaravanCatalog.Call(dialog, "Notify_TransferablesChanged");
            if (dialog.MassUsage > dialog.MassCapacity)
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Cargo exceeds native carrying capacity."); return false; }
            var food = NativeCaravanCatalog.FoodDays(dialog);
            if (food.days < 1f)
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "At least one native day of caravan food is required."); return false; }
            // This native check emits refusal messages, but never creates a lord or transfers cargo.
            if (!(bool)NativeCaravanCatalog.Call(dialog, "CheckForErrors", selected)!)
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Native caravan eligibility refused; inspect game messages."); return false; }
            pawns = selected;
            return true;
        }

        internal static Operations.PreviewReply Preview(Operations.FormCaravan command, Common.ObservationContext context)
        {
            try
            {
                if (!Prepare(command, context, out var map, out var dialog, out var pawns, out var failure))
                    return new Operations.PreviewReply { Failure = failure };
                var carried = new List<Operations.DefCount>();
                foreach (var group in pawns!.SelectMany(p => p.inventory.innerContainer).GroupBy(t => t.def))
                    carried.Add(new Operations.DefCount { DefName = group.Key.defName, Count = group.Sum(t => t.stackCount) });
                var selectedCargo = new List<Operations.DefCount>();
                foreach (var group in dialog!.transferables.Where(g => !(g.AnyThing is Pawn) && g.CountToTransfer > 0).GroupBy(g => g.ThingDef))
                    selectedCargo.Add(new Operations.DefCount { DefName = group.Key.defName, Count = group.Sum(g => g.CountToTransfer) });
                var materials = new List<Operations.StockAvailability>();
                foreach (var group in dialog.transferables.Where(g => !(g.AnyThing is Pawn)).GroupBy(g => g.ThingDef))
                    materials.Add(new Operations.StockAvailability { DefName = group.Key.defName, Available = group.Sum(g => g.MaxCount) });
                var homePawns = map!.mapPawns.FreeColonistsSpawned.Where(p => !pawns!.Contains(p)).ToList();
                var food = NativeCaravanCatalog.FoodDays(dialog);
                var (route, _) = NativeCaravanCatalog.RouteFacts(map, dialog, command.DestinationTile);
                var routePrep = new Operations.RoutePreparation
                {
                    Reachable = route.Reachable, FoodDays = food.days, FoodRotDays = food.tillRot,
                    DestinationTile = command.DestinationTile,
                    Temperature = (float)GenTemperature.GetTemperatureFromSeasonAtTile(Find.TickManager.TicksAbs, new PlanetTile(command.DestinationTile)),
                };
                if (route.HasEstimatedTicks) routePrep.EstimatedTicks = route.EstimatedTicks;
                var caravanPrep = new Operations.CaravanPreparation
                {
                    MassUsage = dialog.MassUsage, MassCapacity = dialog.MassCapacity,
                    HomeDoctors = homePawns.Count(p => !p.Downed && !p.WorkTypeIsDisabled(WorkTypeDefOf.Doctor)),
                    Route = routePrep,
                };
                caravanPrep.SelectedCargo.Add(selectedCargo);
                caravanPrep.CarriedCargo.Add(carried);
                caravanPrep.Materials.Add(materials);
                caravanPrep.HomePawnIds.Add(homePawns.Select(p => p.GetUniqueLoadID()));
                var projected = new Receipts.EffectEvidence { Caravan = new Receipts.CaravanEffect { DestinationTile = command.DestinationTile, AssemblyStarted = true } };
                projected.Caravan.PawnIds.Add(command.PawnIds);
                var evaluation = new Operations.PreviewEvaluation { Context = context.Clone(), Accepted = true, Projected = projected, Caravan = caravanPrep };
                return NativeOperationEnvelope.Preview(new Operations.PreviewReply { Evaluated = evaluation });
            }
            catch (Exception error) { return new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Caravan formation preview failed: " + error.GetType().Name) }; }
        }

        internal static Operations.ExecuteReply Execute(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context)
        {
            var command = request.Operation.FormCaravan; var pre = request.Precondition;
            if (!Valid(command))
                return Refuse(Common.FailureCode.InvalidRequest, "Formation requires an exact current catalog snapshot, unique crew and cargo, and a valid destination.");
            NativeAttemptLedger.Admission? handle = null; Authority.Owner? owner = null; Receipts.EffectEvidence? evidence = null;
            try
            {
                if (!Prepare(command, context, out var map, out var dialog, out var pawns, out var failure))
                    return new Operations.ExecuteReply { Failure = failure };
                if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null)
                    return Refuse(Common.FailureCode.AuthorityRequired, "Current native authority is required.");
                var guard = authority.Check(pre.ExpectedGeneration, pre.LeaseId, pre.Attempt.ControllerSessionId);
                context.NativeGeneration = guard.Snapshot.Generation;
                if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) };
                owner = new Authority.Owner { ControllerSessionId = guard.Snapshot.Lease!.ControllerSessionId, PlayerDirection = guard.Snapshot.Lease.PlayerDirection };
                var admission = state.Ledger.Admit("rimgovernor.operations.v1.Operations/Execute", request, context, owner);
                if (admission.Kind != NativeAttemptLedger.DecisionKind.Admitted) return admission.Reply!;
                handle = admission.Handle!;
                using (authority.Owned())
                {
                    var current = authority.Check(pre.ExpectedGeneration, pre.LeaseId, pre.Attempt.ControllerSessionId);
                    if (!current.Success) throw new InvalidOperationException("Caravan formation authority changed before native effect.");
                    if (!Prepare(command, context, out map, out dialog, out pawns, out failure) || map == null || dialog == null || pawns == null)
                        throw new InvalidOperationException("Caravan formation prerequisites changed after admission.");
                    var expectedIds = pawns.Select(p => p.GetUniqueLoadID()).OrderBy(id => id, StringComparer.Ordinal).ToArray();
                    bool accepted = (bool)NativeCaravanCatalog.Call(dialog, "TryFormAndSendCaravan")!;
                    var caravan = accepted
                        ? Find.WorldObjects.Caravans.SingleOrDefault(c => c.IsPlayerControlled
                            && c.PawnsListForReading.Select(p => p.GetUniqueLoadID()).OrderBy(id => id, StringComparer.Ordinal).SequenceEqual(expectedIds))
                        : null;
                    var record = new NativeCaravanRecord(caravan?.GetUniqueLoadID(), command.DestinationTile, command.PawnIds.ToArray());
                    state.Caravans.Add(pre.Attempt.Clone(), record);
                    var effect = new Receipts.CaravanEffect
                    {
                        CaravanId = record.CaravanId, AssemblyStarted = accepted, PathStarted = caravan != null && caravan.pather.Moving,
                        DestinationTile = command.DestinationTile,
                    };
                    effect.PawnIds.Add(command.PawnIds);
                    evidence = new Receipts.EffectEvidence { Caravan = effect };
                    if (!accepted || caravan == null) throw new InvalidOperationException("Native caravan formation readback did not apply.");
                }
                return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, owner, evidence) };
            }
            catch (Exception error)
            {
                return handle == null
                    ? Refuse(Common.FailureCode.NativeFailure, "Caravan formation validation failed: " + error.GetType().Name)
                    : new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, owner!, evidence!, "Admitted caravan formation requires observation: " + error.GetType().Name) };
            }
        }

        internal static Receipts.Progress Observe(Common.AttemptKey attempt, Common.ObservationContext context, NativeCaravanRecord record)
        {
            var result = new Receipts.Progress { Attempt = attempt.Clone(), Context = context.Clone(), CompleteInspection = false };
            try
            {
                if (record.CaravanId == null)
                {
                    result.CompleteInspection = true;
                    var effect = new Receipts.CaravanEffect { AssemblyStarted = false, DestinationTile = record.DestinationTile };
                    effect.PawnIds.Add(record.PawnIds);
                    result.Unsuccessful = new Receipts.UnsuccessfulEffect { Reason = Receipts.UnsuccessfulReason.OutcomeNotAchieved,
                        Evidence = new Receipts.EffectEvidence { Caravan = effect }, Detail = "Native caravan formation was not accepted." };
                    return result;
                }
                var caravan = Find.WorldObjects.Caravans.SingleOrDefault(c => c.IsPlayerControlled && c.GetUniqueLoadID() == record.CaravanId);
                if (caravan == null)
                {
                    result.Unknown = new Receipts.UnknownEffect { Reason = "The formed caravan is no longer observable; arrival or disbanding is not tracked yet." };
                    return result;
                }
                result.CompleteInspection = true;
                var current = new Receipts.CaravanEffect
                {
                    CaravanId = record.CaravanId, AssemblyStarted = true, PathStarted = caravan.pather.Moving, Stopped = !caravan.pather.Moving,
                    DestinationTile = record.DestinationTile,
                };
                current.PawnIds.Add(caravan.PawnsListForReading.Select(p => p.GetUniqueLoadID()).Where(id => record.PawnIds.Contains(id)));
                result.Completed = new Receipts.CompletedEffect { Evidence = new Receipts.EffectEvidence { Caravan = current } };
            }
            catch (Exception) { result.CompleteInspection = false; result.Unknown = new Receipts.UnknownEffect { Reason = "Caravan formation inspection unavailable." }; }
            return result;
        }

        private static Operations.ExecuteReply Refuse(Common.FailureCode code, string detail) => new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(code, detail) };
    }
}
