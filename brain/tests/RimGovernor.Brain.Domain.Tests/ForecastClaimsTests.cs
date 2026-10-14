using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Observation;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ForecastClaimsTests
{
    private static readonly Resource Material = new("TestMaterial");
    private static readonly Resource Food = new("TestFood");
    private static Fact<EquatableArray<Amount>> Stock => Known<EquatableArray<Amount>>([new(Material, 100), new(Food, 20)]);
    private static Fact<EquatableArray<IngredientReservation>> NoReservations => Known(default(EquatableArray<IngredientReservation>));
    private static ForecastCommitment Promise(string id, Resource resource, double units, string bill = "") => new(id, default)
    { MaterialClaims = [new(resource, Known(units), Known(bill))] };
    private static Fact<EquatableArray<IngredientReservation>> Native(string bill = "bill") => Known<EquatableArray<IngredientReservation>>([
        new() { Pawn = new RimGovernor.Brain.Policy.PawnId("cook"), Bill = Known(bill), Items = [new(Material, 20)] }]);

    [Fact]
    public void NativeHandoffCountsSameBillOnceAndOtherBillsStillCompete()
    {
        var promise = Promise("method", Material, 50, "bill");
        Assert.Equal(50, ForecastMaterialBudgetOf(Stock, Native(), [promise], 1).Value[Material].Value);
        Assert.Equal(30, ForecastMaterialBudgetOf(Stock, Native("other-bill"), [promise], 1).Value[Material].Value);
        Assert.Equal(30, ForecastMaterialBudgetOf(Stock, Native(), [Promise("unplaced", Material, 50)], 1).Value[Material].Value);
        Assert.Equal(80, ForecastMaterialBudgetOf(Stock, Native(), [Promise("small", Material, 10, "bill")], 1).Value[Material].Value);
        Assert.Throws<DomainException>(() => ForecastMaterialBudgetOf(Stock, Native(), [promise, promise with { Id = "duplicate-owner" }], 1));
    }

    [Fact]
    public void CancellationReleasesPromiseButNotAnObservedNativeReservation()
    {
        var request = new ColonyForecastRequest
        {
            Origin = new("world", 1), HorizonDays = 1, Facts = new() { Stock = Stock },
            Construction = new() { IngredientReservations = Native() },
            Commitments = [Promise("cook", Material, 50, "bill")], Proposed = new() { Remove = ["cook"] },
        };
        var forecast = ForecastColony(request);
        Assert.Equal(50, forecast.Baseline.MaterialAvailability.Value[Material].Value);
        Assert.Equal(80, forecast.Proposed.MaterialAvailability.Value[Material].Value);
        Assert.Equal(100, forecast.Proposed.Stocks.Single(s => s.Resource == Material).Points[0].BalanceUnits.Value);
        var released = ForecastColony(request with { Construction = request.Construction with { IngredientReservations = NoReservations } });
        Assert.Equal(100, released.Proposed.MaterialAvailability.Value[Material].Value);
    }

    [Fact]
    public void UnknownPromiseOrNativeLinkOnlyPoisonsItsOwnResource()
    {
        var unknown = new ForecastCommitment("unknown", default) { MaterialClaims = [new(Food, Unknown<double>(), Known(""))] };
        var budget = ForecastMaterialBudgetOf(Stock, NoReservations, [unknown], 1).Value;
        Assert.False(budget[Food].IsKnown);
        Assert.Equal(100, budget[Material].Value);
        unknown = unknown with { MaterialClaims = [new(Material, Known(50.0), Unknown<string>())] };
        Assert.False(ForecastMaterialBudgetOf(Stock, Native(), [unknown], 1).Value[Material].IsKnown);
        var missingId = Known<EquatableArray<IngredientReservation>>([Native().Value[0] with { Bill = default }]);
        Assert.False(ForecastMaterialBudgetOf(Stock, missingId, [Promise("linked", Material, 50, "bill")], 1).Value[Material].IsKnown);
        Assert.False(ForecastMaterialBudgetOf(Stock, default, [], 1).IsKnown);
        Assert.Equal(100, ForecastMaterialBudgetOf(Stock, NoReservations,
            [new("zero", default) { MaterialClaims = [new(Material, Known(0.0), default)] }], 1).Value[Material].Value);
    }

    [Fact]
    public void ClaimsCannotDuplicateInventoryConsumptionOrUseFutureOutputAsStock()
    {
        var claim = Promise("method", Material, 10);
        var effect = new ForecastStockChange(Material, Known(0.5), Known(-10.0));
        Assert.Throws<DomainException>(() => ForecastMaterialBudgetOf(Stock, NoReservations,
            [claim with { StockChanges = [effect] }], 1));
        var future = new ForecastCommitment("future", [effect with { Units = Known(100.0) }]);
        Assert.Equal(-50, ForecastMaterialBudgetOf(Stock, NoReservations,
            [Promise("a", Material, 75), Promise("b", Material, 75), future], 1).Value[Material].Value);
        var beyond = future with { StockChanges = [new(Material, Known(2.0), Unknown<double>())] };
        Assert.Equal(100, ForecastMaterialBudgetOf(Stock, NoReservations, [beyond], 1).Value[Material].Value);
    }

    private static ForecastEquipmentCapacity Tool(string id = "tool", double start = 0) =>
        new(id, "shop", Known<EquatableArray<ForecastTimeWindow>>([new(start, 500)]));
    private static ForecastActivityProblem Jobs() => new()
    {
        Activities = [new("a", "shop", "shop", []), new("b", "shop", "shop", [])],
        Pawns = [new("one", "shop", Known<EquatableArray<ForecastTimeWindow>>([new(0, 500)]), Known(1.0)),
            new("two", "shop", Known<EquatableArray<ForecastTimeWindow>>([new(0, 500)]), Known(1.0))],
        Options = [new("a", "one", Known(100.0), Known(0.0)) { Equipment = ["tool"] },
            new("b", "two", Known(100.0), Known(0.0)) { Equipment = ["tool"] }],
        Equipment = [Tool()], Deadline = Known(200.0), AlternativeLaborCost = Known(1000.0),
    };

    [Fact]
    public void OneToolGeneratesTakingTurnsWhileSeparateToolsPermitParallelWork()
    {
        var jobs = Jobs();
        var one = CompareActivityPlans(jobs).Selected!;
        Assert.Equal(200, one.CompletedAt);
        Assert.Equal(200, one.LaborCost);
        var claims = one.EquipmentClaims.OrderBy(c => c.Start.Value).ToArray();
        Assert.Equal(claims[0].End.Value, claims[1].Start.Value);
        Assert.Null(CompareActivityPlans(jobs with { Deadline = Known(150.0) }).Selected);
        var separate = jobs with
        {
            Equipment = [Tool(), Tool("second")],
            Options = [jobs.Options[0], jobs.Options[1] with { Equipment = ["second"] }],
        };
        Assert.Equal(100, CompareActivityPlans(separate).Selected!.CompletedAt);
    }

    [Fact]
    public void EquipmentReleaseGeneratesWaitingAndJustInTimeAlternativesWithinBudget()
    {
        var jobs = Jobs() with { Equipment = [Tool(start: 50)], Deadline = Known(250.0) };
        var result = CompareActivityPlans(jobs);
        Assert.Equal(250, result.Selected!.CompletedAt);
        Assert.Equal(200, result.Selected.LaborCost);
        Assert.Contains(result.Alternatives, p => p.Allocations.Any(a => a.WaitTicks > 0));
        var bounded = jobs with { ExpansionBudget = 3 };
        var a = CompareActivityPlans(bounded);
        var b = CompareActivityPlans(bounded with { Activities = [.. bounded.Activities.Reverse()], Options = [.. bounded.Options.Reverse()] });
        Assert.True(a.SearchTruncated);
        Assert.Equal(3, a.Expansions);
        Assert.Equal(a.Decision, b.Decision);
        Assert.Equal(a.Findings, b.Findings);
    }

    [Fact]
    public void UnknownOrMisplacedEquipmentDoesNotBlockOptionsThatDoNotUseIt()
    {
        var jobs = Jobs();
        jobs = jobs with
        {
            Activities = [jobs.Activities[0]],
            Options = [jobs.Options[0], new("a", "two", Known(100.0), Known(0.0))],
            Equipment = [Tool() with { Windows = default }],
        };
        var result = CompareActivityPlans(jobs);
        Assert.Equal("two", Assert.Single(result.Selected!.Allocations).Pawn);
        Assert.Contains(result.Findings, f => f.StartsWith("unknown_equipment:", StringComparison.Ordinal));
        result = CompareActivityPlans(jobs with { Equipment = [Tool() with { Location = "elsewhere" }] });
        Assert.Equal("two", Assert.Single(result.Selected!.Allocations).Pawn);
        Assert.Contains(result.Findings, f => f.StartsWith("equipment_location_mismatch:", StringComparison.Ordinal));
    }

    [Fact]
    public void EquipmentObligationsUnionOnceAndUnknownEndStaysLocal()
    {
        var use = new ForecastEquipmentClaim("tool", Known(20.0), Known(100.0));
        var result = ForecastEquipmentAvailabilityOf([Tool(), Tool("other")], [use, use, new("tool", Known(80.0), Known(150.0))]);
        var windows = result.Single(e => e.Id == "tool").Windows.Value;
        Assert.Equal(2, windows.Count);
        Assert.Equal(20, windows[0].End);
        Assert.Equal(150, windows[1].Start);
        result = ForecastEquipmentAvailabilityOf(result, [use with { End = default }]);
        Assert.False(result.Single(e => e.Id == "tool").Windows.IsKnown);
        Assert.True(result.Single(e => e.Id == "other").Windows.IsKnown);
        Assert.Throws<DomainException>(() => ForecastEquipmentAvailabilityOf([Tool()], [use with { End = Known(1.0) }]));
    }

    [Fact]
    public void NativeDecoderRetainsBillIdentityWithoutInventingMissingIdentity()
    {
        var bill = new O.BillState { Id = "native-bill", Reservations = [new() { PawnId = "cook", Items = [new() { DefName = Material.Value, Units = 20 }] }] };
        var snapshot = new O.BillsSnapshot { Benches = [new() { Bills = [bill] }] };
        var observed = ObservationPackage.BillReservations(snapshot);
        Assert.Equal("native-bill", observed.Value[0].Bill.Value);
        var promise = Promise("method", Material, 50, "native-bill");
        Assert.Equal(50, ForecastMaterialBudgetOf(Stock, observed, [promise], 1).Value[Material].Value);
        observed = ObservationPackage.BillReservations(snapshot with { Benches = [snapshot.Benches[0] with { Bills = [bill with { Id = null }] }] });
        Assert.False(observed.Value[0].Bill.IsKnown);
        Assert.False(ForecastMaterialBudgetOf(Stock, observed, [promise], 1).Value[Material].IsKnown);
    }
}
