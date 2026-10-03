#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI.Group;
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // The RitualIntent arm of Actions/Apply (#1639, epic #1598): the player
    // command that starts a ritual whose lord waits for it. "bestowing"/"start"
    // is the Empire bestowing ceremony: once the bestower arrives he waits in
    // LordToil_BestowingCeremony_Wait, whose GetPawnGizmos offers
    // Command_BestowerCeremony; the game starts the ritual only through that
    // gizmo's action (the toil's private StartRitual). Native takes the gizmo
    // the toil itself offers, so every precondition the game applies (the
    // throne room stands) is the game's own, and runs its action with no
    // spectators chosen. An immediate write with no native job; a ritual
    // already started applies again.
    internal static class NativeRitual
    {
        private const string Bestowing = "bestowing";
        private const string Start = "start";

        private sealed class Target
        {
            public Pawn Bestower = null!;
            public Lord Lord = null!;
            public LordJob_BestowingCeremony Job = null!;
            public Command_BestowerCeremony? Command;
            public bool Started;
        }

        // The bestower lord of the colonist's current bestowing-ceremony quest.
        private static Target? Find(Pawn pawn)
        {
            foreach (var faction in Verse.Find.FactionManager.AllFactionsListForReading.Where(f => f?.def != null && f.def.HasRoyalTitles))
            {
                var quest = RoyalTitleUtility.GetCurrentBestowingCeremonyQuest(pawn, faction);
                if (quest == null || quest.State != QuestState.Ongoing) continue;
                var bestower = quest.PartsListForReading.OfType<QuestPart_BestowingCeremony>().FirstOrDefault()?.bestower;
                var lord = bestower?.GetLord();
                if (bestower == null || lord == null || !(lord.LordJob is LordJob_BestowingCeremony job)) continue;
                return new Target { Bestower = bestower, Lord = lord, Job = job, Started = job.ceremonyStarted || !(lord.CurLordToil is LordToil_BestowingCeremony_Wait) };
            }
            return null;
        }

        private static Common.Failure? Resolve(Operations.RitualIntent? command, out Pawn? pawn, out Target? target)
        {
            pawn = null; target = null;
            if (command == null || !command.HasPawnId || !ProtoBoundary.IsIdentifier(command.PawnId) || !command.HasRitual || !command.HasVerb)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "A ritual command requires a pawn, a ritual and a verb.");
            if (command.Ritual != Bestowing)
                return ProtoBoundary.Fail(Common.FailureCode.Unsupported, "Unsupported ritual.");
            if (command.Verb != Start)
                return ProtoBoundary.Fail(Common.FailureCode.Unsupported, "Unsupported ritual verb.");
            pawn = PawnsFinder.AllMaps_FreeColonists.ById(command.PawnId);
            if (pawn == null || pawn.royalty == null)
                return ProtoBoundary.Fail(Common.FailureCode.NotFound, "Exact colonist with royalty is unavailable.");
            target = Find(pawn);
            if (target == null)
                return ProtoBoundary.Fail(Common.FailureCode.NotFound, "No accepted bestowing ceremony with a bestower lord for this colonist.");
            if (target.Started) return null;
            if (!target.Bestower.Spawned || !(target.Lord.CurLordToil is LordToil_BestowingCeremony_Wait toil))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "The bestower is not waiting for the ceremony command.");
            target.Command = toil.GetPawnGizmos(target.Bestower)?.OfType<Command_BestowerCeremony>().FirstOrDefault();
            if (target.Command == null)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "The game offers no ceremony command; the throne room must stand.");
            if (target.Command.Disabled)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, target.Command.disabledReason ?? "The game disables the ceremony command.");
            return null;
        }

        private static Receipts.EffectEvidence Evidence(Operations.RitualIntent command, Target target) => new Receipts.EffectEvidence
        {
            Ritual = new Receipts.RitualEffect { PawnId = command.PawnId, Ritual = command.Ritual, Verb = command.Verb, Started = target.Job.ceremonyStarted || !(target.Lord.CurLordToil is LordToil_BestowingCeremony_Wait) }
        };

        internal static Common.Failure? Validate(Operations.RitualIntent command) => Resolve(command, out _, out _);

        internal static Receipts.EffectEvidence Apply(Operations.RitualIntent command)
        {
            var failure = Resolve(command, out _, out var target);
            if (failure != null || target == null) throw new InvalidOperationException("Ritual start prerequisites changed before apply: " + failure?.Detail);
            if (target.Started || target.Command == null) return Evidence(command, target);
            // The gizmo's action is the toil's StartRitual (private); it takes
            // the participating colonists, none chosen here.
            var action = Traverse.Create(target.Command).Field("action").GetValue<Action<List<Pawn>>>();
            if (action == null) throw new InvalidOperationException("The ceremony command carries no action.");
            action(new List<Pawn>());
            var evidence = Evidence(command, target);
            if (!evidence.Ritual.Started) throw new InvalidOperationException("Native ritual start readback did not apply.");
            return evidence;
        }
    }

    internal sealed class RitualActionHandler : IActionHandler
    {
        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context) => NativeRitual.Validate(action.Ritual);
        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context) => NativeRitual.Apply(action.Ritual);
    }
}
