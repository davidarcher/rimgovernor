using System;
using System.Collections.Generic;
using System.Linq;
using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Verse.AI.Group;
using Common = RimGovernor.Contracts.Wire.Common;
using Defs = RimGovernor.Contracts.Wire.Defs;
using Obs = RimGovernor.Contracts.Wire.Observations;
using PawnId = RimGovernor.Brain.Domain.PawnId;

namespace HomeBridge.BridgeTools
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

        internal static Obs.TradersSnapshot Traders(Map map, Common.ObservationContext context)
        {
            var traders = new List<Obs.Trader>();
            foreach (var pawn in map.mapPawns.AllPawnsSpawned.ToList())
            {
                var tracker = pawn.trader;
                if (tracker == null || tracker.traderKind == null) continue;
                var dismissed = pawn.mindState != null && pawn.mindState.traderDismissed;
                // A caravan still walking to its trade spot is not one the
                // routine's escort can stand beside: its trader moves every
                // tick until the lord's travel toil ends.
                var travelling = pawn.GetLord()?.CurLordToil is LordToil_Travel;
                traders.Add(new Obs.Trader
                {
                    Id = pawn.GetUniqueLoadID(), Orbital = false, Kind = tracker.traderKind.defName, FactionId = FactionId(pawn.Faction),
                    CanTrade = pawn.CanTradeNow && !dismissed && !travelling, Travelling = travelling, GoodsStacks = tracker.Goods.Count(),
                });
            }
            foreach (var ship in map.passingShipManager.passingShips.OfType<TradeShip>())
            {
                var usable = ship.CanTradeNow && Building_OrbitalTradeBeacon.AllPowered(map).Any()
                    && map.mapPawns.FreeColonistsSpawned.Any(p => p.CanTradeWith(ship.Faction, ship.TraderKind).Accepted
                        && map.listerBuildings.allBuildingsColonist.OfType<Building_CommsConsole>().Any(c => NativeTradeAcquisition.ConsoleNegotiator(c, p)));
                traders.Add(new Obs.Trader
                {
                    Id = ship.GetUniqueLoadID(), Orbital = true, Kind = ship.TraderKind.defName, FactionId = FactionId(ship.Faction),
                    CanTrade = usable, Travelling = false, GoodsStacks = ship.Goods.Count(),
                });
            }
            traders.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            var negotiators = map.mapPawns.FreeColonistsSpawned.Where(EligibleNegotiator).ToList();
            negotiators.Sort((x, y) =>
            {
                var c = y.GetStatValue(StatDefOf.TradePriceImprovement).CompareTo(x.GetStatValue(StatDefOf.TradePriceImprovement));
                return c != 0 ? c : string.CompareOrdinal(x.GetUniqueLoadID(), y.GetUniqueLoadID());
            });
            return new Obs.TradersSnapshot
            {
                Context = context, Traders = [.. traders], Negotiators = [.. negotiators.Select(p => new PawnId(p.GetUniqueLoadID()))],
            };
        }

        /// <summary>
        /// A pawn line's pawn: what a purchase ranks (skills, passions,
        /// violence capability) and a prisoner's sale facts, PreTraded's
        /// MemberSold goodwill with the extra home or host faction and what
        /// AllSellableColonyPawns lists on.
        /// </summary>
        private static Obs.TradePawn TradePawn(Pawn p) => new Obs.TradePawn
        {
            Id = p.GetUniqueLoadID(),
            Gender = (Defs.Gender)(int)p.gender,
            Skills = p.skills == null ? [] : [.. p.skills.skills.Select(s => new Obs.Skill { DefName = s.def.defName, Level = s.Level, StoredLevel = s.levelInt, Passion = NativeEnums.Passion(s.passion), Disabled = s.TotallyDisabled })],
            ViolenceCapable = !p.WorkTagIsDisabled(WorkTags.Violent),
            Guest = p.guest != null
                ? new Obs.TradePawnGuest.Tracked((Defs.GuestStatus)(int)p.guest.GuestStatus, p.guest.PrisonerIsSecure)
                : new Obs.TradePawnGuest.NoGuestTracker(),
            Downed = p.Downed,
            ExtraHomeFactionId = FactionId(p.GetExtraHomeFaction()),
            ExtraHostFactionId = FactionId(p.GetExtraHostFaction()),
        };

        // A line thing's quality and hit-point fraction, what a gear sale ranks.
        private static Obs.TradeThingGrade Grade(Thing thing)
        {
            if (!thing.GetInnerIfMinified().TryGetQuality(out var quality) || !thing.def.useHitPoints || thing.MaxHitPoints <= 0)
                return new Obs.TradeThingGrade.Ungraded();
            return new Obs.TradeThingGrade.Graded((int)quality, (double)thing.HitPoints / thing.MaxHitPoints);
        }

        private static Obs.TradeLine Line(Tradeable t, int index)
        {
            var pawn = t.AnyThing as Pawn;
            // One colony thing of a non-pawn line, its stockpile and grade: a
            // packed sculpture sells line by line, matched to its stock id, and
            // the controller sells only the worn dump's gear above its caps.
            var thing = pawn == null ? t.FirstThingColony : null;
            var zone = thing != null && thing.Spawned ? thing.Map.zoneManager.ZoneAt(thing.Position) as Zone_Stockpile : null;
            return new Obs.TradeLine
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
                Grade = thing != null ? Grade(thing) : new Obs.TradeThingGrade.Ungraded(),
                ZoneId = zone?.GetUniqueLoadID() ?? "",
            };
        }

        internal static Obs.TradeSession Session(Common.ObservationContext context)
        {
            var engaged = NativeTradeOperations.LiveTarget(context.Identity, out var target, out var negotiator, out var open) && target != null && negotiator != null;
            return new Obs.TradeSession
            {
                Context = context,
                Engagement = engaged
                    ? new Obs.TradeEngagement.Engaged((target ?? throw new System.InvalidOperationException("Required native value is unavailable: target.")), new PawnId((negotiator ?? throw new System.InvalidOperationException("Required native value is unavailable: negotiator.")).GetUniqueLoadID()), open)
                    : new Obs.TradeEngagement.Idle(),
            };
        }

        internal static bool Sheet(Common.ObservationContext context, string? sessionId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Obs.TradeSheet? sheet, out Common.Failure failure)
        {
            sheet = null;
            if (!NativeTradeOperations.SessionSheet(context.Identity, out var session, out failure)) return false;
            if (!string.IsNullOrEmpty(sessionId) && sessionId != session.SessionId)
            {
                failure = ProtoBoundary.Fail(Common.FailureCode.StaleIdentity, "The requested trade session is not the open one.");
                return false;
            }
            var deal = session.Deal;
            var all = deal.AllTradeables.ToList();
            var currency = deal.CurrencyTradeable;
            var lines = new List<Obs.TradeLine>();
            for (var i = 0; i < all.Count; i++) if (all[i] != null) lines.Add(Line(all[i], i));
            sheet = new Obs.TradeSheet
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
                    ? new Obs.TradeBalance.Counted(currency.CountToTransfer, currency.CountPostDealFor(Transactor.Colony) >= 0)
                    : new Obs.TradeBalance.NoCurrencyLine(),
                TraderHasEnoughSilver = deal.DoesTraderHaveEnoughSilver(),
                Lines = [.. lines],
                DealSignature = session.DealSignature,
                CurrencyKind = NativeTradeOperations.IsFavorSession() ? Defs.TradeCurrency.Favor : Defs.TradeCurrency.Silver,
            };
            return true;
        }
    }

    internal static class NativeTradeObservationTools
    {
        private static void RequireIdentity([System.Diagnostics.CodeAnalysis.NotNull] Obs.ReadScope? scope)
        {
            if (scope?.ExpectedIdentity == null) throw NativeFaults.Refused(ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Identity is required."));
        }

        // The read's result, or the logged ReadFailed unavailability a read that could not complete throws.
        private static T Complete<T>(string what, Func<T> read)
        {
            try { return read(); }
            catch (Exception thrown) when (thrown is not NativeFailureException)
            {
                ModLog.Error("observe", "Native " + what + " read failed: " + thrown);
                throw NativeFaults.Unavailable(new Common.Unavailable { Reason = Common.UnavailableReason.ReadFailed, Detail = "Native " + what + " could not be read completely." });
            }
        }

        internal static Obs.TradersSnapshot HandleListTraders(Obs.TradersRequest request)
        {
            RequireIdentity(request.Scope);
            if (!ProtoBoundary.ValidateIdentity(request.Scope.ExpectedIdentity, out var map, out var context, out var failure)) throw NativeFaults.Refused(failure);
            return Complete("trader census", () => NativeTradeObservation.Traders(map, context));
        }

        internal static Obs.TradeSession HandleReadTradeSession(Obs.TradeSessionRequest request)
        {
            RequireIdentity(request.Scope);
            if (!ProtoBoundary.ValidateIdentity(request.Scope.ExpectedIdentity, out var context, out var failure)) throw NativeFaults.Refused(failure);
            return Complete("trade session", () => NativeTradeObservation.Session(context));
        }

        internal static Obs.TradeSheet HandleReadTradeSheet(Obs.TradeSheetRequest request)
        {
            RequireIdentity(request.Scope);
            if (!ProtoBoundary.ValidateIdentity(request.Scope.ExpectedIdentity, out var context, out var failure)) throw NativeFaults.Refused(failure);
            return Complete("trade sheet", () => NativeTradeObservation.Sheet(context, request.SessionId, out var sheet, out var refused)
                ? sheet
                : throw NativeFaults.Refused(refused));
        }
    }
}
