using System;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimGovernor.Native
{
    internal static class NativeQuestShuttleOperations
    {
        internal static bool LaunchConditionsSatisfied(bool nativeCanLaunch, bool allLoaded, bool blockedWeather, bool blockedCondition)
            => nativeCanLaunch && allLoaded && !blockedWeather && !blockedCondition;
        internal static bool ValidShape(RimGovernor.Brain.GameOrders.Operations.QuestShuttleIntent? command) => command != null
            && command.QuestId is not null && GameThread.IsIdentifier(command.QuestId)
            && (command.Loading is not null || command.Launch)
            && (command.Loading is not RimGovernor.Brain.GameOrders.Operations.QuestShuttleIntentLoading.ExplicitPawnsCase
                || command.Loading.ExplicitPawns is { PawnIds: var ids } && ids.Count > 0 && ids.Count <= 256
                && ids.All(GameThread.IsIdentifier)
                && ids.Distinct().Count() == ids.Count);

        private static RimGovernor.Brain.GameFacts.Common.Failure? Resolve(RimGovernor.Brain.GameOrders.Operations.QuestShuttleIntent? command, RimGovernor.Brain.GameFacts.Common.ObservationContext context,
            out CompShuttle? shuttle, out Pawn[] pawns, out Command_Toggle? autoload, out Command_Action? send)
        {
            shuttle = null; pawns = Array.Empty<Pawn>(); autoload = null; send = null;
            if (!ValidShape(command) || command == null) return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "A quest shuttle requires an exact quest and loading mode or launch.");
            var quest = global::Verse.Find.QuestManager.QuestsListForReading.ById(command.QuestId);
            if (quest == null || quest.State != QuestState.Ongoing)
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.Unavailable, "The quest is not ongoing.");
            var map = GameThread.ResolveMap(context);
            if (map == null) return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.Unavailable, "A loaded map is required.");
            var matches = map.listerThings.AllThings.Select(t => t.TryGetComp<CompShuttle>())
                .Where(s => s != null && s.shipParent != null && quest.PartsListForReading.Any(p => p.QuestPartReserves(s.shipParent))).ToArray();
            if (matches.Length != 1) return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.Unavailable, "An exact single spawned quest shuttle is required.");
            shuttle = matches[0];
            if (!shuttle.ShowLoadingGizmos || shuttle.IsPlayerShuttle || shuttle.permitShuttle)
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.Unavailable, "This shuttle does not offer quest loading.");
            if (command.Loading is RimGovernor.Brain.GameOrders.Operations.QuestShuttleIntentLoading.AutoloadCase)
            {
                autoload = shuttle.CompGetGizmosExtra().OfType<Command_Toggle>().SingleOrDefault(g => g.defaultLabel == "CommandAutoloadTransporters".Translate().ToString());
                if (autoload == null || autoload.Disabled) return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.Unavailable, "Vanilla does not offer autoload for this shuttle.");
            }
            if (command.Loading is RimGovernor.Brain.GameOrders.Operations.QuestShuttleIntentLoading.ExplicitPawnsCase)
            {
                var selected = new System.Collections.Generic.List<Pawn>();
                foreach (var id in command.Loading?.ExplicitPawns?.PawnIds ?? default)
                {
                    if (shuttle.Transporter.innerContainer.OfType<Pawn>().Any(p => p.GetUniqueLoadID() == id)) continue;
                    var pawn = map.mapPawns.AllPawnsSpawned.ById(id);
                    if (pawn == null || !pawn.IsColonistPlayerControlled || pawn.Dead || pawn.Downed || pawn.Drafted || pawn.InMentalState
                        || !shuttle.IsAllowedNow(pawn) || !pawn.CanReach(shuttle.parent, PathEndMode.Touch, Danger.Deadly))
                        return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.Unavailable, "A selected pawn cannot enter this shuttle under vanilla rules.");
                    selected.Add(pawn);
                }
                pawns = selected.ToArray();
            }
            if (command.Launch)
            {
                if (!LaunchConditionsSatisfied(shuttle.CanLaunch.Accepted, shuttle.AllRequiredThingsLoaded,
                    map.weatherManager.CurWeatherPerceived.preventsShuttleLaunch,
                    map.GameConditionManager.ActiveConditions.Any(c => c.def.preventShuttleLaunch)))
                    return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.Unavailable, "The shuttle is not loaded or launch conditions prevent departure.");
                send = shuttle.shipParent.curJob?.GetJobGizmos()?.OfType<Command_Action>()
                    .SingleOrDefault(g => g.defaultLabel == "CommandSendShuttle".Translate().ToString());
                if (send == null || send.Disabled) return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.Unavailable, "Vanilla does not offer a manual send command.");
            }
            return null;
        }

        internal static RimGovernor.Brain.GameFacts.Common.Failure? Validate(RimGovernor.Brain.GameOrders.Operations.QuestShuttleIntent? command, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
            => Resolve(command, context, out _, out _, out _, out _);

        internal static RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Apply(RimGovernor.Brain.GameOrders.Operations.QuestShuttleIntent? command, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var failure = Resolve(command, context, out var shuttle, out var pawns, out var autoload, out var send);
            if (failure != null || shuttle == null || command == null) throw new InvalidOperationException("Quest shuttle prerequisites changed: " + (failure?.Detail ?? ""));
            if (autoload != null && command.Loading is RimGovernor.Brain.GameOrders.Operations.QuestShuttleIntentLoading.AutoloadCase { Value: var requested } && shuttle.Autoload != requested) autoload.toggleAction();
            if (pawns.Length > 0)
            {
                if (!shuttle.Transporter.LoadingInProgressOrReadyToLaunch) TransporterUtility.InitiateLoading(Gen.YieldSingle(shuttle.Transporter));
                foreach (var pawn in pawns) pawn.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.EnterTransporter, shuttle.parent), JobTag.Misc);
            }
            send?.action();
            var effect = new RimGovernor.Brain.GameFacts.Receipts.QuestShuttleEffect { QuestId = command.QuestId, Autoload = shuttle.Autoload };
            if (command.Loading?.ExplicitPawns is { } explicitPawns) effect = effect with { PawnIds = [.. effect.PawnIds, .. explicitPawns.PawnIds] };
            return new RimGovernor.Brain.GameFacts.Receipts.EffectEvidence { Effect = effect };
        }
    }

    internal sealed class QuestShuttleActionHandler : IActionHandler
    {
        public RimGovernor.Brain.GameFacts.Common.Failure? Validate(RimGovernor.Brain.GameOrders.Operations.Action action, RimGovernor.Brain.GameFacts.Common.ObservationContext context) => NativeQuestShuttleOperations.Validate(action.Intent?.QuestShuttle, context);
        public RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Apply(RimGovernor.Brain.GameOrders.Operations.Action action, RimGovernor.Brain.GameFacts.Common.ObservationContext context) => NativeQuestShuttleOperations.Apply(action.Intent?.QuestShuttle, context);
    }
}
