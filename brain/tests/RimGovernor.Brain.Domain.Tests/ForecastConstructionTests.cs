using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Observation;
using RimGovernor.Brain.Bridge.Contracts;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;
using O = RimGovernor.Contracts.Wire.Observations;
using C = RimGovernor.Contracts.Wire.Common;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ForecastConstructionTests
{
    private static readonly Resource Material = new("TestMaterial");
    private static ConstructionSite Site(string id = "site", int x = 1, long owed = 10, double work = 100) => new()
    {
        Id = id, Building = new Building("TestBuilding", new Cell(x, 1), Rotation.North, ""), Stage = "frame",
        WorkLeft = Known(work), MaterialsOwed = Known(EquatableArray.From(new[] { new Amount(Material, owed) })),
        ResourcesComplete = Known(owed == 0), NativeFinishingSkill = Known(4), MinimumFinishingSkill = Known(6),
        QualitySensitive = Known(true), Tier = Known(ConstructionTier.Sustain),
    };
    private static ForecastConstructionWorker Worker(string id = "worker", int skill = 8) => new(
        new WorkPawn { Id = new RimGovernor.Brain.Domain.PawnId(id), Available = Known(true), Applies = Known(true), ConstructionAble = Known(true),
            Skills = Known(EquatableArray.From(new[] { new WorkSkill { Name = "Construction", Level = skill } })) },
        new(id, "stock", Known(EquatableArray.From(new[] { new ForecastTimeWindow(0, 1000) })), Known(1.0)), Known(1.0));
    private static ForecastCommitment Commitment(ConstructionSite site) => new(site.Id, default)
    { Construction = site.Building, AlternativeLaborCost = Known(2000.0) };
    private static ColonyForecastRequest Request(params ConstructionSite[] sites) => new()
    {
        Origin = new("world/load", 100), HorizonDays = 500.0 / GameTime.TicksPerDay,
        Facts = new ColonyForecastFacts { Stock = Known(EquatableArray.From(new[] { new Amount(Material, 15) })) },
        Commitments = EquatableArray.From(sites.Select(Commitment)),
        Construction = new()
        {
            Census = Known(new CurrentConstruction { Colony = true, Sites = EquatableArray.From(sites) }),
            Workers = [Worker()], IngredientReservations = Known(default(EquatableArray<IngredientReservation>)),
            Estimates = sites.Select(s => new ForecastConstructionEstimate(s.Id, "worker", "stock", Known(true), Known(true), Known(100.0), Known(0.0))).ToArray(),
            Routes = sites.Select(s => new ForecastActivityRoute("worker", "stock", s.Id, Known(50.0)))
                .Concat(sites.SelectMany(s => sites.Where(t => t.Id != s.Id).Select(t => new ForecastActivityRoute("worker", s.Id, t.Id, Known(50.0)))))
                .Concat(sites.Select(s => new ForecastActivityRoute("worker", s.Id, "stock", Known(50.0)))).ToArray(),
        },
    };

    [Fact]
    public void ObservedWorkAndMaterialsGenerateDependenciesAndInventoryConsumptionOnce()
    {
        var result = ForecastColony(Request(Site()));
        var construction = Assert.Single(result.Proposed.Construction);
        Assert.Equal("scheduled", construction.Reason);
        var plan = construction.Comparison!.Selected!;
        Assert.Equal(2, plan.Allocations.Length);
        Assert.Equal(100, Assert.Single(plan.Allocations, a => a.Activity == "finish").Start);
        Assert.Equal(200.0 / GameTime.TicksPerDay, construction.CompletedDay.Value);
        Assert.Equal(15, result.Proposed.Stocks[0].Points[0].BalanceUnits.Value);
        Assert.Equal(5, result.Proposed.Stocks[0].Points[^1].BalanceUnits.Value);
        Assert.Single(construction.MaterialChanges);
        Assert.Empty(result.Proposed.Commitments[0].StockChanges);
    }

    [Fact]
    public void DeltaCancellationReleasesMaterialsAndCapacityWithoutChangingBaseline()
    {
        var a = Site("a"); var b = Site("b", 2);
        var request = Request(a, b) with { Proposed = new() { Remove = EquatableArray.From(new List<string> { "a" }) } };
        var result = ForecastColony(request);
        Assert.Equal("scheduled", result.Baseline.Construction[0].Reason);
        Assert.Equal("insufficient_uncommitted_materials", result.Baseline.Construction[1].Reason);
        Assert.Equal("scheduled", Assert.Single(result.Proposed.Construction).Reason);
        Assert.Equal(5, result.Proposed.Stocks[0].Points[^1].BalanceUnits.Value);
    }

    [Fact]
    public void GeneratedDeliveryCanDelegateWhileQualifiedBuilderTravelsBeforeItCompletes()
    {
        var request = Request(Site());
        var builder = Worker();
        builder = builder with { Capacity = builder.Capacity with { OpportunityCostPerTick = Known(3.0) } };
        request = request with { Construction = request.Construction with
        {
            Workers = [builder, Worker("helper", skill: 0)],
            Estimates = [.. request.Construction.Estimates, new("site", "helper", "stock", Known(true), Known(false), Known(100.0), Known(0.0))],
        } };
        var plan = ForecastColony(request).Proposed.Construction[0].Comparison!.Selected!;
        Assert.Equal("helper", Assert.Single(plan.Allocations, a => a.Activity == "deliver").Pawn);
        var finish = Assert.Single(plan.Allocations, a => a.Activity == "finish");
        Assert.Equal("worker", finish.Pawn);
        Assert.Equal(50, finish.Depart);
        Assert.Equal(100, finish.Start);
        Assert.Equal(550, plan.LaborCost);
    }

    [Fact]
    public void OnePawnCannotSupplyTwoFullHorizonsButTwoPawnsCan()
    {
        var a = Site("a", owed: 0, work: 450); var b = Site("b", 2, owed: 0, work: 450);
        var request = Request(a, b);
        Assert.Single(ForecastColony(request).Proposed.Construction, r => r.Reason == "scheduled");
        var extra = Worker("other");
        request = request with { Construction = request.Construction with
        {
            Workers = [Worker(), extra],
            Estimates = [.. request.Construction.Estimates, .. request.Construction.Estimates.Select(e => e with { Pawn = "other" })],
            Routes = [.. request.Construction.Routes, .. request.Construction.Routes.Select(r => r with { Pawn = "other" })],
        } };
        Assert.All(ForecastColony(request).Proposed.Construction, r => Assert.Equal("scheduled", r.Reason));
    }

    [Fact]
    public void FinishingSkillAndTierProtectionsSurviveActivityGeneration()
    {
        var request = Request(Site());
        Assert.Null(Assert.Single(ForecastColony(request with { Construction = request.Construction with { Workers = [Worker(skill: 5)] } }).Proposed.Construction).Comparison!.Selected);
        var high = Site("high") with { Tier = Known(ConstructionTier.Expand) };
        var low = Site("low", 2);
        request = Request(high) with { Construction = Request(high).Construction with { Census = Known(new CurrentConstruction { Colony = true, Sites = EquatableArray.From(new[] { high, low }) }) } };
        Assert.Equal("lower_tier_material_need", Assert.Single(ForecastColony(request).Proposed.Construction).Reason);
    }

    [Fact]
    public void UnknownSiteDoesNotBlockIndependentKnownWorkAndMissingRoutesStayUnknown()
    {
        var a = Site("a") with { WorkLeft = Unknown<double>() };
        var b = Site("b", 2, owed: 0);
        var request = Request(a, b);
        var results = ForecastColony(request).Proposed.Construction;
        Assert.Equal("unknown_construction_work_or_materials", results[0].Reason);
        Assert.Equal("scheduled", results[1].Reason);
        var missing = ForecastColony(Request(b) with { Construction = Request(b).Construction with { Routes = [] } });
        Assert.Null(missing.Proposed.Construction[0].Comparison!.Selected);
        Assert.Contains(missing.Proposed.Construction[0].Comparison!.Findings, f => f.StartsWith("unknown_route:", StringComparison.Ordinal));
    }

    [Fact]
    public void BillHoldsAndProtectedConsumptionCannotFundConstruction()
    {
        var request = Request(Site());
        request = request with { Construction = request.Construction with { IngredientReservations = Known(EquatableArray.From(new[]
        { new IngredientReservation { Pawn = new RimGovernor.Brain.Domain.PawnId("cook"), Items = [new Amount(Material, 6)] } })) } };
        Assert.Equal("insufficient_uncommitted_materials", Assert.Single(ForecastColony(request).Proposed.Construction).Reason);
        request = Request(Site());
        request = request with { Commitments = EquatableArray.From(request.Commitments.Append(new ForecastCommitment("other", EquatableArray.From(new[]
        { new ForecastStockChange(Material, Known(0.0), Known(-6.0)) })))) };
        Assert.Equal("insufficient_uncommitted_materials", Assert.Single(ForecastColony(request).Proposed.Construction).Reason);
        request = Request(Site()) with { Construction = Request(Site()).Construction with { IngredientReservations = default } };
        Assert.Equal("unknown_available_materials", Assert.Single(ForecastColony(request).Proposed.Construction).Reason);
    }

    [Fact]
    public void UpdatedCensusAndAvailabilityInvalidateForecastWithoutStoredPlan()
    {
        var site = Site(); var request = Request(site);
        var complete = new CurrentConstruction { Colony = true, Buildings = EquatableArray.From(new[] { new CurrentBuilding { Id = "built", Building = site.Building } }) };
        Assert.Equal("already_built", Assert.Single(ForecastColony(request with { Construction = request.Construction with { Census = Known(complete) } }).Proposed.Construction).Reason);
        Assert.Equal("construction_gone", Assert.Single(ForecastColony(request with { Construction = request.Construction with { Census = Known(new CurrentConstruction { Colony = true }) } }).Proposed.Construction).Reason);
        var worker = Worker();
        var unavailable = request with { Construction = request.Construction with { Workers = [worker with { Observed = worker.Observed with { Available = Known(false) } }] } };
        Assert.Null(Assert.Single(ForecastColony(unavailable).Proposed.Construction).Comparison!.Selected);
        var remaining = site with { WorkLeft = Known(20.0), MaterialsOwed = Known(default(EquatableArray<Amount>)), ResourcesComplete = Known(true) };
        var revised = ForecastColony(request with { Construction = request.Construction with { Census = Known(new CurrentConstruction { Colony = true, Sites = EquatableArray.From(new[] { remaining }) }) } });
        Assert.Equal(70.0 / GameTime.TicksPerDay, revised.Proposed.Construction[0].CompletedDay.Value);
        Assert.Equal(15, revised.Proposed.Stocks[0].Points[^1].BalanceUnits.Value);
    }

    [Fact]
    public void SearchBudgetIsSharedAndDuplicateConstructionCannotDoubleCharge()
    {
        var request = Request(Site("a", owed: 0), Site("b", 2, owed: 0));
        var forecast = ForecastColony(request with { Construction = request.Construction with { ExpansionBudget = 2 } });
        Assert.Equal("scheduled", forecast.Proposed.Construction[0].Reason);
        Assert.Equal("search_budget_exhausted", forecast.Proposed.Construction[1].Reason);
        Assert.Throws<DomainException>(() => ForecastColony(request with { Commitments = EquatableArray.From(new[] { request.Commitments[0], request.Commitments[0] with { Id = "duplicate" } }) }));
    }

    [Fact]
    public void SelectedIngredientPromisesBlockCompetingConstructionUntilCancelled()
    {
        var request = Request(Site());
        var promise = new ForecastCommitment("cooking", default)
        { MaterialClaims = [new(Material, Known(6.0), Known(""))] };
        request = request with { Commitments = [.. request.Commitments, promise], Proposed = new() { Remove = [promise.Id] } };
        var forecast = ForecastColony(request);
        Assert.Equal("insufficient_uncommitted_materials", Assert.Single(forecast.Baseline.Construction).Reason);
        Assert.Equal("scheduled", Assert.Single(forecast.Proposed.Construction).Reason);
        Assert.Equal(9, forecast.Baseline.MaterialAvailability.Value[Material].Value);
        Assert.Equal(5, forecast.Proposed.MaterialAvailability.Value[Material].Value);
    }

    [Fact]
    public void UnknownConsumptionBlocksOnlyConstructionUsingThatMaterial()
    {
        var request = Request(Site());
        var unrelated = new ForecastCommitment("other", [new(new Resource("TestOther"), Known(0.0), Unknown<double>())]);
        var result = ForecastColony(request with { Commitments = [.. request.Commitments, unrelated] });
        Assert.Equal("scheduled", Assert.Single(result.Proposed.Construction).Reason);
        var related = unrelated with { StockChanges = [new(Material, Known(0.0), Unknown<double>())] };
        result = ForecastColony(request with { Commitments = [.. request.Commitments, related] });
        Assert.Equal("unknown_available_materials", Assert.Single(result.Proposed.Construction).Reason);
    }

    [Fact]
    public void ConstructionUsesSharedEquipmentWindowsAndCancellationReleasesThem()
    {
        var request = Request(Site(owed: 0));
        request = request with
        {
            Commitments = [.. request.Commitments, new("protected", default)
                { EquipmentClaims = [new("tool", Known(0.0), Known(450.0))] }],
            Proposed = new() { Remove = ["protected"] },
            Construction = request.Construction with
            {
                Equipment = [new("tool", "site", Known<EquatableArray<ForecastTimeWindow>>([new(0, 1000)]))],
                Estimates = [request.Construction.Estimates[0] with { FinishEquipment = ["tool"] }],
            },
        };
        var forecast = ForecastColony(request);
        Assert.Null(Assert.Single(forecast.Baseline.Construction).Comparison!.Selected);
        var selected = Assert.Single(forecast.Proposed.Construction).Comparison!.Selected!;
        Assert.Equal(150, selected.CompletedAt);
        Assert.Equal(50, Assert.Single(selected.EquipmentClaims).Start.Value);
        Assert.Equal(150, forecast.Proposed.EquipmentAvailability[0].Windows.Value[1].Start);
        Assert.Equal(450, forecast.Baseline.EquipmentAvailability[0].Windows.Value[0].Start);
    }

    [Fact]
    public void NativeConstructionProjectionRetainsRemainingWorkAndMissingMaterialUnknowns()
    {
        var row = new O.BuildingState
        {
            Building = new O.EntityRef { Id = "frame", Position = new C.Cell { X = 1, Z = 1 } },
            BuildDefName = "TestBuilding", Status = O.BuildingStatus.Frame,
            Rotation = RimGovernor.Contracts.Wire.Placement.Rotation.North,
            Construction = new O.ConstructionState { WorkLeft = 123, ResourcesComplete = false,
                Resources = [new O.MaterialDeficit { DefName = Material.Value, StillNeeded = 7 }] },
        };
        var observed = ObservationPackage.ConstructionFromCensus(new(null, Buildings.Of(row))).Construction.Value;
        Assert.Equal(123, observed.Sites[0].WorkLeft.Value);
        Assert.Equal(7, observed.Sites[0].MaterialsOwed.Value[0].Count);
        var missing = row with { Construction = new O.ConstructionState() };
        var unknown = ObservationPackage.ConstructionFromCensus(new(null, Buildings.Of(missing))).Construction.Value;
        Assert.False(unknown.Sites[0].WorkLeft.IsKnown);
        Assert.False(unknown.Sites[0].MaterialsOwed.IsKnown);
        Assert.Throws<ObservationContractException>(() => ObservationPackage.ConstructionFromCensus(new(null, Buildings.Of(row with { Construction = row.Construction with { WorkLeft = -1 } }))));
    }
}
