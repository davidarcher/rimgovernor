using System.Globalization;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;

namespace RimGovernor.Brain.PlayerApi;

/// <summary>One goal as the in-game panel's table shows it (#3497).</summary>
public sealed record GoalRowView(int Id, string Kind, string Target, string Importance, string Deadline, string State, string Detail);

/// <summary>A theme's goals under its header, or the goals that belong to no theme (<see cref="ThemeId"/> null).</summary>
public sealed record GoalGroupView(int? ThemeId, string Title, string Progress, IReadOnlyList<GoalRowView> Rows);

/// <summary>
/// What the player is editing: the fields a goal form sets. Everything else a goal carries (theme, overrides, map)
/// stays as the stored goal has it.
/// </summary>
public sealed record GoalDraft
{
    public GoalKind Kind { get; init; }

    public string Target { get; init; } = "";

    public string Qualifier { get; init; } = "";

    public int Count { get; init; }

    public Importance Importance { get; init; } = Importance.Normal;

    public int? DeadlineDay { get; init; }

    /// <summary>The draft of <paramref name="goal"/>, for the edit form.</summary>
    public static GoalDraft Of(GoalRecord goal) => new() { Kind = goal.Kind, Target = goal.Target, Qualifier = goal.Qualifier, Count = goal.Count, Importance = goal.Importance, DeadlineDay = goal.DeadlineDay };
}

/// <summary>An edit's outcome: the record to save, or the reason it was refused (<see cref="Record"/> is then the record the edit started from).</summary>
public sealed record GoalEdit(GoalsRecord Record, string? Error)
{
    public bool Ok => Error is null;
}

/// <summary>
/// The pure model behind the in-game goals panel (#3497): the snapshot as table rows grouped by theme, built off the
/// game thread and read by the draw without blocking. Rows sort by importance (high first), then theme, then id.
/// </summary>
public sealed record GoalsViewModel(GoalsRecord Record, IReadOnlyList<GoalGroupView> Groups, IReadOnlyList<ThemeView> Themes, IReadOnlyList<PresetView> Presets)
{
    /// <summary>The state of a goal the Family has not reviewed yet (it was added after the last review).</summary>
    public const string Pending = "Pending";

    public static GoalsViewModel From(GoalsSnapshot snapshot, PresetBook? book = null)
    {
        var record = snapshot.Record;
        var status = snapshot.Statuses.ToDictionary(s => s.Goal.Id);
        var themeStatus = snapshot.Themes.ToDictionary(t => t.Id);
        var ordered = record.Goals
            .OrderByDescending(g => g.Importance)
            .ThenBy(g => g.Theme ?? 0)
            .ThenBy(g => g.Id)
            .ToList();
        var groups = new List<GoalGroupView>();
        var standalone = ordered.Where(g => g.Theme is null).Select(g => Row(g, status, DeadlineOf(g, null))).ToList();
        if (standalone.Count > 0)
        {
            groups.Add(new GoalGroupView(null, "Goals", "", standalone));
        }
        foreach (var theme in record.Themes)
        {
            var rows = ordered.Where(g => g.Theme == theme.Id).Select(g => Row(g, status, DeadlineOf(g, theme))).ToList();
            var progress = themeStatus.TryGetValue(theme.Id, out var t) ? $"{t.Met}/{t.Total} met" : "not reviewed";
            groups.Add(new GoalGroupView(theme.Id, theme.Name, progress, rows));
        }
        return new GoalsViewModel(record, groups, ThemesView.Themes(record, book, status), ThemesView.Presets(record, book));
    }

    private static int? DeadlineOf(GoalRecord goal, ThemeRecord? theme) => goal.DeadlineOverride ?? goal.DeadlineDay ?? theme?.DeadlineDay;

    private static GoalRowView Row(GoalRecord goal, Dictionary<int, GoalStatus> status, int? deadline)
    {
        var (state, detail) = status.TryGetValue(goal.Id, out var s) ? Describe(s) : (Pending, "");
        return new GoalRowView(
            goal.Id,
            Words(goal.Kind.ToString()),
            TargetText(goal),
            (goal.ImportanceOverride ?? goal.Importance).ToString(),
            deadline is { } day ? "day " + day.ToString(CultureInfo.InvariantCulture) : "",
            state,
            detail);
    }

    internal static (string State, string Detail) Describe(GoalStatus s) => s.State switch
    {
        GoalState.Met => ("Met", ""),
        GoalState.Pursuing => ("Pursuing", s.Owner.Value),
        GoalState.Blocked => ("Blocked", s.Reason),
        _ => ("No method", s.Reason),
    };

    internal static string TargetText(GoalRecord goal)
    {
        var parts = new List<string>();
        if (goal.Target.Length > 0)
        {
            parts.Add(goal.Target);
        }
        if (goal.Qualifier.Length > 0)
        {
            parts.Add(goal.Qualifier);
        }
        if (goal.Count > 0)
        {
            parts.Add("x" + goal.Count.ToString(CultureInfo.InvariantCulture));
        }
        return string.Join(' ', parts);
    }

    /// <summary>"StockCount" as "Stock count".</summary>
    public static string Words(string name)
    {
        var text = new System.Text.StringBuilder(name.Length + 2);
        for (var i = 0; i < name.Length; i++)
        {
            text.Append(i == 0 ? name[i] : char.IsUpper(name[i]) ? ' ' : name[i]);
            if (i > 0 && char.IsUpper(name[i]))
            {
                text.Append(char.ToLowerInvariant(name[i]));
            }
        }
        return text.ToString();
    }
}

/// <summary>
/// The panel's edits as new records. Each returns the edited record, or the record it started from with the reason the
/// edit is not valid; the host validates the whole record again on save.
/// </summary>
public static class GoalEdits
{
    public static GoalEdit Add(GoalsRecord record, GoalDraft draft)
    {
        var goal = Apply(new GoalRecord { Id = record.NextId }, draft);
        return Check(record, goal, record.WithNewGoal(goal));
    }

    public static GoalEdit Edit(GoalsRecord record, int goalId, GoalDraft draft)
    {
        if (record.Goals.FirstOrDefault(g => g.Id == goalId) is not { } existing)
        {
            return new GoalEdit(record, "that goal no longer exists");
        }
        var goal = Apply(existing, draft);
        return Check(record, goal, record.With(goal));
    }

    /// <summary>Removes the goal and its membership in any theme.</summary>
    public static GoalEdit Remove(GoalsRecord record, int goalId)
    {
        if (record.Goals.All(g => g.Id != goalId))
        {
            return new GoalEdit(record, "that goal no longer exists");
        }
        return new GoalEdit(
            record with
            {
                Goals = [.. record.Goals.Where(g => g.Id != goalId)],
                Themes = [.. record.Themes.Select(t => t.Members.Contains(goalId) ? t with { Members = [.. t.Members.Where(m => m != goalId)] } : t)],
            },
            null);
    }

    public static GoalEdit SetPreferences(GoalsRecord record, PreferencesRecord preferences)
    {
        try
        {
            preferences.Validate();
        }
        catch (DomainException e)
        {
            return new GoalEdit(record, e.Message);
        }
        return new GoalEdit(record.With(preferences), null);
    }

    private static GoalRecord Apply(GoalRecord goal, GoalDraft d) =>
        goal with { Kind = d.Kind, Target = d.Target.Trim(), Qualifier = d.Qualifier.Trim(), Count = d.Count, Importance = d.Importance, DeadlineDay = d.DeadlineDay };

    private static GoalEdit Check(GoalsRecord before, GoalRecord goal, GoalsRecord after)
    {
        try
        {
            goal.Validate();
        }
        catch (DomainException e)
        {
            return new GoalEdit(before, e.Message);
        }
        return new GoalEdit(after, null);
    }
}
