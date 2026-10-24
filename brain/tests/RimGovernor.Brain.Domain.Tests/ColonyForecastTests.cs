using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ColonyForecastTests
{
    private static readonly Resource Material = new("TestMaterial");
    private static readonly Fact<EquatableArray<Amount>> Stock = Known(EquatableArray.From(new[] { new Amount(Material, 10) }));
    private static ForecastCommitment Change(string id, double day, double units) =>
        new(id, EquatableArray.From(new[] { new ForecastStockChange(Material, Known(day), Known(units)) }));

    private static ColonyForecastRequest Request(params ForecastCommitment[] commitments) => new()
    {
        Origin = new("save/map/load", 123),
        HorizonDays = 3,
        Facts = new ColonyForecastFacts { Stock = Stock },
        Commitments = EquatableArray.From(commitments),
    };

    /// <summary>The stock findings of a three-day projection: what a coupled outcome carries of the inventory walk.</summary>
    private static List<ForecastFinding> Findings(Fact<EquatableArray<Amount>> stock, params ForecastCommitment[] commitments)
    {
        var findings = new List<ForecastFinding>();
        new ColonyForecastRules.InventoryWalk().Run(new(stock, ColonyForecastRules.ForecastCommitmentsById(commitments), 3), default, findings);
        return findings;
    }

    [Fact]
    public void EarlierDeliveryChangesShortfallWithoutChangingObservedStock()
    {
        var request = Request(Change("delivery", 2, 10), Change("build", 1, -15));
        var result = ColonyForecastRules.ForecastColony(request with
        {
            Proposed = new ColonyForecastDelta
            {
                Remove = EquatableArray.From(new List<string> { "delivery" }),
                Add = EquatableArray.From(new[] { Change("delivery", 0.5, 10) }),
            },
        });
        Assert.Equal(5, Assert.Single(Findings(Stock, Change("delivery", 2, 10), Change("build", 1, -15)), f => f.Reason == "stock_shortfall").Units.Value);
        Assert.Empty(Findings(Stock, Change("delivery", 0.5, 10), Change("build", 1, -15)));
        Assert.Equal(2, Assert.Single(result.Baseline.Commitments, c => c.Id == "delivery").StockChanges[0].Day.Value);
        Assert.Equal(0.5, Assert.Single(result.Proposed.Commitments, c => c.Id == "delivery").StockChanges[0].Day.Value);
        Assert.Equal(request.Origin, result.Origin);
        Assert.Contains("pawn_capacity", result.Unsupported);
    }

    [Fact]
    public void EmptyDeltaContinuesAndInputOrderingDoesNotChangeTheForecast()
    {
        var a = Change("use", 1, -4);
        var b = Change("delivery", 2, 2);
        var first = ColonyForecastRules.ForecastColony(Request(a, b));
        var second = ColonyForecastRules.ForecastColony(Request(b, a));
        // The free stock is a dictionary per scenario (no reservations here, so known): compare it by content (#3881).
        static ColonyForecastScenario Plain(ColonyForecastScenario s) => s with { MaterialAvailability = default };
        static KeyValuePair<Resource, Fact<double>>[] Free(ColonyForecastScenario s) => [.. s.MaterialAvailability.Value.OrderBy(p => p.Key.Value, StringComparer.Ordinal)];
        Assert.Equal(Plain(first.Baseline), Plain(first.Proposed));
        Assert.Equal(Plain(first.Baseline), Plain(second.Baseline));
        Assert.Equal(Free(first.Baseline), Free(first.Proposed));
        Assert.Equal(Free(first.Baseline), Free(second.Baseline));
        Assert.Empty(Findings(Stock, b, a));
    }

    [Fact]
    public void UnknownStockIsNotKnownEmpty()
    {
        var use = Change("use", 1, -4);
        Assert.Contains(Findings(default, use), f => f.Reason == "unknown_initial_stock");
        Assert.DoesNotContain(Findings(default, use), f => f.Reason == "stock_shortfall");
        Assert.Equal(4, Assert.Single(Findings(Known(default(EquatableArray<Amount>)), use)).Units.Value);
    }

    [Fact]
    public void UnknownTimingCannotCertifyFutureInventory()
    {
        var change = new ForecastCommitment("delivery", EquatableArray.From(new[]
        {
            new ForecastStockChange(Material, Unknown<double>(), Known(20.0)),
        }));
        Assert.Equal("unknown_stock_effect", Assert.Single(Findings(Stock, change)).Reason);
        // The unknown time may affect any future point, so a later use is certified neither way.
        Assert.Equal(["unknown_stock_effect"], Findings(Stock, change, Change("use", 1, -15)).Select(f => f.Reason));
    }

    [Fact]
    public void HorizonDoesNotTurnLaterBenefitsIntoZeroOrCurrentStock()
    {
        var late = Change("late", 4, 20);
        Assert.Equal("beyond_horizon", Assert.Single(Findings(Stock, late)).Reason);
        Assert.Equal(["beyond_horizon", "stock_shortfall"], Findings(Stock, late, Change("use", 3, -15)).Select(f => f.Reason));
        var result = ColonyForecastRules.ForecastColony(Request(late));
        Assert.Equal(3, result.HorizonDays);
        Assert.Equal(ColonyDiagnosticsRules.MaintenanceHorizonDays, result.Observed.HorizonDays);
    }

    [Fact]
    public void SameTimeOutputDoesNotFinanceItsOwnInput()
    {
        var finding = Assert.Single(Findings(Stock, Change("output", 1, 20), Change("input", 1, -15)));
        Assert.Equal("stock_shortfall", finding.Reason);
        Assert.Equal(5, finding.Units.Value);
    }

    [Fact]
    public void InvalidChangesAndDuplicateCommitmentsAreRejected()
    {
        Assert.Throws<DomainException>(() => ColonyForecastRules.ForecastColony(Request(Change("a", 0, 1), Change("a", 1, 1))));
        Assert.Throws<DomainException>(() => ColonyForecastRules.ForecastColony(Request(Change("bad", -1, 1))));
        Assert.Throws<DomainException>(() => ColonyForecastRules.ForecastColony(Request() with { HorizonDays = double.NaN }));
        Assert.Throws<DomainException>(() => ColonyForecastRules.ForecastColony(Request() with
        {
            Proposed = new ColonyForecastDelta { Remove = EquatableArray.From(new List<string> { "missing" }) },
        }));
    }

    [Fact]
    public void ConstructionDiagnosticsKeepExactResourcesAndUnits()
    {
        var other = new Resource("OtherMaterial");
        var result = ColonyForecastRules.ForecastColony(Request() with
        {
            Facts = new ColonyForecastFacts
            {
                Stock = Known(EquatableArray.From(new[] { new Amount(other, 100) })),
                Construction = new ConstructionInputs
                {
                    Deficit = new Dictionary<Resource, long> { [Material] = 15 },
                },
            },
        });
        var row = Assert.Single(result.Observed.Construction.Value.Materials);
        Assert.Equal(Material, row.Resource);
        Assert.Equal(15, row.ShortfallUnits);
        Assert.Equal(0, row.Stock);
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
        // A use before the unknown quantity's day is still certified; one after it is not.
        var before = Findings(Stock, Change("use", 1, -12), commitment);
        Assert.Equal(2, Assert.Single(before, f => f.Reason == "stock_shortfall").Units.Value);
        Assert.Contains(before, f => f.Reason == "unsupported:work_completion");
        Assert.Contains(before, f => f.Reason == "unknown_stock_effect");
        Assert.DoesNotContain(Findings(Stock, Change("use", 3, -12), commitment), f => f.Reason == "stock_shortfall");
    }

    [Fact]
    public void DiagnosticRatesAreNotInventoryEventsAndFloorsOverlapByMaximum()
    {
        var request = Request();
        var result = ColonyForecastRules.ForecastColony(request with
        {
            HorizonDays = 2,
            Facts = request.Facts with
            {
                Power = new[]
                {
                    new PowerNetworkFact
                    {
                        Id = "net", GenerationW = 0.0,
                        ConsumptionW = 10.0, StoredWD = 30.0,
                    },
                },
                Construction = new ConstructionInputs
                {
                    Deficit = new Dictionary<Resource, long> { [Material] = 15 },
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
        Assert.Empty(result.Baseline.Commitments);
    }
}
