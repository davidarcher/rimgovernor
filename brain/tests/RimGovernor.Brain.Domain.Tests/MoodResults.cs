using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>Asserts a mood timeline's named state (#3897) and builds the flat projected mood the credit tests price.</summary>
internal static class MoodResults
{
    /// <summary>The timeline, asserted projected.</summary>
    public static ForecastMoodResult.Projected Projected(this ForecastMoodResult? result) => Assert.IsType<ForecastMoodResult.Projected>(result);

    /// <summary>The timeline's breaks, asserted projected and assessed.</summary>
    public static ForecastMoodBreaks.Assessed Assessed(this ForecastMoodResult? result) => Assert.IsType<ForecastMoodBreaks.Assessed>(result.Projected().Breaks);

    /// <summary>A projected mood with only its pressure and assessed break tiers set (no risk, trajectory or findings).</summary>
    public static ForecastMoodResult.Projected Flat(double pressure, EquatableArray<double> tiers, EquatableArray<ForecastShiftedBreaks> shifted = default) =>
        new([], 0, 0, 0, 0, pressure, new ForecastMoodBreaks.Assessed(0, tiers, shifted), []);
}
