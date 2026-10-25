using System;
using Verse;

namespace RimGovernor.Native
{
    // DialogIntent: activates one exact observed option of the single
    // force-pausing Verse.Dialog_NodeTree the game opened by itself, or
    // the void awakening confirmation box.
    // The exact window/index/label stand in for an entity precondition and
    // any drift is a refusal, never a different answer. An intent carrying a
    // joiner letter token goes to NativeJoinerLetters instead.
    internal sealed class DialogActionHandler : IActionHandler
    {
        private static RimGovernor.Brain.GameFacts.Common.Failure? Resolve(RimGovernor.Brain.GameOrders.Operations.DialogIntent? c, out Window? dialog, out DiaOption? option)
        {
            dialog = null; option = null;
            if (c == null || c.OptionLabel is null)
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "A dialog answer needs a window, an option index and its label.");
            var pending = ChoiceDialogTools.Pending();
            if (pending == null || !ChoiceDialogTools.Matches(pending, c.WindowId, c.OptionIndex, (c.OptionLabel ?? ""), out var found))
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, "Choice dialog or option changed; inspect again.");
            if (!ChoiceDialogTools.Answerable(pending, found))
                return GameThread.Fail(RimGovernor.Brain.GameFacts.Common.FailureCode.InvalidRequest, ChoiceDialogTools.Unanswerable(pending, found));
            dialog = pending; option = found;
            return null;
        }

        public RimGovernor.Brain.GameFacts.Common.Failure? Validate(RimGovernor.Brain.GameOrders.Operations.Action action, RimGovernor.Brain.GameFacts.Common.ObservationContext context) =>
            action.Intent?.Dialog is { JoinerLetterToken: not null } joiner ? NativeJoinerLetters.Validate(joiner) : Resolve(action.Intent?.Dialog, out _, out _);

        public RimGovernor.Brain.GameFacts.Receipts.EffectEvidence Apply(RimGovernor.Brain.GameOrders.Operations.Action action, RimGovernor.Brain.GameFacts.Common.ObservationContext context)
        {
            var c = action.Intent?.Dialog ?? throw new InvalidOperationException("Dialog intent missing");
            if (c.JoinerLetterToken is not null) return NativeJoinerLetters.Apply(c);
            var failure = Resolve(c, out var dialog, out var option);
            if (failure != null || dialog == null || option == null) throw new InvalidOperationException("Dialog prerequisites changed before apply: " + (failure?.Detail ?? ""));
            var node = ChoiceDialogTools.Node(dialog);
            ChoiceDialogTools.Activate(dialog, option);
            var open = Find.WindowStack != null && Find.WindowStack.Windows.Contains(dialog);
            var advanced = open && !ReferenceEquals(ChoiceDialogTools.Node(dialog), node);
            if (open && !advanced) throw new InvalidOperationException("The activated option neither closed nor advanced the dialog.");
            return new RimGovernor.Brain.GameFacts.Receipts.EffectEvidence { Effect = new RimGovernor.Brain.GameFacts.Receipts.EffectEvidenceEffect.DialogCase() };
        }
    }
}
