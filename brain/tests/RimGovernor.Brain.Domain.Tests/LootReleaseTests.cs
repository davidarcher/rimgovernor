using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class LootReleaseTests
{
    [Fact]
    public void NoReleaseNeedsNoDangerRead()
    {
        var empty = LootReleaseRules.FilterLootRelease([], default);
        Assert.Empty(empty.Rows);
        Assert.Empty(empty.Holds);
        var allowed = LootFixtures.Stack("Thing_Steel1", "Steel", new(1, 1), false, new RimGovernor.Brain.GameFacts.Observations.HaulSafety.Safe { PathLength = 1 }, 1, 0, [], null);
        Assert.Equal(allowed, Assert.Single(LootReleaseRules.FilterLootRelease([allowed], default).Rows));
    }

    [Fact]
    public void EmptyLootReviewDoesNotRequireRemoteContext()
    {
        var ct = TestContext.Current.CancellationToken;
        using var journal = RimGovernor.Brain.Store.Store.Open(Path.Combine(Path.GetTempPath(), "rg-empty-loot-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        var facts = WorkResourceTestFacts.Create("worker");
        var reviewed = journal.Review(new()
        {
            Current = new(new("colony"), new(1), new("load"), new("review"), new(1), default),
            Tick = facts.Tick, Enabled = true, Policy = Rules.DefaultPlannerSettings(), Facts = facts,
        }, ct);
        Assert.Empty(reviewed.Review.EventLoot.Pending);
        Assert.Empty(reviewed.Review.EventLoot.Held);
    }

    [Fact]
    public void AnObservedEmptyDangerCensusAllowsASafeForbiddenStack()
    {
        var forbidden = LootFixtures.Stack("Thing_Steel1", "Steel", new(1, 1), true, new RimGovernor.Brain.GameFacts.Observations.HaulSafety.Safe { PathLength = 1 }, 1, 0, [], null);
        Assert.Equal(forbidden, Assert.Single(LootReleaseRules.FilterLootRelease([forbidden], []).Rows));
    }
}
