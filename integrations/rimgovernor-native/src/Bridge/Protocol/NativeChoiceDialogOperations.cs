#nullable enable
using System;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // Tracks an admitted AnswerDialog attempt so a later ObserveProgress poll
    // re-verifies it from live native state. The dialog and the node it showed
    // when the option was activated are held directly, like every other
    // Native*Record holds live references.
    internal sealed class NativeDialogRecord
    {
        internal readonly Dialog_NodeTree Dialog;
        internal readonly DiaNode? Node;
        internal readonly int WindowId;
        internal readonly int OptionIndex;
        internal readonly string OptionLabel;
        internal bool Activated;
        internal NativeDialogRecord(Dialog_NodeTree dialog, DiaNode? node, int windowId, int optionIndex, string optionLabel)
        { Dialog = dialog; Node = node; WindowId = windowId; OptionIndex = optionIndex; OptionLabel = optionLabel; }
    }

    // Typed AnswerDialog: activates one exact observed option of the single
    // force-pausing Verse.Dialog_NodeTree the game opened by itself (#156),
    // through the same attempt/lease/ledger machinery every other
    // rimgovernor/operations_execute command requires. It is the autopilot
    // counterpart of ConfirmColonyNames for the game's own choice dialogs:
    // the exact window/index/label stand in for an entity precondition and
    // any drift is a refusal, never a different answer.
    internal static class NativeChoiceDialogOperations
    {
        private static bool PrepareStale(Operations.AnswerDialog? command, out Dialog_NodeTree dialog, out DiaOption option)
        {
            dialog = null!; option = null!;
            if (command == null || !command.HasWindowId || !command.HasOptionIndex || !command.HasOptionLabel) return false;
            var pending = ChoiceDialogTools.Pending();
            if (pending == null || !ChoiceDialogTools.Matches(pending, command.WindowId, command.OptionIndex, command.OptionLabel, out var found)) return false;
            dialog = pending; option = found;
            return true;
        }

        internal static Operations.PreviewReply Preview(Operations.AnswerDialog command, Common.ObservationContext context)
        {
            try
            {
                if (command.HasJoinerLetterToken) return NativeJoinerLetters.Preview(command, context);
                if (!PrepareStale(command, out var dialog, out var option))
                    return new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest,
                        "Choice dialog or option changed; inspect again.") };
                var accepted = ChoiceDialogTools.Answerable(dialog, option);
                return NativeOperationEnvelope.Preview(new Operations.PreviewReply { Evaluated = new Operations.PreviewEvaluation {
                    Context = context.Clone(), Accepted = accepted,
                    Reason = accepted ? "" : ChoiceDialogTools.Unanswerable(dialog, option) } });
            }
            catch (Exception error) { return new Operations.PreviewReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Dialog preview failed: " + error.GetType().Name) }; }
        }

        private static Receipts.EffectEvidence Evidence(NativeDialogRecord record)
        {
            var open = Find.WindowStack != null && Find.WindowStack.Windows.Contains(record.Dialog);
            var advanced = open && !ReferenceEquals(ChoiceDialogTools.Node(record.Dialog), record.Node);
            return new Receipts.EffectEvidence { Dialog = new Receipts.DialogEffect { WindowId = record.WindowId, OptionIndex = record.OptionIndex,
                OptionLabel = record.OptionLabel, Activated = record.Activated, Closed = !open, Advanced = advanced } };
        }

        internal static Operations.ExecuteReply Execute(NativeOperationState state, Operations.ExecuteRequest request, Common.ObservationContext context)
        {
            NativeAttemptLedger.Admission? handle = null; Receipts.EffectEvidence? evidence = null;
            var pre = request.Precondition; var command = request.Operation.AnswerDialog;
            try
            {
                if (command.HasJoinerLetterToken) return NativeJoinerLetters.Execute(state, request, context);
                if (!PrepareStale(command, out var dialog, out var option))
                    return new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest,
                        "Choice dialog or option changed; inspect again.") };
                if (!ChoiceDialogTools.Answerable(dialog, option))
                    return new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, ChoiceDialogTools.Unanswerable(dialog, option)) };
                if (!NativeControlAuthority.TryGetForGame(Current.Game, out var authority) || authority == null)
                    return new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.AuthorityRequired, "Native authority is required.") };
                var guard = authority.Check(pre.ExpectedGeneration);
                context.NativeGeneration = guard.Snapshot.Generation;
                if (!guard.Success) return new Operations.ExecuteReply { Failure = NativeAuthorityControlTools.Refusal(guard.Error, context) };
                var admitted = state.Ledger.Admit("rimgovernor.operations.v1.Operations/Execute", request, context);
                if (admitted.Kind != NativeAttemptLedger.DecisionKind.Admitted) return admitted.DecidedReply;
                handle = admitted.AdmittedHandle;
                var record = new NativeDialogRecord(dialog, ChoiceDialogTools.Node(dialog), command.WindowId, command.OptionIndex, command.OptionLabel);
                state.Dialogs.Add(pre.Attempt.Clone(), record);
                using (authority.Owned())
                {
                    if (!authority.Check(pre.ExpectedGeneration).Success
                        || !PrepareStale(command, out var checkedDialog, out var checkedOption) || !ReferenceEquals(checkedDialog, dialog)
                        || !ReferenceEquals(checkedOption, option) || !ChoiceDialogTools.Answerable(checkedDialog, checkedOption))
                        throw new InvalidOperationException("Dialog admission changed before effect.");
                    ChoiceDialogTools.Activate(checkedOption);
                    record.Activated = true;
                    evidence = Evidence(record);
                    if (!evidence.Dialog.Closed && !evidence.Dialog.Advanced)
                        throw new InvalidOperationException("The activated option neither closed nor advanced the dialog.");
                }
                return new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Applied(state.Ledger, handle, pre.Attempt, context, evidence) };
            }
            catch (Exception error)
            {
                return handle == null
                    ? new Operations.ExecuteReply { Failure = ProtoBoundary.Fail(Common.FailureCode.NativeFailure, "Dialog admission failed: " + error.GetType().Name) }
                    : new Operations.ExecuteReply { Receipt = NativeOperationEnvelope.Uncertain(state.Ledger, handle, pre.Attempt, context, evidence, "Admitted dialog answer requires observation: " + error.GetType().Name) };
            }
        }

        internal static Receipts.Progress Observe(Common.AttemptKey attempt, Common.ObservationContext context, NativeDialogRecord record)
        {
            var result = new Receipts.Progress { Attempt = attempt.Clone(), Context = context.Clone(), CompleteInspection = false,
                Unknown = new Receipts.UnknownEffect { Reason = "Exact dialog state is unavailable." } };
            try
            {
                var evidence = Evidence(record);
                result.CompleteInspection = true;
                if (record.Activated && (evidence.Dialog.Closed || evidence.Dialog.Advanced)) result.Completed = new Receipts.CompletedEffect { Evidence = evidence };
                else result.Unsuccessful = new Receipts.UnsuccessfulEffect { Reason = Receipts.UnsuccessfulReason.OutcomeNotAchieved, Evidence = evidence,
                    Detail = record.Activated ? "The dialog still shows the answered node." : "The option was never activated." };
            }
            catch (Exception) { }
            return result;
        }
    }
}
