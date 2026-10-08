#nullable enable
using System;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    internal static class NativeQuestShuttleOperations
    {
        internal static bool LaunchConditionsSatisfied(bool nativeCanLaunch, bool allLoaded, bool blockedWeather, bool blockedCondition)
            => nativeCanLaunch && allLoaded && !blockedWeather && !blockedCondition;
        internal static bool ValidShape(Operations.QuestShuttleIntent? command) => command != null
            && command.HasQuestId && ProtoBoundary.IsIdentifier(command.QuestId)
            && (command.LoadingCase != Operations.QuestShuttleIntent.LoadingOneofCase.None || command.Launch)
            && (command.LoadingCase != Operations.QuestShuttleIntent.LoadingOneofCase.ExplicitPawns
                || command.ExplicitPawns.PawnIds.Count > 0 && command.ExplicitPawns.PawnIds.Count <= 256
                && command.ExplicitPawns.PawnIds.All(ProtoBoundary.IsIdentifier)
                && command.ExplicitPawns.PawnIds.Distinct().Count() == command.ExplicitPawns.PawnIds.Count);

        private static Common.Failure? Resolve(Operations.QuestShuttleIntent command, Common.ObservationContext context,
            out CompShuttle? shuttle, out Pawn[] pawns, out Command_Toggle? autoload, out Command_Action? send)
        {
            shuttle = null; pawns = Array.Empty<Pawn>(); autoload = null; send = null;
            if (!ValidShape(command)) return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "A quest shuttle requires an exact quest and loading mode or launch.");
            var quest = global::Verse.Find.QuestManager.QuestsListForReading.ById(command.QuestId);
            if (quest == null || quest.State != QuestState.Ongoing)
                return ProtoBoundary.Fail(Common.FailureCode.Unavailable, "The quest is not ongoing.");
            var map = ProtoBoundary.ResolveMap(context);
            if (map == null) return ProtoBoundary.Fail(Common.FailureCode.Unavailable, "A loaded map is required.");
            var matches = map.listerThings.AllThings.Select(t => t.TryGetComp<CompShuttle>())
                .Where(s => s != null && s.shipParent != null && quest.PartsListForReading.Any(p => p.QuestPartReserves(s.shipParent))).ToArray();
            if (matches.Length != 1) return ProtoBoundary.Fail(Common.FailureCode.Unavailable, "An exact single spawned quest shuttle is required.");
            shuttle = matches[0]!;
            if (!shuttle.ShowLoadingGizmos || shuttle.IsPlayerShuttle || shuttle.permitShuttle)
                return ProtoBoundary.Fail(Common.FailureCode.Unavailable, "This shuttle does not offer quest loading.");
            if (command.LoadingCase == Operations.QuestShuttleIntent.LoadingOneofCase.Autoload)
            {
                autoload = shuttle.CompGetGizmosExtra().OfType<Command_Toggle>().SingleOrDefault(g => g.defaultLabel == "CommandAutoloadTransporters".Translate().ToString());
                if (autoload == null || autoload.Disabled) return ProtoBoundary.Fail(Common.FailureCode.Unavailable, "Vanilla does not offer autoload for this shuttle.");
            }
            if (command.LoadingCase == Operations.QuestShuttleIntent.LoadingOneofCase.ExplicitPawns)
            {
                var selected = new System.Collections.Generic.List<Pawn>();
                foreach (var id in command.ExplicitPawns.PawnIds)
                {
                    if (shuttle.Transporter.innerContainer.OfType<Pawn>().Any(p => p.GetUniqueLoadID() == id)) continue;
                    var pawn = map.mapPawns.AllPawnsSpawned.ById(id);
                    if (pawn == null || !pawn.IsColonistPlayerControlled || pawn.Dead || pawn.Downed || pawn.Drafted || pawn.InMentalState
                        || !shuttle.IsAllowedNow(pawn) || !pawn.CanReach(shuttle.parent, PathEndMode.Touch, Danger.Deadly))
                        return ProtoBoundary.Fail(Common.FailureCode.Unavailable, "A selected pawn cannot enter this shuttle under vanilla rules.");
                    selected.Add(pawn);
                }
                pawns = selected.ToArray();
            }
            if (command.Launch)
            {
                if (!LaunchConditionsSatisfied(shuttle.CanLaunch.Accepted, shuttle.AllRequiredThingsLoaded,
                    map.weatherManager.CurWeatherPerceived.preventsShuttleLaunch,
                    map.GameConditionManager.ActiveConditions.Any(c => c.def.preventShuttleLaunch)))
                    return ProtoBoundary.Fail(Common.FailureCode.Unavailable, "The shuttle is not loaded or launch conditions prevent departure.");
                send = shuttle.shipParent.curJob?.GetJobGizmos()?.OfType<Command_Action>()
                    .SingleOrDefault(g => g.defaultLabel == "CommandSendShuttle".Translate().ToString());
                if (send == null || send.Disabled) return ProtoBoundary.Fail(Common.FailureCode.Unavailable, "Vanilla does not offer a manual send command.");
            }
            return null;
        }

        internal static Common.Failure? Validate(Operations.QuestShuttleIntent command, Common.ObservationContext context)
            => Resolve(command, context, out _, out _, out _, out _);

        internal static Receipts.EffectEvidence Apply(Operations.QuestShuttleIntent command, Common.ObservationContext context)
        {
            var failure = Resolve(command, context, out var shuttle, out var pawns, out var autoload, out var send);
            if (failure != null || shuttle == null) throw new InvalidOperationException("Quest shuttle prerequisites changed: " + failure?.Detail);
            if (autoload != null && shuttle.Autoload != command.Autoload) autoload.toggleAction();
            if (pawns.Length > 0)
            {
                if (!shuttle.Transporter.LoadingInProgressOrReadyToLaunch) TransporterUtility.InitiateLoading(Gen.YieldSingle(shuttle.Transporter));
                foreach (var pawn in pawns) pawn.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.EnterTransporter, shuttle.parent), JobTag.Misc);
            }
            send?.action();
            var effect = new Receipts.QuestShuttleEffect { QuestId = command.QuestId, ShuttleId = shuttle.parent.GetUniqueLoadID(),
                Autoload = shuttle.Autoload, Loading = shuttle.Transporter.LoadingInProgressOrReadyToLaunch, LaunchRequested = send != null };
            if (command.ExplicitPawns != null) effect.PawnIds.Add(command.ExplicitPawns.PawnIds);
            return new Receipts.EffectEvidence { QuestShuttle = effect };
        }
    }

    internal sealed class QuestShuttleActionHandler : IActionHandler
    {
        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context) => NativeQuestShuttleOperations.Validate(action.QuestShuttle, context);
        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context) => NativeQuestShuttleOperations.Apply(action.QuestShuttle, context);
    }
}
