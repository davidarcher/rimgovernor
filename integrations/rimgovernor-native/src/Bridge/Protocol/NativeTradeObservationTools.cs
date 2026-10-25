using System;
using System.Collections.Generic;
using System.Linq;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Verse.AI.Group;
using Verse.AI;
using PawnId = RimGovernor.Brain.Domain.PawnId;

namespace RimGovernor.Native
{
    // Typed, read-only trade census behind the ReadScope boundary: the map
    // traders a session could open with, the eligible negotiators, and
    // the currently open session's whole sheet. Every read recomputes from
    // current native state; nothing here stages or closes a session or
    // issues an order. A read that cannot be completed throws.
    internal static class NativeTradeObservation
    {
        private static bool SafeBool(Func<bool> f) { try { return f(); } catch { return false; } }

        // Mirrors NativeTradeOperations.PrepareOpen's negotiator eligibility
        // exactly, so a negotiator listed here is one an open would accept
        // (adjacency and reachability aside, which depend on the trader).
        internal static bool EligibleNegotiator(Pawn p) =>
            !SafeBool(() => p.Dead) && !SafeBool(() => p.Downed) && !SafeBool(() => p.InMentalState) && !SafeBool(() => p.WorkTagIsDisabled(WorkTags.Social));

        private static string FactionId(Faction? faction) => faction?.GetUniqueLoadID() ?? "";

        internal static RimGovernor.Brain.GameFacts.Observations.TradersSnapshot Traders(Map map, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var traders = new List<RimGovernor.Brain.GameFacts.Observations.Trader>();
            var negotiators = map.mapPawns.FreeColonistsSpawned.Where(EligibleNegotiator)
                .OrderByDescending(p => p.GetStatValue(StatDefOf.TradePriceImprovement)).ThenBy(p => p.GetUniqueLoadID(), StringComparer.Ordinal).ToList();
            RimGovernor.Brain.GameFacts.Observations.TradeVisit? Visit(ITrader trader, IntVec3 location, Pawn? negotiator, int availableTicks)
            {
                if (availableTicks <= 0 || negotiator is null || trader.TraderKind.orbital && !Building_OrbitalTradeBeacon.AllPowered(map).Any()) return null;
                var gain = negotiator.GetStatValue(StatDefOf.TradePriceImprovement);
                var stock = trader.Goods.Where(t => t is not Pawn && t.stackCount > 0 && t.def != ThingDefOf.Silver)
                    .Select(t => new RimGovernor.Brain.GameFacts.Observations.SettlementStockItem(t.def.defName, t.stackCount,
                        TradeUtility.GetPricePlayerBuy(t, trader.TraderKind.PriceTypeFor(t.def, TradeAction.PlayerBuys).PriceMultiplier(), 1, gain, trader.TradePriceImprovementOffsetForPlayer)));
                return new(new PawnId(negotiator.GetUniqueLoadID()), new(location.x, location.z), availableTicks, RimGovernor.Brain.Domain.EquatableArray.From(stock))
                { Silver = NativeTradeSilver.ForTrader(trader, negotiator) };
            }
            foreach (var pawn in map.mapPawns.AllPawnsSpawned.ToList())
            {
                var tracker = pawn.trader;
                if (tracker == null || tracker.traderKind == null) continue;
                var dismissed = pawn.mindState != null && pawn.mindState.traderDismissed;
                // A caravan still walking to its trade spot is not one the
                // routine's escort can stand beside: its trader moves every
                // tick until the lord's travel toil ends.
                var travelling = pawn.GetLord()?.CurLordToil is LordToil_Travel;
                traders.Add(new RimGovernor.Brain.GameFacts.Observations.Trader
                {
                    Id = pawn.GetUniqueLoadID(), Orbital = false, Kind = tracker.traderKind.defName, FactionId = FactionId(pawn.Faction),
                    CanTrade = pawn.CanTradeNow && !dismissed && !travelling, Travelling = travelling, GoodsStacks = tracker.Goods.Count(),
                    Visit = !dismissed && !travelling && pawn.CanTradeNow ? Visit(pawn, pawn.Position,
                        negotiators.FirstOrDefault(p => p.CanTradeWith(pawn.Faction, tracker.traderKind).Accepted && p.CanReach(pawn, PathEndMode.Touch, Danger.Some)), DepartureTicks(pawn)) : null,
                });
            }
            foreach (var ship in map.passingShipManager.passingShips.OfType<TradeShip>())
            {
                var usable = ship.CanTradeNow && Building_OrbitalTradeBeacon.AllPowered(map).Any()
                    && map.mapPawns.FreeColonistsSpawned.Any(p => p.CanTradeWith(ship.Faction, ship.TraderKind).Accepted
                        && map.listerBuildings.allBuildingsColonist.OfType<Building_CommsConsole>().Any(c => NativeTradeAcquisition.ConsoleNegotiator(c, p)));
                var console = map.listerBuildings.allBuildingsColonist.OfType<Building_CommsConsole>()
                    .OrderBy(c => c.GetUniqueLoadID(), StringComparer.Ordinal).FirstOrDefault(c => negotiators.Any(p => p.CanTradeWith(ship.Faction, ship.TraderKind).Accepted && NativeTradeAcquisition.ConsoleNegotiator(c, p)));
                traders.Add(new RimGovernor.Brain.GameFacts.Observations.Trader
                {
                    Id = ship.GetUniqueLoadID(), Orbital = true, Kind = ship.TraderKind.defName, FactionId = FactionId(ship.Faction),
                    CanTrade = usable, Travelling = false, GoodsStacks = ship.Goods.Count(),
                    Visit = usable && console is not null ? Visit(ship, console.InteractionCell,
                        negotiators.FirstOrDefault(p => p.CanTradeWith(ship.Faction, ship.TraderKind).Accepted && NativeTradeAcquisition.ConsoleNegotiator(console, p)), ship.ticksUntilDeparture) : null,
                });
            }
            traders.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            return new RimGovernor.Brain.GameFacts.Observations.TradersSnapshot
            {
                Context = context, Traders = [.. traders], Negotiators = [.. negotiators.Select(p => new PawnId(p.GetUniqueLoadID()))],
            };
        }

        // Vanilla resets the sampled timer when its stationary source toil becomes active.
        // Reading the live trigger therefore includes both elapsed time and any such extension.
        private static int DepartureTicks(Pawn trader)
        {
            var lord = trader.GetLord() ?? throw new InvalidOperationException("Required trader lord is unavailable.");
            if (lord.CurLordToil is LordToil_ExitMap || lord.CurLordToil is LordToil_ExitMapAndEscortCarriers
                || lord.LordJob is LordJob_VisitColony visit && visit.exitSubgraph.lordToils.Contains(lord.CurLordToil)) return 0;
            if (lord.LordJob is not LordJob_TradeWithColony && lord.LordJob is not LordJob_VisitColony)
                throw new InvalidOperationException("Required trader departure timer is unavailable for " + lord.LordJob.GetType().Name);
            var timers = lord.Graph.transitions.Where(t => t.sources.Contains(lord.CurLordToil))
                .SelectMany(t => t.triggers).OfType<Trigger_TicksPassed>().ToArray();
            // Combat and taking wounded guests suspend the stationary timer. Neither
            // gives a dated opportunity; a later stationary census can plan it again.
            if (timers.Length == 0) return 0;
            if (timers.Length != 1) throw new InvalidOperationException("Required active trader departure timer is unavailable.");
            return timers[0].TicksLeft;
        }

        /// <summary>
        /// A pawn line's pawn: what a purchase ranks (skills, passions,
        /// violence capability) and a prisoner's sale facts, PreTraded's
        /// MemberSold goodwill with the extra home or host faction and what
        /// AllSellableColonyPawns lists on.
        /// </summary>
        private static RimGovernor.Brain.GameFacts.Observations.TradePawn TradePawn(Pawn p) => new RimGovernor.Brain.GameFacts.Observations.TradePawn
        {
            Id = p.GetUniqueLoadID(),
            Gender = (RimGovernor.Brain.GameFacts.Defs.Gender)(int)p.gender,
            Skills = p.skills == null ? [] : [.. p.skills.skills.Select(s => new RimGovernor.Brain.GameFacts.Observations.Skill { DefName = s.def.defName, Level = s.Level, StoredLevel = s.levelInt, Passion = NativeEnums.Passion(s.passion), Disabled = s.TotallyDisabled })],
            ViolenceCapable = !p.WorkTagIsDisabled(WorkTags.Violent),
            Guest = p.guest != null
                ? new RimGovernor.Brain.GameFacts.Observations.TradePawnGuest.Tracked((RimGovernor.Brain.GameFacts.Defs.GuestStatus)(int)p.guest.GuestStatus, p.guest.PrisonerIsSecure)
                : new RimGovernor.Brain.GameFacts.Observations.TradePawnGuest.NoGuestTracker(),
            Downed = p.Downed,
            ExtraHomeFactionId = FactionId(p.GetExtraHomeFaction()),
            ExtraHostFactionId = FactionId(p.GetExtraHostFaction()),
        };

        // A line thing's quality and hit-point fraction, what a gear sale ranks.
        private static RimGovernor.Brain.GameFacts.Observations.TradeThingGrade Grade(Thing thing)
        {
            if (!thing.GetInnerIfMinified().TryGetQuality(out var quality) || !thing.def.useHitPoints || thing.MaxHitPoints <= 0)
                return new RimGovernor.Brain.GameFacts.Observations.TradeThingGrade.Ungraded();
            return new RimGovernor.Brain.GameFacts.Observations.TradeThingGrade.Graded((int)quality, (double)thing.HitPoints / thing.MaxHitPoints);
        }

        private static RimGovernor.Brain.GameFacts.Observations.TradeLine Line(Tradeable t, int index)
        {
            var pawn = t.AnyThing as Pawn;
            // One colony thing of a non-pawn line, its stockpile and grade: a
            // packed sculpture sells line by line, matched to its stock id, and
            // the controller sells only the worn dump's gear above its caps.
            var thing = pawn == null ? t.FirstThingColony : null;
            var zone = thing != null && thing.Spawned ? thing.Map.zoneManager.ZoneAt(thing.Position) as Zone_Stockpile : null;
            return new RimGovernor.Brain.GameFacts.Observations.TradeLine
            {
                LineId = "#" + index,
                DefName = t.ThingDef?.defName ?? "",
                Stuff = t.StuffDef?.defName ?? "",
                ColonyCount = t.CountHeldBy(Transactor.Colony), TraderCount = t.CountHeldBy(Transactor.Trader),
                // GetPriceFor memoises price factors inside the Tradeable: the
                // session's own scratch state, the same write the vanilla dialog
                // makes every frame it draws.
                BuyPrice = t.GetPriceFor(TradeAction.PlayerBuys), SellPrice = t.GetPriceFor(TradeAction.PlayerSells),
                TraderWillTrade = t.TraderWillTrade, Currency = t.IsCurrency,
                Pawn = pawn != null ? TradePawn(pawn) : null,
                TransferCount = t.CountToTransfer, MinimumCount = t.GetMinimumToTransfer(), MaximumCount = t.GetMaximumToTransfer(),
                ThingId = thing?.GetUniqueLoadID() ?? "",
                Grade = thing != null ? Grade(thing) : new RimGovernor.Brain.GameFacts.Observations.TradeThingGrade.Ungraded(),
                ZoneId = zone?.GetUniqueLoadID() ?? "",
            };
        }

        internal static RimGovernor.Brain.GameFacts.Observations.TradeSession Session(RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var engaged = NativeTradeOperations.LiveTarget(context.Identity, out var target, out var negotiator, out var open) && target != null && negotiator != null;
            return new RimGovernor.Brain.GameFacts.Observations.TradeSession
            {
                Context = context,
                Engagement = engaged
                    ? new RimGovernor.Brain.GameFacts.Observations.TradeEngagement.Engaged((target ?? throw new System.InvalidOperationException("Required native value is unavailable: target.")), new PawnId((negotiator ?? throw new System.InvalidOperationException("Required native value is unavailable: negotiator.")).GetUniqueLoadID()), open)
                    : new RimGovernor.Brain.GameFacts.Observations.TradeEngagement.Idle(),
            };
        }

        internal static bool Sheet(RimGovernor.Brain.GameFacts.Common.ObservationContext context, string? sessionId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out RimGovernor.Brain.GameFacts.Observations.TradeSheet? sheet, out RimGovernor.Brain.GameFacts.Common.Failure failure)
        {
            sheet = null;
            if (!NativeTradeOperations.SessionSheet(context.Identity, out var session, out failure)) return false;
            if (!string.IsNullOrEmpty(sessionId) && sessionId != session.SessionId)
            {
                failure = GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.StaleIdentity, "The requested trade session is not the open one.");
                return false;
            }
            var deal = session.Deal;
            var all = deal.AllTradeables.ToList();
            var currency = deal.CurrencyTradeable;
            var lines = new List<RimGovernor.Brain.GameFacts.Observations.TradeLine>();
            for (var i = 0; i < all.Count; i++) if (all[i] != null) lines.Add(Line(all[i], i));
            sheet = new RimGovernor.Brain.GameFacts.Observations.TradeSheet
            {
                Context = context,
                SessionId = session.SessionId,
                Target = session.Target,
                Negotiator = new PawnId(session.Negotiator.GetUniqueLoadID()),
                GiftMode = session.GiftMode,
                CanTradeNow = session.Trader.CanTradeNow,
                // Negative when the colony pays, exactly the currency row's
                // own signed transfer count.
                Balance = currency != null
                    ? new RimGovernor.Brain.GameFacts.Observations.TradeBalance.Counted(currency.CountToTransfer, currency.CountPostDealFor(Transactor.Colony) >= 0)
                    : new RimGovernor.Brain.GameFacts.Observations.TradeBalance.NoCurrencyLine(),
                TraderHasEnoughSilver = deal.DoesTraderHaveEnoughSilver(),
                Lines = [.. lines],
                DealSignature = session.DealSignature,
                CurrencyKind = NativeTradeOperations.IsFavorSession() ? RimGovernor.Brain.GameFacts.Defs.TradeCurrency.Favor : RimGovernor.Brain.GameFacts.Defs.TradeCurrency.Silver,
            };
            return true;
        }
    }

    internal static class NativeTradeObservationTools
    {
        private static void RequireIdentity([System.Diagnostics.CodeAnalysis.NotNull] RimGovernor.Brain.GameFacts.Observations.ReadScope? scope)
        {
            if (scope?.ExpectedIdentity == null) throw NativeFaults.Refused(GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Identity is required."));
        }

        // The read's result, or the logged ReadFailed unavailability a read that could not complete throws.
        private static T Complete<T>(string what, Func<T> read)
        {
            try { return read(); }
            catch (Exception thrown) when (thrown is not NativeFailureException)
            {
                ModLog.Error("observe", "Native " + what + " read failed: " + thrown);
                throw NativeFaults.Unavailable(new RimGovernor.Brain.GameFacts.Common.Unavailable { Reason = RimGovernor.Brain.GameFacts.Common.UnavailableReason.ReadFailed, Detail = "Native " + what + " could not be read completely." });
            }
        }

        internal static RimGovernor.Brain.GameFacts.Observations.TradersSnapshot HandleListTraders(RimGovernor.Brain.GameOrders.Observations.TradersRequest request)
        {
            RequireIdentity(request.Scope);
            if (!GameThread.ValidateIdentity(request.Scope.ExpectedIdentity, out var map, out var context, out var failure)) throw NativeFaults.Refused(failure);
            return Complete("trader census", () => NativeTradeObservation.Traders(map, context));
        }

        internal static RimGovernor.Brain.GameFacts.Observations.TradeSession HandleReadTradeSession(RimGovernor.Brain.GameOrders.Observations.TradeSessionRequest request)
        {
            RequireIdentity(request.Scope);
            if (!GameThread.ValidateIdentity(request.Scope.ExpectedIdentity, out var context, out var failure)) throw NativeFaults.Refused(failure);
            return Complete("trade session", () => NativeTradeObservation.Session(context));
        }

        internal static RimGovernor.Brain.GameFacts.Observations.TradeSheet HandleReadTradeSheet(RimGovernor.Brain.GameOrders.Observations.TradeSheetRequest request)
        {
            RequireIdentity(request.Scope);
            if (!GameThread.ValidateIdentity(request.Scope.ExpectedIdentity, out var context, out var failure)) throw NativeFaults.Refused(failure);
            return Complete("trade sheet", () => NativeTradeObservation.Sheet(context, request.SessionId, out var sheet, out var refused)
                ? sheet
                : throw NativeFaults.Refused(refused));
        }
    }
}
