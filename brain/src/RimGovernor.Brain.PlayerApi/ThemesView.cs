using System.Globalization;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;

namespace RimGovernor.Brain.PlayerApi;

/// <summary>The shipped presets and the installed game's catalog they resolve against (#3502); the host supplies both.</summary>
public sealed record PresetBook(IReadOnlyList<ThemePreset> Presets, IPresetCatalog Catalog)
{
    public ThemePreset? Find(string id) => Presets.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
}

/// <summary>One member of a theme as the Themes tab shows it. <see cref="GoalId"/> is the minted goal; null for a preference, an unavailable member or one removed; <see cref="Restorable"/> marks a removed or deleted member the player can bring back.</summary>
public sealed record ThemeMemberView(string Key, string Title, string State, string Detail, string Importance, string Deadline, int? GoalId, bool Restorable);

/// <summary>An adopted or composed theme: its value-weighted progress and members (removed preset members last).</summary>
public sealed record ThemeView(int ThemeId, string Name, string PresetId, string Importance, string Deadline, double Progress, string ProgressText, IReadOnlyList<ThemeMemberView> Members);

/// <summary>One member of a shipped preset with whether the installed game can apply it.</summary>
public sealed record PresetMemberView(string Key, string Title, bool Available, string Reason);

/// <summary>A shipped preset the player can adopt.</summary>
public sealed record PresetView(string Id, string Name, bool Adopted, IReadOnlyList<PresetMemberView> Members);

/// <summary>Builds the Themes tab's rows (#3502); pure.</summary>
internal static class ThemesView
{
    public const string UnavailableState = "Unavailable";
    public const string PreferenceState = "Preference";
    public const string MissingState = "Not minted";

    /// <summary>How much a goal counts toward its theme's progress: its effective importance.</summary>
    public static int Weight(Importance importance) =>
        importance == Importance.Low ? 1
        : importance == Importance.Normal ? 2
        : importance == Importance.High ? 4
        : throw new DomainException($"undefined importance {importance}");

    public static IReadOnlyList<PresetView> Presets(GoalsRecord record, PresetBook? book)
    {
        if (book is null)
        {
            return [];
        }
        var views = new List<PresetView>();
        foreach (var preset in book.Presets)
        {
            var resolved = PresetRules.Resolve(preset, book.Catalog, new ThemeRecord { Name = preset.Name, Preset = preset.Id });
            var members = resolved.Members.Select(m => new PresetMemberView(m.Key, MemberTitle(m), m.Available, m.UnavailableReason)).ToList();
            views.Add(new PresetView(preset.Id, preset.Name, record.Themes.Any(t => t.Preset == preset.Id), members));
        }
        return views;
    }

    public static IReadOnlyList<ThemeView> Themes(GoalsRecord record, PresetBook? book, IReadOnlyDictionary<int, GoalStatus> status)
    {
        var views = new List<ThemeView>();
        foreach (var theme in record.Themes)
        {
            var preset = theme.Preset.Length > 0 ? book?.Find(theme.Preset) : null;
            var members = preset is null ? AuthoredMembers(record, theme, status) : PresetMembers(record, theme, preset, book!, status);
            var counted = members.Where(m => m.GoalId is not null).ToList();
            var total = counted.Sum(m => Weight(EffectiveImportance(record, m)));
            var met = counted.Where(m => m.State == "Met").Sum(m => Weight(EffectiveImportance(record, m)));
            var fraction = total == 0 ? 0.0 : (double)met / total;
            views.Add(new ThemeView(
                theme.Id,
                theme.Name,
                theme.Preset,
                theme.Importance.ToString(),
                DeadlineText(theme.DeadlineDay),
                fraction,
                total == 0 ? "nothing to pursue" : Math.Round(fraction * 100).ToString(CultureInfo.InvariantCulture) + "% of value met",
                members));
        }
        return views;
    }

    /// <summary>"day N" for a deadline, empty for none.</summary>
    internal static string DeadlineText(GoalDeadline deadline) =>
        deadline is GoalDeadline.Due due ? "day " + due.Day.ToString(CultureInfo.InvariantCulture) : "";

    private static Importance EffectiveImportance(GoalsRecord record, ThemeMemberView member)
    {
        var goal = record.Goals.First(g => g.Id == member.GoalId);
        return goal.ImportanceOverride.Or(goal.Importance);
    }

    private static List<ThemeMemberView> AuthoredMembers(GoalsRecord record, ThemeRecord theme, IReadOnlyDictionary<int, GoalStatus> status) =>
        [.. theme.Members.Select(id => record.Goals.FirstOrDefault(g => g.Id == id)).OfType<GoalRecord>().Select(g => GoalMember(g.Id.ToString(CultureInfo.InvariantCulture), g, theme, status))];

    private static List<ThemeMemberView> PresetMembers(GoalsRecord record, ThemeRecord theme, ThemePreset preset, PresetBook book, IReadOnlyDictionary<int, GoalStatus> status)
    {
        var overrides = new List<MemberOverride>();
        foreach (var member in preset.Members)
        {
            if (member.Goal is { } template && FindGoal(record, theme, template) is { } stored)
            {
                if (stored.ImportanceOverride is ImportanceChoice.Chosen { Value: var importance })
                {
                    overrides.Add(new MemberOverride.ImportanceOf(member.Key, importance));
                }
                if (stored.DeadlineOverride is GoalDeadline.Due { Day: var deadline })
                {
                    overrides.Add(new MemberOverride.DeadlineOf(member.Key, deadline));
                }
            }
        }
        var resolved = PresetRules.Resolve(preset, book.Catalog, theme, overrides);
        var views = new List<ThemeMemberView>();
        foreach (var member in resolved.Members)
        {
            if (member.Goal is null)
            {
                views.Add(new ThemeMemberView(member.Key, MemberTitle(member), PreferenceState, "", "", "", null, false));
            }
            else if (!member.Available)
            {
                views.Add(new ThemeMemberView(member.Key, MemberTitle(member), UnavailableState, member.UnavailableReason, member.Goal.Importance.ToString(), "", null, false));
            }
            else if (FindGoal(record, theme, member.Goal) is { } goal)
            {
                views.Add(GoalMember(member.Key, goal, theme, status) with { Title = MemberTitle(member) });
            }
            else
            {
                views.Add(new ThemeMemberView(member.Key, MemberTitle(member), MissingState, "the goal was deleted; restore to mint it again", member.Goal.Importance.ToString(), "", null, true));
            }
        }
        foreach (var key in theme.RemovedPresetMembers)
        {
            if (preset.Members.FirstOrDefault(m => m.Key == key) is { } removed)
            {
                views.Add(new ThemeMemberView(key, MemberTitle(new ResolvedMember(key, removed.Goal, removed.Preference, "")), "Removed", "", "", "", null, true));
            }
        }
        return views;
    }

    private static ThemeMemberView GoalMember(string key, GoalRecord goal, ThemeRecord theme, IReadOnlyDictionary<int, GoalStatus> status)
    {
        var (state, detail) = status.TryGetValue(goal.Id, out var s) ? GoalsViewModel.Describe(s) : (GoalsViewModel.Pending, "");
        var deadline = goal.DeadlineOverride.Or(goal.DeadlineDay).Or(theme.DeadlineDay);
        return new ThemeMemberView(
            key,
            GoalTitle(goal),
            state,
            detail,
            goal.ImportanceOverride.Or(goal.Importance).ToString(),
            DeadlineText(deadline),
            goal.Id,
            false);
    }

    /// <summary>The stored goal of <paramref name="theme"/> that a preset member's template minted: the same kind, target and qualifier.</summary>
    public static GoalRecord? FindGoal(GoalsRecord record, ThemeRecord theme, GoalRecord template) =>
        record.Goals.FirstOrDefault(g => g.Theme.Is(theme.Id) && g.Kind == template.Kind && g.Target == template.Target && g.Qualifier == template.Qualifier);

    private static string MemberTitle(ResolvedMember m) => m.Goal is { } goal ? GoalTitle(goal) : Describe(m.Preference!);

    private static string GoalTitle(GoalRecord goal)
    {
        var text = GoalsViewModel.TargetText(goal);
        var kind = GoalsViewModel.Words(goal.Kind.ToString());
        return text.Length == 0 ? kind : kind + " " + text;
    }

    private static string Describe(PresetPreference p)
    {
        switch (p)
        {
            case PresetPreference.Risk risk:
                return "Preference: Risk " + risk.Profile;
            case PresetPreference.Forbid forbid:
                return "Preference: Forbid " + string.Join(", ", forbid.Methods);
            case PresetPreference.Diet diet:
                return "Preference: Diet " + string.Join(", ", diet.Overrides);
            case PresetPreference.Apparel apparel:
                return "Preference: Apparel " + apparel.Preference;
        }

        throw new DomainException($"unlisted preference {p}");
    }
}

/// <summary>
/// The Themes tab's edits as new records (#3502), with the same contract as <see cref="GoalEdits"/>. A preset theme
/// keeps no copy of its members: they are minted as goals tagged with the theme, a member's override lives on its goal
/// (<see cref="GoalRecord.ImportanceOverride"/>), and a removed member's key is in <see cref="ThemeRecord.RemovedPresetMembers"/>.
/// </summary>
public static class ThemeEdits
{
    /// <summary>Adopts <paramref name="presetId"/> as a theme with the given importance and deadline and mints its available goal members.</summary>
    public static GoalEdit Adopt(GoalsRecord record, PresetBook? book, string presetId, Importance importance, int? deadlineDay)
    {
        if (book?.Find(presetId) is not { } preset)
        {
            return new GoalEdit(record, "that preset is not available");
        }
        if (record.Themes.Any(t => t.Preset == preset.Id))
        {
            return new GoalEdit(record, $"{preset.Name} is already adopted");
        }
        var theme = new ThemeRecord { Id = record.NextId, Name = preset.Name, Preset = preset.Id, Importance = importance, DeadlineDay = GoalDeadline.Of(deadlineDay) };
        var next = record.WithNewTheme(theme);
        var ids = new List<int>();
        foreach (var goal in PresetRules.Resolve(preset, book.Catalog, theme).Goals())
        {
            ids.Add(next.NextId);
            next = next.WithNewGoal(goal);
        }
        return Check(record, next.With(theme with { Members = ids }));
    }

    /// <summary>Sets (or, with both null, clears) one goal's importance and deadline within its theme; the other members keep the theme's.</summary>
    public static GoalEdit OverrideMember(GoalsRecord record, int goalId, Importance? importance, int? deadlineDay)
    {
        if (record.Goals.FirstOrDefault(g => g.Id == goalId) is not { Theme: ThemeMembership.Member } goal)
        {
            return new GoalEdit(record, "that goal is not in a theme");
        }
        return Check(record, record.With(goal with { ImportanceOverride = ImportanceChoice.Of(importance), DeadlineOverride = GoalDeadline.Of(deadlineDay) }));
    }

    /// <summary>Removes a preset member: the key is recorded and its goal, if minted, is deleted.</summary>
    public static GoalEdit RemoveMember(GoalsRecord record, PresetBook? book, int themeId, string key)
    {
        if (Member(record, book, themeId, key, out var theme, out var member) is { } error)
        {
            return new GoalEdit(record, error);
        }
        if (theme.RemovedPresetMembers.Contains(key, StringComparer.Ordinal))
        {
            return new GoalEdit(record, "that member is already removed");
        }
        var stored = member.Goal is { } template ? ThemesView.FindGoal(record, theme, template) : null;
        var next = record with { Goals = [.. record.Goals.Where(g => g.Id != stored?.Id)] };
        theme = theme with
        {
            RemovedPresetMembers = [.. theme.RemovedPresetMembers.Append(key).OrderBy(k => k, StringComparer.Ordinal)],
            Members = [.. theme.Members.Where(m => m != stored?.Id)],
        };
        return Check(record, next.With(theme));
    }

    /// <summary>Restores a removed preset member, or re-mints one whose goal was deleted, with the theme's importance and deadline.</summary>
    public static GoalEdit RestoreMember(GoalsRecord record, PresetBook? book, int themeId, string key)
    {
        if (Member(record, book, themeId, key, out var theme, out var member) is { } error)
        {
            return new GoalEdit(record, error);
        }
        var wasRemoved = theme.RemovedPresetMembers.Contains(key, StringComparer.Ordinal);
        if (!wasRemoved && (member.Goal is null || ThemesView.FindGoal(record, theme, member.Goal) is not null))
        {
            return new GoalEdit(record, "that member is already present");
        }
        theme = theme with { RemovedPresetMembers = [.. theme.RemovedPresetMembers.Where(k => k != key)] };
        var next = record;
        if (PresetRules.Resolve(book!.Find(theme.Preset)!, book.Catalog, theme).Members.FirstOrDefault(m => m.Key == key) is { Available: true, Goal: { } goal })
        {
            theme = theme with { Members = [.. theme.Members, next.NextId] };
            next = next.WithNewGoal(goal);
        }
        return Check(record, next.With(theme));
    }

    /// <summary>
    /// Composes an authored theme (no preset) from goals that belong to no theme. The theme's importance applies to its
    /// members, and its deadline to those that have none, as a preset's does.
    /// </summary>
    public static GoalEdit Compose(GoalsRecord record, string name, Importance importance, int? deadlineDay, IReadOnlyList<int> goalIds)
    {
        var ids = goalIds.Distinct().Order().ToList();
        if (ids.Count == 0)
        {
            return new GoalEdit(record, "pick at least one goal");
        }
        foreach (var id in ids)
        {
            if (record.Goals.FirstOrDefault(g => g.Id == id) is not { } goal)
            {
                return new GoalEdit(record, $"goal {id} no longer exists");
            }
            if (goal.Theme is ThemeMembership.Member)
            {
                return new GoalEdit(record, $"goal {id} already belongs to a theme");
            }
        }
        var theme = new ThemeRecord { Id = record.NextId, Name = name.Trim(), Importance = importance, DeadlineDay = GoalDeadline.Of(deadlineDay), Members = ids };
        var next = record.WithNewTheme(theme);
        foreach (var id in ids)
        {
            var goal = next.Goals.First(g => g.Id == id);
            next = next.With(goal with { Theme = ThemeMembership.Of(theme.Id), Importance = importance, DeadlineDay = goal.DeadlineDay.Or(GoalDeadline.Of(deadlineDay)) });
        }
        return Check(record, next);
    }

    private static string? Member(GoalsRecord record, PresetBook? book, int themeId, string key, out ThemeRecord theme, out PresetMember member)
    {
        theme = record.Themes.FirstOrDefault(t => t.Id == themeId)!;
        member = null!;
        if (theme is null)
        {
            return "that theme no longer exists";
        }
        if (book?.Find(theme.Preset) is not { } preset)
        {
            return "that theme has no preset";
        }
        member = preset.Members.FirstOrDefault(m => m.Key == key)!;
        return member is null ? "that preset has no such member" : null;
    }

    private static GoalEdit Check(GoalsRecord before, GoalsRecord after)
    {
        try
        {
            after.Validate();
        }
        catch (DomainException e)
        {
            return new GoalEdit(before, e.Message);
        }
        return new GoalEdit(after, null);
    }
}
