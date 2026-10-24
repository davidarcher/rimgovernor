using RimGovernor.Brain.PlayerApi;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>The goals window's Themes tab: adopting a preset, overrides, removal, composing and progress (#3502).</summary>
public sealed class GoalsThemesTests
{
    private static readonly RimGovernor.Brain.Store.World Home = new(new("colony"), new("load"), new(1));

    private sealed class Installed(params string[] names) : IPresetCatalog
    {
        public bool Has(PresetDefClass kind, string name) => names.Contains(name);
    }

    private static PresetBook Book(params string[] installed) => new(
        [
            new ThemePreset(
                "demo",
                "Demo",
                [
                    new PresetMember("stock.Steel", new GoalRecord { Kind = GoalKind.StockCount, Target = "Steel", Count = 100 }),
                    new PresetMember("research.Smithing", new GoalRecord { Kind = GoalKind.ResearchProject, Target = "Smithing" }),
                    new PresetMember("built.Wall", new GoalRecord { Kind = GoalKind.BuiltCount, Target = "Wall", Count = 4 }),
                    new PresetMember("research.Ghost", new GoalRecord { Kind = GoalKind.ResearchProject, Target = "GhostTech" }),
                    new PresetMember("preference.diet", Preference: new PresetPreference.Diet([DietOverride.Cannibal])),
                ]),
        ],
        new Installed(installed));

    private static readonly PresetBook Full = Book("Steel", "Smithing", "Wall");

    private static GoalsRecord Empty => new() { World = Home };

    private static GoalsRecord Adopted(Importance importance = Importance.High, int? deadline = 30) => Ok(ThemeEdits.Adopt(Empty, Full, "demo", importance, deadline));

    private static GoalsRecord Ok(GoalEdit edit)
    {
        Assert.True(edit.Ok, edit.Error);
        return edit.Record;
    }

    private static GoalStatus Met(GoalRecord goal) => new(goal, Satisfaction.Met, GoalState.Met, "", new NeedId(""), GoalDeficit.Of(1.0));

    private static GoalStatus Unmet(GoalRecord goal) => new(goal, Satisfaction.Unmet, GoalState.Pursuing, "", new NeedId("economy/resource"), GoalDeficit.Of(1.0));

    [Fact]
    public void AdoptingMintsTheAvailableMembersWithTheThemesImportanceAndDeadline()
    {
        var record = Adopted();

        var theme = Assert.Single(record.Themes);
        Assert.Equal("demo", theme.Preset);
        Assert.Equal(3, record.Goals.Count);
        Assert.Equal(theme.Members, record.Goals.Select(g => g.Id));
        Assert.All(record.Goals, g =>
        {
            Assert.Equal(theme.Id, g.Theme);
            Assert.Equal(Importance.High, g.Importance);
            Assert.Equal(30, g.DeadlineDay);
        });
        Assert.DoesNotContain(record.Goals, g => g.Target == "GhostTech");
    }

    [Fact]
    public void AnUnavailableMemberShowsTheMissingDefAndIsNotMinted()
    {
        var model = GoalsViewModel.From(new GoalsSnapshot(Adopted(), [], []), Full);

        var ghost = model.Themes.Single().Members.Single(m => m.Key == "research.Ghost");
        Assert.Equal("Unavailable", ghost.State);
        Assert.Contains("GhostTech", ghost.Detail);
        Assert.Null(ghost.GoalId);
        var preset = model.Presets.Single();
        Assert.True(preset.Adopted);
        Assert.Equal(["stock.Steel", "research.Smithing", "built.Wall", "preference.diet"], preset.Members.Where(m => m.Available).Select(m => m.Key));
        Assert.Contains("GhostTech", preset.Members.Single(m => !m.Available).Reason);
    }

    [Fact]
    public void ADlcLessCatalogLeavesThosePresetMembersUnavailableBeforeAdopting()
    {
        var preset = GoalsViewModel.From(new GoalsSnapshot(Empty, [], []), Book("Steel")).Presets.Single();

        Assert.False(preset.Adopted);
        Assert.Equal(["research.Smithing", "built.Wall", "research.Ghost"], preset.Members.Where(m => !m.Available).Select(m => m.Key));
    }

    [Fact]
    public void AdoptingTheSamePresetTwiceIsRefused()
    {
        var again = ThemeEdits.Adopt(Adopted(), Full, "demo", Importance.Low, null);

        Assert.False(again.Ok);
        Assert.Contains("already adopted", again.Error);
    }

    [Fact]
    public void AnOverrideChangesOneMemberOnly()
    {
        var record = Adopted();
        var steel = record.Goals.Single(g => g.Target == "Steel");

        var overridden = Ok(ThemeEdits.OverrideMember(record, steel.Id, Importance.Low, 5));

        var members = GoalsViewModel.From(new GoalsSnapshot(overridden, [], []), Full).Themes.Single().Members.Where(m => m.GoalId is not null).ToList();
        Assert.Equal(["Low", "High", "High"], members.Select(m => m.Importance));
        Assert.Equal(["day 5", "day 30", "day 30"], members.Select(m => m.Deadline));
        Assert.Equal(Importance.High, overridden.Goals.Single(g => g.Target == "Smithing").Importance);
        Assert.Null(overridden.Goals.Single(g => g.Target == "Smithing").ImportanceOverride);
    }

    [Fact]
    public void RemovingAMemberRecordsTheKeyAndTheGoalDisappearsAndRestoringBringsItBack()
    {
        var record = Adopted();
        var theme = record.Themes.Single();

        var removed = Ok(ThemeEdits.RemoveMember(record, Full, theme.Id, "built.Wall"));

        Assert.Equal(["built.Wall"], removed.Themes.Single().RemovedPresetMembers);
        Assert.DoesNotContain(removed.Goals, g => g.Target == "Wall");
        Assert.Equal(removed.Goals.Select(g => g.Id), removed.Themes.Single().Members);
        var view = GoalsViewModel.From(new GoalsSnapshot(removed, [], []), Full).Themes.Single().Members;
        Assert.True(view.Single(m => m.Key == "built.Wall").Restorable);

        var restored = Ok(ThemeEdits.RestoreMember(removed, Full, theme.Id, "built.Wall"));

        Assert.Empty(restored.Themes.Single().RemovedPresetMembers);
        var wall = restored.Goals.Single(g => g.Target == "Wall");
        Assert.Equal(Importance.High, wall.Importance);
        Assert.Contains(wall.Id, restored.Themes.Single().Members);
        Assert.False(ThemeEdits.RestoreMember(restored, Full, theme.Id, "built.Wall").Ok);
    }

    [Fact]
    public void RemovingAnUnavailableMemberJustRecordsTheKey()
    {
        var record = Adopted();

        var removed = Ok(ThemeEdits.RemoveMember(record, Full, record.Themes.Single().Id, "research.Ghost"));

        Assert.Equal(record.Goals, removed.Goals);
        Assert.Equal(["research.Ghost"], removed.Themes.Single().RemovedPresetMembers);
    }

    [Fact]
    public void ComposingAThemeFromThreeGoalsYieldsAnAuthoredThemeWithThoseMembers()
    {
        var record = Empty;
        foreach (var kind in new[] { GoalKind.Colonists, GoalKind.Caravan, GoalKind.PowerSurplus, GoalKind.Colonists })
        {
            record = record.WithNewGoal(new GoalRecord { Kind = kind, Count = 1 });
        }

        var composed = Ok(ThemeEdits.Compose(record, " Expansion ", Importance.High, 40, [1, 3, 2]));

        var theme = Assert.Single(composed.Themes);
        Assert.Equal("", theme.Preset);
        Assert.Equal("Expansion", theme.Name);
        Assert.Equal([1, 2, 3], theme.Members);
        Assert.All(composed.Goals.Where(g => g.Id <= 3), g => Assert.Equal(theme.Id, g.Theme));
        Assert.All(composed.Goals.Where(g => g.Id <= 3), g => Assert.Equal(Importance.High, g.Importance));
        Assert.Null(composed.Goals.Single(g => g.Id == 4).Theme);
        Assert.Equal([1, 2, 3], GoalsViewModel.From(new GoalsSnapshot(composed, [], []), Full).Themes.Single().Members.Select(m => m.GoalId!.Value));
        Assert.False(ThemeEdits.Compose(composed, "Again", Importance.Low, null, [1]).Ok);
        Assert.False(ThemeEdits.Compose(record, " ", Importance.Low, null, [1]).Ok);
        Assert.False(ThemeEdits.Compose(record, "None", Importance.Low, null, []).Ok);
    }

    [Fact]
    public void ProgressIsTheValueWeightedFractionMet()
    {
        var record = Adopted(Importance.Normal);
        var steel = record.Goals.Single(g => g.Target == "Steel");
        var smithing = record.Goals.Single(g => g.Target == "Smithing");
        record = Ok(ThemeEdits.OverrideMember(record, steel.Id, Importance.High, null));
        steel = record.Goals.Single(g => g.Target == "Steel");
        var wall = record.Goals.Single(g => g.Target == "Wall");

        var statuses = new[] { Met(steel), Unmet(smithing), Unmet(wall) };
        var theme = GoalsViewModel.From(new GoalsSnapshot(record, statuses, []), Full).Themes.Single();

        // High (4) met of High (4) + Normal (2) + Normal (2).
        Assert.Equal(0.5, theme.Progress);
        Assert.Equal("50% of value met", theme.ProgressText);
    }
}
