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

        internal static Common.Failure? Validate(Operations.RitualIntent command) =>
            command.HasVerb && command.Verb == NativeRitualBegin.Begin ? NativeRitualBegin.Validate(command) : Resolve(command, out _, out _);

        internal static Receipts.EffectEvidence Apply(Operations.RitualIntent command)
        {
            if (command.HasVerb && command.Verb == NativeRitualBegin.Begin) return NativeRitualBegin.Apply(command);
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

    // The "begin" verb of the RitualIntent (#1659, epic #1653): starts a held
    // ritual precept with no lord waiting, the way the game's begin-ritual
    // dialog does. ritual is the Precept_Ritual load id the ideology read
    // lists, pawn_id the organizer and spot the target cell. Native takes the
    // Command_Ritual the precept itself offers at the target (so the obligation
    // it serves, its forced roles and every precondition the game applies are
    // the game's own), builds the dialog the command would open, replaces the
    // dialog's auto-filled assignments with exactly the intent's roles and
    // spectators, applies the dialog's own blocking-issue check and runs the
    // dialog's confirm action. Nothing is auto-filled: a slot the intent
    // leaves empty is empty, and the game's blocking issues refuse a required
    // one. A ritual of the precept already running applies again.
    internal static class NativeRitualBegin
    {
        internal const string Begin = "begin";

        private sealed class Plan
        {
            public Pawn Organizer = null!;
            public Precept_Ritual Ritual = null!;
            public bool Running;
            public Dialog_BeginRitual? Dialog;
            public RitualRoleAssignments? Assignments;
        }

        private static bool Running(Precept_Ritual ritual) =>
            Verse.Find.Maps.Any(map => map.lordManager.lords.Any(lord => lord.LordJob is LordJob_Ritual job && job.Ritual == ritual));

        private static Common.Failure Invalid(string detail) => ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, detail);
        private static Common.Failure Missing(string detail) => ProtoBoundary.Fail(Common.FailureCode.NotFound, detail);

        private static Common.Failure? Resolve(Operations.RitualIntent command, out Plan? plan)
        {
            plan = null;
            if (!command.HasPawnId || !ProtoBoundary.IsIdentifier(command.PawnId) || !command.HasRitual || !ProtoBoundary.IsIdentifier(command.Ritual) || command.Spot == null || !command.Spot.HasX || !command.Spot.HasZ)
                return Invalid("A ritual begin requires an organizer, a ritual precept and a spot.");
            var organizer = PawnsFinder.AllMaps_FreeColonists.ById(command.PawnId);
            if (organizer == null) return Missing("Exact colonist organizer is unavailable.");
            var ideo = Faction.OfPlayerSilentFail?.ideos?.PrimaryIdeo;
            if (ideo == null) return Missing("The player faction has no ideoligion.");
            var ritual = ideo.PreceptsListForReading.OfType<Precept_Ritual>().FirstOrDefault(p => p.GetUniqueLoadID() == command.Ritual);
            if (ritual == null) return Missing("The ideoligion holds no ritual precept with that id.");
            var found = new Plan { Organizer = organizer, Ritual = ritual, Running = Running(ritual) };
            plan = found;
            if (found.Running) return null;
            var map = organizer.MapHeld;
            if (!organizer.Spawned || map == null) return Invalid("The organizer is not on a map.");
            var cell = new IntVec3(command.Spot.X, 0, command.Spot.Z);
            if (!cell.InBounds(map)) return Invalid("The spot is outside the organizer's map.");

            // The target the precept's own gizmo is offered at: a thing on the
            // spot, else the cell itself.
            var targets = map.thingGrid.ThingsListAt(cell).Select(thing => new TargetInfo(thing)).Concat(new[] { new TargetInfo(cell, map) });
            TargetInfo target = TargetInfo.Invalid;
            List<Command_Ritual> commands = new List<Command_Ritual>();
            foreach (var candidate in targets)
            {
                commands = ritual.GetGizmoFor(candidate)?.OfType<Command_Ritual>().ToList() ?? new List<Command_Ritual>();
                if (commands.Count == 0) continue;
                target = candidate;
                break;
            }
            if (commands.Count == 0) return Missing("The game offers no begin command for this ritual at the spot (no obligation or anytime start, or no valid target there).");
            Command_Ritual? offered = null;
            foreach (var each in commands)
            {
                Traverse.Create(each).Method("ValidateDisabledState").GetValue();
                if (!each.Disabled) { offered = each; break; }
            }
            if (offered == null) return Invalid(commands[0].disabledReason ?? "The game disables the ritual command.");
            var forcedForRole = Traverse.Create(offered).Field("forcedForRole").GetValue<Dictionary<string, Pawn>>();
            var notNow = ritual.behavior.CanStartRitualNow(target, ritual, organizer, forcedForRole);
            if (!string.IsNullOrEmpty(notNow)) return Invalid(notNow);

            var obligation = Traverse.Create(offered).Field("obligation").GetValue<RitualObligation>();
            var dialog = ritual.GetRitualBeginWindow(target, obligation, null, organizer, forcedForRole, organizer) as Dialog_BeginRitual;
            if (dialog == null) return Invalid("The game builds no begin dialog for this ritual.");
            var offeredAssignments = Traverse.Create(dialog).Field("assignments").GetValue<RitualRoleAssignments>();
            if (offeredAssignments == null) return Invalid("The begin dialog carries no assignments.");
            var assignments = new RitualRoleAssignments(ritual, target);
            assignments.Setup(offeredAssignments.AllCandidatePawns, offeredAssignments.NonAssignablePawns, offeredAssignments.ForcedRolesForReading, offeredAssignments.ExtraRequiredPawnsForReading, organizer);
            foreach (var slot in command.Roles)
            {
                if (slot == null || !slot.HasSlot) return Invalid("A role assignment requires a slot.");
                var role = assignments.GetRole(slot.Slot);
                if (role == null) return Missing("The ritual has no role slot " + slot.Slot + ".");
                foreach (var id in slot.PawnIds)
                {
                    var pawn = assignments.AllCandidatePawns.ById(id);
                    if (pawn == null) return Missing("Pawn " + id + " is not a candidate for this ritual.");
                    if (assignments.ForcedRole(pawn)?.id == slot.Slot) continue;
                    var why = assignments.PawnNotAssignableReason(pawn, role);
                    if (!string.IsNullOrEmpty(why)) return Invalid(why);
                    if (!assignments.TryAssign(pawn, role, out var reason, PsychicRitualRoleDef.Context.Runtime, null))
                        return Invalid(reason.ToPlayerReadable().ToString());
                }
            }
            foreach (var id in command.SpectatorPawnIds)
            {
                var pawn = assignments.AllCandidatePawns.ById(id);
                if (pawn == null) return Missing("Pawn " + id + " is not a candidate for this ritual.");
                if (!assignments.CanEverSpectate(pawn) || !assignments.TryAssignSpectate(pawn, null))
                    return Invalid("Pawn " + id + " cannot attend this ritual as a spectator.");
            }
            Traverse.Create(dialog).Field("assignments").SetValue(assignments);
            var issue = Traverse.Create(dialog).Method("BlockingIssues").GetValue<IEnumerable<string>>()?.FirstOrDefault();
            if (!string.IsNullOrEmpty(issue)) return Invalid(issue!);
            found.Dialog = dialog;
            found.Assignments = assignments;
            return null;
        }

        private static Receipts.EffectEvidence Evidence(Operations.RitualIntent command, Plan plan) => new Receipts.EffectEvidence
        {
            Ritual = new Receipts.RitualEffect { PawnId = command.PawnId, Ritual = command.Ritual, Verb = command.Verb, Started = Running(plan.Ritual) }
        };

        internal static Common.Failure? Validate(Operations.RitualIntent command) => Resolve(command, out _);

        internal static Receipts.EffectEvidence Apply(Operations.RitualIntent command)
        {
            var failure = Resolve(command, out var plan);
            if (failure != null || plan == null) throw new InvalidOperationException("Ritual begin prerequisites changed before apply: " + failure?.Detail);
            if (plan.Running) return Evidence(command, plan);
            var action = Traverse.Create(plan.Dialog).Field("action").GetValue<Dialog_BeginRitual.ActionCallback>();
            if (action == null || plan.Assignments == null) throw new InvalidOperationException("The begin dialog carries no confirm action.");
            if (!action(plan.Assignments)) throw new InvalidOperationException("The game declined to begin the ritual.");
            var evidence = Evidence(command, plan);
            if (!evidence.Ritual.Started) throw new InvalidOperationException("Native ritual begin readback did not apply.");
            return evidence;
        }
    }

    internal sealed class RitualActionHandler : IActionHandler
    {
        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context) => NativeRitual.Validate(action.Ritual);
        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context) => NativeRitual.Apply(action.Ritual);
    }
}
