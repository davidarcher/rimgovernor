using RimGovernor.Contracts.Wire.Observations;
using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ForecastHandoffTests
{
    private static readonly Resource Steel = new("Steel");
    private static readonly Resource Food = new("Meal");
    private static readonly Resource Medicine = new("Medicine");

    private static ColonyForecastRequest Request(int steel = 20)
    {
        EquatableArray<Amount> stock = [new(Steel, steel), new(Food, 10), new(Medicine, 3)];
        var input = ProjectGraphTests.Colony() with
        {
            Stocks = new() { Nutrition = 5d, Materials = stock, Medicine = [new(Medicine, 3)] },
            Pawns = [],
        };
        var policy = new ColonyModelPolicy { Projects = new() { Nodes = [new() { Id = ProjectGraph.ResearchId(new("Research")), Kind = ProjectKind.Research, Work = 100 }] } };
        var evidence = new WorkAdmissionEvidence { Stock = Known(stock), Standing = new([], new Dictionary<Resource, long>()),
            Reservations = [], Promises = Known<EquatableArray<ProductionMaterialPromise>>([]) };
        return new() { Origin = new(0), HorizonDays = 1,
            Facts = new() { Stock = Known(stock), Food = new() { Larder = new(), Stocks = [new() { Place = new FoodPlace.Stands(true, ""), Form = new FoodForm.Item(), DefName = Food, Count = 10L, Nutrition = 5d, Rot = new FoodRot.Keeps() }] }, Items = new() { Nutrition = new Dictionary<Resource, double> { [Food] = .5 } } },
            Coupled = new() { Activities = new() { Deadline = 0, Activities = [new("research", "", "", []) { ProjectProgress = new("Research", 1) }] },
                ModelBasis = new(input, policy), Needs = NeedsFixtures.Empty, Resources = new(evidence, new([], [], [], []), default),
                InitialLots = [new("meal", "", Food, 10d) { Freshness = new FoodFreshness.Rots(120000d) }] } };
    }
    private static ForecastCoupledOutcome Outcome() => new(new([new("research", "pawn", 0, 0, 0, 50, 0, 0, 50, 0, 0)], 50, 0), true,
        new Dictionary<string, ForecastNeedsResult>(), []);

    private static ForecastHandoff Handoff(ColonyForecastRequest request, IReadOnlyDictionary<Resource, double> stock,
        ForecastCoupledOutcome? outcome = null, IReadOnlyDictionary<string, ForecastCommitment>? commitments = null)
    {
        commitments ??= new Dictionary<string, ForecastCommitment>();
        return ForecastHandoffPolicy.Project(request, commitments, outcome ?? Outcome(), stock,
            new Dictionary<string, double> { ["research"] = 50 }, ForecastCoupledMaterials.MaterialHoldsOf(request, commitments), new(request, commitments, stock, new Dictionary<string, double>(), null) { MedicineComplete = true, PhysicalCensusComplete = true, Lots = EquatableArray.From(stock.Select(p => new ForecastTerminalLot(p.Key.Value, p.Key, p.Value, double.PositiveInfinity, p.Key == Food ? FoodEligibility.Eligible : FoodEligibility.Ineligible, p.Key == Food ? .5 : 0, [], p.Key == Medicine))) });
    }

    [Fact]
    public void EqualResearchWithDifferentRemainingStocksProducesDifferentUsableState()
    {
        var a = Handoff(Request(), new Dictionary<Resource, double> { [Steel] = 12d, [Food] = 8d, [Medicine] = 3d });
        var b = Handoff(Request(), new Dictionary<Resource, double> { [Steel] = 17d, [Food] = 8d, [Medicine] = 3d });
        Assert.Empty(a.Gaps);
        Assert.Empty(b.Gaps);
        Assert.Equal(50, Assert.Single(a.State!.Projects).Remaining);
        Assert.Equal(a.State!.Projects, b.State!.Projects);
        Assert.Equal(12, Assert.Single(a.State!.Materials, s => s.Resource == Steel).Count);
        Assert.Equal(17, Assert.Single(b.State!.Materials, s => s.Resource == Steel).Count);
        Assert.Equal(4, a.State!.Nutrition);
        Assert.Equal(1, a.State!.Day);
        Assert.Equal(60, a.State!.EndDay);
        var basis = Request().Coupled!.ModelBasis!;
        var continued = ColonyModel.Run(basis.Input, a.State, [], basis.Policy);
        Assert.False(continued.Truncated);
        Assert.Equal(60, continued.Events[^1].Day);
    }

    [Fact]
    public void InventoryWalkExportsProductionConsumptionAndSpoilageExactlyOnce()
    {
        var request = Request();
        var inventory = new ColonyForecastRules.InventoryWalk();
        inventory.Run(new(request.Facts.Stock, new Dictionary<string, ForecastCommitment>(), 1),
            [(Food, .1, -2d), (Food, .2, 4d), (Food, .8, -1d), (Steel, .3, -5d)], []);
        var handoff = Handoff(request, inventory.Terminal);
        Assert.Empty(handoff.Gaps);
        Assert.Equal(5.5, handoff.State!.Nutrition);
        Assert.Equal(15, Assert.Single(handoff.State!.Materials, s => s.Resource == Steel).Count);
    }

    [Fact]
    public void AnUnreadStockLeavesNoStateAndNamesTheGapRatherThanDefaultingTheStock()
    {
        var request = Request();
        var handoff = Handoff(request with { Facts = request.Facts with { Stock = default } }, new Dictionary<Resource, double> { [Steel] = 12d, [Food] = 8d, [Medicine] = 3d });

        Assert.Null(handoff.State);
        Assert.Contains("terminal_stock_unknown", handoff.Gaps);
    }

    [Fact]
    public void UnknownTerminalNeedIsPreservedRatherThanReset()
    {
        var outcome = Outcome() with { Needs = new Dictionary<string, ForecastNeedsResult>
        { ["pawn"] = new([new("food", default, default, default, default), new("rest", Known(.17), default, default, default)], default, default, new(new("pawn", "", new ForecastCapacityWindows.Unread(), 0), []), []) } };
        var handoff = Handoff(Request(), new Dictionary<Resource, double> { [Steel] = 20d, [Food] = 10d, [Medicine] = 3d }, outcome);
        Assert.False(Assert.Single(handoff.Needs, n => n.Kind == "food").Level.IsKnown);
        Assert.Equal(.17, Assert.Single(handoff.Needs, n => n.Kind == "rest").Level.Value);
        Assert.Contains("terminal_need_unknown:pawn:food", handoff.Gaps);
    }

    [Fact]
    public void CompletedComfortAndShelterEffectsCarryWithoutRejectingUnrelatedAssessmentLimits()
    {
        var request = Request();
        var basis = request.Coupled!.ModelBasis!;
        request = request with { Coupled = request.Coupled with { ModelBasis = basis with { Input = basis.Input with
        { Pawns = [new() { Pawn = "pawn", Skills = [], ComfortableMin = 10d, ComfortableMax = 30d, Consciousness = 1, Moving = 1, Manipulation = 1, Sight = 1, Talking = 1, ResearchSpeed = 1, PlantWorkSpeed = 1, Hunts = false }] } },
            Shelters = [new(new(ForecastShelterSource.Planned, "1"), [new(1, 1)], ["research"], ShelterOutcomes.Estimated(0, 5, .01),
                [new("heater", new(1, 1), false, .1, 20d, CoupledThermalTests.Powered(90000d))], [])] } };
        var commitment = new ForecastCommitment("warm", []) { ActivityCompletions = ["research"], ComfortShifts = [new("pawn", -5, 2)] };
        var outcome = Outcome() with { Findings = [new("thermal_relief_unassessed", "")],
            ServedRooms = [new(1, "Wood", 50)], Shelters = [new ForecastShelterOutcome.Projected(new(ForecastShelterSource.Planned, "1"), 50, 20, [], [])] };
        var handoff = Handoff(request, new Dictionary<Resource, double> { [Steel] = 20d, [Food] = 10d, [Medicine] = 3d }, outcome,
            new Dictionary<string, ForecastCommitment> { [commitment.Id] = commitment });
        Assert.Empty(handoff.Gaps);
        Assert.Equal(5, Assert.Single(handoff.State!.Pawns).ComfortableMin);
        Assert.Equal(32, Assert.Single(handoff.State!.Pawns).ComfortableMax);
        Assert.Equal(50d / GameTime.TicksPerDay, Assert.Single(handoff.ActiveEffects).Day);
        Assert.Empty(Assert.Single(handoff.ActiveEffects).Effect.ActivityCompletions);
        Assert.Single(handoff.ServedRooms);
        Assert.Single(handoff.Shelters);
        var shelter = Assert.Single(handoff.ShelterPlans);
        Assert.Equal(20, shelter.Estimated().InitialTemperature);
        Assert.Equal(30050, Assert.IsType<RimGovernor.Contracts.Wire.Observations.HeatSupply.Powered>(Assert.Single(shelter.Sources).Supply).Ticks);
        Assert.Empty(shelter.Prerequisites);
    }

    [Fact]
    public void EligibleTerminalLotsDoNotDependOnAnAggregateCensusEquality()
    {
        var request = Request();
        request = request with { Coupled = request.Coupled! with { ModelBasis = request.Coupled.ModelBasis! with
        { Input = request.Coupled.ModelBasis.Input with { Stocks = request.Coupled.ModelBasis.Input.Stocks with { Nutrition = 4d } } } } };
        var handoff = Handoff(request, new Dictionary<Resource, double> { [Steel] = 20d, [Food] = 10d, [Medicine] = 3d });
        Assert.Equal(5d, handoff.State!.Nutrition);
        Assert.DoesNotContain("terminal_food_census_scope_mismatch", handoff.Gaps);
    }
    [Fact]
    public void LinkedOwnerDebitReleasesOnlyItsHold()
    {
        var request = Request();
        var paid = new ForecastCommitment("paid", [new(Steel, .5, -5d)]);
        var other = new ForecastCommitment("other", []) { MaterialClaims = [new(Steel, Known(3d), Known(""))] };
        var promises = Known<EquatableArray<ProductionMaterialPromise>>([new("paid", [new(Steel, Known(5d), Known(""))]), new("other", other.MaterialClaims)]);
        request = request with { Commitments = [paid, other], Coupled = request.Coupled! with
        { Resources = request.Coupled.Resources with { Evidence = request.Coupled.Resources.Evidence with { Promises = promises } } } };
        var handoff = Handoff(request, new Dictionary<Resource, double> { [Steel] = 15d, [Food] = 10d, [Medicine] = 3d },
            commitments: new Dictionary<string, ForecastCommitment> { [paid.Id] = paid, [other.Id] = other });
        Assert.Empty(handoff.Gaps);
        Assert.Equal(3, Assert.Single(handoff.State!.ExternalHolds).Count);
        Assert.Equal(15, Assert.Single(handoff.State!.Materials, s => s.Resource == Steel).Count);
    }

    [Theory]
    [InlineData(.1)]
    [InlineData(.4)]
    public void CompletedSowingCarriesElapsedGrowthAcrossSeasonBoundary(double completion)
    {
        var request = Request();
        var basis = request.Coupled!.ModelBasis!;
        var nodes = new ProjectGraph { Nodes = [new() { Id = "sow:zone1", Field = "zone1", Crop = ColonyModelFixtures.Crop, Kind = ProjectKind.Sow, Work = 400, Cells = 1 },
            new() { Id = "grow", Field = "zone1", Crop = ColonyModelFixtures.Crop, Kind = ProjectKind.Grow, Prerequisites = ["sow:zone1"] }] };
        var input = basis.Input with { Climate = basis.Input.Climate with { TicksToNextTwelfth = 12000 } };
        request = request with { Coupled = request.Coupled with { ModelBasis = new(input, basis.Policy with { Projects = nodes }),
            Activities = new() { Deadline = 0, Activities = [new("research", "", "", []) { ProjectProgress = new("sow:zone1", 1d / 50) }] } } };
        var outcome = Outcome() with { Plan = new([new("research", "pawn", 0, 0, completion * GameTime.TicksPerDay - 50, completion * GameTime.TicksPerDay, 0, 0, 50, 0, 0)], completion * GameTime.TicksPerDay, 0) };
        var handoff = Handoff(request, new Dictionary<Resource, double> { [Steel] = 20d, [Food] = 10d, [Medicine] = 3d }, outcome);
        Assert.Empty(handoff.Gaps);
        Assert.Equal(ModelProjectStatus.Done, Assert.Single(handoff.State!.Projects, p => p.Id == "sow:zone1").Status);
        Assert.Equal(completion, Assert.Single(handoff.Completed).Day, 8);
        var growth = Assert.Single(handoff.State!.Projects, p => p.Id == "grow/cohort/0");
        Assert.True(growth.GrowthInitialized);
        Assert.True(growth.Remaining < input.Crops[0].HarvestMinGrowth.Value);
    }

    [Fact]
    public void UnrelatedFoodOutputDoesNotPretendToConsumeAHerd()
    {
        var request = Request();
        var basis = request.Coupled!.ModelBasis!;
        request = request with { Coupled = request.Coupled with { ModelBasis = basis with { Policy = basis.Policy with
        { Projects = new() { Nodes = [new() { Id = "hunt", Kind = ProjectKind.Hunt, Work = 100, Nutrition = 5 }] } } },
            Activities = new() { Deadline = 0, Activities = [new("research", "", "", []) { Outputs = [new(Food, 4d)] }] } } };
        var handoff = Handoff(request, new Dictionary<Resource, double> { [Steel] = 20d, [Food] = 14d, [Medicine] = 3d });
        Assert.Empty(handoff.Gaps);
        Assert.Equal(100, Assert.Single(handoff.State!.Projects).Remaining);
    }

    [Fact]
    public async Task SelectedHandoffRoundTripsThroughProductionDecisionAndReport()
    {
        var handoff = Handoff(Request(), new Dictionary<Resource, double> { [Steel] = 20d, [Food] = 10d, [Medicine] = 3d });
        var directory = Path.Combine(Path.GetTempPath(), "forecast-handoff-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var writer = RimGovernor.Brain.Telemetry.Otlp.OtlpFileWriter.Start(new() { Dir = directory }))
            {
                var emitter = new RimGovernor.Brain.Telemetry.TelemetryEmitter(writer);
                emitter.Decide(new() { Kind = RimGovernor.Brain.Telemetry.TelemetryKinds.CoupledSelectionKind, Component = "rounds", Verdict = "selected", Reason = "test", Target = "method",
                    Attrs = new Dictionary<string, object?> { [RimGovernor.Brain.Telemetry.Otlp.ForecastHandoffReader.Key] = RimGovernor.Brain.Telemetry.Otlp.ForecastHandoffReader.Fields(handoff) } });
            }
            var evidence = RimGovernor.Brain.Telemetry.Otlp.ForecastEvidenceReader.Read(Path.Combine(directory, "logs.jsonl"));
            var row = Assert.Single(evidence.Handoffs);
            Assert.True(row.Available);
            Assert.Equal(1, row.Day);
            Assert.Equal(60, row.EndDay);
            Assert.Empty(row.Gaps);
            var report = System.Text.Json.JsonSerializer.Serialize(RimGovernor.Brain.Telemetry.Otlp.ForecastResidualReport.Build(evidence));
            Assert.Contains("Handoffs", report, StringComparison.Ordinal);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }    [Fact]
    public void ReservationWithoutPhysicalStockRemainsVisibleAndCannotFinanceContinuation()
    {
        var request = Request(0);
        var promise = new ForecastCommitment("other", []) { MaterialClaims = [new(Steel, Known(3d), Known(""))] };
        var noSteel = request.Facts.Stock.Value.Where(s => s.Resource != Steel).ToArray();
        var stock = Known(EquatableArray.From(noSteel));
        var basis = request.Coupled!.ModelBasis!;
        request = request with { Facts = request.Facts with { Stock = stock },
            Coupled = request.Coupled with { ModelBasis = basis with { Input = basis.Input with { Stocks = basis.Input.Stocks with { Materials = stock.Value } } },
                Resources = request.Coupled.Resources with { Evidence = request.Coupled.Resources.Evidence with
                { Stock = stock, Promises = Known<EquatableArray<ProductionMaterialPromise>>([new("other", promise.MaterialClaims)]) } } } };
        var handoff = Handoff(request, new Dictionary<Resource, double> { [Food] = 10d, [Medicine] = 3d });
        Assert.Equal(3, Assert.Single(handoff.State!.ExternalHolds).Count);
        Assert.Contains("colony_model_holds_exceed_stock", handoff.Gaps);
    }}
