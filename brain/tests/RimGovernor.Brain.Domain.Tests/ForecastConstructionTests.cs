using RimGovernor.Contracts.Wire.Observations;
using IngredientReservation = RimGovernor.Brain.Policy.IngredientReservation;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Observation;
using RimGovernor.Brain.Bridge.Contracts;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using O = RimGovernor.Contracts.Wire.Observations;
using C = RimGovernor.Contracts.Wire.Common;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ForecastConstructionTests
{
    private static readonly Resource Material = new("TestMaterial");
    private static ConstructionSite Site(string id = "site", int x = 1, long owed = 10, double work = 100) => new()
    {
        Id = id, Building = new Building("TestBuilding", new Cell(x, 1), Rotation.North, ""), Stage = "frame",
        WorkLeft = work, MaterialsOwed = EquatableArray.From(new[] { new Amount(Material, owed) }),
        ResourcesComplete = owed == 0, NativeFinishingSkill = 4, Minimum = new O.FinishingMinimum { Skill = 6, EligibleFinishers = 1 },
        QualitySensitive = true, Gate = new O.ConstructionGate.Tiered { Tier = ConstructionTier.Sustain },
    };
    private static ForecastConstructionWorker Worker(string id = "worker", int skill = 8) => new(
        PawnFixtures.Worker(id) with { Skills = [new WorkSkill { Name = "Construction", Level = skill }] },
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
            Census = CurrentConstruction.Empty with { Sites = EquatableArray.From(sites) },
            Workers = [Worker()], IngredientReservations = [],
            Estimates = sites.Select(s => new ForecastConstructionEstimate(s.Id, "worker", "stock", Known(true), Known(true), Known(100.0), Known(0.0))).ToArray(),
            Routes = sites.Select(s => new ForecastActivityRoute("worker", "stock", s.Id, new RouteTicks.Reachable(50.0)))
                .Concat(sites.SelectMany(s => sites.Where(t => t.Id != s.Id).Select(t => new ForecastActivityRoute("worker", s.Id, t.Id, new RouteTicks.Reachable(50.0)))))
                .Concat(sites.Select(s => new ForecastActivityRoute("worker", s.Id, "stock", new RouteTicks.Reachable(50.0)))).ToArray(),
        },
    };

    [Fact]
    public void ObservedWorkAndMaterialsGenerateDependenciesAndInventoryConsumptionOnce()
    {
        var result = ColonyForecastRules.ForecastColony(Request(Site()));
        var construction = Assert.Single(result.Proposed.Construction);
        Assert.Equal("scheduled", construction.Reason);
        var plan = construction.Comparison!.Selected!;
        Assert.Equal(2, plan.Allocations.Length);
        Assert.Equal(100, Assert.Single(plan.Allocations, a => a.Activity == "finish").Start);
        Assert.Equal(200.0 / GameTime.TicksPerDay, construction.CompletedDay.Value);
        Assert.Equal(-10, Assert.Single(construction.MaterialChanges).Units.Value);
        Assert.Empty(result.Proposed.Commitments[0].StockChanges);
    }

    [Fact]
    public void DeltaCancellationReleasesMaterialsAndCapacityWithoutChangingBaseline()
    {
        var a = Site("a"); var b = Site("b", 2);
        var request = Request(a, b) with { Proposed = new() { Remove = EquatableArray.From(new List<string> { "a" }) } };
        var result = ColonyForecastRules.ForecastColony(request);
        Assert.Equal("scheduled", result.Baseline.Construction[0].Reason);
        Assert.Equal("insufficient_uncommitted_materials", result.Baseline.Construction[1].Reason);
        Assert.Equal("scheduled", Assert.Single(result.Proposed.Construction).Reason);
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
        var plan = ColonyForecastRules.ForecastColony(request).Proposed.Construction[0].Comparison!.Selected!;
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
        Assert.Single(ColonyForecastRules.ForecastColony(request).Proposed.Construction, r => r.Reason == "scheduled");
        var extra = Worker("other");
        request = request with { Construction = request.Construction with
        {
            Workers = [Worker(), extra],
            Estimates = [.. request.Construction.Estimates, .. request.Construction.Estimates.Select(e => e with { Pawn = "other" })],
            Routes = [.. request.Construction.Routes, .. request.Construction.Routes.Select(r => r with { Pawn = "other" })],
        } };
        Assert.All(ColonyForecastRules.ForecastColony(request).Proposed.Construction, r => Assert.Equal("scheduled", r.Reason));
    }

    [Fact]
    public void FinishingSkillAndTierProtectionsSurviveActivityGeneration()
    {
        var request = Request(Site());
        Assert.Null(Assert.Single(ColonyForecastRules.ForecastColony(request with { Construction = request.Construction with { Workers = [Worker(skill: 5)] } }).Proposed.Construction).Comparison!.Selected);
        var high = Site("high") with { Gate = new O.ConstructionGate.Tiered { Tier = ConstructionTier.Expand } };
        var low = Site("low", 2);
        request = Request(high) with { Construction = Request(high).Construction with { Census = CurrentConstruction.Empty with { Sites = EquatableArray.From(new[] { high, low }) } } };
        Assert.Equal("lower_tier_material_need", Assert.Single(ColonyForecastRules.ForecastColony(request).Proposed.Construction).Reason);
    }

    [Fact]
    public void MissingRoutesAreNamedUnrouted()
    {
        var b = Site("b", 2, owed: 0);
        Assert.Equal("scheduled", Assert.Single(ColonyForecastRules.ForecastColony(Request(b)).Proposed.Construction).Reason);
        var missing = ColonyForecastRules.ForecastColony(Request(b) with { Construction = Request(b).Construction with { Routes = [] } });
        Assert.Null(missing.Proposed.Construction[0].Comparison!.Selected);
        Assert.Contains(missing.Proposed.Construction[0].Comparison!.Findings, f => f.StartsWith("unrouted:", StringComparison.Ordinal));
    }

    [Fact]
    public void BillHoldsAndProtectedConsumptionCannotFundConstruction()
    {
        var request = Request(Site());
        request = request with { Construction = request.Construction with { IngredientReservations = EquatableArray.From(new[]
        { new IngredientReservation { Pawn = new RimGovernor.Brain.Domain.PawnId("cook"), Bill = "cook-bill", Items = [new Amount(Material, 6)] } }) } };
        Assert.Equal("insufficient_uncommitted_materials", Assert.Single(ColonyForecastRules.ForecastColony(request).Proposed.Construction).Reason);
        request = Request(Site());
        request = request with { Commitments = EquatableArray.From(request.Commitments.Append(new ForecastCommitment("other", EquatableArray.From(new[]
        { new ForecastStockChange(Material, Known(0.0), Known(-6.0)) })))) };
        Assert.Equal("insufficient_uncommitted_materials", Assert.Single(ColonyForecastRules.ForecastColony(request).Proposed.Construction).Reason);
    }

    [Fact]
    public void UpdatedCensusAndAvailabilityInvalidateForecastWithoutStoredPlan()
    {
        var site = Site(); var request = Request(site);
        var complete = CurrentConstruction.Empty with { Buildings = EquatableArray.From(new[] { new CurrentBuilding { Id = "built", Building = site.Building } }) };
        Assert.Equal("already_built", Assert.Single(ColonyForecastRules.ForecastColony(request with { Construction = request.Construction with { Census = complete } }).Proposed.Construction).Reason);
        Assert.Equal("construction_gone", Assert.Single(ColonyForecastRules.ForecastColony(request with { Construction = request.Construction with { Census = CurrentConstruction.Empty } }).Proposed.Construction).Reason);
        var worker = Worker();
        var unavailable = request with { Construction = request.Construction with { Workers = [worker with { Observed = worker.Observed with { Available = false } }] } };
        Assert.Null(Assert.Single(ColonyForecastRules.ForecastColony(unavailable).Proposed.Construction).Comparison!.Selected);
        var remaining = site with { WorkLeft = 20.0, MaterialsOwed = default, ResourcesComplete = true };
        var revised = ColonyForecastRules.ForecastColony(request with { Construction = request.Construction with { Census = CurrentConstruction.Empty with { Sites = EquatableArray.From(new[] { remaining }) } } });
        Assert.Equal(70.0 / GameTime.TicksPerDay, revised.Proposed.Construction[0].CompletedDay.Value);
        Assert.Empty(revised.Proposed.Construction[0].MaterialChanges);
    }

    [Fact]
    public void SearchBudgetIsSharedAndDuplicateConstructionCannotDoubleCharge()
    {
        var request = Request(Site("a", owed: 0), Site("b", 2, owed: 0));
        var forecast = ColonyForecastRules.ForecastColony(request with { Construction = request.Construction with { ExpansionBudget = 2 } });
        Assert.Equal("scheduled", forecast.Proposed.Construction[0].Reason);
        Assert.Equal("search_budget_exhausted", forecast.Proposed.Construction[1].Reason);
        Assert.Throws<DomainException>(() => ColonyForecastRules.ForecastColony(request with { Commitments = EquatableArray.From(new[] { request.Commitments[0], request.Commitments[0] with { Id = "duplicate" } }) }));
    }

    [Fact]
    public void SelectedIngredientPromisesBlockCompetingConstructionUntilCancelled()
    {
        var request = Request(Site());
        var promise = new ForecastCommitment("cooking", default)
        { MaterialClaims = [new(Material, Known(6.0), Known(""))] };
        request = request with { Commitments = [.. request.Commitments, promise], Proposed = new() { Remove = [promise.Id] } };
        var forecast = ColonyForecastRules.ForecastColony(request);
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
        var result = ColonyForecastRules.ForecastColony(request with { Commitments = [.. request.Commitments, unrelated] });
        Assert.Equal("scheduled", Assert.Single(result.Proposed.Construction).Reason);
        var related = unrelated with { StockChanges = [new(Material, Known(0.0), Unknown<double>())] };
        result = ColonyForecastRules.ForecastColony(request with { Commitments = [.. request.Commitments, related] });
        Assert.Equal("unknown_available_materials", Assert.Single(result.Proposed.Construction).Reason);
    }

    [Fact]
    public void ConstructionUsesSharedEquipmentWindowsAndCancellationReleasesThem()
    {
        var request = Request(Site(owed: 0));
        request = request with
        {
            Commitments = [.. request.Commitments, new("protected", default)
                { EquipmentClaims = [new("tool", 0, 450)] }],
            Proposed = new() { Remove = ["protected"] },
            Construction = request.Construction with
            {
                Equipment = [new("tool", "site", [new(0, 1000)])],
                Estimates = [request.Construction.Estimates[0] with { FinishEquipment = ["tool"] }],
            },
        };
        var forecast = ColonyForecastRules.ForecastColony(request);
        Assert.Null(Assert.Single(forecast.Baseline.Construction).Comparison!.Selected);
        var selected = Assert.Single(forecast.Proposed.Construction).Comparison!.Selected!;
        Assert.Equal(150, selected.CompletedAt);
        Assert.Equal(150, forecast.Proposed.EquipmentAvailability[0].Windows[1].Start);
        Assert.Equal(450, forecast.Baseline.EquipmentAvailability[0].Windows[0].Start);
    }

    [Fact]
    public void NativeConstructionProjectionRetainsRemainingWorkAndOwedMaterials()
    {
        var row = new O.BuildingState
        {
            Building = new O.EntityRef { Id = "frame", DefName = "Frame_Wall", Label = "wall (building)", Place = new O.EntityPlace.OnMap(0, new C.Cell { X = 1, Z = 1 }) },
            Rotation = RimGovernor.Contracts.Wire.Placement.Rotation.North,
            Stuff = null,
            Faction = null,
            Status = O.BuildingStatus.Frame,
            HitPoints = new O.BuildingHitPoints.Untracked(),
            Construction = new O.ConstructionState.Build
            {
                Definition = "TestBuilding",
                WorkLeft = 123,
                Resources = [new O.MaterialDeficit { DefName = Material.Value, StillNeeded = 7 }],
                QualitySensitive = false,
                NativeFinishingSkill = 0,
                Minimum = null,
                Gate = new O.ConstructionGate.Ungated(),
            },
            Service = BuildingServiceWire.NoService,
            Settings = new O.BuildingSettings { Forbidden = false, Control = new O.BuildingControl.None() },
            Burning = false,
            Occupied = new O.Rectangle { Minimum = new C.Cell { X = 1, Z = 1 }, Maximum = new C.Cell { X = 1, Z = 1 } },
            Anomaly = null,
            Door = null,
        };
        var observed = ConstructionReads.ConstructionFromCensus(new(null, Buildings.Of(row)));
        Assert.Equal("TestBuilding", observed.Sites[0].Building!.Definition);
        Assert.Equal(123, observed.Sites[0].WorkLeft);
        Assert.Equal(7, observed.Sites[0].MaterialsOwed[0].Count);
        Assert.False(observed.Sites[0].ResourcesComplete);
        Assert.Null(observed.Sites[0].Minimum);
        Assert.IsType<O.ConstructionGate.Ungated>(observed.Sites[0].Gate);
        var gated = row with
        {
            Construction = ((O.ConstructionState.Build)row.Construction) with
            {
                Minimum = new O.FinishingMinimum { Skill = 6, EligibleFinishers = 2 },
                Gate = new O.ConstructionGate.Tiered { Tier = ConstructionTier.Sustain },
            },
        };
        var gatedSite = ConstructionReads.ConstructionFromCensus(new(null, Buildings.Of(gated))).Sites[0];
        Assert.Equal(new O.FinishingMinimum { Skill = 6, EligibleFinishers = 2 }, gatedSite.Minimum);
        Assert.Equal(ConstructionTier.Sustain, Assert.IsType<O.ConstructionGate.Tiered>(gatedSite.Gate).Tier);
        var missing = row with { Construction = null };
        Assert.Throws<ObservationContractException>(() => ConstructionReads.ConstructionFromCensus(new(null, Buildings.Of(missing))));
    }
}
