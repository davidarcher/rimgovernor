#nullable enable

using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Verse;
using Obs = RimGovernor.Protocol.Observations;

namespace HomeBridge.BridgeTools
{
    // The game opens a force-pausing Verse.Dialog_NodeTree by itself from a
    // handful of places (decompiled 1.6): IncidentWorker_CaravanDemand and
    // IncidentWorker_CaravanMeeting (a player caravan on the world map),
    // QuestPart_Dialog (a quest's own dialog signal), ResearchManager's
    // finished-project completion dialog, ScenPart_GameStartDialog and
    // GenGameEnd. Every one of them holds TickManager.ForcePaused until an
    // option is chosen, and headless play has no UI to choose with (#156).
    // This is the exact native lookup the colony facts census, the clock's
    // dialog_pause stop and Operations.AnswerDialog share. The one other window
    // it answers is the void awakening confirmation (#2437), a force-pausing
    // Dialog_MessageBox that ActivateMonolith opens: its two buttons read as
    // options 0 (Confirm) and 1 (GoBack) so DialogIntent drives it alike.
    internal static class ChoiceDialogTools
    {
        internal static Window? Pending()
        {
            var windows = Find.WindowStack?.Windows.Where(w => w != null && w.forcePause).ToList();
            if (windows == null || windows.Count != 1) return null;
            var window = windows[0];
            return window is Dialog_NodeTree || window is Dialog_MessageBox box && IsAwakeningConfirmation(box) ? window : null;
        }

        private static bool IsAwakeningConfirmation(Dialog_MessageBox box) =>
            box.text.RawText == "VoidAwakeningConfirmationText".Translate().RawText;

        internal static DiaNode? Node(Window dialog) =>
            dialog is Dialog_NodeTree ? AccessTools.Field(typeof(Dialog_NodeTree), "curNode")?.GetValue(dialog) as DiaNode : null;

        internal static string? Title(Window dialog) =>
            dialog is Dialog_MessageBox box ? box.title
            : AccessTools.Field(typeof(Dialog_NodeTree), "title")?.GetValue(dialog) as string;

        internal static string Label(DiaOption option) =>
            (AccessTools.Field(typeof(DiaOption), "text")?.GetValue(option) as string) ?? "";

        internal static bool Interactive(Window dialog)
        {
            if (dialog is Dialog_MessageBox)
                return AccessTools.PropertyGetter(typeof(Dialog_MessageBox), "InteractionDelayExpired")?.Invoke(dialog, null) is true;
            var at = AccessTools.Field(typeof(Dialog_NodeTree), "makeInteractiveAtTime")?.GetValue(dialog);
            return !(at is float) || UnityEngine.Time.realtimeSinceStartup >= (float)at;
        }

        // An option the controller can answer with: enabled, not a hyperlink,
        // and one that either closes the tree or links to a further node. An
        // option doing neither runs an action that opens another window
        // (CaravanMeeting's Trade opens Dialog_Trade) which the controller does
        // not drive, so it stays with the player (#179).
        internal static bool Selectable(DiaOption option) =>
            !option.disabled && option.hyperlink.def == null && (option.resolveTree || option.link != null || option.linkLateBind != null);

        // Selectable, on a dialog whose delayInteractivity grace has passed:
        // the same gate the option's own button applies (OptOnGUI's active).
        internal static bool Answerable(Window dialog, DiaOption option) => Interactive(dialog) && Selectable(option);

        internal static string Unanswerable(Window dialog, DiaOption option) =>
            !Interactive(dialog) ? "The dialog is not interactive yet." : "The option is disabled, a hyperlink or opens another window and cannot answer the dialog.";

        // A message box's buttons A and B as options that run the button's
        // action then close the window, as its own button handler does.
        internal static List<DiaOption> Options(Window dialog)
        {
            if (dialog is not Dialog_MessageBox box) return (dialog is Dialog_NodeTree ? Node(dialog)?.options : null) ?? new List<DiaOption>();
            var options = new List<DiaOption> { Button(box, box.buttonAText, box.buttonAAction) };
            if (box.buttonBText != null) options.Add(Button(box, box.buttonBText, box.buttonBAction));
            return options;
        }

        private static DiaOption Button(Dialog_MessageBox box, string text, System.Action? act) =>
            new DiaOption(text) { resolveTree = true, action = () => { act?.Invoke(); box.Close(); } };

        internal static Obs.ChoiceDialog Snapshot(Window dialog)
        {
            var node = Node(dialog);
            var body = dialog is Dialog_MessageBox box ? box.text.RawText : node == null ? "" : node.text.RawText;
            var result = new Obs.ChoiceDialog { WindowId = dialog.ID, WindowType = dialog.GetType().FullName,
                Title = Text(Title(dialog)), Text = Text(body), Interactive = Interactive(dialog) };
            var options = Options(dialog);
            for (var i = 0; i < options.Count; i++)
            {
                var option = options[i];
                var label = Text(Label(option));
                var row = new Obs.ChoiceDialogOption { Index = i, Label = label, Selectable = Selectable(option), Resolves = option.resolveTree };
                if (option.disabled && !string.IsNullOrEmpty(option.disabledReason)) row.DisabledReason = Text(option.disabledReason);
                row.Keys.AddRange(ChoiceDialogKeys.ForLabel(label.Trim()));
                result.Options.Add(row);
            }
            return result;
        }

        // The same exact-drift test AnswerDialog's preview and execute apply:
        // the observed window, option position and label must all still match.
        internal static bool Matches(Window dialog, int windowId, int optionIndex, string label, out DiaOption option)
        {
            option = null!;
            if (dialog.ID != windowId || optionIndex < 0) return false;
            var options = Options(dialog);
            if (optionIndex >= options.Count) return false;
            var candidate = options[optionIndex];
            if (Text(Label(candidate)) != label) return false;
            option = candidate;
            return true;
        }

        // DiaOption.Activate is what the option's button calls: close on
        // resolveTree, run the option's action, then follow any link.
        internal static void Activate(Window dialog, DiaOption option)
        {
            if (dialog is Dialog_MessageBox) option.action();
            else AccessTools.Method(typeof(DiaOption), "Activate").Invoke(option, new object[0]);
        }

        private static string Text(string? text) => NativeClockEventProjection.Text((text ?? "").StripTags());
    }
}
