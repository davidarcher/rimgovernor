#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // Typed dispatch for the four-step trade vertical: OpenTrade,
    // SetTradeLines, AcceptTrade, EndTrade. Ports the legacy JSON home/trade
    // tool's (HomeTradeTools) native mechanics -- RimWorld's own static
    // TradeSession/TradeDeal, TradeSession.SetupWith, Tradeable.AdjustTo,
    // TradeDeal.TryExecute -- behind the typed boundary, deliberately
    // WITHOUT that tool's Dialog_Trade "watch" theater (opening a real
    // window, holding it on screen, closing it after): that exists so a
    // human watching the game can see a trade land, which has no analogue
    // for a typed native operation driven by the Go controller.
    //
    // RimWorld allows exactly one TradeSession at a time, so unlike the
    // per-target verticals (haul/waste/husbandry/equip) this vertical is a
    // single static session, matching the legacy tool's own static
    // bookkeeping fields -- just re-expressed with CAS tokens instead of a
    // bespoke sessionId/dealSignature pair of ad hoc strings.
    //
    // Two tokens, deliberately different scopes:
    //   SessionToken()  -- coarse identity: session id + trader + negotiator
    //                       + gift mode + colony/load. Stable across
    //                       SetTradeLines calls; used by every op's
    //                       `session` EntityPrecondition.
    //   DealSignature() -- exact staged contents + prices + backing thing
    //                       ids/stackcounts (a direct port of the legacy
    //                       tool's StageSignature()). Changes on every
    //                       SetTradeLines; only AcceptTrade checks it,
    //                       exactly as the legacy tool's dealSignature did.
    // Both are self-computed: no observation reader exposes a trader/
    // negotiator/deal snapshot token yet, the same known follow-up gap
    // documented on NativeWasteOperations and NativeRecoveryOperations.
    internal sealed class NativeTradeRecord
    {
        internal readonly Receipts.EffectEvidence Evidence;
        internal NativeTradeRecord(Receipts.EffectEvidence evidence) { Evidence = evidence; }

        // Every trade sub-operation below resolves synchronously inside its
        // own Execute call -- no native job is issued and there is nothing
        // further for RimWorld to do afterward -- so Observe reports the
        // outcome captured at admission time directly, the same immediate-
        // completion shape NativeHusbandryOperations and NativeWorkSettings
        // use for their own direct writes.
        internal Receipts.Progress Observe(Common.AttemptKey attempt, Common.ObservationContext context) => new Receipts.Progress
        { Attempt = attempt.Clone(), Context = context.Clone(), CompleteInspection = true, Completed = new Receipts.CompletedEffect { Evidence = Evidence } };
    }

    internal static class NativeTradeOperations
    {
        // ---------------------------------------------------- session state
        private static string? _sessionId;
        private static string? _sessionColonyId;
        private static string? _sessionLoadToken;
        private static Map? _sessionMap;
        private static TradeDeal? _sessionDeal;
        private static Pawn? _sessionTrader;
        private static Pawn? _sessionNegotiator;
        private static bool _giftMode;

        private static string Hash(string text)
        {
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
        }

        private static bool SafeBool(Func<bool> f) { try { return f(); } catch { return false; } }
        private static int SafeInt(Func<int> f) { try { return f(); } catch { return 0; } }
        private static float SafeFloat(Func<float> f) { try { return f(); } catch { return 0f; } }
        private static ThingDef? SafeDef(Tradeable t) { try { return t.ThingDef; } catch { return null; } }
        private static bool SafeCanTradeNow(Pawn p) { try { return p.CanTradeNow; } catch { return false; } }
        private static int Chebyshev(IntVec3 a, IntVec3 b) => Math.Max(Math.Abs(a.x - b.x), Math.Abs(a.z - b.z));

        // Mirrors HomeTradeTools.IsPawnRow.
        private static bool IsPawnRow(Tradeable t)
        {
            try { var def = SafeDef(t); if (def != null && def.category == ThingCategory.Pawn) return true; } catch { }
            try { return t.AnyThing is Pawn; } catch { return false; }
        }

        // Mirrors HomeTradeTools.WouldGiveAway.
        private static bool WouldGiveAway(Tradeable t, int target)
        {
            bool gift; try { gift = TradeSession.giftMode; } catch { gift = false; }
            return gift ? target > 0 : target < 0;
        }

        private static Tradeable? SafeCurrencyTradeable(TradeDeal deal) { try { return deal.CurrencyTradeable; } catch { return null; } }
        private static void SafeUpdateCurrency(TradeDeal deal) { try { deal.UpdateCurrencyCount(); } catch { } }

        private static bool IsTradeDialog(Window w)
        {
            var t = w?.GetType();
            while (t != null) { if (t == typeof(Dialog_Trade)) return true; t = t.BaseType; }
            return false;
        }
        private static Window? OpenTradeDialog()
        {
            try { var stack = Find.WindowStack; return stack?.Windows?.OfType<Window>().FirstOrDefault(IsTradeDialog); }
            catch { return null; }
        }

        // -------------------------------------------------------- tokens
        private static string TraderToken(Pawn trader) => "trade-trader-" + Hash(trader.GetUniqueLoadID()
            + "|cantrade=" + SafeCanTradeNow(trader) + "|dismissed=" + SafeBool(() => trader.mindState != null && trader.mindState.traderDismissed)
            + "|pos=" + trader.Position);

        private static string NegotiatorToken(Pawn negotiator) => "trade-negotiator-" + Hash(negotiator.GetUniqueLoadID()
            + "|pos=" + negotiator.Position + "|downed=" + SafeBool(() => negotiator.Downed) + "|dead=" + SafeBool(() => negotiator.Dead)
            + "|mental=" + SafeBool(() => negotiator.InMentalState) + "|socialDisabled=" + SafeBool(() => negotiator.WorkTagIsDisabled(WorkTags.Social)));

        // Coarse session identity: stable across SetTradeLines calls.
        private static string SessionToken() => _sessionId == null ? "" : "trade-session-" + Hash(
            _sessionId + "|trader=" + SafeString(_sessionTrader) + "|negotiator=" + SafeString(_sessionNegotiator)
            + "|gift=" + _giftMode + "|colony=" + _sessionColonyId + "|load=" + _sessionLoadToken);
        private static string SafeString(Pawn? p) { try { return p?.GetUniqueLoadID() ?? ""; } catch { return ""; } }

        // Exact port of HomeTradeTools.StageSignature: every staged row, its
        // computed prices, and the backing thing ids/stackcounts behind it.
        private static string DealSignature()
        {
            var deal = _sessionDeal;
            var parts = new List<string> { "session=" + _sessionId + "|gift=" + _giftMode };
            if (deal != null)
            {
                var all = deal.AllTradeables;
                foreach (var row in all.Where(t => SafeInt(() => t.CountToTransfer) != 0))
                {
                    var rowKey = "row=" + all.IndexOf(row);
                    parts.Add(rowKey + "|count=" + SafeInt(() => row.CountToTransfer)
                        + "|buy=" + SafeFloat(() => row.GetPriceFor(TradeAction.PlayerBuys)).ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                        + "|sell=" + SafeFloat(() => row.GetPriceFor(TradeAction.PlayerSells)).ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                    try { parts.AddRange(row.thingsColony.Select(t => rowKey + "|colony=" + t.ThingID + ":" + t.stackCount + ":" + t.Destroyed)); } catch { }
                    try { parts.AddRange(row.thingsTrader.Select(t => rowKey + "|trader=" + t.ThingID + ":" + t.stackCount + ":" + t.Destroyed)); } catch { }
                }
            }
            parts.Sort(StringComparer.Ordinal);
            var currency = deal != null ? SafeCurrencyTradeable(deal) : null;
            var net = currency != null ? SafeInt(() => currency.CountToTransfer).ToString(System.Globalization.CultureInfo.InvariantCulture) : "?";
            return "trade-deal-" + Hash(string.Join(";", parts) + "|net=" + net);
        }

        // ---------------------------------------------------- session guard
        // Mirrors HomeTradeTools.RequireSession exactly, less the
        // ColonyIdentity GameComponent reference-equality check (replaced by
        // the identity's own colony/load token, which this framework
        // already carries) and the removed requireAdjacent toggle (this
        // adapter always requires adjacency, matching every other native
        // vertical's lack of a "loosen the guard" flag).
        private static bool RequireSession(Common.Identity identity, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.NotFound, "No native trade session is open for this identity.");
            if (_sessionId == null || _sessionColonyId != identity.ColonyId || _sessionLoadToken != identity.LoadToken) return false;
            if (!TradeSession.Active || TradeSession.deal == null || !ReferenceEquals(TradeSession.deal, _sessionDeal)
                || !ReferenceEquals(TradeSession.trader, _sessionTrader) || !ReferenceEquals(TradeSession.playerNegotiator, _sessionNegotiator)
                || !ProtoBoundary.IsLoaded(_sessionMap))
            { failure = ProtoBoundary.Fail(Common.FailureCode.StaleIdentity, "Trade session, map or load changed; do not reuse a stale session."); return false; }
            var trader = _sessionTrader!; var negotiator = _sessionNegotiator!;
            if (!trader.Spawned || trader.Map != _sessionMap || !SafeCanTradeNow(trader) || Find.TickManager == null || !Find.TickManager.Paused
                || SafeBool(() => trader.mindState != null && trader.mindState.traderDismissed)
                || !negotiator.CanTradeWith(trader.Faction, trader.TraderKind).Accepted || Chebyshev(trader.Position, negotiator.Position) > 1)
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Trader or negotiator departed, became unavailable or moved out of adjacency."); return false; }
            return true;
        }

        private static void CloseSession(bool receiveQuest)
        {
            if (receiveQuest && _sessionTrader != null)
            {
                try
                {
                    if (_sessionTrader.mindState != null && _sessionTrader.mindState.hasQuest && _sessionNegotiator != null)
                        TradeUtility.ReceiveQuestFromTrader(_sessionTrader, _sessionNegotiator);
                }
                catch { }
            }
            try { TradeSession.Close(); } catch { }
            _sessionId = null; _sessionColonyId = null; _sessionLoadToken = null; _sessionMap = null;
            _sessionDeal = null; _sessionTrader = null; _sessionNegotiator = null; _giftMode = false;
        }

        // ---------------------------------------------------------- open
        private static bool PrepareOpen(Operations.OpenTrade? command, Common.Identity identity, out Pawn? trader, out Pawn? negotiator, out Common.Failure failure)
        {
            trader = null; negotiator = null;
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "OpenTrade requires an exact eligible trader and negotiator snapshot.");
            if (command == null || !NativeDraftProtocol.ValidEntity(command.Trader) || !NativeDraftProtocol.ValidEntity(command.Negotiator)) return false;
            if (_sessionId != null) { failure = ProtoBoundary.Fail(Common.FailureCode.OwnerConflict, "A native trade session is already open; accept or end it first."); return false; }
            if (OpenTradeDialog() != null) { failure = ProtoBoundary.Fail(Common.FailureCode.Unavailable, "A Dialog_Trade window is already open on screen."); return false; }
            if (TradeSession.Active) { failure = ProtoBoundary.Fail(Common.FailureCode.OwnerConflict, "A TradeSession is already open outside this adapter."); return false; }
            if (Find.TickManager == null || !Find.TickManager.Paused) { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Pause before opening a trade."); return false; }
            var map = ProtoBoundary.ResolveMap(identity);
            if (map == null) { failure = ProtoBoundary.Fail(Common.FailureCode.Unavailable, "No current map."); return false; }
            trader = map.mapPawns.AllPawnsSpawned.SingleOrDefault(p => p.GetUniqueLoadID() == command.Trader.EntityId && p.trader != null && p.trader.traderKind != null);
            if (trader == null) { failure = ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact map caravan trader is unavailable; this adapter does not support direct orbital open."); return false; }
            if (TraderToken(trader) != command.Trader.ExpectedSnapshotToken) { failure = ProtoBoundary.Fail(Common.FailureCode.StaleIdentity, "Trader snapshot changed; observe before new admission."); return false; }
            if (!SafeCanTradeNow(trader)) { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Trader reports CanTradeNow:false."); return false; }
            var negotiatorPawn = map.mapPawns.FreeColonistsSpawned.SingleOrDefault(p => p.GetUniqueLoadID() == command.Negotiator.EntityId);
            negotiator = negotiatorPawn;
            if (negotiatorPawn == null || SafeBool(() => negotiatorPawn.Dead) || SafeBool(() => negotiatorPawn.Downed) || SafeBool(() => negotiatorPawn.InMentalState)
                || SafeBool(() => negotiatorPawn.WorkTagIsDisabled(WorkTags.Social)))
            { failure = ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact eligible negotiator is unavailable."); return false; }
            if (NegotiatorToken(negotiatorPawn) != command.Negotiator.ExpectedSnapshotToken) { failure = ProtoBoundary.Fail(Common.FailureCode.StaleIdentity, "Negotiator snapshot changed; observe before new admission."); return false; }
            var traderPawn = trader;
            if (SafeBool(() => traderPawn.mindState != null && traderPawn.mindState.traderDismissed) || !negotiatorPawn.CanTradeWith(traderPawn.Faction, traderPawn.TraderKind).Accepted
                || !negotiatorPawn.CanReach(traderPawn, PathEndMode.OnCell, Danger.Deadly) || Chebyshev(negotiatorPawn.Position, traderPawn.Position) > 1)
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Native player trade eligibility, reachability or adjacency refused this negotiator."); return false; }
            return true;
        }

        private static Receipts.EffectEvidence OpenEvidence(Pawn trader, bool executed)
        {
            var faction = trader.Faction;
            return new Receipts.EffectEvidence { Trade = new Receipts.TradeEffect
            {
                SessionId = _sessionId ?? "", DealSignature = DealSignature(), Executed = false, Closed = false,
                BeforeSilver = SessionSilver(), BeforeGoodwill = faction != null ? SafeInt(() => faction.PlayerGoodwill) : 0,
                FactionId = faction != null ? faction.GetUniqueLoadID() : "",
                Snapshot = new Receipts.SnapshotEvidence { EntityId = _sessionId ?? "", AfterToken = SessionToken() },
            } };
        }

        private static int SessionSilver()
        {
            if (_sessionDeal == null) return 0;
            var currency = SafeCurrencyTradeable(_sessionDeal);
            return currency != null ? SafeInt(() => currency.CountHeldBy(Transactor.Colony)) : 0;
        }

        // --------------------------------------------------------- set lines
        private readonly struct PreparedLine { internal readonly int Index; internal readonly int Target; internal readonly int Before;
            internal PreparedLine(int index, int target, int before) { Index = index; Target = target; Before = before; } }

        // Resolves a line_id to exactly one row: "#N" absolute index, or an
        // exact defName match. Unlike the legacy JSON tool's fuzzy label/
        // substring matching (built for a human typing a name), a typed
        // caller is expected to already hold an exact defName or index from
        // an observation read, so ambiguity is refused rather than guessed.
        private static bool ResolveRow(List<Tradeable> all, string lineId, out int index, out Common.Failure failure)
        {
            index = -1;
            failure = ProtoBoundary.Fail(Common.FailureCode.NotFound, "No row matches '" + lineId + "'.");
            if (string.IsNullOrEmpty(lineId)) return false;
            var trimmed = lineId.Trim();
            if (trimmed.StartsWith("#", StringComparison.Ordinal))
            {
                if (!int.TryParse(trimmed.Substring(1), out var idx) || idx < 0 || idx >= all.Count) return false;
                index = idx; return true;
            }
            var hits = new List<int>();
            for (var i = 0; i < all.Count; i++) { var def = SafeDef(all[i]); if (def != null && string.Equals(def.defName, trimmed, StringComparison.Ordinal)) hits.Add(i); }
            if (hits.Count != 1)
            {
                failure = ProtoBoundary.Fail(Common.FailureCode.NotFound, hits.Count == 0 ? "No row matches defName '" + trimmed + "'." : "'" + trimmed + "' matches multiple rows; address one by '#index'.");
                return false;
            }
            index = hits[0]; return true;
        }

        // Validates every requested line before applying any of them: a
        // typed SetTradeLines is an atomic admission, unlike the legacy
        // JSON tool's per-line partial-apply report.
        private static bool PrepareLines(Operations.SetTradeLines? command, Common.Identity identity, List<Tradeable> all, out List<PreparedLine> prepared, out Common.Failure failure)
        {
            prepared = new List<PreparedLine>();
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "SetTradeLines requires an exact open session and at least one line.");
            if (command == null || !NativeDraftProtocol.ValidEntity(command.Session) || command.Lines.Count == 0) return false;
            if (command.Session.EntityId != _sessionId || SessionToken() != command.Session.ExpectedSnapshotToken)
            { failure = ProtoBoundary.Fail(Common.FailureCode.StaleIdentity, "Trade session changed; observe before new admission."); return false; }
            if (!RequireSession(identity, out failure)) return false;
            if (OpenTradeDialog() != null) { failure = ProtoBoundary.Fail(Common.FailureCode.Unavailable, "A Dialog_Trade window is open on screen; close it first."); return false; }
            var seen = new HashSet<int>();
            var giftMode = SafeBool(() => TradeSession.giftMode);
            foreach (var line in command.Lines)
            {
                if (!line.HasLineId || !line.HasAbsoluteCount) { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Every trade line requires a line_id and absolute_count."); return false; }
                if (!ResolveRow(all, line.LineId, out var index, out failure)) return false;
                if (!seen.Add(index)) { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Line '" + line.LineId + "' repeats a row already addressed in this request."); return false; }
                var row = all[index]; var target = line.AbsoluteCount;
                if (!SafeBool(() => row.TraderWillTrade) && target != 0) { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "'" + line.LineId + "' will not trade (TraderWillTrade is false)."); return false; }
                if (SafeBool(() => row.IsCurrency) && !giftMode) { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "The silver row is computed from the other lines, not set."); return false; }
                if (IsPawnRow(row) && !(command.HasAllowPawns && command.AllowPawns) && WouldGiveAway(row, target))
                { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Refusing to sell a pawn row without allow_pawns."); return false; }
                AcceptanceReport report;
                try { report = row.CanAdjustTo(target); } catch (Exception e) { failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "CanAdjustTo threw: " + e.GetType().Name); return false; }
                if (!report.Accepted) { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, string.IsNullOrEmpty(report.Reason) ? "The game rejected that count." : report.Reason); return false; }
                prepared.Add(new PreparedLine(index, target, SafeInt(() => row.CountToTransfer)));
            }
            return true;
        }

        // -------------------------------------------------------- accept
        private static bool PrepareAccept(Operations.AcceptTrade? command, Common.Identity identity, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "AcceptTrade requires an exact open session and matching deal signature.");
            if (command == null || !NativeDraftProtocol.ValidEntity(command.Session)) return false;
            if (command.Session.EntityId != _sessionId || SessionToken() != command.Session.ExpectedSnapshotToken)
            { failure = ProtoBoundary.Fail(Common.FailureCode.StaleIdentity, "Trade session changed; observe before new admission."); return false; }
            if (!RequireSession(identity, out failure)) return false;
            if (OpenTradeDialog() != null) { failure = ProtoBoundary.Fail(Common.FailureCode.Unavailable, "A Dialog_Trade window is open on screen; close it first."); return false; }
            var deal = _sessionDeal!;
            SafeUpdateCurrency(deal);
            if (!command.HasExpectedDealSignature || DealSignature() != command.ExpectedDealSignature)
            { failure = ProtoBoundary.Fail(Common.FailureCode.StaleIdentity, "Trade contents or prices changed since preview."); return false; }
            if (command.EconomicFloors.Count > 0)
            {
                var floors = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var f in command.EconomicFloors)
                {
                    if (!f.HasDefName || string.IsNullOrEmpty(f.DefName) || floors.ContainsKey(f.DefName))
                    { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Economic reserve policy is malformed."); return false; }
                    floors[f.DefName] = f.HasCount ? f.Count : 0;
                }
                if (!floors.ContainsKey("Silver") || SafeBool(() => TradeSession.giftMode))
                { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Economic policy requires a silver reserve and an ordinary trade."); return false; }
                foreach (var row in deal.AllTradeables.Where(t => SafeInt(() => t.CountToTransfer) < 0 || SafeBool(() => t.IsCurrency)))
                {
                    var def = SafeDef(row);
                    if (def == null || !floors.TryGetValue(def.defName, out var floor)
                        || SafeInt(() => row.thingsColony.Where(t => !t.Destroyed).Sum(t => t.stackCount)) + SafeInt(() => row.CountToTransfer) < floor)
                    { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Economic stock reserve would be violated."); return false; }
                    if (!SafeBool(() => row.IsCurrency) && (def.IsWeapon || def.IsApparel || def.IsMedicine || def.IsNutritionGivingIngestible || IsPawnRow(row)))
                    { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Economic export is protected."); return false; }
                }
            }
            var stagedCount = deal.AllTradeables.Count(t => SafeInt(() => t.CountToTransfer) != 0);
            if (stagedCount == 0 && !(command.HasAllowEmpty && command.AllowEmpty))
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Nothing is staged; pass allow_empty to trade nothing deliberately."); return false; }
            HashSet<Thing> colonyGoods, traderGoods;
            try
            {
                colonyGoods = new HashSet<Thing>(_sessionTrader!.ColonyThingsWillingToBuy(_sessionNegotiator));
                traderGoods = new HashSet<Thing>(_sessionTrader.trader.Goods);
            }
            catch (Exception e) { failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Trade stock could not be read: " + e.GetType().Name); return false; }
            foreach (var row in deal.AllTradeables.Where(t => SafeInt(() => t.CountToTransfer) != 0))
            {
                var buys = row.ActionToDo == TradeAction.PlayerBuys;
                var source = buys ? row.thingsTrader : row.thingsColony;
                var available = buys ? traderGoods : colonyGoods;
                if (source.Any(t => t.Destroyed || (t.stackCount > 0 && !available.Contains(t))) || !row.CanAdjustTo(row.CountToTransfer).Accepted)
                { failure = ProtoBoundary.Fail(Common.FailureCode.StaleIdentity, "Trade stock changed or is no longer eligible."); return false; }
            }
            if (!SafeBool(() => TradeSession.giftMode))
            {
                var currency = SafeCurrencyTradeable(deal);
                var affordable = currency != null && SafeInt(() => currency.CountPostDealFor(Transactor.Colony)) >= 0;
                var traderAffordable = SafeBool(() => deal.DoesTraderHaveEnoughSilver());
                if (!affordable || !traderAffordable)
                {
                    failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, currency == null ? "This deal has no silver row."
                        : !affordable ? "The colony cannot afford this deal." : "The trader cannot afford this deal.");
                    return false;
                }
            }
            return true;
        }

        // ----------------------------------------------------------- end
        private static bool PrepareEnd(Operations.EndTrade? command, Common.Identity identity, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "EndTrade requires an exact session and an explicit kind.");
            if (command == null || !NativeDraftProtocol.ValidEntity(command.Session) || !command.HasKind || command.Kind == Operations.EndTradeKind.Unspecified) return false;
            if (command.Kind == Operations.EndTradeKind.Cancel)
            {
                if (command.Session.EntityId != _sessionId || SessionToken() != command.Session.ExpectedSnapshotToken)
                { failure = ProtoBoundary.Fail(Common.FailureCode.StaleIdentity, "Trade session changed; observe before new admission."); return false; }
                return RequireSession(identity, out failure);
            }
            // close_dialog: the escape hatch. It always succeeds at sweeping
            // any stray Dialog_Trade window; it only additionally closes
            // OUR session (below, in Execute) when the entity precondition
            // still matches -- a caller pointing at a foreign/stale session
            // gets a window sweep and nothing else, never a refusal.
            return true;
        }

        // -------------------------------------------------------- execute
        internal static Operations.ExecuteReply Execute(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context)
        {
            var operation = request.Operation; var pre = request.Precondition; var identity = context.Identity;
            NativeAttemptLedger.Admission? handle = null; Receipts.EffectEvidence? evidence = null;
            try
            {
                Common.Failure failure;
                if (operation.CommandCase == Operations.Operation.CommandOneofCase.OpenTrade)
                {
                    if (!PrepareOpen(operation.OpenTrade, identity, out var trader, out var negotiator, out failure)) return new Operations.ExecuteReply { Failure = failure };
                    var admitReply = Admit(state, request, context, out handle, out var authority);
                    if (admitReply != null) return admitReply;
                    using (authority!.Owned())
                    {
                        if (!PrepareOpen(operation.OpenTrade, identity, out trader, out negotiator, out failure) || trader == null || negotiator == null)
                            throw new InvalidOperationException("Open prerequisites changed after admission.");
                        try { TradeSession.SetupWith(trader, negotiator, operation.OpenTrade.HasGiftMode && operation.OpenTrade.GiftMode); }
                        catch (Exception e) { throw new InvalidOperationException("TradeSession.SetupWith threw: " + e.GetType().Name, e); }
                        if (!TradeSession.Active || TradeSession.deal == null) throw new InvalidOperationException("TradeSession.SetupWith returned but the session is not active.");
                        _sessionId = Guid.NewGuid().ToString("N"); _sessionColonyId = identity.ColonyId; _sessionLoadToken = identity.LoadToken;
                        _sessionMap = ProtoBoundary.ResolveMap(context); _sessionDeal = TradeSession.deal; _sessionTrader = trader; _sessionNegotiator = negotiator;
                        _giftMode = operation.OpenTrade.HasGiftMode && operation.OpenTrade.GiftMode;
                        evidence = OpenEvidence(trader, false);
                        state.Trade.Add(pre.Attempt.Clone(), new NativeTradeRecord(evidence));
                        if (!TradeSession.Active) throw new InvalidOperationException("Native open readback did not apply.");
                    }
                    return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, evidence) };
                }
                if (operation.CommandCase == Operations.Operation.CommandOneofCase.SetTradeLines)
                {
                    var all0 = RequireSession(identity, out failure) ? _sessionDeal!.AllTradeables : new List<Tradeable>();
                    if (!PrepareLines(operation.SetTradeLines, identity, all0, out var prepared, out failure)) return new Operations.ExecuteReply { Failure = failure };
                    var admitReply = Admit(state, request, context, out handle, out var authority);
                    if (admitReply != null) return admitReply;
                    using (authority!.Owned())
                    {
                        var deal = _sessionDeal; if (deal == null) throw new InvalidOperationException("Trade session closed before native effect.");
                        var all = deal.AllTradeables;
                        if (!PrepareLines(operation.SetTradeLines, identity, all, out prepared, out failure)) throw new InvalidOperationException("Trade lines prerequisites changed after admission.");
                        var lineEffects = new List<Receipts.TradeLineEffect>();
                        foreach (var line in prepared)
                        {
                            var row = all[line.Index];
                            row.AdjustTo(line.Target);
                            lineEffects.Add(new Receipts.TradeLineEffect { LineId = "#" + line.Index, BeforeCount = line.Before, AfterCount = SafeInt(() => row.CountToTransfer) });
                        }
                        SafeUpdateCurrency(deal);
                        evidence = new Receipts.EffectEvidence { Trade = new Receipts.TradeEffect
                        {
                            SessionId = _sessionId ?? "", DealSignature = DealSignature(), Executed = false, Closed = false,
                            BeforeSilver = SessionSilver(), AfterSilver = SessionSilver(),
                            Snapshot = new Receipts.SnapshotEvidence { EntityId = _sessionId ?? "", AfterToken = SessionToken() },
                        } };
                        foreach (var l in lineEffects) evidence.Trade.Lines.Add(l);
                        state.Trade.Add(pre.Attempt.Clone(), new NativeTradeRecord(evidence));
                    }
                    return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, evidence) };
                }
                if (operation.CommandCase == Operations.Operation.CommandOneofCase.AcceptTrade)
                {
                    if (!PrepareAccept(operation.AcceptTrade, identity, out failure)) return new Operations.ExecuteReply { Failure = failure };
                    var admitReply = Admit(state, request, context, out handle, out var authority);
                    if (admitReply != null) return admitReply;
                    using (authority!.Owned())
                    {
                        if (!PrepareAccept(operation.AcceptTrade, identity, out failure)) throw new InvalidOperationException("Accept prerequisites changed after admission.");
                        var deal = _sessionDeal!; var traderPawn = _sessionTrader; var faction = traderPawn?.Faction;
                        var beforeSilver = SessionSilver(); var beforeGoodwill = faction != null ? SafeInt(() => faction.PlayerGoodwill) : 0;
                        bool executed, actuallyTraded; Exception? executeError = null;
                        try { executed = deal.TryExecute(out actuallyTraded); }
                        catch (Exception e) { executed = false; actuallyTraded = false; executeError = e; }
                        var afterGoodwill = faction != null ? SafeInt(() => faction.PlayerGoodwill) : beforeGoodwill;
                        var sessionIdForEvidence = _sessionId ?? "";
                        var receiveQuest = !operation.AcceptTrade.HasReceiveQuest || operation.AcceptTrade.ReceiveQuest;
                        var quest = false;
                        if (receiveQuest && traderPawn != null)
                        {
                            try
                            {
                                if (traderPawn.mindState != null && traderPawn.mindState.hasQuest && _sessionNegotiator != null)
                                { TradeUtility.ReceiveQuestFromTrader(traderPawn, _sessionNegotiator); quest = true; }
                            }
                            catch { quest = false; }
                        }
                        var receivedQuestIds = new List<string>(); if (quest) receivedQuestIds.Add(sessionIdForEvidence);
                        try { TradeSession.Close(); } catch { }
                        _sessionId = null; _sessionColonyId = null; _sessionLoadToken = null; _sessionMap = null;
                        _sessionDeal = null; _sessionTrader = null; _sessionNegotiator = null; _giftMode = false;
                        evidence = new Receipts.EffectEvidence { Trade = new Receipts.TradeEffect
                        {
                            SessionId = sessionIdForEvidence, DealSignature = operation.AcceptTrade.HasExpectedDealSignature ? operation.AcceptTrade.ExpectedDealSignature : "",
                            Executed = executed, ActuallyTraded = actuallyTraded, Closed = true,
                            BeforeSilver = beforeSilver, AfterSilver = 0, BeforeGoodwill = beforeGoodwill, AfterGoodwill = afterGoodwill,
                            FactionId = faction != null ? faction.GetUniqueLoadID() : "",
                            Snapshot = new Receipts.SnapshotEvidence { EntityId = sessionIdForEvidence, AfterToken = "" },
                        } };
                        foreach (var id in receivedQuestIds) evidence.Trade.ReceivedQuestIds.Add(id);
                        state.Trade.Add(pre.Attempt.Clone(), new NativeTradeRecord(evidence));
                        if (executeError != null || !executed) throw new InvalidOperationException("Native trade execution requires observation.", executeError);
                    }
                    return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, evidence) };
                }
                if (operation.CommandCase == Operations.Operation.CommandOneofCase.EndTrade)
                {
                    if (!PrepareEnd(operation.EndTrade, identity, out failure)) return new Operations.ExecuteReply { Failure = failure };
                    var admitReply = Admit(state, request, context, out handle, out var authority);
                    if (admitReply != null) return admitReply;
                    using (authority!.Owned())
                    {
                        if (!PrepareEnd(operation.EndTrade, identity, out failure)) throw new InvalidOperationException("End prerequisites changed after admission.");
                        var closedOurSession = false;
                        var sessionIdForEvidence = _sessionId ?? "";
                        var matches = _sessionId != null && operation.EndTrade.Session.EntityId == _sessionId && SessionToken() == operation.EndTrade.Session.ExpectedSnapshotToken;
                        if (operation.EndTrade.Kind == Operations.EndTradeKind.CloseDialog)
                        {
                            var stack = Find.WindowStack;
                            try { foreach (var w in stack?.Windows?.OfType<Window>().Where(IsTradeDialog).ToList() ?? new List<Window>()) { try { w.Close(false); } catch { try { stack!.TryRemove(w, false); } catch { } } } }
                            catch { }
                        }
                        if (operation.EndTrade.Kind == Operations.EndTradeKind.Cancel || (operation.EndTrade.Kind == Operations.EndTradeKind.CloseDialog && matches))
                        {
                            var receiveQuest = operation.EndTrade.HasReceiveQuest && operation.EndTrade.ReceiveQuest;
                            CloseSession(receiveQuest);
                            closedOurSession = true;
                        }
                        evidence = new Receipts.EffectEvidence { Trade = new Receipts.TradeEffect
                        {
                            SessionId = sessionIdForEvidence, Executed = false, Closed = closedOurSession,
                            Snapshot = new Receipts.SnapshotEvidence { EntityId = sessionIdForEvidence, AfterToken = "" },
                        } };
                        state.Trade.Add(pre.Attempt.Clone(), new NativeTradeRecord(evidence));
                    }
                    return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, evidence) };
                }
                return Refuse(Common.FailureCode.Unsupported, "Trade execute implements OpenTrade, SetTradeLines, AcceptTrade and EndTrade only.");
            }
            catch (Exception error)
            {
                return handle == null
                    ? Refuse(Common.FailureCode.NativeFailure, "Trade validation failed: " + error.GetType().Name)
                    : new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, evidence!, "Admitted trade order requires observation: " + error.GetType().Name) };
            }
        }

        // Returns null when admission succeeded (handle/authority are set and
        // the caller should proceed into authority.Owned()), or the exact
        // reply to return immediately otherwise -- an authority refusal or
        // the ledger's own retry/duplicate/conflict reply.
        private static Operations.ExecuteReply? Admit(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context,
            out NativeAttemptLedger.Admission? handle, out NativeControlAuthority? authority)
        {
            handle = null; authority = null;
            var pre = request.Precondition;
            if (!NativeControlAuthority.TryGetForGame(Current.Game, out authority) || authority == null)
                return Refuse(Common.FailureCode.AuthorityRequired, "Current native authority is required.");
            var guard = authority.Check(pre.ExpectedGeneration);
            context.NativeGeneration = guard.Snapshot.Generation;
            if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) };
            var admission = state.Ledger.Admit("rimgovernor.operations.v1.Operations/Execute", request, context);
            if (admission.Kind != NativeAttemptLedger.DecisionKind.Admitted) return admission.Reply;
            handle = admission.Handle;
            return null;
        }

        // -------------------------------------------------------- preview
        internal static Operations.PreviewReply Preview(Operations.Operation operation, Common.ObservationContext context)
        {
            try
            {
                var identity = context.Identity;
                if (operation.CommandCase == Operations.Operation.CommandOneofCase.OpenTrade)
                {
                    if (!PrepareOpen(operation.OpenTrade, identity, out var trader, out _, out var failure)) return new Operations.PreviewReply { Failure = failure };
                    var faction = trader!.Faction;
                    return NativeOperationEnvelope.Preview(new Operations.PreviewReply { Evaluated = new Operations.PreviewEvaluation
                    {
                        Context = context.Clone(), Accepted = true,
                        Projected = new Receipts.EffectEvidence { Trade = new Receipts.TradeEffect { FactionId = faction != null ? faction.GetUniqueLoadID() : "" } },
                        Trade = new Operations.TradePreparation { SettlementId = trader.GetUniqueLoadID(), Goodwill = faction != null ? SafeInt(() => faction.PlayerGoodwill) : 0 },
                    } });
                }
                if (operation.CommandCase == Operations.Operation.CommandOneofCase.SetTradeLines)
                {
                    var all = RequireSession(identity, out var sessionFailure) ? _sessionDeal!.AllTradeables : new List<Tradeable>();
                    if (!PrepareLines(operation.SetTradeLines, identity, all, out var prepared, out var failure)) return new Operations.PreviewReply { Failure = failure };
                    var evidence = new Receipts.EffectEvidence { Trade = new Receipts.TradeEffect { SessionId = _sessionId ?? "", DealSignature = DealSignature() } };
                    foreach (var l in prepared) evidence.Trade.Lines.Add(new Receipts.TradeLineEffect { LineId = "#" + l.Index, BeforeCount = l.Before, AfterCount = l.Target });
                    return NativeOperationEnvelope.Preview(new Operations.PreviewReply { Evaluated = new Operations.PreviewEvaluation
                    { Context = context.Clone(), Accepted = true, Projected = evidence,
                      Trade = new Operations.TradePreparation { SessionId = _sessionId ?? "", DealSignature = DealSignature(), AvailableSilver = SessionSilver() } } });
                }
                if (operation.CommandCase == Operations.Operation.CommandOneofCase.AcceptTrade)
                {
                    if (!PrepareAccept(operation.AcceptTrade, identity, out var failure)) return new Operations.PreviewReply { Failure = failure };
                    return NativeOperationEnvelope.Preview(new Operations.PreviewReply { Evaluated = new Operations.PreviewEvaluation
                    { Context = context.Clone(), Accepted = true,
                      Projected = new Receipts.EffectEvidence { Trade = new Receipts.TradeEffect { SessionId = _sessionId ?? "", DealSignature = DealSignature() } },
                      Trade = new Operations.TradePreparation { SessionId = _sessionId ?? "", DealSignature = DealSignature(), AvailableSilver = SessionSilver() } } });
                }
                if (operation.CommandCase == Operations.Operation.CommandOneofCase.EndTrade)
                {
                    if (!PrepareEnd(operation.EndTrade, identity, out var failure)) return new Operations.PreviewReply { Failure = failure };
                    return NativeOperationEnvelope.Preview(new Operations.PreviewReply { Evaluated = new Operations.PreviewEvaluation
                    { Context = context.Clone(), Accepted = true, Projected = new Receipts.EffectEvidence { Trade = new Receipts.TradeEffect { SessionId = _sessionId ?? "" } } } });
                }
                return new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.Unsupported, "Trade preview implements OpenTrade, SetTradeLines, AcceptTrade and EndTrade only.") };
            }
            catch (Exception error) { return new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Trade preview failed: " + error.GetType().Name) }; }
        }

        private static Operations.ExecuteReply Refuse(Common.FailureCode code, string detail) => new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(code, detail) };
    }
}
