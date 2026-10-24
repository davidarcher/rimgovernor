using System.Globalization;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// #3802: a served kitchen is credited the food poisoning its meals avoid, the cook's current room chance less the planned
/// kitchen's at its predicted cleanliness, times its meals a day and the ledger's price of one poisoning; a served butchery,
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
            new(room.ToString(CultureInfo.InvariantCulture), "WoodLog", role, Known(30d), Known(2), 9, 0, false) { Cleanliness = cleanliness });

    private static ForecastCoupledContext Context(PlannedRoom[] rooms, ForecastPlannedClosure[] closures, bool cooking = true) =>
        ForecastValueTests.Context(ForecastValueTests.Pawn()) with
        {
            PlannedRoomRoles = rooms.ToDictionary(r => r.Id, r => r.Role),
            LayoutRooms = rooms.ToDictionary(r => r.Id),
            Closures = EquatableArray.From(closures),
            InvestmentHorizonDays = Horizon,
            Support = new() { Known = true, Demands = [new("campfire", "bench", "campfire", new(1, 1), Campfire, Known(0d))] },
            CookingBenches = cooking ? ["campfire"] : [],
            Filth = Filth,
        };

    private static ForecastCoupledOutcome Serving(int room, double poisonCases = 0) =>
        new(new([], 0, 0), Known(true), new Dictionary<string, ForecastNeedsResult>
        {
            ["pawn"] = new([], default, default, new(new("pawn", "", default, default), []), []),
        }, [])
        { ServedRooms = [new(room, "WoodLog", 0)], Support = new() { FoodPoisonExposure = poisonCases } };

    private static double Curve(double cleanliness) => LayoutFilthRules.EvaluateCurve(Filth.FoodPoison, cleanliness);

    /// <summary>The price of one poisoning: the ledger's poisoning term for one expected case.</summary>
    private static double Price(ForecastCoupledContext context, int room)
    {
        var price = ForecastValue.PoisoningPrice(Serving(room), context, Day)!.Value;
        Assert.Equal(ForecastValue.Of(Serving(room, poisonCases: 1), context, Day).FoodPoisoning.Value, price, 12);
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

    /// <summary>An unread prediction (Role "") or no bench cooking now leaves the cook's change unknown: the kitchen is named, not credited.</summary>
    [Theory]
    [InlineData("", true)]
    [InlineData("Kitchen", false)]
    public void AnUnknownCleanlinessOrCurrentRoomIsUnassessed(string role, bool cooking)
    {
        var context = Context([Room(1, PlannedRole.Kitchen, meals: 6)], [Closure(1, 0, role)], cooking);
        var ledger = ForecastValue.Of(Serving(1), context, Day);
        Assert.DoesNotContain(ledger.InvestmentContributions, r => r.Key.StartsWith("kitchen/", StringComparison.Ordinal));
        Assert.Contains("kitchen_credit_unassessed:1", ledger.Findings);
    }
}
