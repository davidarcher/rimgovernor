#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // A request is executed by the real comms job at its contact toil. The
    // contact substitutes the already-selected vanilla request option action.
    // No cooldown or paid-request ledger is saved here: vanilla owns those facts.
    internal sealed class CommsTradeRequestActionHandler : IActionHandler
    {
        private sealed class Contact
        {
            internal Operations.CommsTradeRequestIntent Intent = null!;
            internal Common.Identity Identity = null!;
            internal Pawn Pawn = null!;
            internal Faction Faction = null!;
            internal Building_CommsConsole Console = null!;
            internal Job Job = null!;
            internal int JobId;
            internal bool Owns(Job? job) => ReferenceEquals(job, Job) && job != null && job.loadID == JobId
                && job.def == JobDefOf.UseCommsConsole && ReferenceEquals(job.commTarget, Faction) && ReferenceEquals(job.targetA.Thing, Console);
            internal bool Live() => Owns(Pawn.CurJob) || Pawn.jobs.jobQueue.Any(q => Owns(q.job));
        }
        private static readonly List<Contact> Contacts = new List<Contact>();
        private static bool hooked;

        private static bool Resolve(Operations.CommsTradeRequestIntent intent, Common.Identity identity,
            bool arriving, out Map? map, out Pawn? pawn, out Faction? faction, out Building_CommsConsole? console, out string reason)
        {
            map = ProtoBoundary.ResolveMap(identity); pawn = null; faction = null; console = null; reason = "invalid_request";
            if (map == null || !intent.HasExpectedLastRequestTick || !ProtoBoundary.IsIdentifier(intent.FactionId)
                || !ProtoBoundary.IsIdentifier(intent.TraderKind) || !ProtoBoundary.IsIdentifier(intent.ConsoleId)
                || !ProtoBoundary.IsIdentifier(intent.NegotiatorId)
                || (intent.Kind != Common.TradeRequestKind.Caravan && intent.Kind != Common.TradeRequestKind.Orbital)) return false;
            faction = Find.FactionManager.AllFactionsVisible.FirstOrDefault(f => f.GetUniqueLoadID() == intent.FactionId && !f.IsPlayer && !f.temporary && !f.defeated);
            console = map.listerBuildings.allBuildingsColonist.OfType<Building_CommsConsole>().FirstOrDefault(c => c.GetUniqueLoadID() == intent.ConsoleId);
            pawn = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => p.GetUniqueLoadID() == intent.NegotiatorId);
            if (faction == null || console == null || pawn == null) { reason = "target_missing"; return false; }
            var orbital = intent.Kind == Common.TradeRequestKind.Orbital;
            var kinds = orbital ? faction.def.orbitalTraderKinds : faction.def.caravanTraderKinds;
            var kind = kinds.FirstOrDefault(k => k.defName == intent.TraderKind && k.requestable);
            var last = orbital ? faction.lastOrbitalTraderRequestTick : faction.lastTraderRequestTick;
            reason = last != intent.ExpectedLastRequestTick ? "request_tick_changed" :
                faction.PlayerRelationKind != FactionRelationKind.Ally ? "not_ally" :
                NativeTradeAcquisition.RelationAfterPayment(faction, Faction.OfPlayer.CalculateAdjustedGoodwillChange(faction, orbital ? -30 : -15)) != "Ally" ? "alliance_cost" :
                (long)last + (orbital ? 900000 : 240000) > Find.TickManager.TicksGame ? "cooldown" :
                orbital && (!ModsConfig.OdysseyActive || !faction.def.canRequestOrbitalTrader) ? "orbital_unavailable" :
                !orbital && (!faction.def.canRequestTraders || !faction.def.allowedArrivalTemperatureRange.ExpandedBy(-4f).Includes(map.mapTemperature.SeasonalTemp)) ? "caravan_unavailable" :
                orbital && map.passingShipManager.passingShips.Count != 0 ? "passing_ships" :
                kind == null ? "kind_unavailable" :
                kind.TitleRequiredToTrade != null && (pawn.royalty == null || pawn.GetCurrentTitleSeniorityIn(faction) < kind.TitleRequiredToTrade.seniority) ? "title" :
                !console.CanUseCommsNow || !NativeTradeObservation.EligibleNegotiator(pawn) || !pawn.health.capacities.CapableOf(PawnCapacityDefOf.Talking)
                    || !pawn.CanReach(console, PathEndMode.InteractionCell, Danger.Some) || (!arriving && !pawn.CanReserve(console)) ? "comms_unavailable" : "ready";
            return reason == "ready";
        }

        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context)
        {
            Contacts.RemoveAll(c => !c.Live() || c.Identity.LoadToken != context.Identity.LoadToken || c.Identity.ColonyId != context.Identity.ColonyId);
            var intent = action.CommsTradeRequest;
            if (!Resolve(intent, context.Identity, false, out _, out var pawn, out _, out _, out var reason))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, reason);
            if (Contacts.Any(c => c.Pawn == pawn || c.Console.GetUniqueLoadID() == intent.ConsoleId)
                || pawn!.CurJob?.def == JobDefOf.UseCommsConsole)
                return ProtoBoundary.Fail(Common.FailureCode.OwnerConflict, "comms_work_present");
            if (!InstallHook()) return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "comms_hook_unavailable");
            return null;
        }

        private static bool InstallHook()
        {
            if (hooked) return true;
            try {
                new Harmony("rimgovernor.trade.requests").Patch(AccessTools.Method(typeof(Faction), nameof(Faction.TryOpenComms)),
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(CommsTradeRequestActionHandler), nameof(ContactFaction))));
                hooked = true; return true;
            } catch { return false; }
        }
        private static bool ContactFaction(Faction __instance, Pawn negotiator)
        {
            var contact = Contacts.FirstOrDefault(c => c.Faction == __instance && c.Pawn == negotiator && c.Owns(negotiator.CurJob));
            if (contact == null) return true;
            Contacts.Remove(contact);
            if (!ProtoBoundary.ValidateIdentity(contact.Identity, out _, out _) ||
                !Resolve(contact.Intent, contact.Identity, true, out var map, out _, out var faction, out _, out _)) return false;
            var orbital = contact.Intent.Kind == Common.TradeRequestKind.Orbital;
            var kinds = (orbital ? faction!.def.orbitalTraderKinds : faction!.def.caravanTraderKinds).Where(k => k.requestable).ToList();
            var index = kinds.FindIndex(k => k.defName == contact.Intent.TraderKind);
            var factory = AccessTools.Method(typeof(FactionDialogMaker), orbital ? "RequestOrbitalTraderOption" : "RequestTraderOption");
            var option = factory?.Invoke(null, new object[] { map!, faction, negotiator }) as DiaOption;
            if (option == null || option.disabled || option.link == null || index < 0 || index >= option.link.options.Count) return false;
            // Same def sequence the native factory enumerates, never a translated label.
            option.link.options[index].action?.Invoke();
            return false;
        }

        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context)
        {
            if (!Resolve(action.CommsTradeRequest, context.Identity, false, out _, out var pawn, out var faction, out var console, out var reason))
                throw new InvalidOperationException(reason);
            var intent = action.CommsTradeRequest;
            var job = JobMaker.MakeJob(JobDefOf.UseCommsConsole, console);
            job.commTarget = faction;
            var contact = new Contact { Intent = intent.Clone(), Identity = context.Identity.Clone(), Pawn = pawn!, Faction = faction!, Console = console!, Job = job, JobId = job.loadID };
            Contacts.Add(contact);
            if (!pawn!.jobs.TryTakeOrderedJob(job, JobTag.Misc)) { Contacts.Remove(contact); throw new InvalidOperationException("comms_job_refused"); }
            var last = intent.Kind == Common.TradeRequestKind.Orbital ? faction!.lastOrbitalTraderRequestTick : faction!.lastTraderRequestTick;
            return new Receipts.EffectEvidence { CommsTradeRequest = new Receipts.CommsTradeRequestEffect {
                FactionId = intent.FactionId, Kind = intent.Kind, TraderKind = intent.TraderKind, NegotiatorId = intent.NegotiatorId,
                WorkQueued = true, BeforeRequestTick = last, AfterRequestTick = last, BeforeGoodwill = faction.PlayerGoodwill, AfterGoodwill = faction.PlayerGoodwill } };
        }
    }
}
