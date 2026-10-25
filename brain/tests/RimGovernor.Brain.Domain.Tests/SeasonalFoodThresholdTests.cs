using RimGovernor.Brain.Policy;
using O = RimGovernor.Contracts.Wire.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The seasonal food thresholds across the frost (#251): the harvest gap is fully phased in on the last growing day, so
/// the first non-growing day holds the larder to the thresholds the colony already filled to, and midsummer asks for less.
/// </summary>
public sealed class SeasonalFoodThresholdTests
{
    private const double NonGrowing = 20;


    private static readonly O.FoodClimate LastGrowingDay = new()
    {
        GrowingYear = EquatableArray.From(Enumerable.Range(0, CalendarRules.YearDays).Select(day => day == 0 || day > 20)),
        SowingNow = true,
        Season = "Fall",
        DayOfYear = 39,
        GrowingDays = 40,
        GrowingDaysRemaining = 1,
        GrowingDaysUntil = 0,
        NonGrowingDays = NonGrowing,
    };

    private static RoundsPolicy Seasonal(O.FoodClimate calendar) => RoundsRules.DefaultRoundsPolicy().Seasonal(calendar, default(EquatableArray<DisasterCondition>));

    [Fact]
    public void ThresholdsDoNotJumpAtTheFrost()
    {
        var firstNonGrowingDay = LastGrowingDay with
        {
            DayOfYear = 40, GrowingDaysRemaining = 0, GrowingDaysUntil = NonGrowing, SowingNow = false,
            GrowingYear = EquatableArray.From(Enumerable.Range(0, CalendarRules.YearDays).Select(day => day >= 20)),
        };

        var before = Seasonal(LastGrowingDay);
        var after = Seasonal(firstNonGrowingDay);

        Assert.Equal(before.FoodMinDays, after.FoodMinDays, 9);
        Assert.Equal(before.FoodTargetDays, after.FoodTargetDays, 9);
        Assert.True(after.FoodMinDays > RoundsRules.DefaultRoundsPolicy().FoodMinDays);
    }

    [Fact]
    public void MidsummerPhasesTheGapIn()
    {
        var midsummer = LastGrowingDay with
        {
            DayOfYear = 20, GrowingDaysRemaining = 20,
            GrowingYear = EquatableArray.From(Enumerable.Range(0, CalendarRules.YearDays).Select(day => day < 20 || day >= 40)),
        };

        Assert.True(Seasonal(midsummer).FoodMinDays < Seasonal(LastGrowingDay).FoodMinDays);
    }
}
