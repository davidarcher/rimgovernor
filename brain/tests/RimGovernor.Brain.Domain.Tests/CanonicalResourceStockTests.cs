using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class CanonicalResourceStockTests
{
    [Fact]
    public void ObservedViewsShareTheNativeCensusAndMaterialBudgetsCannotRewriteIt()
    {
        var wood = new Resource("WoodLog");
        var steel = new Resource("Steel");
        var census = ColonyFactsCoreTests.Snapshot() with
        {
            Resources = [new RimGovernor.Brain.GameFacts.Observations.Quantity { DefName = wood.Value, Units = 7 },
                new RimGovernor.Brain.GameFacts.Observations.Quantity { DefName = steel.Value, Units = 11 },
                new RimGovernor.Brain.GameFacts.Observations.Quantity { DefName = wood.Value, Units = 3 }],
            ResourceThings = ["wood-stack", "steel-stack"],
        };
        var facts = ColonyFactsCoreTests.Reading().Facts with
        {
            Census = census,
            MedicalReserve = new() { Census = census, Catalog = ItemFactsFixtureRules.CoreItemFacts() },
        };
        var resource = ResourceFacts.From(facts, new());
        var forecast = ColonyForecastFactsRules.ColonyForecastFactsOf(facts);
        Assert.Same(census, resource.Census);
        Assert.Same(census, forecast.Census);
        Assert.Same(census, forecast.Medicine.Census);
        Assert.Equal(facts.ObservedStock(), resource.ObservedStock());
        Assert.Equal(facts.ObservedStock(), forecast.ObservedStock());
        Assert.Equal(facts.ObservedStock(), forecast.Medicine.ObservedStock());
        Assert.Equal(10, facts.WoodStock());
        Assert.Equal(["wood-stack", "steel-stack"], forecast.Census.ResourceThings);
        var budget = ProductionRules.SharedMaterialBudgetOf(facts.ObservedStock(),
            new Dictionary<Resource, long> { [wood] = 4 },
            new MaterialAvailability.Assessed(new Dictionary<Resource, MaterialUnits> { [steel] = new MaterialUnits.Exact(5) }));
        var free = Assert.IsType<MaterialAvailability.Assessed>(budget).Balances;
        Assert.Equal(new MaterialUnits.Exact(6), free[wood]);
        Assert.Equal(new MaterialUnits.Exact(6), free[steel]);
        Assert.Equal(10, facts.WoodStock());
        Assert.Equal(11, Assert.Single(forecast.ObservedStock(), r => r.Resource == steel).Count);
        Assert.Same(census, resource.Census);
    }

    [Fact]
    public void MissingCensusFailsWhileAnExplicitEmptyCensusIsObserved()
    {
        Assert.Throws<RequiredFactException>(() => new ReviewFacts().ObservedStock());
        Assert.Throws<RequiredFactException>(() => new ResourceFacts().ObservedStock());
        Assert.Throws<RequiredFactException>(() => new ColonyForecastFacts().ObservedStock());
        Assert.Throws<RequiredFactException>(() => new MedicalReserveObservation().ObservedStock());
        var empty = ColonyFactsCoreTests.Snapshot() with { Resources = [], ResourceThings = [] };
        Assert.Empty((new ResourceFacts { Census = empty }).ObservedStock());
        Assert.Empty((new ColonyForecastFacts { Census = empty }).ObservedStock());
        Assert.Empty((new MedicalReserveObservation { Census = empty }).ObservedStock());
    }
}

