#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimGovernor.Brain.PlayerApi;
using RimGovernor.Brain.Policy;
using UnityEngine;
using Verse;

namespace HomeBridge.BridgeTools
{
    /// <summary>
    /// The player's goals, in game (#3497): a table of goals grouped under their theme with each goal's state and reason,
    /// a form to add or edit a goal, and the standing preferences. A plain Verse window: the draw reads the cached
    /// <see cref="GoalsPanel"/> model and never waits; the panel polls the brain's view off the game thread and saves
    /// edits through the brain's editor in order. See docs/developers/architecture/in-game-panel.md.
    /// </summary>
    internal sealed class GoalsWindow : Window
    {
        private const float PollSeconds = 1.5f;
        private const float RowHeight = 26f;

        private readonly GoalsPanel panel;
        private volatile PresetBook? book;
        private bool bookStarted;
        private bool themesTab;
        private Vector2 scroll;
        private float viewHeight = 600f;
        private float nextPoll;

        // The goal form. editingId is the goal being edited; null adds a new one.
        private int? editingId;
        private GoalKind kind = GoalKind.StockCount;
        private string target = "";
        private string qualifier = "";
        private string countText = "";
        private string deadlineText = "";
        private Importance importance = Importance.Normal;
        private string formError = "";

        // The protected-id fields, seeded once from the stored preferences.
        private bool seeded;
        private string protectedThings = "";
        private string protectedPawns = "";

        // The Themes tab's state: the adopt form, the member override form, the compose form.
        private Importance adoptImportance = Importance.Normal;
        private string adoptDeadlineText = "";
        private int? overrideGoalId;
        private Importance overrideImportance = Importance.Normal;
        private string overrideDeadlineText = "";
        private string composeName = "";
        private Importance composeImportance = Importance.Normal;
        private string composeDeadlineText = "";
        private readonly HashSet<int> composeGoals = new HashSet<int>();

        public GoalsWindow()
        {
            panel = new GoalsPanel(() => BrainBootstrap.Host?.GoalsView, () => BrainBootstrap.Host?.GoalsEditor, () => book);
            draggable = true;
            resizeable = true;
            forcePause = false;
            doCloseX = true;
            absorbInputAroundWindow = false;
            preventCameraMotion = false;
            optionalTitle = "RimGovernor goals";
        }

        public override Vector2 InitialSize => new Vector2(920f, 620f);

        public override void WindowUpdate()
        {
            base.WindowUpdate();
            if (Time.realtimeSinceStartup < nextPoll) return;
            nextPoll = Time.realtimeSinceStartup + PollSeconds;
            StartBook();
            _ = panel.RefreshAsync(CancellationToken.None).ContinueWith(
                t => Log.Warning($"[RimGovernor] The goals window could not read the goals: {t.Exception?.GetBaseException().Message}"),
                TaskContinuationOptions.OnlyOnFaulted);
        }

        public override void DoWindowContents(Rect inRect)
        {
            if (!panel.BrainStarted)
            {
                Widgets.Label(inRect, "Brain not started.");
                return;
            }
            var model = panel.Model;
            if (model == null)
            {
                Widgets.Label(inRect, "Reading goals...");
                return;
            }
            if (!seeded)
            {
                seeded = true;
                protectedThings = string.Join(", ", model.Record.Preferences.ProtectedThings);
                protectedPawns = string.Join(", ", model.Record.Preferences.ProtectedPawns);
            }
            var view = new Rect(0f, 0f, inRect.width - 18f, viewHeight);
            Widgets.BeginScrollView(inRect, ref scroll, view);
            var list = new Listing_Standard();
            list.Begin(view);
            Message(list);
            var tabs = list.GetRect(RowHeight);
            if (Widgets.ButtonText(new Rect(tabs.x, tabs.y, tabs.width / 2f - 2f, tabs.height), themesTab ? "Goals" : "Goals (shown)")) themesTab = false;
            if (Widgets.ButtonText(new Rect(tabs.x + tabs.width / 2f, tabs.y, tabs.width / 2f, tabs.height), themesTab ? "Themes (shown)" : "Themes")) themesTab = true;
            if (themesTab)
            {
                Themes(list, model);
            }
            else
            {
                Table(list, model);
                list.GapLine();
                Form(list);
                list.GapLine();
                Preferences(list, model.Record.Preferences);
            }
            viewHeight = list.CurHeight + 8f;
            list.End();
            Widgets.EndScrollView();
        }

        // The shipped presets over the installed game's catalog, built once off the game thread (the Def databases are
        // written at boot and read-only after). Until it exists the Themes tab lists no presets.
        private void StartBook()
        {
            if (bookStarted) return;
            bookStarted = true;
            _ = Task.Run(() => book = NativeCatalogFactory.Presets()).ContinueWith(
                t => Log.Warning($"[RimGovernor] The goals window could not build the preset catalog: {t.Exception?.GetBaseException().Message}"),
                TaskContinuationOptions.OnlyOnFaulted);
        }

        private void Themes(Listing_Standard list, GoalsViewModel model)
        {
            var presets = panel.Presets;
            Text.Font = GameFont.Medium;
            list.Label("Presets");
            Text.Font = GameFont.Small;
            if (presets == null) list.Label("Reading the game's definitions...");
            Pick(list, "Adopt with importance", adoptImportance.ToString(), Enum.GetValues(typeof(Importance)).Cast<Importance>(), i => i.ToString(), i => adoptImportance = i);
            Field(list, "Adopt deadline (game day, blank for none)", ref adoptDeadlineText);
            foreach (var preset in model.Presets)
            {
                var rect = list.GetRect(RowHeight);
                Widgets.Label(new Rect(rect.x, rect.y, rect.width * 0.7f, rect.height), preset.Adopted ? preset.Name + " (adopted)" : preset.Name);
                if (!preset.Adopted && Widgets.ButtonText(new Rect(rect.x + rect.width * 0.7f, rect.y, rect.width * 0.3f, rect.height - 2f), "Adopt"))
                {
                    if (!TryNumber(adoptDeadlineText, out var deadline)) { formError = "Deadline must be a whole game day or blank."; }
                    else
                    {
                        formError = "";
                        var id = preset.Id;
                        var level = adoptImportance;
                        panel.Submit(r => ThemeEdits.Adopt(r, presets, id, level, deadline));
                    }
                }
                foreach (var member in preset.Members)
                    list.Label("    " + member.Title + (member.Available ? "" : "  -- " + member.Reason));
            }
            list.GapLine();
            Text.Font = GameFont.Medium;
            list.Label("Themes");
            Text.Font = GameFont.Small;
            if (model.Themes.Count == 0) list.Label("No themes yet. Adopt a preset above or compose one below.");
            foreach (var theme in model.Themes) ThemeBlock(list, model, theme, presets);
            list.GapLine();
            Compose(list, model);
        }

        private void ThemeBlock(Listing_Standard list, GoalsViewModel model, ThemeView theme, PresetBook? presets)
        {
            Text.Font = GameFont.Medium;
            list.Label($"{theme.Name}  ({theme.ProgressText}; {theme.Importance}{(theme.Deadline.Length > 0 ? "; " + theme.Deadline : "")})");
            Text.Font = GameFont.Small;
            foreach (var member in theme.Members)
            {
                var rect = list.GetRect(RowHeight);
                var widths = new[] { 0.34f, 0.10f, 0.20f, 0.08f, 0.08f };
                var texts = new[] { member.Title, member.State, member.Detail, member.Importance, member.Deadline };
                var x = rect.x;
                for (var i = 0; i < texts.Length; i++)
                {
                    var w = rect.width * widths[i];
                    Widgets.Label(new Rect(x, rect.y, w - 4f, rect.height), texts[i].Truncate(w - 4f));
                    x += w;
                }
                var rest = rect.xMax - x;
                if (member.GoalId is { } goalId && Widgets.ButtonText(new Rect(x, rect.y, rest / 2f - 2f, rect.height - 2f), "Override"))
                {
                    var goal = model.Record.Goals.FirstOrDefault(g => g.Id == goalId);
                    overrideGoalId = goalId;
                    overrideImportance = goal == null ? Importance.Normal : goal.ImportanceOverride.Or(goal.Importance);
                    overrideDeadlineText = goal?.DeadlineOverride is GoalDeadline.Due due ? due.Day.ToString(CultureInfo.InvariantCulture) : "";
                }
                if (theme.PresetId.Length > 0)
                {
                    var key = member.Key;
                    var themeId = theme.ThemeId;
                    var button = new Rect(x + rest / 2f, rect.y, rest / 2f, rect.height - 2f);
                    if (member.Restorable)
                    {
                        if (Widgets.ButtonText(button, "Restore")) panel.Submit(r => ThemeEdits.RestoreMember(r, presets, themeId, key));
                    }
                    else if (Widgets.ButtonText(button, "Remove")) panel.Submit(r => ThemeEdits.RemoveMember(r, presets, themeId, key));
                }
                if (overrideGoalId is { } open && member.GoalId == open) OverrideForm(list, open);
            }
        }

        private void OverrideForm(Listing_Standard list, int goalId)
        {
            Pick(list, "Member importance", overrideImportance.ToString(), Enum.GetValues(typeof(Importance)).Cast<Importance>(), i => i.ToString(), i => overrideImportance = i);
            Field(list, "Member deadline (game day, blank for the theme's)", ref overrideDeadlineText);
            var rect = list.GetRect(RowHeight);
            if (Widgets.ButtonText(new Rect(rect.x, rect.y, rect.width / 3f - 2f, rect.height), "Apply override"))
            {
                if (!TryNumber(overrideDeadlineText, out var deadline)) { formError = "Deadline must be a whole game day or blank."; return; }
                formError = "";
                var level = overrideImportance;
                panel.Submit(r => ThemeEdits.OverrideMember(r, goalId, level, deadline));
                overrideGoalId = null;
            }
            if (Widgets.ButtonText(new Rect(rect.x + rect.width / 3f, rect.y, rect.width / 3f - 2f, rect.height), "Clear override"))
            {
                panel.Submit(r => ThemeEdits.OverrideMember(r, goalId, null, null));
                overrideGoalId = null;
            }
            if (Widgets.ButtonText(new Rect(rect.x + 2f * rect.width / 3f, rect.y, rect.width / 3f, rect.height), "Cancel")) overrideGoalId = null;
        }

        private void Compose(Listing_Standard list, GoalsViewModel model)
        {
            Text.Font = GameFont.Medium;
            list.Label("Compose a theme from goals");
            Text.Font = GameFont.Small;
            Field(list, "Name", ref composeName);
            Pick(list, "Importance", composeImportance.ToString(), Enum.GetValues(typeof(Importance)).Cast<Importance>(), i => i.ToString(), i => composeImportance = i);
            Field(list, "Deadline (game day, blank for none)", ref composeDeadlineText);
            var free = model.Groups.FirstOrDefault(g => g.ThemeId == null)?.Rows ?? new List<GoalRowView>();
            if (free.Count == 0) list.Label("Every goal already belongs to a theme.");
            foreach (var row in free)
            {
                var on = composeGoals.Contains(row.Id);
                var was = on;
                list.CheckboxLabeled($"{row.Kind} {row.Target}", ref on);
                if (on != was) { if (on) composeGoals.Add(row.Id); else composeGoals.Remove(row.Id); }
            }
            if (!list.ButtonText("Create theme")) return;
            if (!TryNumber(composeDeadlineText, out var deadline)) { formError = "Deadline must be a whole game day or blank."; return; }
            formError = "";
            var name = composeName;
            var level = composeImportance;
            var ids = composeGoals.ToList();
            panel.Submit(r => ThemeEdits.Compose(r, name, level, deadline, ids));
            composeGoals.Clear();
            composeName = composeDeadlineText = "";
        }

        private void Message(Listing_Standard list)
        {
            var text = formError.Length > 0 ? formError : panel.Message;
            if (text.Length > 0) list.Label(text);
        }

        private void Table(Listing_Standard list, GoalsViewModel model)
        {
            if (model.Groups.Count == 0)
            {
                list.Label("No goals yet. Add one below.");
                return;
            }
            foreach (var group in model.Groups)
            {
                Text.Font = GameFont.Medium;
                list.Label(group.Progress.Length > 0 ? $"{group.Title}  ({group.Progress})" : group.Title);
                Text.Font = GameFont.Small;
                Columns(list.GetRect(RowHeight), "Kind", "Target", "Importance", "Deadline", "State", "Why", null);
                foreach (var row in group.Rows) Row(list, row);
            }
        }

        private void Row(Listing_Standard list, GoalRowView row)
        {
            var rect = list.GetRect(RowHeight);
            Columns(rect, row.Kind, row.Target, row.Importance, row.Deadline, row.State, row.Detail, row);
        }

        // Seven columns: six labels and the row's buttons (header when row is null).
        private void Columns(Rect rect, string kindText, string targetText, string importanceText, string deadlineText, string stateText, string whyText, GoalRowView? row)
        {
            var widths = new[] { 0.13f, 0.22f, 0.09f, 0.09f, 0.1f, 0.19f };
            var texts = new[] { kindText, targetText, importanceText, deadlineText, stateText, whyText };
            var x = rect.x;
            for (var i = 0; i < texts.Length; i++)
            {
                var w = rect.width * widths[i];
                Widgets.Label(new Rect(x, rect.y, w - 4f, rect.height), texts[i].Truncate(w - 4f));
                x += w;
            }
            if (row == null) return;
            var rest = rect.xMax - x;
            if (Widgets.ButtonText(new Rect(x, rect.y, rest / 2f - 2f, rect.height - 2f), "Edit")) Edit(row.Id);
            if (Widgets.ButtonText(new Rect(x + rest / 2f, rect.y, rest / 2f, rect.height - 2f), "Remove")) panel.Submit(r => GoalEdits.Remove(r, row.Id));
        }

        private void Edit(int goalId)
        {
            var goal = panel.Model?.Record.Goals.FirstOrDefault(g => g.Id == goalId);
            if (goal == null) return;
            var draft = GoalDraft.Of(goal);
            editingId = goalId;
            kind = draft.Kind;
            target = draft.Target;
            qualifier = draft.Qualifier;
            countText = draft.Count > 0 ? draft.Count.ToString(CultureInfo.InvariantCulture) : "";
            deadlineText = draft.DeadlineDay is GoalDeadline.Due due ? due.Day.ToString(CultureInfo.InvariantCulture) : "";
            importance = draft.Importance;
            formError = "";
        }

        private void Form(Listing_Standard list)
        {
            list.Label(editingId is { } id ? $"Edit goal {id}" : "Add a goal");
            Pick(list, "Kind", kind.ToString(), Enum.GetValues(typeof(GoalKind)).Cast<GoalKind>(), k => k.ToString(), k => kind = k);
            Field(list, "Target (def name, role, id)", ref target);
            Field(list, "Qualifier (stuff, skill, recipe)", ref qualifier);
            Field(list, "Count", ref countText);
            Field(list, "Deadline (game day, blank for none)", ref deadlineText);
            Pick(list, "Importance", importance.ToString(), Enum.GetValues(typeof(Importance)).Cast<Importance>(), i => i.ToString(), i => importance = i);
            var rect = list.GetRect(RowHeight);
            var label = editingId == null ? "Add goal" : "Save changes";
            if (Widgets.ButtonText(new Rect(rect.x, rect.y, rect.width / 2f - 2f, rect.height), label)) Submit();
            if (editingId != null && Widgets.ButtonText(new Rect(rect.x + rect.width / 2f, rect.y, rect.width / 2f, rect.height), "Cancel edit"))
            {
                editingId = null;
                formError = "";
            }
        }

        private void Submit()
        {
            if (!TryNumber(countText, out var count) || count is null or < 0) { formError = "Count must be a whole number."; return; }
            if (!TryNumber(deadlineText, out var deadline)) { formError = "Deadline must be a whole game day or blank."; return; }
            var draft = new GoalDraft { Kind = kind, Target = target, Qualifier = qualifier, Count = count ?? 0, Importance = importance, DeadlineDay = GoalDeadline.Of(deadline) };
            var id = editingId;
            panel.Submit(r => id is { } goalId ? GoalEdits.Edit(r, goalId, draft) : GoalEdits.Add(r, draft));
            editingId = null;
            formError = "";
            target = qualifier = countText = deadlineText = "";
        }

        // A blank is null; a non-number fails.
        private static bool TryNumber(string text, out int? value)
        {
            value = null;
            if (text.Trim().Length == 0) return true;
            if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) return false;
            value = parsed;
            return true;
        }

        private static void Field(Listing_Standard list, string label, ref string value)
        {
            var rect = list.GetRect(RowHeight);
            Widgets.Label(new Rect(rect.x, rect.y, rect.width * 0.4f, rect.height), label);
            value = Widgets.TextField(new Rect(rect.x + rect.width * 0.4f, rect.y, rect.width * 0.6f, rect.height - 2f), value);
        }

        private static void Pick<T>(Listing_Standard list, string label, string current, IEnumerable<T> options, Func<T, string> name, Action<T> choose)
        {
            var rect = list.GetRect(RowHeight);
            Widgets.Label(new Rect(rect.x, rect.y, rect.width * 0.4f, rect.height), label);
            if (Widgets.ButtonText(new Rect(rect.x + rect.width * 0.4f, rect.y, rect.width * 0.6f, rect.height - 2f), GoalsViewModel.Words(current)))
                Find.WindowStack.Add(new FloatMenu(options.Select(o => new FloatMenuOption(GoalsViewModel.Words(name(o)), () => choose(o))).ToList()));
        }

        private void Preferences(Listing_Standard list, PreferencesRecord prefs)
        {
            Text.Font = GameFont.Medium;
            list.Label("Preferences");
            Text.Font = GameFont.Small;
            Pick(list, "Risk profile", prefs.RiskProfile.ToString(), Enum.GetValues(typeof(RiskProfile)).Cast<RiskProfile>(), v => v.ToString(), v => Save(prefs with { RiskProfile = v }));
            Pick(list, "Pace", prefs.Pace.ToString(), Enum.GetValues(typeof(Pace)).Cast<Pace>(), v => v.ToString(), v => Save(prefs with { Pace = v }));
            Pick(list, "Apparel", prefs.Apparel.ToString(), Enum.GetValues(typeof(ApparelPreference)).Cast<ApparelPreference>(), v => v.ToString(), v => Save(prefs with { Apparel = v }));
            Pick(list, "Drug policy", prefs.DrugPolicy.ToString(), Enum.GetValues(typeof(DrugPolicyPreference)).Cast<DrugPolicyPreference>(), v => v.ToString(), v => Save(prefs with { DrugPolicy = v }));
            list.Label("Forbidden methods");
            foreach (var method in Enum.GetValues(typeof(ForbiddenMethod)).Cast<ForbiddenMethod>())
            {
                var on = prefs.Forbids(method);
                var was = on;
                list.CheckboxLabeled(GoalsViewModel.Words(method.ToString()), ref on);
                if (on != was) Save(prefs with { Forbidden = Toggled(prefs.Forbidden, method, on) });
            }
            list.Label("Diet");
            foreach (var diet in Enum.GetValues(typeof(DietOverride)).Cast<DietOverride>())
            {
                var on = prefs.Diet.Contains(diet);
                var was = on;
                list.CheckboxLabeled(GoalsViewModel.Words(diet.ToString()), ref on);
                if (on != was) Save(prefs with { Diet = Toggled(prefs.Diet, diet, on) });
            }
            Field(list, "Protected things (comma separated)", ref protectedThings);
            Field(list, "Protected pawns (comma separated)", ref protectedPawns);
            if (list.ButtonText("Apply protected lists"))
                Save(prefs with { ProtectedThings = Ids(protectedThings), ProtectedPawns = Ids(protectedPawns) });
        }

        private static IReadOnlyList<T> Toggled<T>(IReadOnlyList<T> current, T item, bool on) where T : struct, Enum
            => current.Where(v => !v.Equals(item)).Concat(on ? new[] { item } : Array.Empty<T>()).OrderBy(v => Convert.ToInt32(v, CultureInfo.InvariantCulture)).ToList();

        private static IReadOnlyList<string> Ids(string text)
            => text.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList();

        private void Save(PreferencesRecord preferences) => panel.Submit(r => GoalEdits.SetPreferences(r, preferences));
    }
}
