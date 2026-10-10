#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    // Pure reads of vanilla request prerequisites. Request execution belongs
    // to Hands; these reads never open dialogs, queue incidents or charge goodwill.
    internal static class NativeTradeAcquisition
    {
        internal const string ToolName = "rimgovernor/observations_read_trade_acquisition";

        // FactionDialogMaker writes these inline (no const or static field), so
        // GameConstants cannot carry them; this is their one native copy, shared
        // by the observation and the request validation.
        internal static class TraderRequest
        {
            internal static int GoodwillDelta(bool orbital) => orbital ? -30 : -15;
            internal static int CooldownTicks(bool orbital) => orbital ? 900000 : 240000;
            internal static int ArrivalMinTicks(bool orbital) => orbital ? 2500 : 120000;
            internal static int ArrivalMaxTicks(bool orbital) => orbital ? 5000 : 120000;
        }

        internal static bool ConsoleNegotiator(Building_CommsConsole console, Pawn pawn) =>
            console.CanUseCommsNow && NativeTradeObservation.EligibleNegotiator(pawn)
            && pawn.health.capacities.CapableOf(PawnCapacityDefOf.Talking)
            && pawn.CanReach(console, PathEndMode.InteractionCell, Danger.Some)
            && pawn.CanReserve(console);

        // FactionRelation.CheckKindThresholds uses effective goodwill after
        // clamping base goodwill and the situation ceiling. An Ally stays Ally
        // down to (but excluding) zero; DiplomacyTuning holds the Hostile and Ally thresholds.
        internal static string RelationAfterPayment(Faction faction, int delta)
        {
            var projected = Math.Min(Math.Max(DiplomacyTuning.MinGoodwill, Math.Min(DiplomacyTuning.MaxGoodwill,
                faction.BaseGoodwillWith(Faction.OfPlayer) + delta)),
                Find.GoodwillSituationManager.GetMaxGoodwill(faction));
            if (projected <= DiplomacyTuning.BecomeHostileThreshold) return "Hostile";
            if (projected >= DiplomacyTuning.BecomeAllyThreshold) return "Ally";
            if (faction.PlayerRelationKind == FactionRelationKind.Ally && projected > 0) return "Ally";
            if (faction.PlayerRelationKind == FactionRelationKind.Hostile && projected < 0) return "Hostile";
            return "Neutral";
        }

        internal static Obs.TradeAcquisition Read(Map map, Common.ObservationContext context)
        {
            var now = Find.TickManager.TicksGame;
            var result = new Obs.TradeAcquisition { Context = context.Clone(), OrbitalAvailable = ModsConfig.OdysseyActive };
            foreach (QueuedIncident queued in Find.Storyteller.incidentQueue)
            {
                var incident = queued.FiringIncident;
                if (incident.parms.target != map || incident.parms.faction == null || incident.parms.traderKind == null) continue;
                var kind = incident.def == IncidentDefOf.TraderCaravanArrival ? Common.TradeRequestKind.Caravan :
                    incident.def == IncidentDefOf.OrbitalTraderArrival ? Common.TradeRequestKind.Orbital : Common.TradeRequestKind.Unspecified;
                if (kind == Common.TradeRequestKind.Unspecified) continue;
                result.Arrivals.Add(new Obs.TradeRequestArrival { FactionId = incident.parms.faction.GetUniqueLoadID(), Kind = kind,
                    TraderKind = incident.parms.traderKind.defName, ArrivalTick = queued.FireTick, RetryTicks = queued.RetryDurationTicks, TriedToFire = queued.TriedToFire });
            }
            foreach (var pawn in map.mapPawns.FreeColonistsSpawned)
            {
                foreach (var job in pawn.jobs.jobQueue.Select(q => q.job).Concat(new[] { pawn.CurJob })) {
                    if (job?.def != JobDefOf.UseCommsConsole || !(job.commTarget is Faction faction) || !(job.targetA.Thing is Building_CommsConsole console)) continue;
                    result.CommsWork.Add(new Obs.TradeCommsWork { NegotiatorId = pawn.GetUniqueLoadID(), ConsoleId = console.GetUniqueLoadID(), FactionId = faction.GetUniqueLoadID(), JobId = job.loadID });
                }
            }
            var consoles = map.listerBuildings.allBuildingsColonist.OfType<Building_CommsConsole>()
                .Where(c => c.CanUseCommsNow).OrderBy(c => c.GetUniqueLoadID()).ToList();
            var eligible = map.mapPawns.FreeColonistsSpawned.Where(p => consoles.Any(c => ConsoleNegotiator(c, p)))
                .OrderBy(p => p.GetUniqueLoadID()).ToList();
            foreach (var console in consoles)
            {
                var row = new Obs.TradeConsole { Id = console.GetUniqueLoadID() };
                row.NegotiatorIds.Add(eligible.Where(p => ConsoleNegotiator(console, p)).Select(p => p.GetUniqueLoadID()));
                result.Consoles.Add(row);
            }
            foreach (var ship in map.passingShipManager.passingShips.OrderBy(s => s.GetUniqueLoadID()))
            {
                var trader = ship as TradeShip;
                var row = new Obs.PassingTradeShip { Id = ship.GetUniqueLoadID(), Trader = trader != null,
                    DepartureTick = (long)now + ship.ticksUntilDeparture };
                if (trader != null) { row.TraderKind = trader.TraderKind.defName; row.CanTrade = trader.CanTradeNow; }
                if (ship.Faction != null) row.FactionId = ship.Faction.GetUniqueLoadID();
                result.PassingShips.Add(row);
            }
            foreach (var faction in Find.FactionManager.AllFactionsVisible.Where(f => !f.IsPlayer && !f.temporary)
                .OrderBy(f => f.GetUniqueLoadID()))
            {
                foreach (var orbital in new[] { false, true })
                {
                    if (orbital ? !ModsConfig.OdysseyActive || !faction.def.canRequestOrbitalTrader : !faction.def.canRequestTraders) continue;
                    var kinds = orbital ? faction.def.orbitalTraderKinds : faction.def.caravanTraderKinds;
                    foreach (var kind in kinds.Where(k => k.requestable).OrderBy(k => k.defName))
                    {
                        var delta = Faction.OfPlayer.CalculateAdjustedGoodwillChange(faction, TraderRequest.GoodwillDelta(orbital));
                        var last = orbital ? faction.lastOrbitalTraderRequestTick : faction.lastTraderRequestTick;
                        var row = new Obs.TradeRequestOption { FactionId = faction.GetUniqueLoadID(),
                            Kind = orbital ? Common.TradeRequestKind.Orbital : Common.TradeRequestKind.Caravan,
                            TraderKind = kind.defName, Goodwill = faction.PlayerGoodwill, GoodwillCost = -delta,
                            RelationAfterPayment = RelationAfterPayment(faction, delta), LastRequestTick = last,
                            CooldownRemainingTicks = Math.Max(0L, (long)last + TraderRequest.CooldownTicks(orbital) - now),
                            ArrivalMinTicks = TraderRequest.ArrivalMinTicks(orbital), ArrivalMaxTicks = TraderRequest.ArrivalMaxTicks(orbital) };
                        row.NegotiatorIds.Add(eligible.Where(p => kind.TitleRequiredToTrade == null ||
                            p.royalty != null && p.GetCurrentTitleSeniorityIn(faction) >= kind.TitleRequiredToTrade.seniority)
                            .Select(p => p.GetUniqueLoadID()));
                        row.Reason = faction.PlayerRelationKind != FactionRelationKind.Ally ? "not_ally" :
                            row.RelationAfterPayment != "Ally" ? "alliance_cost" :
                            row.CooldownRemainingTicks > 0 ? "cooldown" :
                            orbital && result.PassingShips.Count != 0 ? "passing_ships" :
                            !orbital && !faction.def.allowedArrivalTemperatureRange.ExpandedBy(-4f).Includes(map.mapTemperature.SeasonalTemp) ? "temperature" :
                            consoles.Count == 0 ? "console_unavailable" : row.NegotiatorIds.Count == 0 ? "negotiator_unavailable" : "ready";
                        row.Eligible = row.Reason == "ready";
                        result.Requests.Add(row);
                    }
                }
            }
            return result;
        }
    }

    public sealed class NativeTradeAcquisitionTools
    {
        private static Obs.TradeAcquisition Read(Map map, Common.ObservationContext context, Obs.TradeAcquisitionRequest request)
        {
            var row = NativeTradeAcquisition.Read(map, context);
            if (request.Pack != null) row.Pack = NativeCaravanOperations.Preview(request.Pack, context);
            return row;
        }
        [Tool(NativeTradeAcquisition.ToolName, Title = "Read controllable trade prerequisites",
            Description = "Official TradeAcquisitionRequest ProtoJSON. Read-only usable comms consoles and negotiators, all passing ships and native request eligibility, adjusted goodwill cost, post-payment relation, cooldown and arrival bounds. Unknown sampled arrival time stays absent. Issues no orders.")]
        [ToolResponse("payload", "string", "Official TradeAcquisitionReply ProtoJSON.", Always = true)]
        public async Task<object> ReadTradeAcquisition(IRimBridgeContext ctx, CancellationToken cancellationToken,
            [ToolParameter(Description = "Raw value must be a TradeAcquisitionRequest ProtoJSON string.")] object? request = null)
        {
            if (!ProtoBoundary.TryParse(ctx, NativeTradeAcquisition.ToolName, request!, Obs.TradeAcquisitionRequest.Parser, out var parsed, out var failure)
                || parsed.Scope?.ExpectedIdentity == null)
                return ProtoBoundary.Encode(new Obs.TradeAcquisitionReply { Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Identity is required.") });
            return await ProtoBoundary.OnMainThread(ctx, () =>
            {
                if (!ProtoBoundary.ValidateIdentity(parsed.Scope.ExpectedIdentity, out var map, out var context, out failure))
                    return ProtoBoundary.Encode(new Obs.TradeAcquisitionReply { Failure = failure });
                try { return ProtoBoundary.Encode(new Obs.TradeAcquisitionReply { Observed = Read(map, context, parsed) }); }
                catch (Exception) { return ProtoBoundary.Encode(new Obs.TradeAcquisitionReply { Unavailable = new Common.Unavailable
                    { Reason = Common.UnavailableReason.ReadFailed, Detail = "Native trade prerequisites could not be read completely." } }); }
            }, cancellationToken).ConfigureAwait(false);
        }
    }
}
