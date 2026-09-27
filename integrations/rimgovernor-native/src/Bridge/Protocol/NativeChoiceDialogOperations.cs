#nullable enable
using System;
using Verse;
using Common = RimGovernor.Protocol.Common;
using Operations = RimGovernor.Protocol.Operations;
using Receipts = RimGovernor.Protocol.Receipts;

namespace HomeBridge.BridgeTools
{
    // DialogIntent (#941): activates one exact observed option of the single
    // force-pausing Verse.Dialog_NodeTree the game opened by itself (#156).
    // The exact window/index/label stand in for an entity precondition and
    // any drift is a refusal, never a different answer. An intent carrying a
    // joiner letter token goes to NativeJoinerLetters instead.
    internal sealed class DialogActionHandler : IActionHandler
    {
        private static Common.Failure? Resolve(Operations.DialogIntent? c, out Dialog_NodeTree dialog, out DiaOption option)
        {
            dialog = null!; option = null!;
            if (c == null || !c.HasWindowId || !c.HasOptionIndex || !c.HasOptionLabel)
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "A dialog answer needs a window, an option index and its label.");
            var pending = ChoiceDialogTools.Pending();
            if (pending == null || !ChoiceDialogTools.Matches(pending, c.WindowId, c.OptionIndex, c.OptionLabel, out var found))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, "Choice dialog or option changed; inspect again.");
            if (!ChoiceDialogTools.Answerable(pending, found))
                return ProtoBoundary.Fail(Common.FailureCode.InvalidRequest, ChoiceDialogTools.Unanswerable(pending, found));
            dialog = pending; option = found;
            return null;
        }

        public Common.Failure? Validate(Operations.Action action, Common.ObservationContext context) =>
            action.Dialog != null && action.Dialog.HasJoinerLetterToken ? NativeJoinerLetters.Validate(action.Dialog) : Resolve(action.Dialog, out _, out _);

        public Receipts.EffectEvidence Apply(Operations.Action action, Common.ObservationContext context)
        {
            var c = action.Dialog;
            if (c.HasJoinerLetterToken) return NativeJoinerLetters.Apply(c);
            var failure = Resolve(c, out var dialog, out var option);
            if (failure != null) throw new InvalidOperationException("Dialog prerequisites changed before apply: " + failure.Detail);
            var node = ChoiceDialogTools.Node(dialog);
            ChoiceDialogTools.Activate(option);
            var open = Find.WindowStack != null && Find.WindowStack.Windows.Contains(dialog);
            var advanced = open && !ReferenceEquals(ChoiceDialogTools.Node(dialog), node);
            if (open && !advanced) throw new InvalidOperationException("The activated option neither closed nor advanced the dialog.");
            return new Receipts.EffectEvidence { Dialog = new Receipts.DialogEffect { WindowId = c.WindowId, OptionIndex = c.OptionIndex,
                OptionLabel = c.OptionLabel, Activated = true, Closed = !open, Advanced = advanced } };
        }
    }
}
