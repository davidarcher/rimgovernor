using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Store;

/// <summary>
/// The player's goals, themes and preferences (#3489), kept in the save as the <c>family/goals</c> blob: intent the
/// world cannot show. Goal and theme ids are minted from <see cref="NextId"/> and never reused. The record is
/// colony-scoped.
/// </summary>
public sealed record GoalsRecord
{
    public World World { get; init; }

    /// <summary>The next id to mint for a goal or a theme.</summary>
    public int NextId { get; init; } = 1;

    /// <summary>Ascending by id.</summary>
    public IReadOnlyList<GoalRecord> Goals { get; init; } = [];

    /// <summary>Ascending by id.</summary>
    public IReadOnlyList<ThemeRecord> Themes { get; init; } = [];

    public PreferencesRecord Preferences { get; init; } = new();

    /// <summary>Whether the record holds nothing the player set; an empty record is not stored.</summary>
    public bool IsEmpty => Goals.Count == 0 && Themes.Count == 0 && Preferences.IsDefault;

    public void Validate()
    {
        World.Validate();
        DomainException.Require(NextId >= 1, "invalid goals next id");
        for (var i = 0; i < Goals.Count; i++)
        {
            var goal = Goals[i];
            goal.Validate();
            DomainException.Require(goal.Id < NextId && (i == 0 || Goals[i - 1].Id < goal.Id), "duplicate or unordered goal id");
            DomainException.Require(goal.Theme is not ThemeMembership.Member { Id: var t } || Themes.Any(x => x.Id == t && x.Members.Contains(goal.Id)), "goal theme does not list the goal");
        }
        for (var i = 0; i < Themes.Count; i++)
        {
            var theme = Themes[i];
            theme.Validate();
            DomainException.Require(theme.Id < NextId && (i == 0 || Themes[i - 1].Id < theme.Id), "duplicate or unordered theme id");
            DomainException.Require(theme.Members.All(m => Goals.Any(g => g.Id == m && g.Theme.Is(theme.Id))), "theme member is not a goal of the theme");
        }
        Preferences.Validate();
    }

    /// <summary>The record with <paramref name="goal"/> added under a minted id.</summary>
    public GoalsRecord WithNewGoal(GoalRecord goal) => this with { NextId = NextId + 1, Goals = [.. Goals, goal with { Id = NextId }] };

    /// <summary>The record with <paramref name="theme"/> added under a minted id.</summary>
    public GoalsRecord WithNewTheme(ThemeRecord theme) => this with { NextId = NextId + 1, Themes = [.. Themes, theme with { Id = NextId }] };

    /// <summary>The record with the goal of <paramref name="goal"/>'s id replaced.</summary>
    public GoalsRecord With(GoalRecord goal) => this with { Goals = [.. Goals.Select(g => g.Id == goal.Id ? goal : g)] };

    /// <summary>The record with the theme of <paramref name="theme"/>'s id replaced.</summary>
    public GoalsRecord With(ThemeRecord theme) => this with { Themes = [.. Themes.Select(t => t.Id == theme.Id ? theme : t)] };

    public GoalsRecord With(PreferencesRecord preferences) => this with { Preferences = preferences };
}

public sealed partial class Store
{
    public const string GovernorGoalsKey = "family/goals";

    private const int MaxGoalsBytes = 256 * 1024;

    internal static GoalsRecord? LoadGoalsIn(StoreTransaction tx)
    {
        var r = LoadSingleton<GoalsRecord>(tx, "goals", MaxGoalsBytes);
        return r is null ? null : Validated(r, r.Validate, "goals");
    }

    private static void PutGoalsIn(StoreTransaction tx, GoalsRecord r)
    {
        r.Validate();
        if (r.IsEmpty)
        {
            _ = tx.Execute("DELETE FROM goals");
            return;
        }
        PutSingleton(tx, "goals", r, MaxGoalsBytes);
    }

    /// <summary>
    /// The goals of the world's colony; empty when none or when the record belongs to another colony. A reload of
    /// the same colony keeps them, on whichever map.
    /// </summary>
    public GoalsRecord LoadGoals(World w, CancellationToken cancellationToken) =>
        Run(tx => LoadGoalsIn(tx) is { } r && r.World.Colony == w.Colony ? r with { World = w } : new GoalsRecord { World = w }, cancellationToken);

    public Task<GoalsRecord> LoadGoalsAsync(World w, CancellationToken cancellationToken) => Task.Run(() => LoadGoals(w, cancellationToken), cancellationToken);

    /// <summary>Replaces the stored goals with <paramref name="r"/>; an empty record clears them.</summary>
    public void SaveGoals(GoalsRecord r, CancellationToken cancellationToken) => Run(tx => PutGoalsIn(tx, r), cancellationToken);
}
