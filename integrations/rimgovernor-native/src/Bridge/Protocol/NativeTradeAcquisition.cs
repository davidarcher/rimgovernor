using System;
using System.Collections.Generic;
using System.Linq;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Host.Sdk;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimGovernor.Native
{
    // Pure reads of vanilla request prerequisites. Request execution belongs
    // to Hands; these reads never open dialogs, queue incidents or charge goodwill.
    internal static class NativeTradeAcquisition
    {

        // FactionDialogMaker writes these inline (no const or static field), so
        // GameConstants cannot carry them; this is their one native copy, shared
        // by the observation and the request validation.
        internal static class TraderRequest
        {
            internal static int GoodwillDelta(bool orbital) => orbital ? -30 : -15;
            internal static int CooldownTicks(bool orbital) => orbital ? 900000 : 240000;
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

        internal static List<RimGovernor.Brain.GameFacts.Observations.TradeRequestOption> RequestOptions(Map map)
        {
            var now = Find.TickManager.TicksGame;
            var consoles = map.listerBuildings.allBuildingsColonist.OfType<Building_CommsConsole>()
                .Where(c => c.CanUseCommsNow).OrderBy(c => c.GetUniqueLoadID(), StringComparer.Ordinal).ToList();
            var eligible = map.mapPawns.FreeColonistsSpawned.Where(p => consoles.Any(c => ConsoleNegotiator(c, p)))
                .OrderBy(p => p.GetUniqueLoadID()).ToList();
            var ships = map.passingShipManager.passingShips;
            var requests = new List<RimGovernor.Brain.GameFacts.Observations.TradeRequestOption>();
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
                        var relation = RelationAfterPayment(faction, delta);
                        var cooldown = Math.Max(0L, (long)last + TraderRequest.CooldownTicks(orbital) - now);
                        var negotiators = eligible.Where(p => kind.TitleRequiredToTrade == null ||
                            p.royalty != null && p.GetCurrentTitleSeniorityIn(faction) >= kind.TitleRequiredToTrade.seniority)
                            .Select(p => p.GetUniqueLoadID()).ToList();
                        var console = consoles.FirstOrDefault(c => eligible.Any(p => negotiators.Contains(p.GetUniqueLoadID()) && ConsoleNegotiator(c, p)));
                        if (console is not null) negotiators = eligible.Where(p => negotiators.Contains(p.GetUniqueLoadID()) && ConsoleNegotiator(console, p)).Select(p => p.GetUniqueLoadID()).ToList();
                        var ready = faction.PlayerRelationKind == FactionRelationKind.Ally && relation == "Ally"
                            && cooldown <= 0 && !(orbital && ships.Count != 0)
                            && (orbital || faction.def.allowedArrivalTemperatureRange.ExpandedBy(-4f).Includes(map.mapTemperature.SeasonalTemp))
                            && consoles.Count != 0 && negotiators.Count != 0;
                        requests.Add(new RimGovernor.Brain.GameFacts.Observations.TradeRequestOption { FactionId = faction.GetUniqueLoadID(),
                            Kind = orbital ? RimGovernor.Brain.GameFacts.Common.TradeRequestKind.Orbital : RimGovernor.Brain.GameFacts.Common.TradeRequestKind.Caravan,
                            TraderKind = kind.defName, Goodwill = faction.PlayerGoodwill, GoodwillCost = -delta,
                            RelationAfterPayment = relation, LastRequestTick = last, CooldownRemainingTicks = cooldown,
                            ArrivalMaxTicks = TraderRequest.ArrivalMaxTicks(orbital), Eligible = ready, NegotiatorIds = [.. negotiators],
                            ConsoleId = console?.GetUniqueLoadID() ?? "", ConsoleCell = console is null ? default : new(console.InteractionCell.x, console.InteractionCell.z) });
                    }
                }
            }
            return requests;
        }

        internal static RimGovernor.Brain.GameFacts.Observations.TradeAcquisition Read(Map map, RimGovernor.Brain.GameFacts.Common.ObservationContext context, RimGovernor.Brain.GameFacts.Observations.TradePackEstimate? pack)
        {
            var arrivals = new List<RimGovernor.Brain.GameFacts.Observations.TradeRequestArrival>();
            foreach (QueuedIncident queued in Find.Storyteller.incidentQueue)
            {
                var incident = queued.FiringIncident;
                if (incident.parms.target != map || incident.parms.faction == null || incident.parms.traderKind == null) continue;
                RimGovernor.Brain.GameFacts.Common.TradeRequestKind kind;
                if (incident.def == IncidentDefOf.TraderCaravanArrival) kind = RimGovernor.Brain.GameFacts.Common.TradeRequestKind.Caravan;
                else if (incident.def == IncidentDefOf.OrbitalTraderArrival) kind = RimGovernor.Brain.GameFacts.Common.TradeRequestKind.Orbital;
                else continue;
                arrivals.Add(new RimGovernor.Brain.GameFacts.Observations.TradeRequestArrival { FactionId = incident.parms.faction.GetUniqueLoadID(), Kind = kind,
                    TraderKind = incident.parms.traderKind.defName, ArrivalTick = queued.FireTick });
            }
            var work = new List<RimGovernor.Brain.GameFacts.Observations.TradeCommsWork>();
            foreach (var pawn in map.mapPawns.FreeColonistsSpawned)
            {
                foreach (var job in pawn.jobs.jobQueue.Select(q => q.job).Concat(new[] { pawn.CurJob })) {
                    if (job?.def != JobDefOf.UseCommsConsole || !(job.commTarget is Faction faction) || !(job.targetA.Thing is Building_CommsConsole console)) continue;
                    work.Add(new RimGovernor.Brain.GameFacts.Observations.TradeCommsWork { NegotiatorId = pawn.GetUniqueLoadID(), ConsoleId = console.GetUniqueLoadID(), FactionId = faction.GetUniqueLoadID() });
                }
            }
            var consoles = map.listerBuildings.allBuildingsColonist.OfType<Building_CommsConsole>()
                .Where(c => c.CanUseCommsNow).OrderBy(c => c.GetUniqueLoadID()).ToList();
            var eligible = map.mapPawns.FreeColonistsSpawned.Where(p => consoles.Any(c => ConsoleNegotiator(c, p)))
                .OrderBy(p => p.GetUniqueLoadID()).ToList();
            var ships = map.passingShipManager.passingShips.OrderBy(s => s.GetUniqueLoadID())
                .Select(s => new RimGovernor.Brain.GameFacts.Observations.PassingTradeShip { TraderKind = (s as TradeShip)?.TraderKind.defName ?? "" }).ToList();
            var requests = RequestOptions(map);
            return new RimGovernor.Brain.GameFacts.Observations.TradeAcquisition
            {
                Context = context,
                Consoles = [.. consoles.Select(console => new RimGovernor.Brain.GameFacts.Observations.TradeConsole { Id = console.GetUniqueLoadID(),
                    NegotiatorIds = [.. eligible.Where(p => ConsoleNegotiator(console, p)).Select(p => p.GetUniqueLoadID())] })],
                PassingShips = [.. ships],
                Requests = [.. requests],
                OrbitalAvailable = ModsConfig.OdysseyActive,
                Pack = pack,
                Arrivals = [.. arrivals],
                CommsWork = [.. work],
            };
        }
    }

    internal static class NativeTradeAcquisitionTools
    {
        internal static RimGovernor.Brain.GameFacts.Observations.TradeAcquisition HandleReadTradeAcquisition(RimGovernor.Brain.GameOrders.Observations.TradeAcquisitionRequest request)
        {
            if (request.Scope?.ExpectedIdentity == null)
                throw NativeFaults.Refused(GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Identity is required."));
            if (!GameThread.ValidateIdentity(request.Scope.ExpectedIdentity, out var map, out var context, out var failure))
                throw NativeFaults.Refused(failure);
            try
            {
                return NativeTradeAcquisition.Read(map, context, request.Pack != null ? NativeCaravanOperations.Preview(request.Pack, context) : null);
            }
            catch (Exception thrown) when (thrown is not NativeFailureException)
            {
                ModLog.Error("observe", "Native trade prerequisites read failed: " + thrown);
                throw NativeFaults.Unavailable(new RimGovernor.Brain.GameFacts.Common.Unavailable
                    { Reason = RimGovernor.Brain.GameFacts.Common.UnavailableReason.ReadFailed, Detail = "Native trade prerequisites could not be read completely." });
            }
        }
    }
}
