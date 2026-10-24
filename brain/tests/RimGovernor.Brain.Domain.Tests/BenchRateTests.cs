using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class BenchRateTests
{
    [Fact]
    public void PositiveDemandAgainstZeroCapacityIsNoCapacity()
    {
        Assert.IsType<BenchesWanted.NoCapacity>(BenchRateRules.Wanted(3, 0));
        // No worker at the table is a zero capacity, so a bench that makes nothing wants no count of benches.
        var none = BenchRateRules.BenchCapacity(new BenchRecipe { WorkPerUnit = 100, BenchWorkSpeed = 1 }, []);
        Assert.Equal(0, none);
        Assert.IsType<BenchesWanted.NoCapacity>(BenchRateRules.Wanted(1, none));
    }

    [Fact]
    public void ZeroDemandWantsNoBenchesWhateverTheCapacity()
    {
        Assert.Equal(new BenchesWanted.Count(0), BenchRateRules.Wanted(0, 0));
        Assert.Equal(new BenchesWanted.Count(0), BenchRateRules.Wanted(0, 5));
    }

    [Fact]
    public void BenchesWantedRoundsUp()
    {
        Assert.Equal(new BenchesWanted.Count(1), BenchRateRules.Wanted(5, 5));
        Assert.Equal(new BenchesWanted.Count(3), BenchRateRules.Wanted(11, 5));
    }

    [Fact]
    public void CapacityIsWorkerTicksAtBenchSpeedOverRecipeWork()
    {
        var recipe = new BenchRecipe { WorkPerUnit = GameTime.TicksPerHour, BenchWorkSpeed = 0.5 };
        var worker = new BenchWorker { WorkHoursPerDay = 8, WorkSpeed = 1.5 };
        Assert.Equal(8 * 1.5 * 0.5, BenchRateRules.BenchCapacity(recipe, [worker]), 9);
    }

    [Fact]
    public void RefillWindowNamesEveryClass()
    {
        Assert.Equal(BenchRateRules.FoodRefillWindowDays, BenchRateRules.RefillWindowDays(ResourceClass.Food));
        Assert.Equal(BenchRateRules.MaterialRefillWindowDays, BenchRateRules.RefillWindowDays(ResourceClass.Material));
        Assert.Throws<DomainException>(() => BenchRateRules.RefillWindowDays(ResourceClass.None));
        Assert.Equal(2, BenchRateRules.DemandRate(6, ResourceClass.Material));
        Assert.Equal(0, BenchRateRules.DemandRate(-4, ResourceClass.Food));
    }

    [Fact]
    public void InvalidInputsThrow()
    {
        Assert.Throws<DomainException>(() => BenchRateRules.Wanted(double.NaN, 1));
        Assert.Throws<DomainException>(() => BenchRateRules.Wanted(1, -1));
        Assert.Throws<DomainException>(() => BenchRateRules.BenchCapacity(new BenchRecipe { WorkPerUnit = 0, BenchWorkSpeed = 1 }, []));
        Assert.Throws<DomainException>(() => BenchRateRules.DemandRate(double.PositiveInfinity, ResourceClass.Food));
    }
}
