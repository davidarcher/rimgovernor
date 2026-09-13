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
    // Typed dispatch for the settlement-gift direct-write order:
    // GiftCaravanSilver. Ports the legacy home/caravan_gift tool's
    // (CaravanGiftTools.Run) native mechanics -- CaravanVisitUtility.
    // SettlementVisitedNow, the static TradeSession/TradeDeal in gift mode,
    // FactionGiftUtility.GetGoodwillChange -- behind the typed boundary. Like
    // AcceptQuest and the trade vertical, this resolves synchronously inside
    // Execute: there is no native job to observe afterward beyond the trade
    // session's own outcome, so Observe reports the outcome captured at
    // admission time.
    //
    // Two CAS tokens, deliberately different scopes, both self-computed (the
    // same known pattern NativeTradeOperations documents for its own session
    // tokens, and now NativeWorldObservation for settlement/faction):
    //   caravan.expected_snapshot_token -- CaravanToken(id, tile, moving,
    //     sorted crew): reproducible by Go from ReadWorldProgression alone,
    //     so no dedicated candidate read is needed just to admit a caravan
    //     that has not moved since the caller last observed it.
    //   faction.expected_snapshot_token -- NativeWorldObservation.FactionToken,
    //     read fresh via ReadWorld's Settlement.faction_snapshot immediately
    //     before dispatch; this is the actual freshness gate on goodwill and
    //     hostility, which Go cannot reproduce on its own.
    // expected_pawn_ids is the exact crew snapshot (mirrors the legacy tool's
    // own membership check) independent of either token.
    internal sealed class NativeSettlementGiftRecord
    {
        internal readonly Receipts.EffectEvidence Evidence;
        internal NativeSettlementGiftRecord(Receipts.EffectEvidence evidence) { Evidence = evidence; }
        internal Receipts.Progress Observe(Common.AttemptKey attempt, Common.ObservationContext context) => new Receipts.Progress
        { Attempt = attempt.Clone(), Context = context.Clone(), CompleteInspection = true, Completed = new Receipts.CompletedEffect { Evidence = Evidence } };
    }

    internal static class NativeSettlementGiftOperations
    {
        private static string Hash(string text)
        {
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
        }

        // Exact port of Go's caravanToken (bridge/settlement_gift.go): both
        // sides hash the same already-observed CaravanState fields, so Go
        // never needs a dedicated candidate read to learn this value.
        // Booleans render as literal "true"/"false" (not .ToString()'s
        // "True"/"False") so Go's %t formatting produces an identical string.
        internal static string CaravanToken(string caravanId, int tile, bool moving, System.Collections.Generic.IEnumerable<string> pawnIds) =>
            "caravan-gift-" + Hash(caravanId + "|" + tile + "|" + (moving ? "true" : "false") + "|" + string.Join(",", pawnIds.OrderBy(id => id, StringComparer.Ordinal)));

        private static bool ValidCommand(Operations.GiftCaravanSilver? command) => command != null
            && NativeDraftProtocol.ValidEntity(command.Caravan) && NativeDraftProtocol.ValidEntity(command.Faction)
            && command.ExpectedPawnIds.Count > 0 && command.ExpectedPawnIds.Count <= 64
            && command.ExpectedPawnIds.All(ProtoBoundary.IsIdentifier)
            && command.ExpectedPawnIds.Distinct().Count() == command.ExpectedPawnIds.Count
            && command.HasSilver && command.Silver > 0;

        // Re-validates everything a stale read could have gotten wrong:
        // exact caravan identity/membership/position token, that it is
        // currently visiting a nonhostile trading settlement of the exact
        // expected faction with a fresh faction token, no competing trade
        // session, and that native negotiation is actually available.
        // Mirrors legacy CaravanGiftTools.Run's own checks.
        private static bool Prepare(Operations.GiftCaravanSilver command, out Caravan? caravan, out Settlement? settlement, out Pawn? negotiator, out Common.Failure failure)
        {
            caravan = null; settlement = null; negotiator = null;
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Settlement gift requires an exact current caravan/faction snapshot and positive silver.");
            if (!ValidCommand(command)) return false;
            if (Find.TickManager == null || !Find.TickManager.Paused) { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Pause before gifting."); return false; }
            if (TradeSession.Active || Find.WindowStack.Windows.OfType<Dialog_Trade>().Any())
            { failure = ProtoBoundary.Fail(Common.FailureCode.OwnerConflict, "An existing trade belongs to its current owner."); return false; }
            caravan = Find.WorldObjects.Caravans.SingleOrDefault(c => c.IsPlayerControlled && c.GetUniqueLoadID() == command.Caravan.EntityId);
            if (caravan == null) { failure = ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact player caravan is unavailable."); return false; }
            var pawnIds = caravan.PawnsListForReading.Select(p => p.GetUniqueLoadID()).ToArray();
            if (!pawnIds.OrderBy(id => id, StringComparer.Ordinal).SequenceEqual(command.ExpectedPawnIds.OrderBy(id => id, StringComparer.Ordinal)))
            { failure = ProtoBoundary.Fail(Common.FailureCode.StaleIdentity, "Caravan membership changed; observe before new admission."); return false; }
            if (CaravanToken(caravan.GetUniqueLoadID(), caravan.Tile.tileId, caravan.pather.Moving, pawnIds) != command.Caravan.ExpectedSnapshotToken)
            { failure = ProtoBoundary.Fail(Common.FailureCode.StaleIdentity, "Caravan position changed; observe before new admission."); return false; }
            settlement = CaravanVisitUtility.SettlementVisitedNow(caravan);
            if (settlement == null || settlement.Faction == null || settlement.Faction.GetUniqueLoadID() != command.Faction.EntityId
                || !settlement.CanTradeNow || settlement.Faction.HostileTo(Faction.OfPlayer) || settlement.Faction.IsPlayer)
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Visit the exact nonhostile trading settlement of that faction first."); return false; }
            if (NativeWorldObservation.FactionToken(settlement.Faction) != command.Faction.ExpectedSnapshotToken)
            { failure = ProtoBoundary.Fail(Common.FailureCode.StaleIdentity, "Faction relation or goodwill changed; observe before new admission."); return false; }
            var command2 = CaravanVisitUtility.TradeCommand(caravan, settlement.Faction, settlement.TraderKind);
            negotiator = BestCaravanPawnUtility.FindBestNegotiator(caravan, settlement.Faction, settlement.TraderKind);
            if (command2.Disabled || negotiator == null) { failure = ProtoBoundary.Fail(Common.FailureCode.Unavailable, "Native negotiation is unavailable."); return false; }
            return true;
        }

        internal static Operations.PreviewReply Preview(Operations.GiftCaravanSilver? command, Common.ObservationContext context)
        {
            try
            {
                if (!Prepare(command!, out var caravan, out var settlement, out _, out var failure))
                    return new Operations.PreviewReply { Failure = failure };
                return NativeOperationEnvelope.Preview(new Operations.PreviewReply { Evaluated = new Operations.PreviewEvaluation
                {
                    Context = context.Clone(), Accepted = true,
                    Projected = new Receipts.EffectEvidence { Trade = new Receipts.TradeEffect { FactionId = settlement!.Faction!.GetUniqueLoadID() } },
                } });
            }
            catch (Exception error) { return new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Settlement gift preview failed: " + error.GetType().Name) }; }
        }

        internal static Operations.ExecuteReply Execute(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context)
        {
            var command = request.Operation.GiftCaravanSilver; var pre = request.Precondition;
            NativeAttemptLedger.Admission? handle = null; Authority.Owner? owner = null; Receipts.EffectEvidence? evidence = null;
            try
            {
                if (!Prepare(command, out var caravan, out var settlement, out var negotiator, out var failure)) return new Operations.ExecuteReply { Failure = failure };
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
                    if (!current.Success) throw new InvalidOperationException("Settlement gift authority changed before native effect.");
                    if (!Prepare(command, out caravan, out settlement, out negotiator, out failure) || caravan == null || settlement == null || negotiator == null)
                        throw new InvalidOperationException("Settlement gift prerequisites changed after admission.");
                    var faction = settlement.Faction!;
                    var beforeGoodwill = faction.PlayerGoodwill;
                    try
                    {
                        TradeSession.SetupWith(settlement, negotiator, true);
                        var row = TradeSession.deal.AllTradeables.SingleOrDefault(t => t.ThingDef == ThingDefOf.Silver);
                        if (row == null || !row.CanAdjustTo(command.Silver).Accepted) throw new InvalidOperationException("Native caravan silver is insufficient.");
                        row.AdjustTo(command.Silver);
                        var beforeSilver = row.CountHeldBy(Transactor.Colony);
                        var gain = FactionGiftUtility.GetGoodwillChange(TradeSession.deal.AllTradeables, faction);
                        if (gain <= 0 || beforeGoodwill >= 100) throw new InvalidOperationException("Native gift has no goodwill benefit.");
                        bool executed = false, actuallyTraded = false;
                        try { executed = TradeSession.deal.TryExecute(out actuallyTraded); }
                        catch (Exception e) { executed = false; actuallyTraded = false; throw new InvalidOperationException("Native gift execution threw: " + e.GetType().Name, e); }
                        finally
                        {
                            caravan.RecacheInventory();
                            var afterSilver = CaravanInventoryUtility.AllInventoryItems(caravan).Where(t => t.def == ThingDefOf.Silver).Sum(t => t.stackCount);
                            evidence = new Receipts.EffectEvidence { Trade = new Receipts.TradeEffect
                            {
                                SessionId = pre.Attempt.ActionId, Executed = executed, ActuallyTraded = actuallyTraded, Closed = true,
                                BeforeSilver = beforeSilver, AfterSilver = afterSilver, BeforeGoodwill = beforeGoodwill, AfterGoodwill = faction.PlayerGoodwill,
                                FactionId = faction.GetUniqueLoadID(),
                                Snapshot = new Receipts.SnapshotEvidence { EntityId = command.Faction.EntityId, BeforeToken = command.Faction.ExpectedSnapshotToken, AfterToken = NativeWorldObservation.FactionToken(faction) },
                            } };
                            // Recorded before the success check below throws, so a
                            // partial/uncertain outcome (Native gift did not
                            // confirm execution) remains observable afterward --
                            // the same "record before the success check" order
                            // NativeQuestOperations and NativeTradeOperations use.
                            state.SettlementGifts.Add(pre.Attempt.Clone(), new NativeSettlementGiftRecord(evidence));
                        }
                        if (!executed || !actuallyTraded) throw new InvalidOperationException("Native gift did not confirm execution; inspect before retrying.");
                    }
                    finally { try { TradeSession.Close(); } catch (Exception) { } }
                }
                return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, owner, evidence!) };
            }
            catch (Exception error)
            {
                return handle == null
                    ? Refuse(Common.FailureCode.NativeFailure, "Settlement gift validation failed: " + error.GetType().Name)
                    : new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, owner!, evidence!, "Admitted settlement gift requires observation: " + error.GetType().Name) };
            }
        }

        internal static Receipts.Progress Observe(Common.AttemptKey attempt, Common.ObservationContext context, NativeSettlementGiftRecord record) => record.Observe(attempt, context);

        private static Operations.ExecuteReply Refuse(Common.FailureCode code, string detail) => new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(code, detail) };
    }
}
