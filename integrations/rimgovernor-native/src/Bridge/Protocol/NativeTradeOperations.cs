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
        private readonly NativeTradeApproach? approach;
        internal NativeTradeRecord(Receipts.EffectEvidence evidence) { Evidence = evidence; }
        internal NativeTradeRecord(NativeTradeApproach approach) { Evidence = approach.Evidence; this.approach = approach; }

        // Every trade sub-operation resolves synchronously inside its own
        // Execute call, so Observe reports the outcome captured at admission
        // time directly (the immediate-completion shape
        // NativeHusbandryOperations and NativeWorkSettings use) -- except an
        // OpenTrade whose negotiator had to walk: that one is a native Goto
        // the trader, and its session opens on arrival (see
        // NativeTradeApproach).
        internal Receipts.Progress Observe(Common.AttemptKey attempt, Common.ObservationContext context)
        {
            if (approach != null) return approach.Observe(attempt, context);
            return new Receipts.Progress
            { Attempt = attempt.Clone(), Context = context.Clone(), CompleteInspection = true, Completed = new Receipts.CompletedEffect { Evidence = Evidence } };
        }
    }

    // One OpenTrade whose negotiator was not beside the trader at admission.
    // Native issues the vanilla ordered Goto with the trader pawn itself as
    // the target -- Pawn_PathFollower re-paths to a moving target, so the
    // walk tracks the trader's wandering -- and opens the session the tick
    // the pather arrives (NativeTradeOperations.Arrived), which is the only
    // moment adjacency can be relied on against a pawn that never stands
    // still. Observe reports the walk pending, the arrival's session
    // completed, and an ended job with no session unsuccessful; a stopped
    // walk that still left the pawn adjacent opens the session at the
    // observation instead.
    internal sealed class NativeTradeApproach
    {
        internal readonly Pawn Trader;
        internal readonly Pawn Negotiator;
        internal readonly bool GiftMode;
        internal readonly Common.Identity Identity;
        internal readonly Map Map;
        internal Job Job;
        private int jobId;
        private readonly Common.ObservationContext admitted;
        internal Receipts.EffectEvidence Evidence;
        internal string? Failure;
        internal bool Opened;
        // Walks issued so far; a walk the game ends short of the trader is
        // reissued from the next inspection, up to MaxWalks in all.
        internal int Walks;
        internal const int MaxWalks = 4;
        internal NativeTradeApproach(Pawn trader, Pawn negotiator, bool giftMode, Common.Identity identity, Map map, Job job, Receipts.EffectEvidence evidence, Common.ObservationContext context)
        { Trader = trader; Negotiator = negotiator; GiftMode = giftMode; Identity = identity.Clone(); Map = map; Job = job; jobId = job.loadID; Evidence = evidence; admitted = context.Clone(); }
        internal void Walked(Job job) { Job = job; jobId = job.loadID; Walks++; }
        // RimWorld pools Job instances: the same object can return as another
        // job under a new loadID, so identity alone never proves the walk is
        // still the issued one.
        private bool Live() { try { return Job.loadID == jobId && Job.def == JobDefOf.Goto && ReferenceEquals(Job.targetA.Thing, Trader); } catch { return false; } }
        internal bool Current() { try { return Live() && ReferenceEquals(Negotiator.CurJob, Job); } catch { return false; } }
        private bool Queued() { try { return Live() && Negotiator.jobs != null && Negotiator.jobs.jobQueue.Any(q => ReferenceEquals(q.job, Job)); } catch { return false; } }
        internal Receipts.Progress Observe(Common.AttemptKey attempt, Common.ObservationContext context)
        {
            var result = new Receipts.Progress { Attempt = attempt.Clone(), Context = context.Clone(), CompleteInspection = true };
            try
            {
                if (!context.Identity.Equals(admitted.Identity) || context.Tick < admitted.Tick) throw new InvalidOperationException("Current trade approach context cannot be inspected.");
                if (!Opened && Failure == null && !Current() && !Queued()) NativeTradeOperations.WalkEnded(this);
                if (Opened) result.Completed = new Receipts.CompletedEffect { Evidence = Evidence };
                else if (Failure != null) result.Unsuccessful = new Receipts.UnsuccessfulEffect { Reason = Receipts.UnsuccessfulReason.Interrupted, Evidence = Evidence, Detail = Failure };
                else result.Pending = new Receipts.PendingEffect { Evidence = Evidence };
            }
            catch (Exception error) { result.CompleteInspection = false; result.Unknown = new Receipts.UnknownEffect { Reason = "Trade approach inspection unavailable: " + error.GetType().Name }; }
            return result;
        }
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
        // session opens from it on arrival, and a new open refuses while it
        // stands.
        private static NativeTradeApproach? _approach;
        private static bool _arrivalHook;
        private const string ArrivalHookOwner = "homebridge.trade-approach";

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

        // ------------------------------------------------ observation surface
        // The typed trade reads (NativeTradeObservationTools) expose the
        // same tokens this adapter checks, so an observed trader/negotiator
        // ref carries exactly the CAS token PrepareOpen will compare.
        internal static string TraderSnapshotToken(Pawn trader) => TraderToken(trader);
        internal static string NegotiatorSnapshotToken(Pawn negotiator) => NegotiatorToken(negotiator);

        internal readonly struct OpenSession
        {
            internal readonly string SessionId, SessionToken, DealSignature;
            internal readonly TradeDeal Deal; internal readonly Pawn Trader, Negotiator; internal readonly bool GiftMode;
            internal OpenSession(string id, string token, string signature, TradeDeal deal, Pawn trader, Pawn negotiator, bool gift)
            { SessionId = id; SessionToken = token; DealSignature = signature; Deal = deal; Trader = trader; Negotiator = negotiator; GiftMode = gift; }
        }

        // SessionSheet is the read-side counterpart of RequireSession: the
        // open session for this identity, or the same refusal an operation
        // against it would get.
        internal static bool SessionSheet(Common.Identity identity, out OpenSession session, out Common.Failure failure)
        {
            session = default;
            if (!RequireSession(identity, out failure)) return false;
            session = new OpenSession(_sessionId!, SessionToken(), DealSignature(), _sessionDeal!, _sessionTrader!, _sessionNegotiator!, _giftMode);
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
            if (!trader.Spawned || trader.Map != _sessionMap || !SafeCanTradeNow(trader) || Find.TickManager == null || !Find.TickManager.Paused
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
        private static bool PrepareOpen(Operations.OpenTrade? command, Common.Identity identity, out Pawn? trader, out Pawn? negotiator, out Common.Failure failure)
        {
            trader = null; negotiator = null;
            failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "OpenTrade requires an exact eligible trader and negotiator snapshot.");
            if (command == null || !NativeDraftProtocol.ValidEntity(command.Trader) || !NativeDraftProtocol.ValidEntity(command.Negotiator)) return false;
            if (_sessionId != null) { failure = ProtoBoundary.Fail(Common.FailureCode.OwnerConflict, "A native trade session is already open; accept or end it first."); return false; }
            if (_approach != null && _approach.Current()) { failure = ProtoBoundary.Fail(Common.FailureCode.OwnerConflict, "A negotiator is already walking to a trader; observe that open first."); return false; }
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
        // Pawn_PathFollower.PatherArrived fires the tick a walk ends at its
        // destination; a walked open resolves there, while the trader is
        // still beside the pawn.
        private static bool ArrivalHook()
        {
            if (_arrivalHook) return true;
            try
            {
                var target = AccessTools.Method(typeof(Pawn_PathFollower), "PatherArrived");
                if (target == null) return false;
                new Harmony(ArrivalHookOwner).Patch(target, postfix: new HarmonyMethod(AccessTools.Method(typeof(NativeTradeOperations), nameof(AfterArrived))));
                _arrivalHook = true;
            }
            catch { _arrivalHook = false; }
            return _arrivalHook;
        }
        private static void AfterArrived(Pawn_PathFollower __instance)
        {
            var approach = _approach;
            if (approach == null) return;
            if (!ReferenceEquals(__instance, approach.Negotiator.pather) || !approach.Current()) return;
            Arrived(approach, "The negotiator arrived but could not open the session");
        }
        // A walk the game ended without the arrival hook firing: beside the
        // trader it opens as an arrival; short of the trader it is walked
        // again while walks remain, otherwise the open fails.
        internal static void WalkEnded(NativeTradeApproach approach)
        {
            var trader = approach.Trader; var negotiator = approach.Negotiator;
            if (Adjacent(trader, negotiator) || approach.Walks >= NativeTradeApproach.MaxWalks
                || _sessionId != null || TradeSession.Active || !trader.Spawned || !SafeCanTradeNow(trader)
                || !negotiator.Spawned || SafeBool(() => negotiator.Downed) || SafeBool(() => negotiator.Dead) || SafeBool(() => negotiator.InMentalState))
            { Arrived(approach, "The walk to the trader ended before arrival (walk " + approach.Walks + " of " + NativeTradeApproach.MaxWalks + ")"); return; }
            try
            {
                var job = Walk(trader, negotiator);
                if (job == null) { Arrived(approach, "The negotiator would not walk to the trader again"); return; }
                approach.Walked(job);
            }
            catch (Exception error) { approach.Failure = "Walking to the trader again threw " + error.GetType().Name + "."; if (ReferenceEquals(_approach, approach)) _approach = null; }
        }
        // Orders one walk: a Goto job on the trader, re-aimed to touch it
        // (Goto paths OnCell, which a cell holding the trader never
        // satisfies; the pather keeps the end mode as it follows the
        // wandering trader). Null when the negotiator refused the order.
        private static Job? Walk(Pawn trader, Pawn negotiator)
        {
            var job = JobMaker.MakeJob(JobDefOf.Goto, trader);
            if (job == null || job.def != JobDefOf.Goto || !ReferenceEquals(job.targetA.Thing, trader)) throw new InvalidOperationException("Native Goto job could not be prepared.");
            if (!negotiator.jobs.TryTakeOrderedJob(job, JobTag.Misc) || !ReferenceEquals(negotiator.CurJob, job)) return null;
            negotiator.pather.StartPath(trader, PathEndMode.Touch);
            return job;
        }
        // Resolves a walked open: the session opens if the pair is still
        // eligible and adjacent, otherwise the open fails with the reason.
        internal static void Arrived(NativeTradeApproach approach, string detail)
        {
            if (approach.Opened || approach.Failure != null) return;
            if (ReferenceEquals(_approach, approach)) _approach = null;
            try
            {
                var trader = approach.Trader; var negotiator = approach.Negotiator;
                string? reason = null;
                if (_sessionId != null || TradeSession.Active) reason = "another trade session is open";
                else if (!trader.Spawned || trader.Map != approach.Map || !SafeCanTradeNow(trader) || SafeBool(() => trader.mindState != null && trader.mindState.traderDismissed)) reason = "the trader departed or stopped trading";
                else if (!negotiator.Spawned || SafeBool(() => negotiator.Dead) || SafeBool(() => negotiator.Downed) || SafeBool(() => negotiator.InMentalState)
                    || !negotiator.CanTradeWith(trader.Faction, trader.TraderKind).Accepted) reason = "the negotiator is no longer eligible";
                else if (!Adjacent(trader, negotiator)) reason = "the negotiator is not beside the trader (" + Chebyshev(negotiator.Position, trader.Position) + " cells)";
                if (reason != null) { approach.Failure = detail + ": " + reason + "."; return; }
                BindSession(trader, negotiator, approach.GiftMode, approach.Identity, approach.Map);
                approach.Evidence = OpenEvidence(trader, false);
                approach.Opened = true;
            }
            catch (Exception error) { approach.Failure = detail + ": " + error.GetType().Name + "."; }
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
                    if (!TryAdmit(state, request, context, out handle, out var authority, out var refusal)) return refusal;
                    using (authority.Owned())
                    {
                        if (!PrepareOpen(operation.OpenTrade, identity, out trader, out negotiator, out failure) || trader == null || negotiator == null)
                            throw new InvalidOperationException("Open prerequisites changed after admission.");
                        var giftMode = operation.OpenTrade.HasGiftMode && operation.OpenTrade.GiftMode;
                        var map = ProtoBoundary.ResolveMap(context);
                        if (map == null) throw new InvalidOperationException("No current map after admission.");
                        if (Adjacent(trader, negotiator))
                        {
                            BindSession(trader, negotiator, giftMode, identity, map);
                            evidence = OpenEvidence(trader, false);
                            state.Trade.Add(pre.Attempt.Clone(), new NativeTradeRecord(evidence));
                            if (!TradeSession.Active) throw new InvalidOperationException("Native open readback did not apply.");
                        }
                        else
                        {
                            // The negotiator walks; the session opens on arrival.
                            evidence = ApproachEvidence(trader);
                            var placeholder = JobMaker.MakeJob(JobDefOf.Goto, trader);
                            if (placeholder == null) throw new InvalidOperationException("Native Goto job could not be prepared.");
                            var approach = new NativeTradeApproach(trader, negotiator, giftMode, identity, map, placeholder, evidence, context);
                            // Registered before the order: an already
                            // adjacent pather arrives inside StartPath.
                            _approach = approach;
                            Job? walked;
                            try { walked = Walk(trader, negotiator); }
                            catch (Exception e) { _approach = null; throw new InvalidOperationException("Ordered Goto threw: " + e.GetType().Name, e); }
                            if (walked == null) { _approach = null; throw new InvalidOperationException("The negotiator did not take the walk to the trader."); }
                            approach.Walked(walked);
                            if (approach.Opened) { evidence = approach.Evidence; state.Trade.Add(pre.Attempt.Clone(), new NativeTradeRecord(evidence)); }
                            else if (approach.Failure != null) throw new InvalidOperationException(approach.Failure);
                            else state.Trade.Add(pre.Attempt.Clone(), new NativeTradeRecord(approach));
                        }
                    }
                    return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, evidence) };
                }
                if (operation.CommandCase == Operations.Operation.CommandOneofCase.SetTradeLines)
                {
                    var all0 = RequireSession(identity, out failure) ? _sessionDeal!.AllTradeables : new List<Tradeable>();
                    if (!PrepareLines(operation.SetTradeLines, identity, all0, out var prepared, out failure)) return new Operations.ExecuteReply { Failure = failure };
                    if (!TryAdmit(state, request, context, out handle, out var authority, out var refusal)) return refusal;
                    using (authority.Owned())
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
                    if (!TryAdmit(state, request, context, out handle, out var authority, out var refusal)) return refusal;
                    using (authority.Owned())
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
                    if (!TryAdmit(state, request, context, out handle, out var authority, out var refusal)) return refusal;
                    using (authority.Owned())
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
                    : new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, evidence, "Admitted trade order requires observation: " + error.GetType().Name) };
            }
        }

        // Returns null when admission succeeded (handle/authority are set and
        // the caller should proceed into authority.Owned()), or the exact
        // reply to return immediately otherwise -- an authority refusal or
        // the ledger's own retry/duplicate/conflict reply.
        private static bool TryAdmit(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context,
            [NotNullWhen(true)] out NativeAttemptLedger.Admission? handle, [NotNullWhen(true)] out NativeControlAuthority? authority,
            [NotNullWhen(false)] out Operations.ExecuteReply? refusal)
        {
            handle = null; authority = null; refusal = null;
            var pre = request.Precondition;
            if (!NativeControlAuthority.TryGetForGame(Current.Game, out authority) || authority == null)
            { refusal = Refuse(Common.FailureCode.AuthorityRequired, "Current native authority is required."); return false; }
            var guard = authority.Check(pre.ExpectedGeneration);
            context.NativeGeneration = guard.Snapshot.Generation;
            if (!guard.Success) { refusal = new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) }; return false; }
            var admission = state.Ledger.Admit("rimgovernor.operations.v1.Operations/Execute", request, context);
            if (admission.Kind != NativeAttemptLedger.DecisionKind.Admitted) { refusal = admission.DecidedReply; return false; }
            handle = admission.AdmittedHandle;
            return true;
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
                    { Context = context.Clone(), Accepted = true, Projected = new Receipts.EffectEvidence { Trade = new Receipts.TradeEffect { SessionId = _sessionId ?? "" } },
                      Trade = new Operations.TradePreparation { SessionId = _sessionId ?? "", DealSignature = DealSignature(), AvailableSilver = SessionSilver() } } });
                }
                return new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.Unsupported, "Trade preview implements OpenTrade, SetTradeLines, AcceptTrade and EndTrade only.") };
            }
            catch (Exception error) { return new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Trade preview failed: " + error.GetType().Name) }; }
        }

        private static Operations.ExecuteReply Refuse(Common.FailureCode code, string detail) => new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(code, detail) };
    }
}
