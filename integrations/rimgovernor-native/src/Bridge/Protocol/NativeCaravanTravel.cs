#nullable enable
using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Authority = RimGovernor.Protocol.Authority;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // Typed dispatch for TravelCaravan: ports legacy CaravanTools.Execute's
    // "move"/"visit"/"return"/"stop" actions (CaravanTool.cs) behind the
    // typed boundary. Unlike FormCaravan (NativeCaravanOperations, which
    // forms and departs in one native call), this family only ever acts on
    // an already-formed, already-observed player caravan through its
    // existing path follower -- there is no dialog/catalog step.
    //
    // CaravanToken is self-computed identically by Go (bridge/travel_caravan.go
    // travelCaravanToken) and here from id/tile/moving only: TravelCaravan
    // carries no expected_pawn_ids in its wire message (unlike
    // GiftCaravanSilver/FulfillQuest), because route/hold admission never
    // depends on exactly which pawns currently ride along. The
    // "caravan-travel-" prefix keeps a stale write intended for this family
    // from ever being admitted as CaravanDeparture/QuestFulfill/
    // SettlementGift's own caravan tokens, and vice versa.
    internal sealed class NativeCaravanTravelRecord
    {
        internal readonly string CaravanId;
        internal readonly Operations.TravelKind Kind;
        internal readonly int DestinationTile;
        internal NativeCaravanTravelRecord(string caravanId, Operations.TravelKind kind, int destinationTile)
        { CaravanId = caravanId; Kind = kind; DestinationTile = destinationTile; }
    }

    internal static class NativeCaravanTravel
    {
        private static string Hash(string text)
        {
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
        }

        // Exact port of Go's travelCaravanToken (bridge/travel_caravan.go).
        internal static string CaravanToken(string caravanId, int tile, bool moving) =>
            "caravan-travel-" + Hash(caravanId + "|" + tile + "|" + (moving ? "true" : "false"));

        private static bool HasDestination(Operations.TravelKind kind) =>
            kind == Operations.TravelKind.Move || kind == Operations.TravelKind.Visit;

        private static bool ValidKind(Operations.TravelKind kind) =>
            kind == Operations.TravelKind.Move || kind == Operations.TravelKind.Visit
            || kind == Operations.TravelKind.ReturnHome || kind == Operations.TravelKind.Stop;

        private static bool Valid(Operations.TravelCaravan? command) => command != null
            && NativeDraftProtocol.ValidEntity(command.Caravan) && command.HasKind && ValidKind(command.Kind)
            && (HasDestination(command.Kind)
                ? command.HasDestinationTile && command.DestinationTile >= 0
                : !command.HasDestinationTile);

        // Re-validates everything a stale read could have gotten wrong: exact
        // caravan identity/position token, and (for Move/Visit/ReturnHome) an
        // exact reachable target with a legal arrival action. Mirrors legacy
        // CaravanTools.Execute's move/visit/return/stop branch exactly.
        private static bool Prepare(Operations.TravelCaravan command, out Caravan? caravan, out PlanetTile target,
            out CaravanArrivalAction? arrival, out Common.Failure failure)
        {
            caravan = null; target = default; arrival = null;
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest,
                "Caravan travel requires an exact current caravan snapshot and, for move/visit, a valid destination.");
            if (!Valid(command)) return false;
            if (Find.TickManager == null || !Find.TickManager.Paused)
            { failure = ProtoBoundary.Fail(Common.FailureCode.Unavailable, "A paused game is required."); return false; }
            caravan = Find.WorldObjects.Caravans.SingleOrDefault(c => c.IsPlayerControlled && c.GetUniqueLoadID() == command.Caravan.EntityId);
            if (caravan == null) { failure = ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact player caravan is unavailable."); return false; }
            if (CaravanToken(caravan.GetUniqueLoadID(), caravan.Tile.tileId, caravan.pather.Moving) != command.Caravan.ExpectedSnapshotToken)
            { failure = ProtoBoundary.Fail(Common.FailureCode.StaleIdentity, "Caravan position changed; observe before new travel order."); return false; }
            if (command.Kind == Operations.TravelKind.Stop) return true;
            var map = Find.CurrentMap;
            target = command.Kind == Operations.TravelKind.ReturnHome ? (map != null ? map.Tile : default) : new PlanetTile(command.DestinationTile);
            if (command.Kind == Operations.TravelKind.ReturnHome && map == null)
            { failure = ProtoBoundary.Fail(Common.FailureCode.Unavailable, "A loaded current map is required to return home."); return false; }
            if (!target.Valid || target.tileId >= Find.WorldGrid.TilesCount || !caravan.CanReach(target))
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Destination is invalid or unreachable."); return false; }
            if (command.Kind == Operations.TravelKind.ReturnHome)
            {
                if (map == null || !map.IsPlayerHome || !CaravanArrivalAction_Enter.CanEnter(caravan, map.Parent))
                { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Current home cannot be entered."); return false; }
                arrival = new CaravanArrivalAction_Enter(map!.Parent);
            }
            else if (command.Kind == Operations.TravelKind.Visit)
            {
                var visitTarget = target;
                var settlement = Find.WorldObjects.Settlements.SingleOrDefault(s => s.Tile == visitTarget);
                if (!CaravanArrivalAction_VisitSettlement.CanVisit(caravan, settlement))
                { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "No eligible settlement visit at this tile."); return false; }
                arrival = new CaravanArrivalAction_VisitSettlement(settlement);
            }
            return true;
        }

        internal static Operations.PreviewReply Preview(Operations.TravelCaravan command, Common.ObservationContext context)
        {
            try
            {
                if (!Prepare(command, out var caravan, out var target, out _, out var failure))
                    return new Operations.PreviewReply { Failure = failure };
                Operations.RoutePreparation? routePrep = null;
                if (command.Kind != Operations.TravelKind.Stop)
                {
                    using (var path = caravan!.Tile.Layer.Pather.FindPath(caravan.Tile, target, caravan))
                    {
                        var food = caravan.DaysWorthOfFood;
                        routePrep = new Operations.RoutePreparation
                        {
                            Reachable = path.Found, FoodDays = food.days, FoodRotDays = food.tillRot,
                            DestinationTile = target.tileId,
                            Temperature = (float)GenTemperature.GetTemperatureFromSeasonAtTile(Find.TickManager.TicksAbs, target),
                        };
                        if (path.Found) routePrep.EstimatedTicks = CaravanArrivalTimeEstimator.EstimatedTicksToArrive(caravan.Tile, target, path, 0f, caravan.TicksPerMove, Find.TickManager.TicksAbs);
                    }
                }
                var effect = new Receipts.CaravanEffect { CaravanId = caravan!.GetUniqueLoadID() };
                if (command.Kind == Operations.TravelKind.Stop) effect.Stopped = true;
                else { effect.PathStarted = true; effect.DestinationTile = target.tileId; }
                var caravanPrep = new Operations.CaravanPreparation();
                if (routePrep != null) caravanPrep.Route = routePrep;
                var evaluation = new Operations.PreviewEvaluation
                { Context = context.Clone(), Accepted = true, Projected = new Receipts.EffectEvidence { Caravan = effect }, Caravan = caravanPrep };
                return NativeOperationEnvelope.Preview(new Operations.PreviewReply { Evaluated = evaluation });
            }
            catch (Exception error) { return new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Caravan travel preview failed: " + error.GetType().Name) }; }
        }

        internal static Operations.ExecuteReply Execute(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context)
        {
            var command = request.Operation.TravelCaravan; var pre = request.Precondition;
            if (!Valid(command))
                return Refuse(Common.FailureCode.InvalidRequest, "Caravan travel requires an exact current caravan snapshot and, for move/visit, a valid destination.");
            NativeAttemptLedger.Admission? handle = null; Receipts.EffectEvidence? evidence = null;
            try
            {
                if (!Prepare(command, out var caravan, out var target, out var arrival, out var failure))
                    return new Operations.ExecuteReply { Failure = failure };
                if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null)
                    return Refuse(Common.FailureCode.AuthorityRequired, "Current native authority is required.");
                var guard = authority.Check(pre.ExpectedGeneration);
                context.NativeGeneration = guard.Snapshot.Generation;
                if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) };
                var admission = state.Ledger.Admit("rimgovernor.operations.v1.Operations/Execute", request, context);
                if (admission.Kind != NativeAttemptLedger.DecisionKind.Admitted) return admission.Reply!;
                handle = admission.Handle!;
                using (authority.Owned())
                {
                    var current = authority.Check(pre.ExpectedGeneration);
                    if (!current.Success) throw new InvalidOperationException("Caravan travel authority changed before native effect.");
                    if (!Prepare(command, out caravan, out target, out arrival, out failure) || caravan == null)
                        throw new InvalidOperationException("Caravan travel prerequisites changed after admission.");
                    var effect = new Receipts.CaravanEffect { CaravanId = caravan.GetUniqueLoadID() };
                    if (command.Kind == Operations.TravelKind.Stop)
                    {
                        caravan.pather.StopDead();
                        effect.Stopped = !caravan.pather.Moving;
                    }
                    else
                    {
                        // Mirrors legacy CaravanTool.cs's own contract exactly: accepted
                        // means StartPath's own return value, nothing more. Requiring
                        // caravan.pather.Moving as well is wrong -- a return-home order
                        // issued while the caravan is still sitting at the home tile (the
                        // pather never advanced it because the game stayed paused the
                        // whole time) resolves its arrival action immediately and never
                        // sets Moving, even though StartPath legitimately accepted and
                        // dispatched the order.
                        bool started = caravan.pather.StartPath(target, arrival);
                        effect.PathStarted = started;
                        effect.DestinationTile = target.tileId;
                    }
                    var record = new NativeCaravanTravelRecord(caravan.GetUniqueLoadID(), command.Kind, effect.HasDestinationTile ? effect.DestinationTile : -1);
                    state.CaravanTravels.Add(pre.Attempt.Clone(), record);
                    evidence = new Receipts.EffectEvidence { Caravan = effect };
                    if (command.Kind == Operations.TravelKind.Stop ? !effect.Stopped : !effect.PathStarted)
                        throw new InvalidOperationException("Native caravan travel readback did not apply.");
                }
                return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, evidence) };
            }
            catch (Exception error)
            {
                return handle == null
                    ? Refuse(Common.FailureCode.NativeFailure, "Caravan travel validation failed: " + error.GetType().Name)
                    : new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, evidence!, "Admitted caravan travel requires observation: " + error.GetType().Name) };
            }
        }

        // Route completion (arrival, settlement visit, disbanding at home) is
        // expected to remove the caravan from the world, so its disappearance
        // reads as Completed for Move/Visit/ReturnHome -- unlike FormCaravan's
        // Observe, which treats disappearance as ambiguous because there is no
        // prior "still assembling" state to distinguish from arrival. A held
        // (Stop) caravan disappearing is never an expected outcome of holding,
        // so that case stays Unknown, matching FormCaravan's own caution.
        internal static Receipts.Progress Observe(Common.AttemptKey attempt, Common.ObservationContext context, NativeCaravanTravelRecord record)
        {
            var result = new Receipts.Progress { Attempt = attempt.Clone(), Context = context.Clone(), CompleteInspection = false };
            try
            {
                var caravan = Find.WorldObjects.Caravans.SingleOrDefault(c => c.IsPlayerControlled && c.GetUniqueLoadID() == record.CaravanId);
                if (caravan == null)
                {
                    if (record.Kind == Operations.TravelKind.Stop)
                    {
                        result.Unknown = new Receipts.UnknownEffect { Reason = "The held caravan is no longer observable." };
                        return result;
                    }
                    result.CompleteInspection = true;
                    var arrived = new Receipts.CaravanEffect { CaravanId = record.CaravanId, PathStarted = true, DestinationTile = record.DestinationTile };
                    result.Completed = new Receipts.CompletedEffect { Evidence = new Receipts.EffectEvidence { Caravan = arrived } };
                    return result;
                }
                result.CompleteInspection = true;
                var current = new Receipts.CaravanEffect
                {
                    CaravanId = record.CaravanId, PathStarted = caravan.pather.Moving, Stopped = !caravan.pather.Moving,
                    DestinationTile = record.DestinationTile,
                };
                result.Completed = new Receipts.CompletedEffect { Evidence = new Receipts.EffectEvidence { Caravan = current } };
            }
            catch (Exception) { result.CompleteInspection = false; result.Unknown = new Receipts.UnknownEffect { Reason = "Caravan travel inspection unavailable." }; }
            return result;
        }

        private static Operations.ExecuteReply Refuse(Common.FailureCode code, string detail) => new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(code, detail) };
    }
}
