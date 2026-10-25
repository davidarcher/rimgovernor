using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;

namespace RimGovernor.Native
{
    internal static partial class NativeTradeOperations
    {
        private static RimGovernor.Brain.GameFacts.Common.TradeTarget? _sessionTarget;
        private static OrbitalContact? _contact;
        private static bool _contactHook;

        // Target identity includes the inventory owner; a second caravan at
        // the same settlement cannot take over another caravan's session.
        private static string TargetKey(RimGovernor.Brain.GameFacts.Common.TradeTarget? target)
        {
            switch (target?.Kind)
            {
                case RimGovernor.Brain.GameFacts.Common.TradeTargetKind.MapTraderCase { Value: var map }: return map.TraderId ?? "";
                case RimGovernor.Brain.GameFacts.Common.TradeTargetKind.OrbitalShipCase { Value: var ship }: return string.IsNullOrEmpty(ship.ShipId) ? "" : "orbital/" + ship.ShipId;
                case RimGovernor.Brain.GameFacts.Common.TradeTargetKind.SettlementCase { Value: var settlement }: return string.IsNullOrEmpty(settlement.SettlementId) || string.IsNullOrEmpty(settlement.CaravanId) ? "" : "settlement/" + settlement.SettlementId + "/" + settlement.CaravanId;
                default: return "";
            }
        }
        private static string SessionTargetKey() => _sessionTarget == null ? "" : TargetKey(_sessionTarget);
        private static Caravan? ResolveCaravan(string? id) => Find.WorldObjects.Caravans.FirstOrDefault(c => c.GetUniqueLoadID() == id && c.Faction == Faction.OfPlayer);
        private static Settlement? ResolveSettlement(string? id) => Find.WorldObjects.Settlements.FirstOrDefault(s => s.GetUniqueLoadID() == id);

        private static bool SessionParticipantsLive()
        {
            var trader = _sessionTrader; var pawn = _sessionNegotiator;
            if (trader == null || pawn == null || _sessionTarget == null || !trader.CanTradeNow || !NativeTradeObservation.EligibleNegotiator(pawn)
                || !pawn.CanTradeWith(trader.Faction, trader.TraderKind).Accepted) return false;
            switch (_sessionTarget.Kind)
            {
                case RimGovernor.Brain.GameFacts.Common.TradeTargetKind.MapTraderCase:
                    return trader is Pawn seller && seller.Spawned && seller.Map == _sessionMap && !SafeBool(() => seller.mindState.traderDismissed)
                        && pawn.Spawned && pawn.Map == _sessionMap;
                case RimGovernor.Brain.GameFacts.Common.TradeTargetKind.OrbitalShipCase:
                    return trader is TradeShip ship && pawn.Spawned && pawn.Map == _sessionMap && _sessionMap.passingShipManager.passingShips.Contains(ship);
                case RimGovernor.Brain.GameFacts.Common.TradeTargetKind.SettlementCase { Value: var visit }:
                    var caravan = ResolveCaravan(visit.CaravanId);
                    return caravan != null && ReferenceEquals(ResolveSettlement(visit.SettlementId), trader)
                        && caravan.PawnsListForReading.Contains(pawn) && ReferenceEquals(pawn.GetCaravan(), caravan)
                        && ReferenceEquals(CaravanVisitUtility.SettlementVisitedNow(caravan), trader);
                default: return false;
            }
        }
        // Vanilla memoizes a negotiator's price inside each Tradeable. Rebuild
        // that memo before preview/signature checks so an old session cannot
        // accept a price quoted before the negotiator's trade stats changed.
        private static void RefreshPrices(TradeDeal deal)
        {
            var price = AccessTools.Field(typeof(Tradeable), "pricePlayerBuy");
            if (price == null) throw new InvalidOperationException("Native price field is unavailable.");
            foreach (var row in deal.AllTradeables) price.SetValue(row, -1f);
        }

        private static bool CaravanCargoFits(TradeDeal deal)
        {
            if (_sessionTarget?.Kind is not RimGovernor.Brain.GameFacts.Common.TradeTargetKind.SettlementCase { Value: var visit }) return true;
            var caravan = ResolveCaravan(visit.CaravanId);
            if (caravan == null) return false;
            var things = new List<Thing>(caravan.PawnsListForReading);
            things.AddRange(CaravanInventoryUtility.AllInventoryItems(caravan));
            var usage = CollectionsMassCalculator.MassUsageLeftAfterTradeableTransfer(things, deal.AllTradeables, IgnorePawnsInventoryMode.Ignore, includePawnsMass: caravan.Shuttle != null);
            var capacity = caravan.Shuttle != null ? caravan.Shuttle.TransporterComp.MassCapacity : CollectionsMassCalculator.CapacityLeftAfterTradeableTransfer(things, deal.AllTradeables, new StringBuilder());
            if (caravan.Shuttle != null) usage -= caravan.Shuttle.GetStatValue(StatDefOf.Mass);
            return !float.IsNaN(usage) && !float.IsInfinity(usage) && usage <= capacity;
        }

        private sealed class OrbitalContact
        {
            internal readonly RimGovernor.Brain.GameFacts.Common.TradeTarget Target;
            internal readonly RimGovernor.Brain.GameFacts.Common.Identity Identity;
            internal readonly TradeShip Ship;
            internal readonly Building_CommsConsole Console;
            internal readonly Pawn Pawn;
            internal readonly bool Gift;
            internal Job? Job;
            internal int JobId;
            internal OrbitalContact(RimGovernor.Brain.GameOrders.Operations.TradeIntent intent, RimGovernor.Brain.GameFacts.Common.Identity identity, TradeShip ship, Building_CommsConsole console, Pawn pawn)
            { Target = intent.Target ?? new RimGovernor.Brain.GameFacts.Common.TradeTarget(); Identity = identity; Ship = ship; Console = console; Pawn = pawn; Gift = intent.Step?.Open?.GiftMode == true; }
            internal bool Owns(Job? job) => job != null && ReferenceEquals(job, Job) && job.loadID == JobId && job.def == JobDefOf.UseCommsConsole && ReferenceEquals(job.commTarget, Ship) && ReferenceEquals(job.targetA.Thing, Console);
            internal bool Live() => Owns(Pawn.CurJob) || Pawn.jobs.jobQueue.Any(q => Owns(q.job));
        }

        private static bool PrepareWorldOpen(RimGovernor.Brain.GameOrders.Operations.TradeIntent intent, RimGovernor.Brain.GameFacts.Common.Identity identity, out ITrader? seller, out Pawn? pawn, out Building_CommsConsole? console, out RimGovernor.Brain.GameFacts.Common.Failure failure)
        {
            seller = null; pawn = null; console = null;
            failure = GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "World trade target or negotiator is unavailable.");
            DropForeignSession(identity);
            if (_sessionId != null)
            {
                if (!RequireParticipants(TargetKey(intent.Target), (intent.NegotiatorId ?? ""), identity, out failure)) return false;
                if (_giftMode != (intent.Step?.Open?.GiftMode == true)) { failure = GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.OwnerConflict, "Gift mode differs."); return false; }
                seller = _sessionTrader; pawn = _sessionNegotiator; return true;
            }
            if (_walk != null && _walk.Live()) { failure = GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.OwnerConflict, "Another trade walk is live."); return false; }
            if (_contact != null && _contact.Live())
            {
                if (TargetKey(_contact.Target) != TargetKey(intent.Target) || _contact.Pawn.GetUniqueLoadID() != (intent.NegotiatorId ?? "") || _contact.Gift != (intent.Step?.Open?.GiftMode == true)
                    || (_contact.Identity.ColonyId ?? "") != (identity.ColonyId ?? "") || (_contact.Identity.LoadToken ?? "") != (identity.LoadToken ?? ""))
                { failure = GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.OwnerConflict, "Another comms contact is live."); return false; }
                seller = _contact.Ship; pawn = _contact.Pawn; console = _contact.Console; return true;
            }
            if (TradeSession.Active || OpenTradeDialog() != null) { failure = GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.OwnerConflict, "Another trade session is open."); return false; }
            var map = GameThread.ResolveMap(identity);
            if (map == null) return false;
            if (intent.Target?.Kind is RimGovernor.Brain.GameFacts.Common.TradeTargetKind.SettlementCase { Value: var visit })
            {
                var caravan = ResolveCaravan(visit.CaravanId);
                var settlement = ResolveSettlement(visit.SettlementId);
                pawn = caravan?.PawnsListForReading.FirstOrDefault(p => p.GetUniqueLoadID() == (intent.NegotiatorId ?? "") && p.Faction == Faction.OfPlayer);
                seller = settlement;
                return caravan != null && settlement != null && settlement.CanTradeNow && ReferenceEquals(CaravanVisitUtility.SettlementVisitedNow(caravan), settlement)
                    && pawn != null && ReferenceEquals(pawn.GetCaravan(), caravan) && NativeTradeObservation.EligibleNegotiator(pawn) && pawn.CanTradeWith(settlement.Faction, settlement.TraderKind).Accepted;
            }
            var ship = map.passingShipManager.passingShips.OfType<TradeShip>().FirstOrDefault(s => s.GetUniqueLoadID() == intent.Target?.Kind?.OrbitalShip?.ShipId && s.CanTradeNow);
            pawn = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => p.GetUniqueLoadID() == (intent.NegotiatorId ?? ""));
            seller = ship;
            if (ship == null || pawn == null || !pawn.CanTradeWith(ship.Faction, ship.TraderKind).Accepted || !Building_OrbitalTradeBeacon.AllPowered(map).Any()) return false;
            var negotiator = pawn;
            console = map.listerBuildings.allBuildingsColonist.OfType<Building_CommsConsole>().OrderBy(c => c.GetUniqueLoadID()).FirstOrDefault(c => NativeTradeAcquisition.ConsoleNegotiator(c, negotiator));
            return console != null && InstallContactHook();
        }

        private static bool InstallContactHook()
        {
            if (_contactHook) return true;
            try
            {
                new Harmony(ArrivalHookOwner).Patch(AccessTools.Method(typeof(TradeShip), nameof(TradeShip.TryOpenComms)), prefix: new HarmonyMethod(AccessTools.Method(typeof(NativeTradeOperations), nameof(ContactShip))));
                _contactHook = true; return true;
            }
            catch { return false; }
        }
        // Vanilla UseCommsConsole reaches its contact toil and calls the ship.
        // Only our matching job substitutes a headless shared session.
        private static bool ContactShip(TradeShip __instance, Pawn negotiator)
        {
            var contact = _contact;
            if (contact == null || !ReferenceEquals(contact.Ship, __instance) || !ReferenceEquals(contact.Pawn, negotiator) || !contact.Owns(negotiator.CurJob)) return true;
            _contact = null;
            if (_sessionId != null || TradeSession.Active || OpenTradeDialog() != null || !__instance.CanTradeNow || !contact.Console.CanUseCommsNow
                || !GameThread.IsLoaded(contact.Console.Map) || !NativeTradeObservation.EligibleNegotiator(negotiator) || !negotiator.CanTradeWith(__instance.Faction, __instance.TraderKind).Accepted
                || !contact.Console.Map.passingShipManager.passingShips.Contains(__instance)
                || !GameThread.ValidateIdentity(contact.Identity, out _, out var ignoredFailure)) return false;
            BindWorldSession(__instance, negotiator, contact.Gift, contact.Identity, contact.Console.Map, contact.Target);
            return false;
        }
        private static void BindWorldSession(ITrader seller, Pawn pawn, bool gift, RimGovernor.Brain.GameFacts.Common.Identity identity, Map map, RimGovernor.Brain.GameFacts.Common.TradeTarget target)
        {
            TradeSession.SetupWith(seller, pawn, gift);
            if (!TradeSession.Active || TradeSession.deal == null) throw new InvalidOperationException("Trade setup failed.");
            _sessionId = Guid.NewGuid().ToString("N"); _sessionColonyId = identity.ColonyId ?? ""; _sessionLoadToken = identity.LoadToken ?? "";
            _sessionMap = map; _sessionDeal = TradeSession.deal; _sessionTrader = seller; _sessionNegotiator = pawn; _giftMode = gift; _sessionTarget = target;
        }
        private static RimGovernor.Brain.GameFacts.Receipts.EffectEvidence ApplyWorldOpen(RimGovernor.Brain.GameOrders.Operations.TradeIntent intent, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var contextIdentity = context.Identity ?? throw new InvalidOperationException("Observation context has no identity.");
            if (!PrepareWorldOpen(intent, contextIdentity, out var seller, out var pawn, out var console, out _) || seller == null || pawn == null) throw new InvalidOperationException("World trade prerequisites changed.");
            if (_sessionId == null && intent.Target?.Kind is RimGovernor.Brain.GameFacts.Common.TradeTargetKind.SettlementCase)
                BindWorldSession(seller, pawn, intent.Step?.Open?.GiftMode == true, contextIdentity, (GameThread.ResolveMap(context) ?? throw new System.InvalidOperationException("Required native value is unavailable: GameThread.ResolveMap(context).")), intent.Target);
            else if (_sessionId == null && !(_contact?.Live() ?? false))
            {
                var contact = new OrbitalContact(intent, contextIdentity, (TradeShip)seller, (console ?? throw new System.InvalidOperationException("Required native value is unavailable: console.")), pawn);
                var job = JobMaker.MakeJob(JobDefOf.UseCommsConsole, console);
                job.commTarget = (TradeShip)seller; contact.Job = job; contact.JobId = job.loadID;
                _contact = contact;
                if (!pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc)) { _contact = null; throw new InvalidOperationException("Comms job refused."); }
            }
            RimGovernor.Brain.GameFacts.Receipts.TradeEffect effect = _sessionId is { } sessionId
                ? new RimGovernor.Brain.GameFacts.Receipts.TradeEffect.OpenCase(sessionId, seller.Faction?.GetUniqueLoadID() ?? throw new InvalidOperationException("A world trader has no faction."))
                : new RimGovernor.Brain.GameFacts.Receipts.TradeEffect.ApproachingCase();
            return new RimGovernor.Brain.GameFacts.Receipts.EffectEvidence { Effect = effect };
        }

        private static bool CancelApproach(string key, string negotiator)
        {
            var contact = _contact;
            if (contact != null && TargetKey(contact.Target) == key && contact.Pawn.GetUniqueLoadID() == negotiator)
            {
                contact.Pawn.jobs.jobQueue.RemoveAll(contact.Pawn, j => contact.Owns(j));
                if (contact.Owns(contact.Pawn.CurJob)) contact.Pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);
                _contact = null; return true;
            }
            var walk = _walk;
            if (walk != null && walk.Trader.GetUniqueLoadID() == key && walk.Negotiator.GetUniqueLoadID() == negotiator)
            {
                walk.Negotiator.jobs.jobQueue.RemoveAll(walk.Negotiator, j => walk.Owns(j));
                if (walk.Owns(walk.Negotiator.CurJob)) walk.Negotiator.jobs.EndCurrentJob(JobCondition.InterruptForced);
                _walk = null; return true;
            }
            return false;
        }

        internal static bool LiveTarget(RimGovernor.Brain.GameFacts.Common.Identity identity, out RimGovernor.Brain.GameFacts.Common.TradeTarget? target, out Pawn? pawn, out bool open)
        {
            target = null; pawn = null; open = false;
            if (_sessionId != null && RequireSession(identity, out _)) { target = _sessionTarget; pawn = _sessionNegotiator; open = true; return true; }
            if (_contact != null && (_contact.Identity.ColonyId ?? "") == (identity.ColonyId ?? "") && (_contact.Identity.LoadToken ?? "") == (identity.LoadToken ?? "") && _contact.Live() && _contact.Ship.CanTradeNow)
            { target = _contact.Target; pawn = _contact.Pawn; return true; }
            if (Live(identity, out var seller, out pawn, out open) && seller != null && !open)
            { target = new RimGovernor.Brain.GameFacts.Common.TradeTarget { Kind = new RimGovernor.Brain.GameFacts.Common.MapTradeTarget { TraderId = seller.GetUniqueLoadID() } }; return true; }
            return false;
        }
    }
}
