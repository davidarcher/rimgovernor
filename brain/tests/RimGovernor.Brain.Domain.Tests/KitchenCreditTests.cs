using System.Globalization;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// #3802: a served kitchen is credited the food poisoning its meals avoid, the stat's roomless chance less the planned
/// kitchen's at its predicted cleanliness (#3835: its own planned cooking, not the cook's current bench), times its meals a
/// day and the ledger's price of one poisoning; a served butchery,
/// the poisoning its blood would cause in the kitchen it keeps it out of.
/// </summary>
public sealed class KitchenCreditTests
{
    private const double Day = GameTime.TicksPerDay;
    private const double Horizon = 10;
    // FoodPoisonChance's roomless score: a campfire outdoors.
    private const double Campfire = .02;
    private static readonly FilthModel Filth = BedStationPriceTests.Filth with { FoodPoisonRoomless = Campfire };
    private static readonly Dictionary<string, FilthRuns> Blood = new() { [BedStationPriceTests.Butcher] = new() { Runs = 2, Ticks = 1500 } };

    private static PlannedRoom Room(int id, PlannedRole role, double meals, IReadOnlyDictionary<string, FilthRuns>? runs = null) =>
        new() { Id = id, Role = role, Meals = meals, FilthRuns = runs ?? new Dictionary<string, FilthRuns>() };

    private static ForecastPlannedClosure Closure(int room, double cleanliness, string role = "Kitchen") =>
        new(room, "WoodLog", [], ["planned/room" + room.ToString(CultureInfo.InvariantCulture) + "/wall/WoodLog"], [],
            PlannedRoomPredictionTests.Closed(room.ToString(CultureInfo.InvariantCulture), "WoodLog", role, 30d, 2, 9, 0, false) with { Cleanliness = cleanliness });

    private static ForecastCoupledContext Context(PlannedRoom[] rooms, ForecastPlannedClosure[] closures) =>
        ForecastValueTests.Context(ForecastValueTests.Pawn()) with
        {
            PlannedRoomRoles = rooms.ToDictionary(r => r.Id, r => r.Role),
            LayoutRooms = rooms.ToDictionary(r => r.Id),
            Closures = EquatableArray.From(closures),
            InvestmentHorizonDays = Horizon,
            Filth = Filth,
        };

    private static ForecastCoupledOutcome Serving(int room, double poisonCases = 0) =>
        new(new([], 0, 0), true, new Dictionary<string, ForecastNeedsResult>
        {
            ["pawn"] = new([], new ForecastTerm.Unassessed("not_projected"), new ForecastTerm.Unassessed("not_projected"), new(new("pawn", "", new ForecastCapacityWindows.Unread(), 0), []), []),
        }, [])
        { ServedRooms = [new(room, "WoodLog", 0)], Support = new() { FoodPoisonExposure = poisonCases } };

    private static double Curve(double cleanliness) => LayoutFilthRules.EvaluateCurve(Filth.FoodPoison, cleanliness);

    /// <summary>The price of one poisoning: the ledger's poisoning term for one expected case.</summary>
    private static double Price(ForecastCoupledContext context, int room)
    {
        var price = ForecastValue.PoisoningPrice(Serving(room), context, Day);
        Assert.Equal(ForecastValue.Of(Serving(room, poisonCases: 1), context, Day).FoodPoisoning.Loss(), price, 12);
        return price;
    }

    [Fact]
    public void ACookAtAnOutdoorCampfireIsCreditedTheChanceThePlannedKitchenSavesItsMeals()
    {
        // A cook at an outdoor campfire (the roomless .02) against a planned kitchen at -1 (.012 on the test curve), 6 meals a day.
        var context = Context([Room(1, PlannedRole.Kitchen, meals: 6)], [Closure(1, -1)]);
        var price = Price(context, 1);
        Assert.Equal(.5, price, 12);
        var row = Assert.Single(ForecastValue.Of(Serving(1), context, Day).InvestmentContributions, r => r.Key.StartsWith("kitchen/", StringComparison.Ordinal));
        Assert.Equal((PlannedRoomCredits.Key(PlannedRole.Kitchen, 1, KitchenCredits.Poisoning), Horizon), (row.Key, row.Units));
        Assert.Equal(-((Campfire - Curve(-1)) * 6 * price) * Horizon, row.Value, 12);
    }

    [Fact]
    public void AButcheryMovingItsBloodOutOfTheKitchenIsCreditedTheKitchensPoisoningChange()
    {
        // Two runs a day of 1500 ticks leave one unit of Filth_Blood (-10) standing in the 9-cell kitchen for 2100 ticks a day.
        var context = Context([Room(1, PlannedRole.Kitchen, meals: 6), Room(2, PlannedRole.Butchery, meals: 0, Blood)], [Closure(1, -2), Closure(2, -1, "Workshop")]);
        const double exposure = 2100d / GameTime.TicksPerDay;
        var delta = (1 - exposure) * Curve(-2) + exposure * Curve(-2 - (10 / 9d)) - Curve(-2);
        Assert.True(delta > 0);
        var price = Price(context, 2);
        var row = Assert.Single(ForecastValue.Of(Serving(2), context, Day).InvestmentContributions, r => r.Key.StartsWith("butchery/", StringComparison.Ordinal));
        Assert.Equal((PlannedRoomCredits.Key(PlannedRole.Butchery, 2, "1"), Horizon), (row.Key, row.Units));
        Assert.Equal(-(delta * 6 * price) * Horizon, row.Value, 12);
    }

    /// <summary>#3835: a kitchen shape whose ring closes no room (native answers no prediction row, #3834) leaves its stations
    /// roomless, as they are now: it saves no poisoning.</summary>
    [Fact]
    public void AKitchenThatClosesNoRoomSavesNothing()
    {
        var context = Context([Room(1, PlannedRole.Kitchen, meals: 6)], []);
        var row = Assert.Single(ForecastValue.Of(Serving(1), context, Day).InvestmentContributions, r => r.Key.StartsWith("kitchen/", StringComparison.Ordinal));
        Assert.Equal(0, row.Value, 12);
    }

    /// <summary>#3835: a kitchen whose layout group plans no meals is the no-cooking state, worth nothing.</summary>
    [Fact]
    public void AKitchenWithoutPlannedMealsEarnsNothing()
    {
        var context = Context([Room(1, PlannedRole.Kitchen, meals: 0)], [Closure(1, -1)]);
        var ledger = ForecastValue.Of(Serving(1), context, Day);
        Assert.DoesNotContain(ledger.InvestmentContributions, r => r.Key.StartsWith("kitchen/", StringComparison.Ordinal));
        Assert.DoesNotContain(ledger.Findings, f => f.Contains("unassessed:", StringComparison.Ordinal));
    }

    /// <summary>#3835: a planned kitchen without its layout room (a room the plan no longer holds) throws a required fact.</summary>
    [Fact]
    public void AKitchenWithoutItsLayoutRoomThrowsARequiredFact()
    {
        var context = Context([Room(1, PlannedRole.Kitchen, meals: 6)], [Closure(1, -1)]) with { LayoutRooms = new Dictionary<int, PlannedRoom>() };
        Assert.Throws<RequiredFactException>(() => ForecastValue.Of(Serving(1), context, Day));
    }
}
