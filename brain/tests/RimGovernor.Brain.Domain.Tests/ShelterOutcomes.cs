using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>Reads of a shelter outcome's named states for assertions.</summary>
internal static class ShelterOutcomes
{
    public static ForecastShelterOutcome.Projected Projected(this ForecastShelterOutcome outcome) => Assert.IsType<ForecastShelterOutcome.Projected>(outcome);
    public static ForecastShelterOutcome.Scheduled Scheduled(this ForecastShelterOutcome outcome) => Assert.IsAssignableFrom<ForecastShelterOutcome.Scheduled>(outcome);
    public static bool IsProjected(this ForecastShelterOutcome outcome) => outcome is ForecastShelterOutcome.Projected;
    /// <summary>An estimated room that stands open air until its shell completes.</summary>
    public static ForecastShelterThermal Estimated(double initial, double outdoors, double exchange) =>
        new ForecastShelterThermal.Estimated(initial, outdoors, exchange, new ForecastShelterStanding.Open());
    public static ForecastShelterThermal.Estimated Estimated(this ForecastShelterPlan plan) => Assert.IsType<ForecastShelterThermal.Estimated>(plan.Thermal);
}