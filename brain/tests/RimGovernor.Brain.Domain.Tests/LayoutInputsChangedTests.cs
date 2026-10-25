using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The hourly layout check names what drifted since the last replan (the layout_replan_tiled row's inputs_changed) and
/// replans on structural drift only, with a daily floor (#3739).
/// </summary>
public sealed class LayoutInputsChangedTests
{
    private static readonly Bounds Map = new(4, 1);

    internal static SurveyCell[] Cells() =>
    [
        new() { Cell = new Cell(0, 0), Walkable = true, Footing = Footing.Firm, Fertility = 1.0 },
        new() { Cell = new Cell(1, 0), Walkable = true, Footing = Footing.Firm, Fertility = 1.0 },
        new() { Cell = new Cell(2, 0), Walkable = true, Footing = Footing.Light, Fertility = 0.7 },
        new() { Cell = new Cell(3, 0), Rock = true, Footing = Footing.Firm, ThickRoof = true },
    ];

    internal static LayoutInputs Inputs(SurveyCell[] cells, string key = "k") => new(new Tick(100), key, Map, [.. cells]);

    [Fact]
    public void ChangedNamesThePlanTickThenEachDriftedPartInOrder()
    {
        var before = new LayoutInputs(new Tick(10), "a", default, default)
        {
            Parts = new Dictionary<string, string> { ["population"] = "1", ["plan"] = "x", ["gone"] = "g" },
        };
        var after = new LayoutInputs(new Tick(20), "b", default, default)
        {
            Parts = new Dictionary<string, string> { ["population"] = "2", ["plan"] = "x", ["pawns"] = "3" },
        };

        Assert.Equal(["plan_tick", "gone", "pawns", "population"], after.Changed(before, false));
        Assert.Equal(["plan_tick", "gone", "pawns", "population"], after.Changed(before, true));
        Assert.Empty(after.Changed(after, false));
    }

    [Fact]
    public void AKeyThatDiffersWithNoPartToBlameIsNamedKey()
    {
        var a = new LayoutInputs(default, "a", default, default);
        var b = new LayoutInputs(default, "b", default, default);

        Assert.Equal(["key"], b.Changed(a, false));
        Assert.True(!b.Same(a));
    }

    [Fact]
    public void SurveysThatDifferOnlyInBuiltTreePropOrRuinCellsAreTheSame()
    {
        var before = Cells();
        var after = Cells();
        after[0] = after[0] with { Built = true };
        after[1] = after[1] with { Tree = true };
        after[2] = after[2] with { Prop = true, Ruin = true };

        Assert.True(Inputs(after).Same(Inputs(before)));
        Assert.Empty(Inputs(after).Changed(Inputs(before), false));
    }

    [Fact]
    public void ARockOrFootingChangeIsDrift()
    {
        var rock = Cells();
        rock[0] = rock[0] with { Rock = true };
        var footing = Cells();
        footing[2] = footing[2] with { Footing = Footing.Firm };

        Assert.False(Inputs(rock).Same(Inputs(Cells())));
        Assert.Equal(["survey"], Inputs(rock).Changed(Inputs(Cells()), false));
        Assert.False(Inputs(footing).Same(Inputs(Cells())));
        Assert.Equal(["survey"], Inputs(footing).Changed(Inputs(Cells()), false));
    }

    [Fact]
    public void StockMoodAndTheOtherHourlyFactsDoNotChangeTheKey()
    {
        var s = FirstLayoutPlanTests.Survey("mild");
        var f = FirstLayoutPlanTests.Facts(s, 3) with { ExpectedMood = new StationMood.Expected(0.4) };
        var drifted = f with
        {
            Stock = new Dictionary<Resource, long> { [new Resource("WoodLog")] = 300 },
            ExpectedMood = new StationMood.Expected(0.8),
            FoodReserveDays = 12,
            OutdoorCurve = EquatableArray.From(new[] { -22.0, -18, -10, -2, 4, 9, 12, 10, 3, -6, -14, -20 }),
        };

        Assert.Equal(f.InputsKey(), drifted.InputsKey());
        var names = f.InputsKeyParts().Select(p => p.Key).ToList();
        Assert.DoesNotContain("graph", names);
        foreach (var gone in new[] { "stock", "orders", "expected_mood", "nutrition_per_day", "food_reserve_days", "crops", "outdoor_curve" })
        {
            Assert.DoesNotContain(gone, names);
        }
        Assert.Contains("population", names);
        Assert.Contains("plan", names);
        Assert.Contains("barracks", names);
    }

    [Fact]
    public void StationInputsKeyIsItsNamedPartsJoined()
    {
        var f = new StationFacts();

        var parts = f.InputsKeyParts();

        Assert.Equal(string.Concat(parts.Select(p => p.Value)), f.InputsKey());
        Assert.Contains(parts, p => p.Key == "plan");
        Assert.Equal(parts.Count, parts.Select(p => p.Key).Distinct().Count());
    }

    [Fact]
    public void ChangedNamesDailyOnTheDailyFloorWhenNothingDrifted()
    {
        Assert.Equal(["daily"], Inputs(Cells()).Changed(Inputs(Cells()), true));
        Assert.Equal(["key"], Inputs(Cells(), "b").Changed(Inputs(Cells()), true));
    }
}
