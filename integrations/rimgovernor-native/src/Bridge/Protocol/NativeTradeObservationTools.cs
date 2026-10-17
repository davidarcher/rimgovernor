#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RimWorld;
using Verse;
using Verse.AI.Group;
using Common = RimGovernor.Contracts.Wire.Common;
using Defs = RimGovernor.Contracts.Wire.Defs;
using Obs = RimGovernor.Contracts.Wire.Observations;

namespace HomeBridge.BridgeTools
{
    // Typed, read-only trade census behind the ReadScope boundary: the map
    // traders a session could open with, the eligible negotiators, and
    // the currently open session's whole sheet. Every read recomputes from
    // current native state; nothing here stages or closes a session or
    // issues an order.
    internal static class NativeTradeObservation
    {

        private static bool SafeBool(Func<bool> f) { try { return f(); } catch { return false; } }
        private static int SafeInt(Func<int> f) { try { return f(); } catch { return 0; } }
        private static float SafeFloat(Func<float> f) { try { return f(); } catch { return 0f; } }
        private static string SafeText(Func<string?> f) { try { return f() ?? ""; } catch { return ""; } }


        // A trader pawn's row head; its token is the row's trader_snapshot.
        private static Obs.EntityRef PawnRef(Pawn pawn) => new Obs.EntityRef
        {
            Id = pawn.GetUniqueLoadID(), DefName = pawn.def.defName, Label = SafeText(() => pawn.LabelShort), MapId = pawn.Map?.uniqueID ?? -1,
            Position = new Common.Cell { X = pawn.Position.x, Z = pawn.Position.z },
        };
        private static Obs.SnapshotRef PawnToken(Pawn pawn, Common.ObservationContext context) =>
            new Obs.SnapshotRef { Context = context, EntityId = pawn.GetUniqueLoadID(), Token = pawn.GetUniqueLoadID() };

        // Mirrors NativeTradeOperations.PrepareOpen's negotiator eligibility
        // exactly, so a negotiator listed here is one an open would accept
        // (adjacency and reachability aside, which depend on the trader).
        internal static bool EligibleNegotiator(Pawn p) =>
            !SafeBool(() => p.Dead) && !SafeBool(() => p.Downed) && !SafeBool(() => p.InMentalState) && !SafeBool(() => p.WorkTagIsDisabled(WorkTags.Social));

        private static float TradeStat(Pawn p) => SafeFloat(() => p.GetStatValue(StatDefOf.TradePriceImprovement));

        internal static Obs.TradersSnapshot Traders(Map map, Common.ObservationContext context)
        {
            var snapshot = new Obs.TradersSnapshot { Context = context };
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
                    Entity = PawnRef(pawn), TraderSnapshot = PawnToken(pawn, context),
                    Kind = tracker.traderKind.defName, Faction = NativeRef.Of(pawn.Faction),
                    CanTrade = canTrade && !dismissed && !travelling, Travelling = travelling, Orbital = false, GoodsStacks = (uint)SafeInt(() => tracker.Goods.Count()),
                };
                if (!canTrade) row = row with { Reason = "CanTradeNow is false (downed, in a mental state, asleep, hostile or out of stock)." };
                else if (dismissed) row = row with { Reason = "The trader was dismissed." };
                else if (travelling) row = row with { Reason = "The caravan is still travelling to its trade spot." };
                traders.Add(row);
            }
            foreach (var ship in map.passingShipManager.passingShips.OfType<TradeShip>())
            {
                var id = ship.GetUniqueLoadID();
                var usable = ship.CanTradeNow && Building_OrbitalTradeBeacon.AllPowered(map).Any()
                    && map.mapPawns.FreeColonistsSpawned.Any(p => p.CanTradeWith(ship.Faction, ship.TraderKind).Accepted
                        && map.listerBuildings.allBuildingsColonist.OfType<Building_CommsConsole>().Any(c => NativeTradeAcquisition.ConsoleNegotiator(c, p)));
                traders.Add(new Obs.Trader { Entity = new Obs.EntityRef { Id = id, Label = ship.FullTitle, MapId = map.uniqueID, Position = new Common.Cell { X = 0, Z = 0 } },
                    TraderSnapshot = new Obs.SnapshotRef { Context = context, EntityId = id, Token = id }, Kind = ship.TraderKind.defName,
                    Faction = NativeRef.Of(ship.Faction), CanTrade = usable, Travelling = false, Orbital = true, GoodsStacks = (uint)ship.Goods.Count() });
            }
            traders.Sort((a, b) => string.CompareOrdinal(a.Entity?.Id, b.Entity?.Id));
            snapshot = snapshot with { Traders = [.. snapshot.Traders, .. traders] };
            var negotiators = map.mapPawns.FreeColonistsSpawned.Where(EligibleNegotiator).ToList();
            negotiators.Sort((x, y) =>
            {
                var c = TradeStat(y).CompareTo(TradeStat(x));
                return c != 0 ? c : string.CompareOrdinal(x.GetUniqueLoadID(), y.GetUniqueLoadID());
            });
            snapshot = snapshot with { Negotiators = [.. snapshot.Negotiators, .. negotiators.Select(p => NativeRef.Thing(p))] };
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
            var favor = SafeBool(() => t.IsFavor);
            var line = new Obs.TradeLine
            {
                Index = (uint)index, LineId = "#" + index,
                Definition = new Obs.DefinitionRef { DefName = def != null ? def.defName : "" },
                Stuff = SafeText(() => t.StuffDef != null ? t.StuffDef.defName : ""),
                ColonyCount = SafeInt(() => t.CountHeldBy(Transactor.Colony)), TraderCount = SafeInt(() => t.CountHeldBy(Transactor.Trader)),
                // GetPriceFor memoises price factors inside the Tradeable: the
                // session's own scratch state, the same write the vanilla dialog
                // makes every frame it draws.
                BuyPrice = SafeFloat(() => t.GetPriceFor(TradeAction.PlayerBuys)), SellPrice = SafeFloat(() => t.GetPriceFor(TradeAction.PlayerSells)),
                MarketValue = SafeFloat(() => t.BaseMarketValue),
                TraderWillTrade = SafeBool(() => t.TraderWillTrade), Currency = SafeBool(() => t.IsCurrency), Pawn = pawn, Favor = favor,
                TransferCount = SafeInt(() => t.CountToTransfer), MinimumCount = SafeInt(() => t.GetMinimumToTransfer()), MaximumCount = SafeInt(() => t.GetMaximumToTransfer()),
            };
            if (!pawn)
            {
                // One colony thing and its quality: a packed
                // sculpture sells line by line, matched to its stock id.
                Thing? thing; try { thing = t.FirstThingColony; } catch { thing = null; }
                if (thing != null)
                {
                    var id = SafeText(() => thing.GetUniqueLoadID());
                    if (id != "") line = line with { ThingId = id };
                    try { if (thing.GetInnerIfMinified().TryGetQuality(out var quality)) line = line with { Quality = (int)quality }; } catch { }
                    // Gear sale: the thing's hit-point fraction and the
                    // stockpile it lies in, so the controller sells only the worn
                    // dump's gear above the incinerator's cap.
                    try { if (thing.def.useHitPoints && thing.MaxHitPoints > 0) line = line with { HitPointsFraction = (double)thing.HitPoints / thing.MaxHitPoints }; } catch { }
                    try
                    {
                        var zone = thing.Spawned ? thing.Map.zoneManager.ZoneAt(thing.Position) as Zone_Stockpile : null;
                        if (zone != null) line = line with { ZoneId = zone.GetUniqueLoadID() };
                    }
                    catch { }
                }
            }
            if (pawn)
            {
                // The purchase choice reads the pawn's skills, passions
                // and violence capability; an unreadable pawn leaves them absent.
                Pawn? p; try { p = t.AnyThing as Pawn; } catch { p = null; }
                if (p != null)
                {
                    line = line with { PawnId = SafeText(() => p.GetUniqueLoadID()) };
                    line = line with { PawnGender = SafeText(() => p.gender.ToString()) };
                    line = line with { ViolenceCapable = SafeBool(() => !p.WorkTagIsDisabled(WorkTags.Violent)) };
                    // A prisoner's sale facts: what PreTraded charges
                    // (MemberSold goodwill with the extra home or host faction)
                    // and what AllSellableColonyPawns lists on.
                    if (p.guest != null)
                    {
                        line = line with { GuestStatus = SafeText(() => p.guest.GuestStatus.ToString()) };
                        line = line with { PrisonerSecure = SafeBool(() => p.guest.PrisonerIsSecure) };
                    }
                    line = line with { PawnDowned = SafeBool(() => p.Downed) };
                    try { var home = p.GetExtraHomeFaction(); if (home != null) line = line with { ExtraHomeFaction = NativeRef.Of(home) }; } catch { }
                    try { var host = p.GetExtraHostFaction(); if (host != null) line = line with { ExtraHostFaction = NativeRef.Of(host) }; } catch { }
                    try
                    {
                        if (p.skills != null)
                            foreach (var s in p.skills.skills)
                                line = line with { Skills = [.. line.Skills, new Obs.Skill { DefName = s.def.defName, Level = s.Level, Passion = NativeEnums.Passion(s.passion), Disabled = s.TotallyDisabled }] };
                    }
                    catch { line = line with { Skills = [] }; }
                }
            }
            return line;
        }

        internal static Obs.TradeSession Session(Common.ObservationContext context)
        {
            var session = new Obs.TradeSession { Context = context };
            if (NativeTradeOperations.LiveTarget(context.Identity, out var target, out var negotiator, out var open) && target != null && negotiator != null)
            { session = session with { Target = target }; session = session with { NegotiatorId = negotiator.GetUniqueLoadID() }; session = session with { Open = open }; }
            return session;
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
                Snapshot = new Obs.SnapshotRef { Context = context, EntityId = session.SessionId, Token = session.DealSignature },
                SessionId = session.SessionId,
                Target = session.Target,
                Negotiator = NativeRef.Thing(session.Negotiator),
                GiftMode = session.GiftMode,
                CanTradeNow = SafeBool(() => session.Trader.CanTradeNow),
                TraderHasEnoughSilver = SafeBool(() => deal.DoesTraderHaveEnoughSilver()),
                DealSignature = session.DealSignature,
                CurrencyKind = NativeTradeOperations.IsFavorSession() ? Defs.TradeCurrency.Favor : Defs.TradeCurrency.Silver,
            };
            if (currency != null)
            {
                // Negative when the colony pays, exactly the currency row's
                // own signed transfer count.
                sheet = sheet with { Balance = SafeInt(() => currency.CountToTransfer) };
                sheet = sheet with { ColonyCanAfford = SafeInt(() => currency.CountPostDealFor(Transactor.Colony)) >= 0 };
            }
            for (var i = 0; i < all.Count; i++) if (all[i] != null) sheet = sheet with { Lines = [.. sheet.Lines, Line(all[i], i)] };
            return true;
        }
    }

    internal static class NativeTradeObservationTools
    {
        internal static Obs.TradersReply HandleListTraders(Obs.TradersRequest request)
        {
            if (request.Scope?.ExpectedIdentity == null)
                return new Obs.TradersReply { Outcome = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Identity is required.") };
            if (!ProtoBoundary.ValidateIdentity(request.Scope.ExpectedIdentity, out var map, out var context, out var failure))
                return new Obs.TradersReply { Outcome = new Obs.TradersReplyOutcome.FailureCase(failure) };
            try { return new Obs.TradersReply { Outcome = NativeTradeObservation.Traders(map, context) }; }
            catch (Exception) { return new Obs.TradersReply { Outcome =
                new Common.Unavailable { Reason = Common.UnavailableReason.ReadFailed, Detail = "Native trader census could not be read completely." } }; }
        }

        internal static Obs.TradeSessionReply HandleReadTradeSession(Obs.TradeSessionRequest request)
        {
            if (request.Scope?.ExpectedIdentity == null)
                return new Obs.TradeSessionReply { Outcome = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Identity is required.") };
            if (!ProtoBoundary.ValidateIdentity(request.Scope.ExpectedIdentity, out var context, out var failure))
                return new Obs.TradeSessionReply { Outcome = new Obs.TradeSessionReplyOutcome.FailureCase(failure) };
            try { return new Obs.TradeSessionReply { Outcome = NativeTradeObservation.Session(context) }; }
            catch (Exception) { return new Obs.TradeSessionReply { Outcome =
                new Common.Unavailable { Reason = Common.UnavailableReason.ReadFailed, Detail = "Native trade session could not be read." } }; }
        }

        internal static Obs.TradeSheetReply HandleReadTradeSheet(Obs.TradeSheetRequest request)
        {
            if (request.Scope?.ExpectedIdentity == null)
                return new Obs.TradeSheetReply { Outcome = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Identity is required.") };
            if (!ProtoBoundary.ValidateIdentity(request.Scope.ExpectedIdentity, out var context, out var failure))
                return new Obs.TradeSheetReply { Outcome = new Obs.TradeSheetReplyOutcome.FailureCase(failure) };
            try
            {
                if (!NativeTradeObservation.Sheet(context, request.SessionId, out var sheet, out failure))
                    return new Obs.TradeSheetReply { Outcome = new Obs.TradeSheetReplyOutcome.FailureCase(failure) };
                var reply = new Obs.TradeSheetReply { Outcome = sheet };
                return reply;
            }
            catch (Exception) { return new Obs.TradeSheetReply { Outcome =
                new Common.Unavailable { Reason = Common.UnavailableReason.ReadFailed, Detail = "Native trade sheet could not be read completely." } }; }
        }
    }
}
