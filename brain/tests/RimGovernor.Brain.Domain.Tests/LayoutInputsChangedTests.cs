using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>The hourly layout check names what drifted since the last replan (the layout_replan_tiled row's inputs_changed).</summary>
public sealed class LayoutInputsChangedTests
{
    [Fact]
    public void ChangedNamesThePlanTickThenEachDriftedPartInOrder()
    {
        var before = new LayoutInputs(new Tick(10), "a", default, default)
        {
            Parts = new Dictionary<string, string> { ["stock"] = "1", ["orders"] = "x", ["gone"] = "g" },
        };
        var after = new LayoutInputs(new Tick(20), "b", default, default)
        {
            Parts = new Dictionary<string, string> { ["stock"] = "2", ["orders"] = "x", ["pawns"] = "3" },
        };

        Assert.Equal(["plan_tick", "gone", "pawns", "stock"], after.Changed(before));
        Assert.Empty(after.Changed(after));
    }

    [Fact]
    public void AKeyThatDiffersWithNoPartToBlameIsNamedKey()
    {
        var a = new LayoutInputs(default, "a", default, default);
        var b = new LayoutInputs(default, "b", default, default);

        Assert.Equal(["key"], b.Changed(a));
        Assert.True(!b.Same(a));
    }

    [Fact]
    public void StationInputsKeyIsItsNamedPartsJoined()
    {
        var f = new StationFacts();

        var parts = f.InputsKeyParts();

        Assert.Equal(string.Concat(parts.Select(p => p.Value)), f.InputsKey());
        Assert.Contains(parts, p => p.Key == "plan");
        Assert.Contains(parts, p => p.Key == "expected_mood");
        Assert.Contains(parts, p => p.Key is "stock" or "graph");
        Assert.Equal(parts.Count, parts.Select(p => p.Key).Distinct().Count());
    }

    [Fact]
    public void ExpectedMoodKeysInBandsOfFivePercent()
    {
        var low = new StationFacts { ExpectedMood = Known(0.61) };
        var near = new StationFacts { ExpectedMood = Known(0.62) };
        var far = new StationFacts { ExpectedMood = Known(0.71) };
        var unknown = new StationFacts();

        Assert.Equal(low.InputsKey(), near.InputsKey());
        Assert.NotEqual(low.InputsKey(), far.InputsKey());
        Assert.NotEqual(low.InputsKey(), unknown.InputsKey());
        Assert.Null(unknown.ExpectedMoodBand());
    }
}
