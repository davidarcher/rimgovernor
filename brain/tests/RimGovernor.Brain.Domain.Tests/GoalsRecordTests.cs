using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using RimGovernor.Brain.Store.Codec;
using Xunit;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>The player's goals, themes and preferences (#3489): their validation and the <c>family/goals</c> blob across a save and reload.</summary>
public sealed class GoalsRecordTests
{
    private static readonly RimGovernor.Brain.Store.World Home = new(new("colony"), new("load"), new(1));

    private static GoalsRecord Full()
    {
        var record = new GoalsRecord { World = Home }
            .WithNewGoal(new GoalRecord { Kind = GoalKind.ResearchProject, Target = "Smithing", Importance = Importance.High, DeadlineDay = GoalDeadline.Of(30) })
            .WithNewGoal(new GoalRecord { Kind = GoalKind.WornCount, Target = "Apparel_Parka", Qualifier = "Leather_Plain", Count = 4 })
            .WithNewGoal(new GoalRecord { Kind = GoalKind.Skill, Target = "pawn-7", Qualifier = "Shooting", Count = 12 })
            .WithNewTheme(new ThemeRecord { Name = "Winter", Preset = "winter-prep", Importance = Importance.High, DeadlineDay = GoalDeadline.Of(40), RemovedPresetMembers = ["a", "b"] });
        var theme = record.Themes[0];
        record = record.With(theme with { Members = [2, 3] })
            .With(record.Goals[1] with { Theme = ThemeMembership.Of(theme.Id), ImportanceOverride = ImportanceChoice.Of(Importance.Low), DeadlineOverride = GoalDeadline.Of(35) })
            .With(record.Goals[2] with { Theme = ThemeMembership.Of(theme.Id) });
        return record.With(new PreferencesRecord
        {
            RiskProfile = RiskProfile.Cautious, Pace = Pace.Development,
            Forbidden = [ForbiddenMethod.HumanButchery, ForbiddenMethod.Slavery], ProtectedThings = ["t1", "t2"], ProtectedPawns = ["p1"],
            Diet = [DietOverride.Vegetarian], Apparel = ApparelPreference.ArmorOnly, DrugPolicy = DrugPolicyPreference.Medical,
        });
    }

    [Fact]
    public void FullRecordSurvivesStoreJsonAndSaveAndReload()
    {
        var ct = TestContext.Current.CancellationToken;
        var record = Full();
        record.Validate();
        var back = StoreJson.Deserialize<GoalsRecord>(StoreJson.Serialize(record));
        Assert.Equal(record.Goals, back.Goals);
        Assert.Equal(record.Themes[0].Members, back.Themes[0].Members);
        Assert.Equal(record.Themes[0] with { Members = [], RemovedPresetMembers = [] }, back.Themes[0] with { Members = [], RemovedPresetMembers = [] });
        Assert.Equal(record.Preferences.Forbidden, back.Preferences.Forbidden);
        Assert.Equal(record.Preferences with { Forbidden = [], ProtectedThings = [], ProtectedPawns = [], Diet = [] }, back.Preferences with { Forbidden = [], ProtectedThings = [], ProtectedPawns = [], Diet = [] });
        Assert.Equal(record.NextId, back.NextId);

        using var source = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-goals-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        source.SaveGoals(record, ct);
        var saved = source.GovernorStateBlobs(ct);
        Assert.Contains(Journal.GovernorGoalsKey, saved.Keys);

        // A world change rebuilds the table from the save; the same colony reads it back on a new load.
        using var loaded = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-goals-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        loaded.RebuildOwners(null, ct);
        loaded.RebuildFamilies(saved, ct);
        var reloaded = Home with { Load = new("load-2") };
        var restored = loaded.LoadGoals(reloaded, ct);
        Assert.Equal(reloaded, restored.World);
        Assert.Equal(record.Goals, restored.Goals);
        Assert.Equal(record.NextId, restored.NextId);
        Assert.Equal(["a", "b"], restored.Themes[0].RemovedPresetMembers);
        Assert.Equal([ForbiddenMethod.HumanButchery, ForbiddenMethod.Slavery], restored.Preferences.Forbidden);
        Assert.Equal(ApparelPreference.ArmorOnly, restored.Preferences.Apparel);

        // Another colony's save holds none of it.
        Assert.True(loaded.LoadGoals(Home with { Colony = new("other") }, ct).IsEmpty);
    }

    [Fact]
    public void UnsetChoicesAreNamedStatesAndKeepTheNullWireShape()
    {
        Assert.Equal(ThemeMembership.Alone, ThemeMembership.Of(null));
        Assert.Null(ThemeMembership.Alone.ToWire());
        Assert.True(ThemeMembership.Of(4).Is(4));
        Assert.False(ThemeMembership.Alone.Is(4));
        Assert.Equal(4, ThemeMembership.Of(4).ToWire());

        Assert.Equal(ImportanceChoice.Inherited, ImportanceChoice.Of(null));
        Assert.Null(ImportanceChoice.Inherited.ToWire());
        Assert.Equal(Importance.Low, ImportanceChoice.Of(Importance.Low).Or(Importance.High));
        Assert.Equal(Importance.High, ImportanceChoice.Inherited.Or(Importance.High));

        Assert.Equal(GoalDeadline.Of(9), GoalDeadline.Of(9).Or(GoalDeadline.Of(20)));
        Assert.Equal(GoalDeadline.Of(20), GoalDeadline.Unbounded.Or(GoalDeadline.Of(20)));
        Assert.Null(GoalDeadline.Unbounded.ToWire());

        var preferences = new PreferencesRecord();
        Assert.True(preferences.IsDefault);
        Assert.Equal(DrugPolicyPreference.Unmanaged, preferences.DrugPolicy);
        Assert.False((preferences with { DrugPolicy = DrugPolicyPreference.None }).IsDefault);
        var back = StoreJson.Deserialize<PreferencesRecord>(StoreJson.Serialize(preferences));
        Assert.Equal(DrugPolicyPreference.Unmanaged, back.DrugPolicy);
        Assert.Equal(ApparelPreference.Dressed, back.Apparel);
        Assert.Equal(GoalDeadline.Unbounded, StoreJson.Deserialize<GoalRecord>(StoreJson.Serialize(new GoalRecord { Id = 1 })).DeadlineDay);
    }

    [Fact]
    public void EmptyRecordRemovesTheRow()
    {
        var ct = TestContext.Current.CancellationToken;
        using var store = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-goals-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        store.SaveGoals(Full(), ct);
        Assert.Contains(Journal.GovernorGoalsKey, store.GovernorStateBlobs(ct).Keys);
        store.SaveGoals(new GoalsRecord { World = Home }, ct);
        Assert.DoesNotContain(Journal.GovernorGoalsKey, store.GovernorStateBlobs(ct).Keys);
    }

    [Fact]
    public void ValidateRejectsBrokenRecords()
    {
        var full = Full();
        var duplicate = full with { Goals = [full.Goals[0], full.Goals[0] with { Kind = GoalKind.Caravan }, .. full.Goals.Skip(1)] };
        Assert.Throws<DomainException>(duplicate.Validate);

        var orphan = full with { Themes = [full.Themes[0] with { Members = [2, 3, 99] }] };
        Assert.Throws<DomainException>(orphan.Validate);

        var negative = full with { Goals = [full.Goals[0] with { Count = -1 }, .. full.Goals.Skip(1)] };
        Assert.Throws<DomainException>(negative.Validate);

        var late = full with { Goals = [full.Goals[0] with { DeadlineDay = GoalDeadline.Of(0) }, .. full.Goals.Skip(1)] };
        Assert.Throws<DomainException>(late.Validate);

        var unlisted = full with { Themes = [full.Themes[0] with { Members = [2] }] };
        Assert.Throws<DomainException>(unlisted.Validate);
    }
}
