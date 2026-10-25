using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>A shelter's thermal inputs are estimated or unestimated, its standing room enclosed or open, and its outcome
/// incomplete, unprojected or projected (#3897).</summary>
public sealed class ForecastShelterStateTests
{
    private static readonly ThermalSource Heater = new("heater", new(1, 1), true, .35, 20d, CoupledThermalTests.Powered(1e6));
    private static ForecastShelterPlan Plan(ForecastShelterThermal thermal, params string[] prerequisites) =>
        new(new(ForecastShelterSource.Planned, "1"), [new(1, 1)], EquatableArray.From(prerequisites), thermal, [Heater], []);
    private static ForecastActivityPlan Wall(double end) => new([new("wall", "pawn", 0, 0, 0, end, 0, 0, end, end, 0)], end, end);

    [Fact]
    public void AnEstimatedShelterWhoseShellCompletesIsProjected()
    {
        var outcome = Assert.IsType<ForecastShelterOutcome.Projected>(
            ForecastShelterPolicy.ForecastShelter(Plan(ShelterOutcomes.Estimated(-10, -10, .0001), "wall"), Wall(60), 300));
        Assert.Equal(60, outcome.CompleteAt);
        Assert.Equal(outcome.Trajectory[^1].Temperature, outcome.TemperatureAtHorizon);
    }

    [Fact]
    public void AnUnestimatedShelterCompletesUnprojected()
    {
        var outcome = Assert.IsType<ForecastShelterOutcome.Unprojected>(
            ForecastShelterPolicy.ForecastShelter(Plan(new ForecastShelterThermal.Unestimated(), "wall"), Wall(60), 300));
        Assert.Equal(60, outcome.CompleteAt);
        Assert.Contains("unknown_shelter_thermal_estimate", outcome.Findings);
    }

    [Fact]
    public void AShellBeyondTheHorizonIsUnprojectedAtItsCompletion()
    {
        var outcome = Assert.IsType<ForecastShelterOutcome.Unprojected>(
            ForecastShelterPolicy.ForecastShelter(Plan(ShelterOutcomes.Estimated(-10, -10, .0001), "wall"), Wall(400), 300));
        Assert.Equal(400, outcome.CompleteAt);
        Assert.Contains("shelter_beyond_horizon", outcome.Findings);
    }

    [Fact]
    public void AnUnscheduledPrerequisiteLeavesTheShelterIncomplete()
    {
        var outcome = Assert.IsType<ForecastShelterOutcome.Incomplete>(
            ForecastShelterPolicy.ForecastShelter(Plan(ShelterOutcomes.Estimated(-10, -10, .0001), "roof"), Wall(60), 300));
        Assert.Contains("incomplete_shelter_prerequisites", outcome.Findings);
    }

    [Fact]
    public void OnlyAnEnclosedStandingRoomHoldsItsTemperatureBeforeItsShellCompletes()
    {
        var enclosed = Plan(new ForecastShelterThermal.Estimated(20, -10, .0001, new ForecastShelterStanding.Enclosed(.001)), "wall");
        var standing = ForecastShelterPolicy.Standing(enclosed with { Sources = [] }, 2400);
        Assert.Equal(-10 + 30 * Math.Exp(-.001 * 2400), standing[^1].Temperature, 9);
        Assert.Empty(ForecastShelterPolicy.Standing(Plan(ShelterOutcomes.Estimated(20, -10, .0001), "wall"), 2400));
        Assert.Empty(ForecastShelterPolicy.Standing(Plan(new ForecastShelterThermal.Unestimated(), "wall"), 2400));
    }
}