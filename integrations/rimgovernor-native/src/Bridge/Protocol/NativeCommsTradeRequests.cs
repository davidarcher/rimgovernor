using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimGovernor.Native
{
    // A request is executed by the real comms job at its contact toil. The
    // contact substitutes the already-selected vanilla request option action.
    // No cooldown or paid-request ledger is saved here: vanilla owns those facts.
    internal sealed class CommsTradeRequestActionHandler : IActionHandler
    {
        private sealed class Contact
        {
            internal required RimGovernor.Brain.GameOrders.Operations.CommsTradeRequestIntent Intent;
            internal required RimGovernor.Brain.GameFacts.Common.Identity Identity;
            internal required Pawn Pawn;
            internal required Faction Faction;
            internal required Building_CommsConsole Console;
            internal required Job Job;
            internal int JobId;
            internal bool Owns(Job? job) => ReferenceEquals(job, Job) && job != null && job.loadID == JobId
                && job.def == JobDefOf.UseCommsConsole && ReferenceEquals(job.commTarget, Faction) && ReferenceEquals(job.targetA.Thing, Console);
            internal bool Live() => Owns(Pawn.CurJob) || Pawn.jobs.jobQueue.Any(q => Owns(q.job));
        }
        private static readonly List<Contact> Contacts = new List<Contact>();
        private static bool hooked;

        private static bool Resolve(RimGovernor.Brain.GameOrders.Operations.CommsTradeRequestIntent intent, RimGovernor.Brain.GameFacts.Common.Identity identity,
            bool arriving, out Map? map, out Pawn? pawn, out Faction? faction, out Building_CommsConsole? console, out string reason)
        {
            map = GameThread.ResolveMap(identity); pawn = null; faction = null; console = null; reason = "invalid_request";
            if (map == null || !GameThread.IsIdentifier(intent.FactionId)
                || !GameThread.IsIdentifier(intent.TraderKind) || !GameThread.IsIdentifier(intent.ConsoleId)
                || !GameThread.IsIdentifier(intent.NegotiatorId)
                || (intent.Kind != RimGovernor.Brain.GameFacts.Common.TradeRequestKind.Caravan && intent.Kind != RimGovernor.Brain.GameFacts.Common.TradeRequestKind.Orbital)) return false;
            faction = Find.FactionManager.AllFactionsVisible.FirstOrDefault(f => f.GetUniqueLoadID() == (intent.FactionId ?? "") && !f.IsPlayer && !f.temporary && !f.defeated);
            console = map.listerBuildings.allBuildingsColonist.OfType<Building_CommsConsole>().FirstOrDefault(c => c.GetUniqueLoadID() == (intent.ConsoleId ?? ""));
            pawn = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => p.GetUniqueLoadID() == (intent.NegotiatorId ?? ""));
            if (faction == null || console == null || pawn == null) { reason = "target_missing"; return false; }
            var orbital = intent.Kind == RimGovernor.Brain.GameFacts.Common.TradeRequestKind.Orbital;
            var kinds = orbital ? faction.def.orbitalTraderKinds : faction.def.caravanTraderKinds;
            var kind = kinds.FirstOrDefault(k => k.defName == (intent.TraderKind ?? "") && k.requestable);
            var last = orbital ? faction.lastOrbitalTraderRequestTick : faction.lastTraderRequestTick;
            reason = last != intent.ExpectedLastRequestTick ? "request_tick_changed" :
                faction.PlayerRelationKind != FactionRelationKind.Ally ? "not_ally" :
                NativeTradeAcquisition.RelationAfterPayment(faction, Faction.OfPlayer.CalculateAdjustedGoodwillChange(faction, NativeTradeAcquisition.TraderRequest.GoodwillDelta(orbital))) != "Ally" ? "alliance_cost" :
                (long)last + NativeTradeAcquisition.TraderRequest.CooldownTicks(orbital) > Find.TickManager.TicksGame ? "cooldown" :
                orbital && (!ModsConfig.OdysseyActive || !faction.def.canRequestOrbitalTrader) ? "orbital_unavailable" :
                !orbital && (!faction.def.canRequestTraders || !faction.def.allowedArrivalTemperatureRange.ExpandedBy(-4f).Includes(map.mapTemperature.SeasonalTemp)) ? "caravan_unavailable" :
                orbital && map.passingShipManager.passingShips.Count != 0 ? "passing_ships" :
                kind == null ? "kind_unavailable" :
                kind.TitleRequiredToTrade != null && (pawn.royalty == null || pawn.GetCurrentTitleSeniorityIn(faction) < kind.TitleRequiredToTrade.seniority) ? "title" :
                !console.CanUseCommsNow || !NativeTradeObservation.EligibleNegotiator(pawn) || !pawn.health.capacities.CapableOf(PawnCapacityDefOf.Talking)
                    || !pawn.CanReach(console, PathEndMode.InteractionCell, Danger.Some) || (!arriving && !pawn.CanReserve(console)) ? "comms_unavailable" : "ready";
            return reason == "ready";
        }

        public RimGovernor.Brain.GameFacts.Common.Failure? Validate(RimGovernor.Brain.GameOrders.Operations.Action action, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            Contacts.RemoveAll(c => !c.Live() || (c.Identity.LoadToken ?? "") != (context.Identity?.LoadToken ?? "") || (c.Identity.ColonyId ?? "") != (context.Identity?.ColonyId ?? ""));
            var intent = action.Intent?.CommsTradeRequest;
            if (intent is null || context.Identity is null) return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "comms_intent_missing");
            if (!Resolve(intent, context.Identity, false, out _, out var pawn, out _, out _, out var reason))
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, reason);
            if (Contacts.Any(c => c.Pawn == pawn || c.Console.GetUniqueLoadID() == intent.ConsoleId)
                || (pawn ?? throw new System.InvalidOperationException("Required native value is unavailable: pawn.")).CurJob?.def == JobDefOf.UseCommsConsole)
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.OwnerConflict, "comms_work_present");
            if (!InstallHook()) return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "comms_hook_unavailable");
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
            if (!GameThread.ValidateIdentity(contact.Identity, out _, out _) ||
                !Resolve(contact.Intent, contact.Identity, true, out var map, out _, out var faction, out _, out _)) return false;
            var orbital = contact.Intent.Kind == RimGovernor.Brain.GameFacts.Common.TradeRequestKind.Orbital;
            var kinds = (orbital ? (faction ?? throw new System.InvalidOperationException("Required native value is unavailable: faction.")).def.orbitalTraderKinds : (faction ?? throw new System.InvalidOperationException("Required native value is unavailable: faction.")).def.caravanTraderKinds).Where(k => k.requestable).ToList();
            var index = kinds.FindIndex(k => k.defName == (contact.Intent.TraderKind ?? ""));
            var factory = AccessTools.Method(typeof(FactionDialogMaker), orbital ? "RequestOrbitalTraderOption" : "RequestTraderOption");
            var option = factory?.Invoke(null, new object[] { (map ?? throw new System.InvalidOperationException("Required native value is unavailable: map.")), faction, negotiator }) as DiaOption;
            if (option == null || option.disabled || option.link == null || index < 0 || index >= option.link.options.Count) return false;
            // Same def sequence the native factory enumerates, never a translated label.
            option.link.options[index].action?.Invoke();
            return false;
        }

        public RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Apply(RimGovernor.Brain.GameOrders.Operations.Action action, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var intent = action.Intent?.CommsTradeRequest;
            if (intent is null || context.Identity is null) throw new InvalidOperationException("comms_intent_missing");
            if (!Resolve(intent, context.Identity, false, out _, out var pawn, out var faction, out var console, out var reason))
                throw new InvalidOperationException(reason);
            var job = JobMaker.MakeJob(JobDefOf.UseCommsConsole, console);
            job.commTarget = faction;
            var contact = new Contact { Intent = intent, Identity = context.Identity, Pawn = (pawn ?? throw new System.InvalidOperationException("Required native value is unavailable: pawn.")), Faction = (faction ?? throw new System.InvalidOperationException("Required native value is unavailable: faction.")), Console = (console ?? throw new System.InvalidOperationException("Required native value is unavailable: console.")), Job = job, JobId = job.loadID };
            Contacts.Add(contact);
            if (!(pawn ?? throw new System.InvalidOperationException("Required native value is unavailable: pawn.")).jobs.TryTakeOrderedJob(job, JobTag.Misc)) { Contacts.Remove(contact); throw new InvalidOperationException("comms_job_refused"); }
            return new RimGovernor.Brain.GameFacts.Receipts.EffectEvidence { Effect = new RimGovernor.Brain.GameFacts.Receipts.CommsTradeRequestEffect {
                FactionId = intent.FactionId, TraderKind = intent.TraderKind,
                WorkQueued = true } };
        }
    }
}
