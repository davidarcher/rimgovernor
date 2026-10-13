using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ColonyForecastTests
{
    private static readonly Resource Material = new("TestMaterial");
    private static ForecastCommitment Change(string id, double day, double units) =>
        new(id, EquatableArray.From(new[] { new ForecastStockChange(Material, Known(day), Known(units)) }));

    private static ColonyForecastRequest Request(params ForecastCommitment[] commitments) => new()
    {
        Origin = new("save/map/load", 123),
        HorizonDays = 3,
        Facts = new ColonyForecastFacts
        {
            Stock = Known(EquatableArray.From(new[] { new Amount(Material, 10) })),
        },
        Commitments = EquatableArray.From(commitments),
    };

    [Fact]
    public void EarlierDeliveryChangesShortfallWithoutChangingObservedStock()
    {
        var request = Request(Change("delivery", 2, 10), Change("build", 1, -15));
        var result = ForecastColony(request with
        {
            Proposed = new ColonyForecastDelta
            {
                Remove = EquatableArray.From(new List<string> { "delivery" }),
                Add = EquatableArray.From(new[] { Change("delivery", 0.5, 10) }),
            },
            Assumptions = EquatableArray.From(new List<string> { "delivery times supplied by caller" }),
        });
        Assert.Equal(5, Assert.Single(result.Baseline.Findings, f => f.Reason == "stock_shortfall").Units.Value);
        Assert.Empty(result.Proposed.Findings);
        Assert.Equal(10, result.Baseline.Stocks[0].Points[0].BalanceUnits.Value);
        Assert.Equal(10, result.Proposed.Stocks[0].Points[0].BalanceUnits.Value);
        Assert.Equal(5, result.Proposed.Stocks[0].Points[^1].BalanceUnits.Value);
        Assert.Equal(request.Origin, result.Origin);
        Assert.Contains("pawn_capacity", result.Unsupported);
    }

    [Fact]
    public void EmptyDeltaContinuesAndInputOrderingDoesNotChangeTheForecast()
    {
        var a = Change("use", 1, -4);
        var b = Change("delivery", 2, 2);
        var first = ForecastColony(Request(a, b));
        var second = ForecastColony(Request(b, a));
        Assert.Equal(first.Baseline, first.Proposed);
        Assert.Equal(first.Baseline, second.Baseline);
        Assert.Equal(8, first.Baseline.Stocks[0].Points[^1].BalanceUnits.Value);
    }

    [Fact]
    public void UnknownStockIsNotKnownEmpty()
    {
        var request = Request(Change("use", 1, -4));
        var unknown = ForecastColony(request with { Facts = new ColonyForecastFacts() });
        Assert.False(unknown.Baseline.Stocks[0].Points[^1].BalanceUnits.IsKnown);
        Assert.Contains(unknown.Baseline.Findings, f => f.Reason == "unknown_initial_stock");
        var empty = ForecastColony(request with
        {
            Facts = new ColonyForecastFacts { Stock = Known(default(EquatableArray<Amount>)) },
        });
        Assert.Equal(4, Assert.Single(empty.Baseline.Findings).Units.Value);
    }

    [Fact]
    public void UnknownTimingCannotCertifyFutureInventory()
    {
        var change = new ForecastCommitment("delivery", EquatableArray.From(new[]
        {
            new ForecastStockChange(Material, Unknown<double>(), Known(20.0)),
        }));
        var result = ForecastColony(Request(change));
        Assert.Equal(10, result.Proposed.Stocks[0].Points[0].BalanceUnits.Value);
        Assert.False(result.Proposed.Stocks[0].Points[^1].BalanceUnits.IsKnown);
        Assert.Equal("unknown_stock_effect", Assert.Single(result.Proposed.Findings).Reason);
    }

    [Fact]
    public void HorizonDoesNotTurnLaterBenefitsIntoZeroOrCurrentStock()
    {
        var result = ForecastColony(Request(Change("late", 4, 20)));
        Assert.Equal(10, result.Proposed.Stocks[0].Points[^1].BalanceUnits.Value);
        Assert.Equal("beyond_horizon", Assert.Single(result.Proposed.Findings).Reason);
        Assert.Equal(3, result.HorizonDays);
        Assert.Equal(MaintenanceHorizonDays, result.Observed.HorizonDays);
    }

    [Fact]
    public void SameTimeOutputDoesNotFinanceItsOwnInput()
    {
        var result = ForecastColony(Request(Change("output", 1, 20), Change("input", 1, -15)));
        Assert.Equal(5, Assert.Single(result.Proposed.Findings).Units.Value);
        Assert.Equal(15, result.Proposed.Stocks[0].Points[^1].BalanceUnits.Value);
    }

    [Fact]
    public void InvalidChangesAndDuplicateCommitmentsAreRejected()
    {
        Assert.Throws<DomainException>(() => ForecastColony(Request(Change("a", 0, 1), Change("a", 1, 1))));
        Assert.Throws<DomainException>(() => ForecastColony(Request(Change("bad", -1, 1))));
        Assert.Throws<DomainException>(() => ForecastColony(Request() with { HorizonDays = double.NaN }));
        Assert.Throws<DomainException>(() => ForecastColony(Request() with
        {
            Proposed = new ColonyForecastDelta { Remove = EquatableArray.From(new List<string> { "missing" }) },
        }));
    }

    [Fact]
    public void ConstructionDiagnosticsKeepExactResourcesAndUnits()
    {
        var other = new Resource("OtherMaterial");
        var result = ForecastColony(Request() with
        {
            Facts = new ColonyForecastFacts
            {
                Stock = Known(EquatableArray.From(new[] { new Amount(other, 100) })),
                Construction = new ConstructionInputs
                {
                    Deficit = Known<IReadOnlyDictionary<Resource, long>>(new Dictionary<Resource, long> { [Material] = 15 }),
                },
            },
        });
        var row = Assert.Single(result.Observed.Construction.Value.Materials);
        Assert.Equal(Material, row.Resource);
        Assert.Equal(15, row.ShortfallUnits);
        Assert.Equal(0, row.Stock);
        Assert.Empty(result.Baseline.Findings);
    }

    [Fact]
    public void UnknownQuantityStartsAtItsEventAndUnsupportedWorkStaysExplicit()
    {
        var commitment = new ForecastCommitment("work", EquatableArray.From(new[]
        {
            new ForecastStockChange(Material, Known(2.0), Unknown<double>()),
        }))
        {
            UnsupportedEffects = EquatableArray.From(new List<string> { "work_completion" }),
        };
        var result = ForecastColony(Request(Change("use", 1, -2), commitment));
        var points = result.Baseline.Stocks[0].Points;
        Assert.Equal(8, points[1].BalanceUnits.Value);
        Assert.False(points[2].BalanceUnits.IsKnown);
        Assert.Contains(result.Baseline.Findings, f => f.Reason == "unsupported:work_completion");
        Assert.Contains(result.Baseline.Findings, f => f.Reason == "unknown_stock_effect");
    }

    [Fact]
    public void DiagnosticRatesAreNotInventoryEventsAndFloorsOverlapByMaximum()
    {
        var request = Request();
        var result = ForecastColony(request with
        {
            HorizonDays = 2,
            Facts = request.Facts with
            {
                Power = new[]
                {
                    new PowerNetworkFact
                    {
                        Id = "net", GenerationW = Known(0.0),
                        ConsumptionW = Known(10.0), StoredWD = Known(30.0),
                    },
                },
                Construction = new ConstructionInputs
                {
                    Deficit = Known<IReadOnlyDictionary<Resource, long>>(new Dictionary<Resource, long> { [Material] = 15 }),
                    Admitted = EquatableArray.From(new[]
                    {
                        new AdmittedCost { Resource = Material, Action = new ActionId("build"), Count = 20 },
                    }),
                },
            },
        });
        Assert.Equal(3, Assert.Single(result.Observed.Power.Value.Nets).ReserveDays);
        Assert.Equal(20, Assert.Single(result.Observed.Construction.Value.Materials).Need);
        Assert.Equal(10, Assert.Single(result.Observed.Construction.Value.Materials).ShortfallUnits);
        Assert.Equal(10, result.Baseline.Stocks[0].Points[^1].BalanceUnits.Value);
        Assert.Equal(2, result.Baseline.Stocks[0].Points[^1].Day);
        Assert.Empty(result.Baseline.Findings);
    }
}
