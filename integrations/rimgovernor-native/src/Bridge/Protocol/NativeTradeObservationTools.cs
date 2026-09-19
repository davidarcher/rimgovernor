#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using RimBridgeServer.Sdk;
using RimWorld;
using Verse;
using Verse.AI.Group;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    // Typed, read-only trade census behind the ReadScope boundary: the map
    // traders a session could open with (each carrying the exact CAS token
    // NativeTradeOperations.PrepareOpen checks), the eligible negotiators, and
    // the currently open session's whole sheet. Every read recomputes from
    // current native state; nothing here opens, stages or closes a session.
    internal static class NativeTradeObservation
    {
        internal const string TradersToolName = "rimgovernor/observations_list_traders";
        internal const string SheetToolName = "rimgovernor/observations_read_trade_sheet";

        private static bool SafeBool(Func<bool> f) { try { return f(); } catch { return false; } }
        private static int SafeInt(Func<int> f) { try { return f(); } catch { return 0; } }
        private static float SafeFloat(Func<float> f) { try { return f(); } catch { return 0f; } }
        private static string SafeText(Func<string?> f) { try { return f() ?? ""; } catch { return ""; } }

        private static Obs.Completeness Complete(int count) => new Obs.Completeness
        { Page = new Common.PageInfo { Complete = true }, Matched = (ulong)count, Returned = (ulong)count, Filtered = 0, Unreadable = 0 };

        private static Obs.EntityRef PawnRef(Pawn pawn, Common.ObservationContext context, string token) => new Obs.EntityRef
        {
            Id = pawn.GetUniqueLoadID(), DefName = pawn.def.defName, Label = SafeText(() => pawn.LabelShort), MapId = pawn.Map?.uniqueID ?? -1,
            Position = new Common.Cell { X = pawn.Position.x, Z = pawn.Position.z },
            Snapshot = new Obs.SnapshotRef { Context = context.Clone(), EntityId = pawn.GetUniqueLoadID(), Token = token },
        };

        // Mirrors NativeTradeOperations.PrepareOpen's negotiator eligibility
        // exactly, so a negotiator listed here is one an open would accept
        // (adjacency and reachability aside, which depend on the trader).
        internal static bool EligibleNegotiator(Pawn p) =>
            !SafeBool(() => p.Dead) && !SafeBool(() => p.Downed) && !SafeBool(() => p.InMentalState) && !SafeBool(() => p.WorkTagIsDisabled(WorkTags.Social));

        private static float TradeStat(Pawn p) => SafeFloat(() => p.GetStatValue(StatDefOf.TradePriceImprovement));

        internal static Obs.TradersSnapshot Traders(Map map, Common.ObservationContext context)
        {
            var snapshot = new Obs.TradersSnapshot { Context = context.Clone() };
            var traders = new List<Obs.Trader>();
            foreach (var pawn in map.mapPawns.AllPawnsSpawned.ToList())
            {
                Pawn_TraderTracker? tracker;
                try { tracker = pawn.trader; } catch { tracker = null; }
                if (tracker == null || tracker.traderKind == null) continue;
                var canTrade = SafeBool(() => pawn.CanTradeNow);
                var dismissed = SafeBool(() => pawn.mindState != null && pawn.mindState.traderDismissed);
                // A caravan still walking to its trade spot is not one the
                // routine's escort can stand beside: its trader moves every
                // tick until the lord's travel toil ends.
                var travelling = SafeBool(() => pawn.GetLord()?.CurLordToil is LordToil_Travel);
                var row = new Obs.Trader
                {
                    Trader_ = PawnRef(pawn, context, NativeTradeOperations.TraderSnapshotToken(pawn)),
                    Kind = tracker.traderKind.defName, FactionId = pawn.Faction != null ? pawn.Faction.GetUniqueLoadID() : "",
                    CanTrade = canTrade && !dismissed && !travelling, Travelling = travelling, Orbital = false, GoodsStacks = (uint)SafeInt(() => tracker.Goods.Count()),
                };
                if (!canTrade) row.Reason = "CanTradeNow is false (downed, in a mental state, asleep, hostile or out of stock).";
                else if (dismissed) row.Reason = "The trader was dismissed.";
                else if (travelling) row.Reason = "The caravan is still travelling to its trade spot.";
                traders.Add(row);
            }
            traders.Sort((a, b) => string.CompareOrdinal(a.Trader_.Id, b.Trader_.Id));
            snapshot.Traders.Add(traders);
            var negotiators = map.mapPawns.FreeColonistsSpawned.Where(EligibleNegotiator).ToList();
            negotiators.Sort((x, y) =>
            {
                var c = TradeStat(y).CompareTo(TradeStat(x));
                return c != 0 ? c : string.CompareOrdinal(x.GetUniqueLoadID(), y.GetUniqueLoadID());
            });
            snapshot.Negotiators.Add(negotiators.Select(p => PawnRef(p, context, NativeTradeOperations.NegotiatorSnapshotToken(p))));
            snapshot.Completeness = Complete(traders.Count + negotiators.Count);
            return snapshot;
        }

        private static bool IsPawnRow(Tradeable t)
        {
            try { var def = t.ThingDef; if (def != null && def.category == ThingCategory.Pawn) return true; } catch { }
            try { return t.AnyThing is Pawn; } catch { return false; }
        }

        private static Obs.TradeLine Line(Tradeable t, int index)
        {
            ThingDef? def; try { def = t.ThingDef; } catch { def = null; }
            var pawn = IsPawnRow(t);
            var line = new Obs.TradeLine
            {
                Index = (uint)index, LineId = "#" + index,
                Definition = new Obs.DefinitionRef { DefName = def != null ? def.defName : "", Label = def != null ? def.label ?? "" : "" },
                Stuff = SafeText(() => t.StuffDef != null ? t.StuffDef.defName : ""),
                Category = def != null && def.FirstThingCategory != null ? def.FirstThingCategory.defName : "",
                ColonyCount = SafeInt(() => t.CountHeldBy(Transactor.Colony)), TraderCount = SafeInt(() => t.CountHeldBy(Transactor.Trader)),
                // GetPriceFor memoises price factors inside the Tradeable: the
                // session's own scratch state, the same write the vanilla dialog
                // makes every frame it draws.
                BuyPrice = SafeFloat(() => t.GetPriceFor(TradeAction.PlayerBuys)), SellPrice = SafeFloat(() => t.GetPriceFor(TradeAction.PlayerSells)),
                BuyPriceType = SafeText(() => t.PriceTypeFor(TradeAction.PlayerBuys).ToString()), SellPriceType = SafeText(() => t.PriceTypeFor(TradeAction.PlayerSells).ToString()),
                MarketValue = SafeFloat(() => t.BaseMarketValue),
                TraderWillTrade = SafeBool(() => t.TraderWillTrade), Currency = SafeBool(() => t.IsCurrency), Pawn = pawn,
                TransferCount = SafeInt(() => t.CountToTransfer), MinimumCount = SafeInt(() => t.GetMinimumToTransfer()), MaximumCount = SafeInt(() => t.GetMaximumToTransfer()),
                // The same classification AcceptTrade's economic floors refuse
                // to export, so selection never stages what acceptance rejects.
                ProtectedExport = def == null || def.IsWeapon || def.IsApparel || def.IsMedicine || def.IsNutritionGivingIngestible || pawn,
                Food = pawn ? null : NativeTradeFoodFacts.Read(def),
            };
            if (pawn) line.PawnDescription = SafeText(() => t.Label);
            return line;
        }

        internal static bool Sheet(Common.ObservationContext context, string? sessionId, out Obs.TradeSheet sheet, out Common.Failure failure)
        {
            sheet = new Obs.TradeSheet();
            if (!NativeTradeOperations.SessionSheet(context.Identity, out var session, out failure)) return false;
            if (!string.IsNullOrEmpty(sessionId) && sessionId != session.SessionId)
            {
                failure = ProtoBoundary.Fail(Common.FailureCode.StaleIdentity, "The requested trade session is not the open one.");
                return false;
            }
            var deal = session.Deal;
            var all = deal.AllTradeables.ToList();
            Tradeable? currency; try { currency = deal.CurrencyTradeable; } catch { currency = null; }
            try { deal.UpdateCurrencyCount(); } catch { }
            sheet = new Obs.TradeSheet
            {
                Snapshot = new Obs.SnapshotRef { Context = context.Clone(), EntityId = session.SessionId, Token = session.SessionToken },
                SessionId = session.SessionId,
                Trader = PawnRef(session.Trader, context, NativeTradeOperations.TraderSnapshotToken(session.Trader)),
                Negotiator = PawnRef(session.Negotiator, context, NativeTradeOperations.NegotiatorSnapshotToken(session.Negotiator)),
                GiftMode = session.GiftMode,
                NegotiatorAdjacent = Math.Max(Math.Abs(session.Trader.Position.x - session.Negotiator.Position.x), Math.Abs(session.Trader.Position.z - session.Negotiator.Position.z)) <= 1,
                CanTradeNow = SafeBool(() => session.Trader.CanTradeNow),
                TraderHasEnoughSilver = SafeBool(() => deal.DoesTraderHaveEnoughSilver()),
                DealSignature = session.DealSignature,
            };
            if (currency != null)
            {
                // Negative when the colony pays, exactly the currency row's
                // own signed transfer count.
                sheet.Balance = SafeInt(() => currency.CountToTransfer);
                sheet.ColonyCanAfford = SafeInt(() => currency.CountPostDealFor(Transactor.Colony)) >= 0;
            }
            for (var i = 0; i < all.Count; i++) if (all[i] != null) sheet.Lines.Add(Line(all[i], i));
            sheet.Completeness = Complete(sheet.Lines.Count);
            return true;
        }
    }

    public sealed class NativeTradeObservationTools
    {
        [Tool(NativeTradeObservation.TradersToolName, Title = "List map traders and eligible negotiators",
            Description = "Official TradersRequest ProtoJSON. Read-only census of every trader caravan pawn on the identified map with its exact OpenTrade snapshot token, and every colonist eligible to negotiate with its own token. Orbital ships are not listed (direct orbital opening is unsupported). Does not advance time or issue orders.")]
        [ToolResponse("payload", "string", "Official observations TradersReply ProtoJSON.", Always = true)]
        public async Task<object> ListTraders(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Raw value must be a TradersRequest ProtoJSON string.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, NativeTradeObservation.TradersToolName, request!, Obs.TradersRequest.Parser, out var parsed, out var failure)
                || parsed.Scope?.ExpectedIdentity == null) return ProtoBoundary.Encode(new Obs.TradersReply { Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Identity is required.") });
            return await ProtoBoundary.OnMainThread(ctx, () =>
            {
                if (!ProtoBoundary.ValidateIdentity(parsed.Scope.ExpectedIdentity, out var map, out var context, out failure))
                    return ProtoBoundary.Encode(new Obs.TradersReply { Failure = failure });
                try { return ProtoBoundary.Encode(new Obs.TradersReply { Observed = NativeTradeObservation.Traders(map, context) }); }
                catch (Exception) { return ProtoBoundary.Encode(new Obs.TradersReply { Unavailable =
                    new Common.Unavailable { Reason = Common.UnavailableReason.ReadFailed, Detail = "Native trader census could not be read completely." } }); }
            }, cancellationToken).ConfigureAwait(false);
        }

        [Tool(NativeTradeObservation.SheetToolName, Title = "Read the open trade session's sheet",
            Description = "Official TradeSheetRequest ProtoJSON. Read-only, complete and unfiltered sheet of the native trade session this adapter opened: every row with both sides' counts and prices, the session CAS token as the snapshot token, the current deal signature and both affordability verdicts. Refuses when no session is open for the identity or the requested session_id is not the open one. Filters and pagination are not applied; the whole sheet is one page.")]
        [ToolResponse("payload", "string", "Official observations TradeSheetReply ProtoJSON.", Always = true)]
        public async Task<object> ReadTradeSheet(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Raw value must be a TradeSheetRequest ProtoJSON string.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, NativeTradeObservation.SheetToolName, request!, Obs.TradeSheetRequest.Parser, out var parsed, out var failure)
                || parsed.Scope?.ExpectedIdentity == null) return ProtoBoundary.Encode(new Obs.TradeSheetReply { Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Identity is required.") });
            if (parsed.Page != null && parsed.Page.HasCursor && parsed.Page.Cursor.Length > 0)
                return ProtoBoundary.Encode(new Obs.TradeSheetReply { Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "The trade sheet is a single page; cursors are not issued.") });
            return await ProtoBoundary.OnMainThread(ctx, () =>
            {
                if (!ProtoBoundary.ValidateIdentity(parsed.Scope.ExpectedIdentity, out var context, out failure))
                    return ProtoBoundary.Encode(new Obs.TradeSheetReply { Failure = failure });
                try
                {
                    if (!NativeTradeObservation.Sheet(context, parsed.HasSessionId ? parsed.SessionId : null, out var sheet, out failure))
                        return ProtoBoundary.Encode(new Obs.TradeSheetReply { Failure = failure });
                    var reply = new Obs.TradeSheetReply { Observed = sheet };
                    if (Encoding.UTF8.GetByteCount(JsonFormatter.Default.Format(reply)) > ProtoBoundary.MaximumEnvelopeBytes)
                        return ProtoBoundary.Encode(new Obs.TradeSheetReply { Unavailable =
                            new Common.Unavailable { Reason = Common.UnavailableReason.LimitExceeded, Detail = "Trade sheet exceeds 1MiB." } });
                    return ProtoBoundary.Encode(reply);
                }
                catch (Exception) { return ProtoBoundary.Encode(new Obs.TradeSheetReply { Unavailable =
                    new Common.Unavailable { Reason = Common.UnavailableReason.ReadFailed, Detail = "Native trade sheet could not be read completely." } }); }
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
