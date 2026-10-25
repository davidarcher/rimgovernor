using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class VisitorAdultChanceTests
{
    internal static VisitorGenerationRead Generation(params VisitorPawnOption[] options) => new()
    {
        MinimumPoints = options.Length == 0 ? 0 : options.Min(o => o.Cost),
        CostCurve = [new(0, 1000)],
        Groups = [new() { Commonality = 1, MaximumPoints = double.MaxValue, Options = [.. options] }],
    };

    internal static readonly VisitorGenerationRead Adults = Generation(new VisitorPawnOption("adult", 50, 50, 1, true, int.MaxValue, false));

    [Fact]
    public void AdultOnlyChildOnlyAndNoGeneratableGroupHaveExactEndpoints()
    {
        Assert.Equal(1, VisitorAdultChance.Of(Adults));
        Assert.Equal(0, VisitorAdultChance.Of(Generation(new VisitorPawnOption("child", 50, 50, 1, false, int.MaxValue, false))));
        Assert.Equal(0, VisitorAdultChance.Of(new()));
        Assert.Equal(0, VisitorAdultChance.Of(Adults with { Groups = [Adults.Groups[0] with { Commonality = 0 }] }));
        // The native ensureCanGenerateAtLeastOnePawn clamp can put all point mass above the curve's endpoint.
        Assert.Equal(1, VisitorAdultChance.Of(Generation(new VisitorPawnOption("adult", 600, 600, 1, true, 1, false))));
        Assert.Throws<RequiredFactException>(() => VisitorAdultChance.Of(Adults with { CostCurve = [] }));
    }

    [Fact]
    public void AnAdultMustFitTheRemainingBudgetAndDrawsUseTheNativeCostFractionWeight()
    {
        var group = Generation(new VisitorPawnOption("adult", 100, 100, 1, true, int.MaxValue, false), new("child", 25, 25, 1, false, int.MaxValue, false));
        Assert.Equal(0, VisitorAdultChance.AtPoints(group, 99));
        // Child's cost fraction 0.25 interpolates 0.01~0.3 to 0.155. Drawing it makes the adult unaffordable.
        Assert.Equal(1 / 1.155, VisitorAdultChance.AtPoints(group, 100), 12);

        group = Generation(new VisitorPawnOption("adult", 30, 30, 1, true, int.MaxValue, false), new("smallChild", 20, 20, 1, false, int.MaxValue, false),
            new("largeChild", 60, 60, 1, false, int.MaxValue, false));
        // The 60-cost option sets highestCost for the entire native loop, even once it stops fitting.
        var at40 = 12 / 17.0;
        var at60 = (1 + (5 / 12.0 * at40)) / (29 / 12.0);
        Assert.Equal((1 + (5 / 12.0 * at60)) / (29 / 12.0), VisitorAdultChance.AtPoints(group, 80), 12);
    }

    [Fact]
    public void KindCapsAreSharedAcrossXenotypesAndOnlyOneLeaderCanBeChosen()
    {
        var group = Generation(new VisitorPawnOption("adult", 50, 50, 1, true, int.MaxValue, false), new("child", 25, 25, 1, false, int.MaxValue, false));
        Assert.Equal(0.875, VisitorAdultChance.AtPoints(group, 100), 12);
        group = Generation(new VisitorPawnOption("adult", 50, 50, 1, true, int.MaxValue, false), new("child", 25, 25, 1, false, 1, false),
            new("child", 40, 25, 1, false, 1, false));
        Assert.Equal(1, VisitorAdultChance.AtPoints(group, 100), 12);
        group = Generation(new VisitorPawnOption("adultLeader", 50, 50, 1, true, int.MaxValue, true), new("childLeader", 25, 25, 1, false, int.MaxValue, true));
        Assert.Equal(0.5, VisitorAdultChance.AtPoints(group, 100), 12);
    }

    [Fact]
    public void VisitorPointsDensityIsIntegratedAcrossEveryGroupSizeBoundaryAndTheMinimumClamp()
    {
        var group = Generation(new VisitorPawnOption("adult", 50, 50, 1, true, int.MaxValue, false), new("child", 50, 50, 1, false, int.MaxValue, false));
        // Analytic trapezoid areas of the native density on each 50-point budget interval. The first
        // includes the 45~50 mass clamped to 50. With n draws, an adult occurs with 1 - (1/2)^n.
        double[] masses = [52.5, 40.625, 21.875, 10.625, 6.875, 4.375, 3.125, 1.875, 0.625];
        var expected = masses.Select((mass, i) => mass * (1 - Math.Pow(0.5, i + 1))).Sum() / 142.5;
        Assert.Equal(expected, VisitorAdultChance.Of(group), 12);
        var weighted = Generation(new VisitorPawnOption("adult", 50, 50, 1, true, int.MaxValue, false), new("child", 50, 50, 3, false, int.MaxValue, false));
        Assert.Equal(0.25, VisitorAdultChance.AtPoints(weighted, 50), 12);
        Assert.Equal(1 - Math.Pow(0.75, 2), VisitorAdultChance.AtPoints(weighted, 100), 12);
    }

    [Fact]
    public void MakerCommonalityAndMaximumPointsAreAppliedBeforeThePawnDraw()
    {
        var child = Generation(new VisitorPawnOption("child", 50, 50, 1, false, 1, false));
        var read = child with { Groups = [child.Groups[0], Adults.Groups[0] with { Commonality = 3, MaximumPoints = 100 }] };
        Assert.Equal(0.75, VisitorAdultChance.AtPoints(read, 75), 12);
        Assert.Equal(0, VisitorAdultChance.AtPoints(read, 101));
        Assert.Equal(0.75 * 52.5 / 142.5, VisitorAdultChance.Of(read), 12);
    }

    [Fact]
    public void TheFactionCapUsesBaseCostWhileXenotypeCostSpendsThePoints()
    {
        var read = Generation(new VisitorPawnOption("adult", 100, 50, 1, true, 1, false), new("child", 25, 25, 1, false, 1, false))
            with { MinimumPoints = 25, CostCurve = [new(0, 0), new(500, 100)] };
        Assert.Equal(0, VisitorAdultChance.AtPoints(read, 249));
        Assert.Equal(1, VisitorAdultChance.AtPoints(read, 251), 12);
        // Cap reaches base cost 50 at 250; the integrated adult probability is exactly the density above it.
        Assert.Equal((6.875 + 10) / 142.5, VisitorAdultChance.Of(read), 12);
    }
}
