#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using RimWorld;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Obs = RimGovernor.Protocol.Observations;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    internal sealed class NativeJoinerLetterRecord
    {
        internal readonly ChoiceLetter_AcceptJoiner Letter;
        internal readonly Pawn Pawn;
        internal readonly Operations.AnswerDialog Command;
        internal bool Activated;
        internal NativeJoinerLetterRecord(ChoiceLetter_AcceptJoiner letter, Pawn pawn, Operations.AnswerDialog command)
        { Letter = letter; Pawn = pawn; Command = command.Clone(); }
    }

    // Only the Core walk-in offer is supported: its signal admits exactly one
    // pawn immediately. Other AcceptJoiner quest families remain player choices.
    internal static class NativeJoinerLetters
    {
        private static Pawn? Joiner(ChoiceLetter_AcceptJoiner letter) => letter.quest?.PartsListForReading
            .OfType<QuestPart_PawnsArrive>().SelectMany(p => p.pawns).Distinct().SingleOrDefault();

        private static IEnumerable<ChoiceLetter_AcceptJoiner> Pending() => Find.LetterStack.LettersListForReading
            .OfType<ChoiceLetter_AcceptJoiner>().Where(l => l.quest?.root?.defName == "WandererJoins"
                && l.CanShowInLetterStack && !l.TimeoutPassed && l.MapToUse == Find.CurrentMap);

        private static string Token(ChoiceLetter_AcceptJoiner letter, Pawn pawn)
        {
            var text = string.Join("|", letter.GetUniqueLoadID(), letter.quest.GetUniqueLoadID(), letter.quest.State,
                pawn.GetUniqueLoadID(), pawn.Dead, pawn.Faction?.GetUniqueLoadID(), letter.MapToUse?.uniqueID,
                letter.disappearAtTick, letter.signalAccept, letter.signalReject,
                string.Join("|", letter.Choices.Take(2).Select(o => ChoiceDialogTools.Label(o) + ":" + o.disabled)));
            using (var hash = SHA256.Create())
                return "joiner-" + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
        }

        internal static List<Obs.JoinerLetter> Snapshot()
        {
            var rows = new List<Obs.JoinerLetter>();
            foreach (var letter in Pending().OrderBy(l => l.ID))
            {
                var pawn = Joiner(letter);
                if (pawn == null || pawn.Dead) continue;
                var accept = letter.Choices.First();
                rows.Add(new Obs.JoinerLetter { LetterId = letter.ID, SnapshotToken = Token(letter, pawn),
                    PawnId = pawn.GetUniqueLoadID(), ExpiresTick = letter.disappearAtTick,
                    AcceptLabel = ChoiceDialogTools.Label(accept), CanAccept = !accept.disabled && accept.action != null });
            }
            return rows;
        }

        private static bool Prepare(Operations.AnswerDialog command, out ChoiceLetter_AcceptJoiner letter, out Pawn pawn, out DiaOption option)
        {
            letter = null!; pawn = null!; option = null!;
            if (!command.HasWindowId || !command.HasOptionIndex || command.OptionIndex != 0
                || !command.HasOptionLabel || !command.HasJoinerLetterToken || string.IsNullOrEmpty(command.JoinerLetterToken)) return false;
            var found = Pending().SingleOrDefault(l => l.ID == command.WindowId);
            if (found == null) return false;
            var joiner = Joiner(found);
            if (joiner == null || joiner.Dead || joiner.Faction == Faction.OfPlayer || Token(found, joiner) != command.JoinerLetterToken) return false;
            var accept = found.Choices.First();
            if (accept.disabled || accept.action == null || ChoiceDialogTools.Label(accept) != command.OptionLabel) return false;
            letter = found; pawn = joiner; option = accept;
            return true;
        }

        internal static Operations.PreviewReply Preview(Operations.AnswerDialog command, Common.ObservationContext context)
        {
            if (!Prepare(command, out _, out _, out _)) return new Operations.PreviewReply {
                Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Joiner letter changed or expired; inspect again.") };
            return NativeOperationEnvelope.Preview(new Operations.PreviewReply { Evaluated = new Operations.PreviewEvaluation {
                Context = context.Clone(), Accepted = true } });
        }

        private static Receipts.EffectEvidence Evidence(NativeJoinerLetterRecord record) => new Receipts.EffectEvidence {
            Dialog = new Receipts.DialogEffect { WindowId = record.Command.WindowId, OptionIndex = 0,
                OptionLabel = record.Command.OptionLabel, JoinerLetterToken = record.Command.JoinerLetterToken,
                JoinerPawnId = record.Pawn.GetUniqueLoadID(), Activated = record.Activated,
                Closed = record.Letter.ArchivedOnly, Advanced = false,
                Joined = !record.Pawn.Dead && record.Pawn.Spawned && record.Pawn.Map == record.Letter.MapToUse && record.Pawn.IsFreeColonist }
        };

        internal static Operations.ExecuteReply Execute(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context)
        {
            NativeAttemptLedger.Admission? handle = null;
            Receipts.EffectEvidence? evidence = null;
            var command = request.Operation.AnswerDialog; var pre = request.Precondition;
            try
            {
                if (!Prepare(command, out var letter, out var pawn, out _)) return new Operations.ExecuteReply {
                    Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Joiner letter changed or expired; inspect again.") };
                if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null)
                    return new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.AuthorityRequired, "Native authority is required.") };
                var guard = authority.Check(pre.ExpectedGeneration);
                context.NativeGeneration = guard.Snapshot.Generation;
                if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) };
                var admitted = state.Ledger.Admit("rimgovernor.operations.v1.Operations/Execute", request, context);
                if (admitted.Kind != NativeAttemptLedger.DecisionKind.Admitted) return admitted.DecidedReply;
                handle = admitted.AdmittedHandle;
                var record = new NativeJoinerLetterRecord(letter, pawn, command);
                state.JoinerLetters.Add(pre.Attempt.Clone(), record);
                using (authority.Owned())
                {
                    if (!authority.Check(pre.ExpectedGeneration).Success || !Prepare(command, out var current, out var currentPawn, out var option)
                        || !ReferenceEquals(current, letter) || !ReferenceEquals(currentPawn, pawn))
                        throw new InvalidOperationException("Joiner letter admission changed before effect.");
                    // Letter choices have no owning Dialog_NodeTree until opened.
                    // Run the native button action, as the headless fixture does;
                    // DiaOption.Activate would dereference that absent window.
                    option.action();
                    record.Activated = true;
                    evidence = Evidence(record);
                    if (!evidence.Dialog.Closed || !evidence.Dialog.Joined) throw new InvalidOperationException("Joiner did not arrive.");
                }
                return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, evidence) };
            }
            catch (Exception error)
            {
                return handle == null ? new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Joiner letter validation failed: " + error.GetType().Name) }
                    : new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, evidence, "Joiner answer requires observation: " + error.GetType().Name) };
            }
        }

        internal static Receipts.Progress Observe(Common.AttemptKey attempt, Common.ObservationContext context, NativeJoinerLetterRecord record)
        {
            var evidence = Evidence(record);
            var result = new Receipts.Progress { Attempt = attempt.Clone(), Context = context.Clone(), CompleteInspection = true };
            if (record.Activated && evidence.Dialog.Closed && evidence.Dialog.Joined) result.Completed = new Receipts.CompletedEffect { Evidence = evidence };
            else result.Unsuccessful = new Receipts.UnsuccessfulEffect { Reason = Receipts.UnsuccessfulReason.OutcomeNotAchieved, Evidence = evidence,
                Detail = "The exact joiner is not a living spawned free colonist on the offered map." };
            return result;
        }
    }
}
