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
    // dialog_pause stop and Operations.AnswerDialog share.
    internal static class ChoiceDialogTools
    {
        internal static Dialog_NodeTree? Pending()
        {
            var windows = Find.WindowStack?.Windows.Where(w => w != null && w.forcePause).ToList();
            return windows != null && windows.Count == 1 ? windows[0] as Dialog_NodeTree : null;
        }

        internal static DiaNode? Node(Dialog_NodeTree dialog) =>
            AccessTools.Field(typeof(Dialog_NodeTree), "curNode")?.GetValue(dialog) as DiaNode;

        internal static string? Title(Dialog_NodeTree dialog) =>
            AccessTools.Field(typeof(Dialog_NodeTree), "title")?.GetValue(dialog) as string;

        internal static string Label(DiaOption option) =>
            (AccessTools.Field(typeof(DiaOption), "text")?.GetValue(option) as string) ?? "";

        internal static bool Interactive(Dialog_NodeTree dialog)
        {
            var at = AccessTools.Field(typeof(Dialog_NodeTree), "makeInteractiveAtTime")?.GetValue(dialog);
            return !(at is float) || UnityEngine.Time.realtimeSinceStartup >= (float)at;
        }

        internal static bool Selectable(DiaOption option) => !option.disabled && option.hyperlink.def == null;

        internal static List<DiaOption> Options(Dialog_NodeTree dialog) => Node(dialog)?.options ?? new List<DiaOption>();

        internal static Obs.ChoiceDialog Snapshot(Dialog_NodeTree dialog)
        {
            var node = Node(dialog);
            var result = new Obs.ChoiceDialog { WindowId = dialog.ID, WindowType = dialog.GetType().FullName,
                Title = Text(Title(dialog)), Text = Text(node == null ? "" : node.text.RawText), Interactive = Interactive(dialog) };
            var options = Options(dialog);
            for (var i = 0; i < options.Count; i++)
            {
                var option = options[i];
                var row = new Obs.ChoiceDialogOption { Index = i, Label = Text(Label(option)), Selectable = Selectable(option), Resolves = option.resolveTree };
                if (option.disabled && !string.IsNullOrEmpty(option.disabledReason)) row.DisabledReason = Text(option.disabledReason);
                result.Options.Add(row);
            }
            return result;
        }

        // The same exact-drift test AnswerDialog's preview and execute apply:
        // the observed window, option position and label must all still match.
        internal static bool Matches(Dialog_NodeTree dialog, int windowId, int optionIndex, string label, out DiaOption option)
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
        internal static void Activate(DiaOption option) =>
            AccessTools.Method(typeof(DiaOption), "Activate").Invoke(option, new object[0]);

        private static string Text(string? text) => NativeClockEventProjection.Text((text ?? "").StripTags());
    }
}
