#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // The trade arm of Actions/Apply (NativeActionDispatch): OpenTrade,
    // SetTradeLines, AcceptTrade, EndTrade. Ports the legacy JSON home/trade
    // tool's (HomeTradeTools) native mechanics -- RimWorld's own static
    // TradeSession/TradeDeal, TradeSession.SetupWith, Tradeable.AdjustTo,
    // TradeDeal.TryExecute -- behind the typed boundary, deliberately
    // WITHOUT that tool's Dialog_Trade "watch" theater (opening a real
    // window, holding it on screen, closing it after): that exists so a
    // human watching the game can see a trade land, which has no analogue
    // for a typed native operation driven by the Go controller.
    //
    // RimWorld allows exactly one TradeSession at a time, so every operation
    // is an idempotent intent against that one live session, named by its
    // trader and negotiator: open-or-reuse, set lines to X, accept if the
    // deal signature matches, end. Each is validated against live state when
    // it applies and refused with a reason when the session is gone or held
    // by a different pair; the controller keeps no session identity of its
    // own. DealSignature() (staged contents, prices and backing things) is
    // the one content check: AcceptTrade applies only to the deal it saw.
    // An applied receipt is the whole outcome; there is no per-attempt
    // record, and the controller reads what followed from the trade-session
    // read (Live) and the session sheet. Reads never issue orders.

    // One OpenTrade whose negotiator was not beside the trader at admission:
    // vanilla's ordered TradeWithPawn job, which walks the negotiator after
    // the (possibly wandering) trader and, on arrival, runs the toil that
    // would open Dialog_Trade. Native patches that toil
    // (NativeTradeOperations.Arrive) to open the adapter session instead,
    // with no window. A walk the game interrupts is not reissued: it simply
    // stops reading as live, and the controller sends the open again.
    internal sealed class NativeTradeWalk
    {
        internal readonly Pawn Trader;
        internal readonly Pawn Negotiator;
        internal readonly bool GiftMode;
        internal readonly Common.Identity Identity;
        internal readonly Map Map;
        internal readonly Job Job;
        private readonly int jobId;
        internal NativeTradeWalk(Pawn trader, Pawn negotiator, bool giftMode, Common.Identity identity, Map map, Job job)
        { Trader = trader; Negotiator = negotiator; GiftMode = giftMode; Identity = identity.Clone(); Map = map; Job = job; jobId = job.loadID; }
        // RimWorld pools Job instances: the same object can return as another
        // job under a new loadID, so identity alone never proves the walk is
        // still the issued one.
        internal bool Owns(Job? job) { try { return job != null && ReferenceEquals(job, Job) && Job.loadID == jobId && Job.def == JobDefOf.TradeWithPawn && ReferenceEquals(Job.targetA.Thing, Trader); } catch { return false; } }
        // The negotiator is still running (or has queued) the issued job.
        internal bool Live() { try { return Owns(Negotiator.CurJob) || Negotiator.jobs != null && Negotiator.jobs.jobQueue.Any(q => Owns(q.job)); } catch { return false; } }
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
        // The one OpenTrade still walking its negotiator to the trader; a
        // session opens from it on arrival, and a new open by another pair
        // refuses while it is live.
        private static NativeTradeWalk? _walk;
        private static bool _arrivalHook;
        private const string ArrivalHookOwner = "homebridge.trade-arrival";

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

        internal readonly struct OpenSession
        {
            internal readonly string SessionId, DealSignature;
            internal readonly TradeDeal Deal; internal readonly Pawn Trader, Negotiator; internal readonly bool GiftMode;
            internal OpenSession(string id, string signature, TradeDeal deal, Pawn trader, Pawn negotiator, bool gift)
            { SessionId = id; DealSignature = signature; Deal = deal; Trader = trader; Negotiator = negotiator; GiftMode = gift; }
        }

        // SessionSheet is the read-side counterpart of RequireSession: the
        // open session for this identity, or the same refusal an operation
        // against it would get.
        internal static bool SessionSheet(Common.Identity identity, out OpenSession session, out Common.Failure failure)
        {
            session = default;
            if (!RequireSession(identity, out failure)) return false;
            session = new OpenSession(_sessionId!, DealSignature(), _sessionDeal!, _sessionTrader!, _sessionNegotiator!, _giftMode);
            return true;
        }

        // The live session, held by exactly the named trader and negotiator.
        private static bool RequireParticipants(string traderId, string negotiatorId, Common.Identity identity, out Common.Failure failure)
        {
            if (!RequireSession(identity, out failure)) return false;
            if (string.IsNullOrEmpty(traderId) || string.IsNullOrEmpty(negotiatorId) || traderId != SafeString(_sessionTrader) || negotiatorId != SafeString(_sessionNegotiator))
            { failure = ProtoBoundary.Fail(Common.FailureCode.StaleIdentity, "The open trade session is held by a different trader or negotiator."); return false; }
            return true;
        }

        // ---------------------------------------------------- session guard
        // Mirrors HomeTradeTools.RequireSession exactly, less the
        // ColonyIdentity GameComponent reference-equality check (replaced by
        // the identity's own colony/load token, which this framework
        // already carries). Adjacency is the physical act of opening, which
        // OpenTrade enforces at the moment the session is set up; once open,
        // the session binds its participants the way the vanilla dialog
        // does, and the sheet, line staging, accept and end need only both
        // still present, tradeable and eligible.
        private static bool RequireSession(Common.Identity identity, out Common.Failure failure)
        {
            failure = ProtoBoundary.Fail(Common.FailureCode.NotFound, "No native trade session is open for this identity.");
            if (_sessionId == null || _sessionColonyId != identity.ColonyId || _sessionLoadToken != identity.LoadToken) return false;
            if (!TradeSession.Active || TradeSession.deal == null || !ReferenceEquals(TradeSession.deal, _sessionDeal)
                || !ReferenceEquals(TradeSession.trader, _sessionTrader) || !ReferenceEquals(TradeSession.playerNegotiator, _sessionNegotiator)
                || !ProtoBoundary.IsLoaded(_sessionMap))
            { failure = ProtoBoundary.Fail(Common.FailureCode.StaleIdentity, "Trade session, map or load changed; do not reuse a stale session."); return false; }
            var trader = _sessionTrader!; var negotiator = _sessionNegotiator!;
            if (!trader.Spawned || trader.Map != _sessionMap || !SafeCanTradeNow(trader)
                || SafeBool(() => trader.mindState != null && trader.mindState.traderDismissed)
                || !negotiator.CanTradeWith(trader.Faction, trader.TraderKind).Accepted)
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Trader or negotiator departed or became unavailable."); return false; }
            if (SafeBool(() => negotiator.Dead) || SafeBool(() => negotiator.Downed) || SafeBool(() => negotiator.InMentalState) || !negotiator.Spawned || negotiator.Map != _sessionMap)
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "The negotiator is no longer able to trade."); return false; }
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
        // The live session or walk an OpenTrade intent names exactly, which the
        // intent reuses instead of opening again.
        private enum OpenReuse { None, Session, Walk }

        private static bool PrepareOpen(Operations.OpenTrade? command, Common.Identity identity, out Pawn? trader, out Pawn? negotiator, out OpenReuse reuse, out Common.Failure failure)
        {
            trader = null; negotiator = null; reuse = OpenReuse.None;
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "OpenTrade requires a trader and a negotiator.");
            if (command == null || string.IsNullOrEmpty(command.TraderId) || string.IsNullOrEmpty(command.NegotiatorId) || command.TraderId == command.NegotiatorId) return false;
            var giftMode = command.HasGiftMode && command.GiftMode;
            if (_sessionId != null)
            {
                if (!RequireParticipants(command.TraderId, command.NegotiatorId, identity, out failure)) return false;
                if (_giftMode != giftMode) { failure = ProtoBoundary.Fail(Common.FailureCode.OwnerConflict, "The open trade session differs in gift mode."); return false; }
                reuse = OpenReuse.Session; trader = _sessionTrader; negotiator = _sessionNegotiator; return true;
            }
            var walk = _walk;
            if (walk != null && walk.Live())
            {
                if (SafeString(walk.Trader) != command.TraderId || SafeString(walk.Negotiator) != command.NegotiatorId || walk.GiftMode != giftMode)
                { failure = ProtoBoundary.Fail(Common.FailureCode.OwnerConflict, "Another negotiator is already walking to a trader."); return false; }
                reuse = OpenReuse.Walk; trader = walk.Trader; negotiator = walk.Negotiator; return true;
            }
            if (OpenTradeDialog() != null) { failure = ProtoBoundary.Fail(Common.FailureCode.Unavailable, "A Dialog_Trade window is already open on screen."); return false; }
            if (TradeSession.Active) { failure = ProtoBoundary.Fail(Common.FailureCode.OwnerConflict, "A TradeSession is already open outside this adapter."); return false; }
            var map = ProtoBoundary.ResolveMap(identity);
            if (map == null) { failure = ProtoBoundary.Fail(Common.FailureCode.Unavailable, "No current map."); return false; }
            trader = map.mapPawns.AllPawnsSpawned.SingleOrDefault(p => p.GetUniqueLoadID() == command.TraderId && p.trader != null && p.trader.traderKind != null);
            if (trader == null) { failure = ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact map caravan trader is unavailable; this adapter does not support direct orbital open."); return false; }
            if (!SafeCanTradeNow(trader)) { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Trader reports CanTradeNow:false."); return false; }
            var negotiatorPawn = map.mapPawns.FreeColonistsSpawned.SingleOrDefault(p => p.GetUniqueLoadID() == command.NegotiatorId);
            negotiator = negotiatorPawn;
            if (negotiatorPawn == null || SafeBool(() => negotiatorPawn.Dead) || SafeBool(() => negotiatorPawn.Downed) || SafeBool(() => negotiatorPawn.InMentalState)
                || SafeBool(() => negotiatorPawn.WorkTagIsDisabled(WorkTags.Social)))
            { failure = ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact eligible negotiator is unavailable."); return false; }
            var traderPawn = trader;
            if (SafeBool(() => traderPawn.mindState != null && traderPawn.mindState.traderDismissed) || !negotiatorPawn.CanTradeWith(traderPawn.Faction, traderPawn.TraderKind).Accepted
                || !negotiatorPawn.CanReach(traderPawn, PathEndMode.Touch, Danger.Deadly))
            { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Native player trade eligibility or reachability refused this negotiator."); return false; }
            if (Chebyshev(negotiatorPawn.Position, traderPawn.Position) > 1 && !ArrivalHook())
            { failure = ProtoBoundary.Fail(Common.FailureCode.Unavailable, "The negotiator must walk to the trader and the arrival hook is unavailable."); return false; }
            return true;
        }

        private static bool Adjacent(Pawn trader, Pawn negotiator) => Chebyshev(negotiator.Position, trader.Position) <= 1;

        // Opens the session for a prepared trader/negotiator pair standing
        // beside each other. Shared by the immediate open at admission and
        // the arrival of a walked open; the caller holds whatever authority
        // the moment needs.
        private static void BindSession(Pawn trader, Pawn negotiator, bool giftMode, Common.Identity identity, Map map)
        {
            try { TradeSession.SetupWith(trader, negotiator, giftMode); }
            catch (Exception e) { throw new InvalidOperationException("TradeSession.SetupWith threw: " + e.GetType().Name, e); }
            if (!TradeSession.Active || TradeSession.deal == null) throw new InvalidOperationException("TradeSession.SetupWith returned but the session is not active.");
            _sessionId = Guid.NewGuid().ToString("N"); _sessionColonyId = identity.ColonyId; _sessionLoadToken = identity.LoadToken;
            _sessionMap = map; _sessionDeal = TradeSession.deal; _sessionTrader = trader; _sessionNegotiator = negotiator;
            _giftMode = giftMode;
        }

        // ------------------------------------------------------- arrival
        // JobDriver_TradeWithPawn yields a touch-goto toil on the trader, then
        // a toil whose initAction opens Dialog_Trade. A pass-through postfix
        // on MakeNewToils wraps that last toil's initAction: for the adapter's
        // walk it opens the session instead; for any other job (a player's
        // own trade order) the vanilla action runs unchanged.
        private static bool ArrivalHook()
        {
            if (_arrivalHook) return true;
            try
            {
                var target = AccessTools.Method(typeof(JobDriver_TradeWithPawn), "MakeNewToils");
                if (target == null) return false;
                new Harmony(ArrivalHookOwner).Patch(target, postfix: new HarmonyMethod(AccessTools.Method(typeof(NativeTradeOperations), nameof(TradeToils))));
                _arrivalHook = true;
            }
            catch { _arrivalHook = false; }
            return _arrivalHook;
        }
        private static IEnumerable<Toil> TradeToils(IEnumerable<Toil> __result, JobDriver_TradeWithPawn __instance)
        {
            var toils = __result.ToList();
            var trade = toils.LastOrDefault();
            var vanilla = trade?.initAction;
            if (trade != null && vanilla != null)
            {
                var driver = __instance;
                trade.initAction = () => { if (!Arrive(driver.job)) vanilla(); };
            }
            return toils;
        }
        // The adapter's walk reached the trader: open the session if the pair
        // may still trade, never the dialog. A pair that may not is left
        // without a session; the controller reads neither walk nor session and
        // sends the open again. False for a job that is not the walk.
        private static bool Arrive(Job? job)
        {
            var walk = _walk;
            if (walk == null || !walk.Owns(job)) return false;
            _walk = null;
            try
            {
                var trader = walk.Trader; var negotiator = walk.Negotiator;
                if (_sessionId != null || TradeSession.Active || OpenTradeDialog() != null
                    || !trader.Spawned || trader.Map != walk.Map || !SafeCanTradeNow(trader) || SafeBool(() => trader.mindState != null && trader.mindState.traderDismissed)
                    || !negotiator.Spawned || SafeBool(() => negotiator.Dead) || SafeBool(() => negotiator.Downed) || SafeBool(() => negotiator.InMentalState)
                    || !negotiator.CanTradeWith(trader.Faction, trader.TraderKind).Accepted || !ProtoBoundary.IsLoaded(walk.Map))
                    return true;
                BindSession(trader, negotiator, walk.GiftMode, walk.Identity, walk.Map);
            }
            catch { }
            return true;
        }
        // The adapter's one live trade for this identity: the open session's
        // pair (open) or a walk the negotiator is still running. Reads only.
        internal static bool Live(Common.Identity identity, out Pawn? trader, out Pawn? negotiator, out bool open)
        {
            trader = null; negotiator = null; open = false;
            if (_sessionId != null)
            {
                if (_sessionColonyId != identity.ColonyId || _sessionLoadToken != identity.LoadToken) return false;
                trader = _sessionTrader; negotiator = _sessionNegotiator; open = true;
                return true;
            }
            var walk = _walk;
            if (walk == null || walk.Identity.ColonyId != identity.ColonyId || walk.Identity.LoadToken != identity.LoadToken || !walk.Live()) return false;
            trader = walk.Trader; negotiator = walk.Negotiator;
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
            } };
        }

        // A walked open's evidence before arrival: no session yet, so no id,
        // signature or snapshot; the faction and starting silver are known.
        private static Receipts.EffectEvidence ApproachEvidence(Pawn trader)
        {
            var faction = trader.Faction;
            return new Receipts.EffectEvidence { Trade = new Receipts.TradeEffect
            {
                SessionId = "", DealSignature = "", Executed = false, Closed = false,
                BeforeSilver = 0, BeforeGoodwill = faction != null ? SafeInt(() => faction.PlayerGoodwill) : 0,
                FactionId = faction != null ? faction.GetUniqueLoadID() : "",
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
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "SetTradeLines requires at least one line.");
            if (command == null || command.Lines.Count == 0) return false;
            if (!RequireParticipants(command.TraderId, command.NegotiatorId, identity, out failure)) return false;
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
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "AcceptTrade requires a request.");
            if (command == null) return false;
            if (!RequireParticipants(command.TraderId, command.NegotiatorId, identity, out failure)) return false;
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
                var proteinPurchase = deal.AllTradeables.Any(t => SafeInt(() => t.CountToTransfer) > 0
                    && !IsPawnRow(t) && NativeTradeFoodFacts.Protein(SafeDef(t)));
                foreach (var row in deal.AllTradeables.Where(t => SafeInt(() => t.CountToTransfer) < 0 || SafeBool(() => t.IsCurrency)))
                {
                    var def = SafeDef(row);
                    if (def == null || !floors.TryGetValue(def.defName, out var floor)
                        || SafeInt(() => row.thingsColony.Where(t => !t.Destroyed).Sum(t => t.stackCount)) + SafeInt(() => row.CountToTransfer) < floor)
                    { failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Economic stock reserve would be violated."); return false; }
                    var cropSurplus = floor > 0 && proteinPurchase && NativeTradeFoodFacts.Crop(def);
                    if (!SafeBool(() => row.IsCurrency) && (def.IsWeapon || def.IsApparel || def.IsMedicine || def.IsNutritionGivingIngestible && !cropSurplus || IsPawnRow(row)))
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
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "EndTrade requires an explicit kind.");
            if (command == null || !command.HasKind || command.Kind == Operations.EndTradeKind.Unspecified) return false;
            // A cancel with no session left is already done, and one whose
            // trader has since left still closes; a session held by a
            // different pair is refused.
            if (command.Kind == Operations.EndTradeKind.Cancel && _sessionId != null && !EndMatches(command))
            { failure = ProtoBoundary.Fail(Common.FailureCode.StaleIdentity, "The open trade session is held by a different trader or negotiator."); return false; }
            // close_dialog: the escape hatch. It always succeeds at sweeping
            // any stray Dialog_Trade window; it only additionally closes the
            // session (ApplyEnd) when the named pair holds it.
            return true;
        }

        // Whether an EndTrade names the pair holding this adapter's session.
        private static bool EndMatches(Operations.EndTrade command) =>
            _sessionId != null && !string.IsNullOrEmpty(command.TraderId) && command.TraderId == SafeString(_sessionTrader) && command.NegotiatorId == SafeString(_sessionNegotiator);

        // --------------------------------------------------------- intent
        // Actions/Apply's trade arm (NativeActionDispatch). The pair on the
        // TradeIntent names the session; it replaces the step's own ids.
        internal static Operations.TradeIntent Normalized(Operations.TradeIntent intent)
        {
            var copy = intent.Clone();
            switch (copy.StepCase)
            {
                case Operations.TradeIntent.StepOneofCase.Open: copy.Open.TraderId = copy.TraderId; copy.Open.NegotiatorId = copy.NegotiatorId; break;
                case Operations.TradeIntent.StepOneofCase.SetLines: copy.SetLines.TraderId = copy.TraderId; copy.SetLines.NegotiatorId = copy.NegotiatorId; break;
                case Operations.TradeIntent.StepOneofCase.Accept: copy.Accept.TraderId = copy.TraderId; copy.Accept.NegotiatorId = copy.NegotiatorId; break;
                case Operations.TradeIntent.StepOneofCase.End: copy.End.TraderId = copy.TraderId; copy.End.NegotiatorId = copy.NegotiatorId; break;
            }
            return copy;
        }

        // Whether the step applies to live state now; null when it does.
        internal static Common.Failure? Validate(Operations.TradeIntent intent, Common.Identity identity)
        {
            Common.Failure failure;
            switch (intent.StepCase)
            {
                case Operations.TradeIntent.StepOneofCase.Open:
                    return PrepareOpen(intent.Open, identity, out _, out _, out _, out failure) ? null : failure;
                case Operations.TradeIntent.StepOneofCase.SetLines:
                    var all = RequireSession(identity, out _) ? _sessionDeal!.AllTradeables : new List<Tradeable>();
                    return PrepareLines(intent.SetLines, identity, all, out _, out failure) ? null : failure;
                case Operations.TradeIntent.StepOneofCase.Accept:
                    return PrepareAccept(intent.Accept, identity, out failure) ? null : failure;
                case Operations.TradeIntent.StepOneofCase.End:
                    return PrepareEnd(intent.End, identity, out failure) ? null : failure;
                default:
                    return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "A trade intent names exactly one step.");
            }
        }

        // What the step would stage, without touching the session.
        internal static Receipts.EffectEvidence Preview(Operations.TradeIntent intent, Common.Identity identity)
        {
            var evidence = new Receipts.EffectEvidence { Trade = new Receipts.TradeEffect { SessionId = _sessionId ?? "", DealSignature = _sessionId != null ? DealSignature() : "" } };
            if (intent.StepCase == Operations.TradeIntent.StepOneofCase.SetLines && RequireSession(identity, out _)
                && PrepareLines(intent.SetLines, identity, _sessionDeal!.AllTradeables, out var prepared, out _))
                foreach (var l in prepared) evidence.Trade.Lines.Add(new Receipts.TradeLineEffect { LineId = "#" + l.Index, BeforeCount = l.Before, AfterCount = l.Target });
            return evidence;
        }

        // Applies a validated step; the caller holds native authority. A
        // throw after a partial effect leaves the session to the next read.
        internal static Receipts.EffectEvidence Apply(Operations.TradeIntent intent, Common.ObservationContext context)
        {
            switch (intent.StepCase)
            {
                case Operations.TradeIntent.StepOneofCase.Open: return ApplyOpen(intent.Open, context);
                case Operations.TradeIntent.StepOneofCase.SetLines: return ApplyLines(intent.SetLines, context.Identity);
                case Operations.TradeIntent.StepOneofCase.Accept: return ApplyAccept(intent.Accept, context.Identity);
                case Operations.TradeIntent.StepOneofCase.End: return ApplyEnd(intent.End, context.Identity);
                default: throw new InvalidOperationException("A trade intent names exactly one step.");
            }
        }

        private static Receipts.EffectEvidence ApplyOpen(Operations.OpenTrade command, Common.ObservationContext context)
        {
            var identity = context.Identity;
            if (!PrepareOpen(command, identity, out var trader, out var negotiator, out var reuse, out _) || trader == null || negotiator == null)
                throw new InvalidOperationException("Open prerequisites changed after validation.");
            var giftMode = command.HasGiftMode && command.GiftMode;
            var map = ProtoBoundary.ResolveMap(context);
            if (map == null) throw new InvalidOperationException("No current map.");
            if (reuse == OpenReuse.Session) return OpenEvidence(trader, false);
            if (reuse == OpenReuse.Walk) return ApproachEvidence(trader);
            if (Adjacent(trader, negotiator))
            {
                BindSession(trader, negotiator, giftMode, identity, map);
                if (!TradeSession.Active) throw new InvalidOperationException("Native open readback did not apply.");
                return OpenEvidence(trader, false);
            }
            // The negotiator walks after the trader; the session opens on
            // arrival (Arrive).
            var job = JobMaker.MakeJob(JobDefOf.TradeWithPawn, trader);
            if (job == null || job.def != JobDefOf.TradeWithPawn || !ReferenceEquals(job.targetA.Thing, trader)) throw new InvalidOperationException("Native TradeWithPawn job could not be prepared.");
            // Registered before the order: a pawn already in touch arrives
            // inside TryTakeOrderedJob.
            _walk = new NativeTradeWalk(trader, negotiator, giftMode, identity, map, job);
            bool taken;
            try { taken = negotiator.jobs.TryTakeOrderedJob(job, JobTag.Misc); }
            catch (Exception e) { _walk = null; throw new InvalidOperationException("Ordered TradeWithPawn threw: " + e.GetType().Name, e); }
            if (!taken) { _walk = null; throw new InvalidOperationException("The negotiator did not take the walk to the trader."); }
            return _sessionId != null && ReferenceEquals(_sessionTrader, trader) ? OpenEvidence(trader, false) : ApproachEvidence(trader);
        }

        private static Receipts.EffectEvidence ApplyLines(Operations.SetTradeLines command, Common.Identity identity)
        {
            var deal = _sessionDeal; if (deal == null) throw new InvalidOperationException("Trade session closed before native effect.");
            var all = deal.AllTradeables;
            if (!PrepareLines(command, identity, all, out var prepared, out _)) throw new InvalidOperationException("Trade lines prerequisites changed after validation.");
            var lineEffects = new List<Receipts.TradeLineEffect>();
            foreach (var line in prepared)
            {
                var row = all[line.Index];
                row.AdjustTo(line.Target);
                lineEffects.Add(new Receipts.TradeLineEffect { LineId = "#" + line.Index, BeforeCount = line.Before, AfterCount = SafeInt(() => row.CountToTransfer) });
            }
            SafeUpdateCurrency(deal);
            var evidence = new Receipts.EffectEvidence { Trade = new Receipts.TradeEffect
            {
                SessionId = _sessionId ?? "", DealSignature = DealSignature(), Executed = false, Closed = false,
                BeforeSilver = SessionSilver(), AfterSilver = SessionSilver(),
            } };
            foreach (var l in lineEffects) evidence.Trade.Lines.Add(l);
            return evidence;
        }

        private static Receipts.EffectEvidence ApplyAccept(Operations.AcceptTrade command, Common.Identity identity)
        {
            if (!PrepareAccept(command, identity, out _)) throw new InvalidOperationException("Accept prerequisites changed after validation.");
            var deal = _sessionDeal!; var traderPawn = _sessionTrader; var faction = traderPawn?.Faction;
            var beforeSilver = SessionSilver(); var beforeGoodwill = faction != null ? SafeInt(() => faction.PlayerGoodwill) : 0;
            bool executed, actuallyTraded;
            try { executed = deal.TryExecute(out actuallyTraded); }
            catch { executed = false; actuallyTraded = false; }
            var afterGoodwill = faction != null ? SafeInt(() => faction.PlayerGoodwill) : beforeGoodwill;
            var sessionIdForEvidence = _sessionId ?? "";
            var receiveQuest = !command.HasReceiveQuest || command.ReceiveQuest;
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
            try { TradeSession.Close(); } catch { }
            _sessionId = null; _sessionColonyId = null; _sessionLoadToken = null; _sessionMap = null;
            _sessionDeal = null; _sessionTrader = null; _sessionNegotiator = null; _giftMode = false;
            // A deal the game would not execute still ends the session: the
            // intent applied, and its evidence says the deal did not.
            var evidence = new Receipts.EffectEvidence { Trade = new Receipts.TradeEffect
            {
                SessionId = sessionIdForEvidence, DealSignature = command.HasExpectedDealSignature ? command.ExpectedDealSignature : "",
                Executed = executed, ActuallyTraded = actuallyTraded, Closed = true,
                BeforeSilver = beforeSilver, AfterSilver = 0, BeforeGoodwill = beforeGoodwill, AfterGoodwill = afterGoodwill,
                FactionId = faction != null ? faction.GetUniqueLoadID() : "",
            } };
            if (quest) evidence.Trade.ReceivedQuestIds.Add(sessionIdForEvidence);
            return evidence;
        }

        private static Receipts.EffectEvidence ApplyEnd(Operations.EndTrade command, Common.Identity identity)
        {
            if (!PrepareEnd(command, identity, out _)) throw new InvalidOperationException("End prerequisites changed after validation.");
            var sessionIdForEvidence = _sessionId ?? "";
            var matches = EndMatches(command);
            if (command.Kind == Operations.EndTradeKind.CloseDialog)
            {
                var stack = Find.WindowStack;
                try { foreach (var w in stack?.Windows?.OfType<Window>().Where(IsTradeDialog).ToList() ?? new List<Window>()) { try { w.Close(false); } catch { try { stack!.TryRemove(w, false); } catch { } } } }
                catch { }
            }
            if (matches) CloseSession(command.HasReceiveQuest && command.ReceiveQuest);
            return new Receipts.EffectEvidence { Trade = new Receipts.TradeEffect { SessionId = sessionIdForEvidence, Executed = false, Closed = matches } };
        }
    }
}
