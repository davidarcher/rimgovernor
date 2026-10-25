using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Bridge;
using Xunit;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ForecastClaimsTests
{
    private static readonly Resource Material = new("TestMaterial");
    private static readonly Resource Food = new("TestFood");
    private static EquatableArray<Amount> Stock => [new(Material, 100), new(Food, 20)];
    private static EquatableArray<IngredientReservation> NoReservations => [];
    private static ForecastCommitment Promise(string id, Resource resource, double units, string bill = "") => new(id, default)
    { MaterialClaims = [new(resource, new MaterialUnits.Exact(units), bill)] };
    private static EquatableArray<IngredientReservation> Native(string bill = "bill") => [
        BillWire.Reservation(bill, "cook", new Amount(Material, 20))];

    [Fact]
    public void NativeHandoffCountsSameBillOnceAndOtherBillsStillCompete()
    {
        var promise = Promise("method", Material, 50, "bill");
        Assert.Equal(50, ForecastClaims.ForecastMaterialBudgetOf(Stock, Native(), [promise], 1)[Material].ExactCount());
        Assert.Equal(30, ForecastClaims.ForecastMaterialBudgetOf(Stock, Native("other-bill"), [promise], 1)[Material].ExactCount());
        Assert.Equal(30, ForecastClaims.ForecastMaterialBudgetOf(Stock, Native(), [Promise("unplaced", Material, 50)], 1)[Material].ExactCount());
        Assert.Equal(80, ForecastClaims.ForecastMaterialBudgetOf(Stock, Native(), [Promise("small", Material, 10, "bill")], 1)[Material].ExactCount());
        Assert.Throws<DomainException>(() => ForecastClaims.ForecastMaterialBudgetOf(Stock, Native(), [promise, promise with { Id = "duplicate-owner" }], 1));
    }

    [Fact]
    public void CancellationReleasesPromiseButNotAnObservedNativeReservation()
    {
        Assert.Equal(50, ForecastClaims.ForecastMaterialBudgetOf(Stock, Native(), [Promise("cook", Material, 50, "bill")], 1)[Material].ExactCount());
        Assert.Equal(80, ForecastClaims.ForecastMaterialBudgetOf(Stock, Native(), [], 1)[Material].ExactCount());
        Assert.Equal(100, ForecastClaims.ForecastMaterialBudgetOf(Stock, NoReservations, [], 1)[Material].ExactCount());
    }

    [Fact]
    public void UnknownPromiseLinkOnlyPoisonsItsOwnResource()
    {
        var unknown = new ForecastCommitment("unknown", default) { MaterialClaims = [new(Food, new MaterialUnits.Open(), "")] };
        var budget = ForecastClaims.ForecastMaterialBudgetOf(Stock, NoReservations, [unknown], 1);
        Assert.IsType<MaterialUnits.Open>(budget[Food]);
        Assert.Equal(100, budget[Material].ExactCount());
        unknown = unknown with { MaterialClaims = [new(Material, new MaterialUnits.Open(), "")] };
        Assert.IsType<MaterialUnits.Open>(ForecastClaims.ForecastMaterialBudgetOf(Stock, Native(), [unknown], 1)[Material]);
        // A native hold always names its bill (#3881): one without is a broken read.
        Assert.Throws<DomainException>(() => ForecastClaims.ForecastMaterialBudgetOf(Stock, [Native()[0] with { Bill = "" }], [Promise("linked", Material, 50, "bill")], 1));
        Assert.Equal(100, ForecastClaims.ForecastMaterialBudgetOf(Stock, NoReservations,
            [new("zero", default) { MaterialClaims = [new(Material, new MaterialUnits.Exact(0), "")] }], 1)[Material].ExactCount());
    }

    [Fact]
    public void ClaimsCannotDuplicateInventoryConsumptionOrUseFutureOutputAsStock()
    {
        var claim = Promise("method", Material, 10);
        var effect = new ForecastStockChange(Material, 0.5, -10.0);
        Assert.Throws<DomainException>(() => ForecastClaims.ForecastMaterialBudgetOf(Stock, NoReservations,
            [claim with { StockChanges = [effect] }], 1));
        var future = new ForecastCommitment("future", [effect with { Units = 100.0 }]);
        Assert.Equal(-50, ForecastClaims.ForecastMaterialBudgetOf(Stock, NoReservations,
            [Promise("a", Material, 75), Promise("b", Material, 75), future], 1)[Material].ExactCount());
        var beyond = future with { StockChanges = [new(Material, 2.0, -10.0)] };
        Assert.Equal(100, ForecastClaims.ForecastMaterialBudgetOf(Stock, NoReservations, [beyond], 1)[Material].ExactCount());
    }

    private static ForecastEquipmentCapacity Tool(string id = "tool", double start = 0) =>
        new(id, "shop", [new(start, 500)]);
    private static ForecastActivityProblem Jobs() => new()
    {
        Activities = [new("a", "shop", "shop", []), new("b", "shop", "shop", [])],
        Pawns = [new("one", "shop", new ForecastCapacityWindows.Read([new(0, 500)]), 1.0),
            new("two", "shop", new ForecastCapacityWindows.Read([new(0, 500)]), 1.0)],
        Options = [new("a", "one", 100, 0) { Equipment = ["tool"] },
            new("b", "two", 100, 0) { Equipment = ["tool"] }],
        Equipment = [Tool()], Deadline = 200.0,
    };

    [Fact]
    public void OneToolGeneratesTakingTurnsWhileSeparateToolsPermitParallelWork()
    {
        var jobs = Jobs();
        var one = ForecastActivities.CompareActivityPlans(jobs).Alternatives[0];
        Assert.Equal(200, one.CompletedAt);
        Assert.Equal(200, one.LaborCost);
        var claims = ForecastActivities.EquipmentClaimsOf(jobs, one).OrderBy(c => c.Start).ToArray();
        Assert.Equal(claims[0].End, claims[1].Start);
        Assert.Empty(ForecastActivities.CompareActivityPlans(jobs with { Deadline = 150.0 }).Alternatives);
        var separate = jobs with
        {
            Equipment = [Tool(), Tool("second")],
            Options = [jobs.Options[0], jobs.Options[1] with { Equipment = ["second"] }],
        };
        Assert.Equal(100, ForecastActivities.CompareActivityPlans(separate).Alternatives[0].CompletedAt);
    }

    [Fact]
    public void EquipmentReleaseGeneratesWaitingAndJustInTimeAlternativesWithinBudget()
    {
        var jobs = Jobs() with { Equipment = [Tool(start: 50)], Deadline = 250.0 };
        var result = ForecastActivities.CompareActivityPlans(jobs);
        Assert.Equal(250, result.Alternatives[0].CompletedAt);
        Assert.Equal(200, result.Alternatives[0].LaborCost);
        Assert.Contains(result.Alternatives, p => p.Allocations.Any(a => a.WaitTicks > 0));
        var bounded = jobs with { ExpansionBudget = 3 };
        var a = ForecastActivities.CompareActivityPlans(bounded);
        var b = ForecastActivities.CompareActivityPlans(bounded with { Activities = [.. bounded.Activities.Reverse()], Options = [.. bounded.Options.Reverse()] });
        Assert.True(a.SearchTruncated);
        Assert.Equal(3, a.Expansions);
        Assert.Equal(a.Alternatives.Select(p => p.LaborCost), b.Alternatives.Select(p => p.LaborCost));
        Assert.Equal(a.Findings, b.Findings);
    }

    [Fact]
    public void UnknownOrMisplacedEquipmentDoesNotBlockOptionsThatDoNotUseIt()
    {
        var jobs = Jobs();
        jobs = jobs with
        {
            Activities = [jobs.Activities[0]],
            Options = [jobs.Options[0], new("a", "two", 100, 0)],
            Equipment = [],
        };
        var result = ForecastActivities.CompareActivityPlans(jobs);
        Assert.Equal("two", Assert.Single(result.Alternatives[0].Allocations).Pawn);
        Assert.Contains(result.Findings, f => f.StartsWith("unknown_equipment:", StringComparison.Ordinal));
        result = ForecastActivities.CompareActivityPlans(jobs with { Equipment = [Tool() with { Location = "elsewhere" }] });
        Assert.Equal("two", Assert.Single(result.Alternatives[0].Allocations).Pawn);
        Assert.Contains(result.Findings, f => f.StartsWith("equipment_location_mismatch:", StringComparison.Ordinal));
    }

    [Fact]
    public void EquipmentObligationsUnionOnceAndLeaveOtherEquipmentWhole()
    {
        var use = new ForecastEquipmentClaim("tool", 20, 100);
        var result = ForecastClaims.ForecastEquipmentAvailabilityOf([Tool(), Tool("other")], [use, use, new("tool", 80, 150)]);
        var windows = result.Single(e => e.Id == "tool").Windows;
        Assert.Equal(2, windows.Count);
        Assert.Equal(20, windows[0].End);
        Assert.Equal(150, windows[1].Start);
        Assert.Equal([new ForecastTimeWindow(0, 500)], result.Single(e => e.Id == "other").Windows);
        Assert.Throws<DomainException>(() => ForecastClaims.ForecastEquipmentAvailabilityOf([Tool()], [use with { End = 1 }]));
    }

    [Fact]
    public void NativeDecoderRetainsBillIdentityAndThrowsOnAnUnreadOne()
    {
        var bill = BillWire.Bill("native-bill") with { Reservations = [new() { PawnId = "cook", Items = [new() { DefName = Material.Value, Units = 20 }] }] };
        var snapshot = BillWire.Census(bill);
        var observed = ConstructionReads.BillReservations(snapshot);
        Assert.Equal("native-bill", observed[0].Bill);
        var promise = Promise("method", Material, 50, "native-bill");
        Assert.Equal(50, ForecastClaims.ForecastMaterialBudgetOf(Stock, observed, [promise], 1)[Material].ExactCount());
        // Native reads the bench census every frame (#3881): a missing census or a reservation without its bill is unread.
        Assert.Throws<RequiredFactException>(() => ConstructionReads.BillReservations(snapshot with { Benches = [snapshot.Benches[0] with { Bills = [bill with { Id = "" }] }] }));
        Assert.Throws<RequiredFactException>(() => ConstructionReads.BillReservations(null));
    }
}
