using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// #3796: the per-role planned-room credit seam. A room serves from the day its one reconcile owes nothing (epic #3650 P2),
/// its credit runs perDay from then to tier 2's horizon, and a role is priced exactly when it has a credit (P3). The
/// storage credit here is a test-only one of a capability-day a day; no non-bedroom role ships a credit.
/// </summary>
public sealed class PlannedRoomCreditTests
{
    private const double Day = GameTime.TicksPerDay;
    private const double Horizon = 10;
    private const string Wall = "planned/room1/wall/WoodLog", Shelf = "planned/room1/shelf", Roof = "planned/room1/roof";
    private const string Delivery = "planned/room1/wall/WoodLog/delivery";

    /// <summary>The test-only storage credit: one capability-day a served day, booked to no pawn.</summary>
    private static readonly IReadOnlyDictionary<PlannedRole, RoomCredit> StorageCredit = new Dictionary<PlannedRole, RoomCredit>
    {
        [PlannedRole.Storage] = (_, _, _, _) => [new("shelves", "", 1)],
    };

    private static ForecastPlannedSite Site(string id, string stuff = "") => new(id, 1, stuff, id == Wall ? [Delivery] : [], [id + "/finish"]);

    /// <summary>
    /// Room 1 a storage room, started, one builder: its wood wall ring two builder-days, a stuff-less roof and shelves a
    /// builder-day, a floor blueprint and a removal on it, and a removal on its shared wall with room 2.
    /// </summary>
    private static ForecastCoupledContext Context(IReadOnlyDictionary<PlannedRole, RoomCredit> credits, double shelves = Day, double removal = 0) =>
        ForecastValueTests.Context(ForecastValueTests.Pawn()) with
        {
            Activities = new() { Activities = [new(Delivery, "stack", "planned/room1", []) { Inputs = [new(new("WoodLog"), Known(10d))] }] },
            PlannedRoomRoles = new Dictionary<int, PlannedRole> { [1] = PlannedRole.Storage, [2] = PlannedRole.Workshop },
            RoomCredits = credits,
            PlannedSites = [Site(Wall, "WoodLog"), Site(Roof), Site(Shelf)],
            Closures = [new(1, "WoodLog", [new(1, 1)], [Wall], [Roof], new("1", "WoodLog", "Storage", Known(10d), Known(1), 4, 0, false))],
            RoomItems = [new("floor-1", [1], ["site-0-finish"]), new("rubble-1", [1], ["removal-1"]), new("shared-1", [1, 2], ["removal-2"])],
            OwedWork = new Dictionary<string, double>
            {
                [Wall] = 2 * Day, [Roof] = 0, [Shelf] = shelves, ["floor-1"] = 0, ["rubble-1"] = removal, ["shared-1"] = 0,
            },
            OpenRooms = new HashSet<int> { 1 },
            PlannedBuilders = 1,
            InvestmentHorizonDays = Horizon,
        };

    private static ForecastCoupledOutcome Outcome(ForecastCoupledContext context, IReadOnlyDictionary<string, double>? finished = null)
    {
        var service = PlannedRoomCredits.ServiceDays(context, finished ?? new Dictionary<string, double>(), Day);
        return new(new([], 0, 0), Known(true), new Dictionary<string, ForecastNeedsResult>(), [])
        {
            ServedRooms = service.Served, UnassessedRooms = service.Unassessed,
            RequestedDevelopment = [Wall, Shelf, Roof, "planned/room2/wall/WoodLog"],
        };
    }

    private static ForecastInvestmentContribution[] Credits(ForecastCoupledContext context, ForecastCoupledOutcome outcome) =>
        [.. ForecastValue.Of(outcome, context, Day).InvestmentContributions.Where(r => r.Key.StartsWith("storage/", StringComparison.Ordinal))];

    private static readonly string[] ReliefNeeds = ["flooring/floor-1", "removal/rubble-1", "removal/shared-1", "flooring/elsewhere"];

    /// <summary>A floor's and two removals' relief, as the placed-site and removal roots request it, and a floor of no planned room.</summary>
    private static ForecastCommitment[] Relief() =>
        [.. ReliefNeeds.Select(need => new ForecastCommitment(need, [])
        {
            Outcome = new(need, "construction-method", need, Known(Day), default)
            { Requirements = new([new(need, ProductionNeedPriority.Maintenance, 1.0)], default, default) },
        })];

    private static double Development(ForecastCoupledContext context) =>
        ForecastCoupledSelection.CoupledConsequences(Outcome(context), Relief(), context, Day, Outcome(context), null).Development;

    [Fact]
    public void AStorageRoomClosingAtDayTwoWithADayOfShelvesIsCreditedFromDayThree()
    {
        var context = Context(StorageCredit);
        var outcome = Outcome(context);
        Assert.Equal(new ForecastRoomService(1, "WoodLog", 3 * Day), Assert.Single(outcome.ServedRooms));
        var row = Assert.Single(Credits(context, outcome));
        Assert.Equal(("", "storage/1/shelves", Horizon - 3), (row.Pawn, row.Key, row.Units));
        Assert.Equal(-(Horizon - 3), row.Value, 12);
        // A removal's work is the room's owed work too: a day of it serves the room a day later.
        Assert.Equal(Horizon - 4, Assert.Single(Credits(Context(StorageCredit, removal: Day), Outcome(Context(StorageCredit, removal: Day)))).Units, 9);
    }

    [Fact]
    public void APricedRoomsSitesRoofFloorAndRemovalsLeaveTheDevelopmentCount()
    {
        // Unpriced: its three units, room 2's wall and the four reliefs count. Priced: room 2's wall and the floor elsewhere,
        // the shared wall's removal leaving with the priced room.
        Assert.Equal(8, Development(Context(new Dictionary<PlannedRole, RoomCredit>())));
        Assert.Equal(2, Development(Context(StorageCredit)));
        Assert.Contains("removal/shared-1", PlannedRoomCredits.PricedUnits(Context(StorageCredit)));
    }

    [Fact]
    public void ARoleWithNoCreditCountsAsDevelopmentAndIsChargedNoMaterial()
    {
        var unpriced = Context(new Dictionary<PlannedRole, RoomCredit>());
        Assert.False(unpriced.Priced(1));
        Assert.Empty(PlannedRoomCredits.PricedUnits(unpriced));
        Assert.Empty(ForecastValue.ChargedMaterials(unpriced));
        Assert.Empty(Credits(unpriced, Outcome(unpriced)));
        var priced = Context(StorageCredit);
        Assert.True(priced.Priced(1));
        Assert.Equal([new Resource("WoodLog")], ForecastValue.ChargedMaterials(priced));
    }

    [Fact]
    public void ARinglessRoomServesWhenItsSitesAreDone()
    {
        // Room 1 owes no ring: no closure, only its shelves and the floor.
        var context = Context(StorageCredit) with { Closures = [], PlannedSites = [Site(Shelf)], RoomItems = [new("floor-1", [1], ["site-0-finish"])] };
        var finished = new Dictionary<string, double> { [Shelf + "/finish"] = 900, ["site-0-finish"] = 400 };
        Assert.Equal(new ForecastRoomService(1, "", 900), Assert.Single(Outcome(context, finished).ServedRooms));
        // Left owing, it serves at its owed work over the builder, never inside the horizon.
        Assert.Equal(new ForecastRoomService(1, "", Day), Assert.Single(Outcome(context, new Dictionary<string, double> { ["site-0-finish"] = 400 }).ServedRooms));
        Assert.Equal(2 * Day, Assert.Single(Outcome(context with { OwedWork = new Dictionary<string, double> { [Shelf] = 2 * Day, ["floor-1"] = 0 } }).ServedRooms).ServedAt);
    }

    [Fact]
    public void APricedRoomWithoutBuildersOrWithAnUnknownCreditIsUnassessedAndCreditedNothing()
    {
        var unbuilt = Context(StorageCredit) with { PlannedBuilders = 0 };
        var ledger = ForecastValue.Of(Outcome(unbuilt), unbuilt, Day);
        Assert.Contains("storage_credit_unassessed:1", ledger.Findings);
        Assert.DoesNotContain(ledger.InvestmentContributions, r => r.Key.StartsWith("storage/", StringComparison.Ordinal));
        var unknown = Context(new Dictionary<PlannedRole, RoomCredit> { [PlannedRole.Storage] = (_, _, _, _) => null });
        ledger = ForecastValue.Of(Outcome(unknown), unknown, Day);
        Assert.Contains("storage_credit_unassessed:1", ledger.Findings);
        Assert.DoesNotContain(ledger.InvestmentContributions, r => r.Key.StartsWith("storage/", StringComparison.Ordinal));
    }
}
