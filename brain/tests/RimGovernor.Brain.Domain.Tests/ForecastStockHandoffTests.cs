using RimGovernor.Brain.Runtime;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.GameFacts.Common;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;
using Xunit;
using IngredientReservation = RimGovernor.Brain.Policy.IngredientReservation;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ForecastStockHandoffTests
{
    private static readonly Resource Meal = new("Meal");
    private static readonly Resource Medicine = new("Medicine");
    private static FoodStock Stock(string id, long count, long rot = 120000) => new FoodStock {
        Id = id, DefName = Meal.Value, Count = count, Nutrition = count * .5, Place = new FoodPlace.Stands(true, ""), Form = new FoodForm.Item(),
        Rot = new FoodRot.Rots(rot), Eaters = [new("animal")],
        IsHumanMeat = false, RawMeat = false, IsHumanlike = false, Vegetable = false, RawClass = FoodIngredientClass.None, Holder = new PawnId(string.Empty), UnitMass = 0, TemperatureC = 0, RotRate = 0, Forbidden = false };
    private static ColonyForecastRequest Request(params FoodStock[] stocks) => Request(new HashSet<string>(), stocks);
    private static ColonyForecastRequest Request(HashSet<string> reserved, params FoodStock[] stocks)
    {
        EquatableArray<Amount> physical = [new(Meal, stocks.Where(s => !reserved.Contains(s.Id) && s.Holder.IsEmpty).Sum(s => s.Count)), new(Medicine, 5)];
        var facts = new ColonyForecastFacts
        {
            Census = ColonyFactsCoreTests.Snapshot() with { Resources = EquatableArray.From(physical.Select(amount => new RimGovernor.Brain.GameFacts.Observations.Quantity { DefName = amount.Resource.Value, Units = amount.Count })), ResourceThings = EquatableArray.From(stocks.Where(s => !reserved.Contains(s.Id) && s.Holder.IsEmpty).Select(s => s.Id).Append("med")) },
            Food = new() { Reserved = reserved, Larder = new() { RawMeatNutrition = 0, CookDemandNutrition = 0, Corpses = [] }, Stocks = stocks, Consumers = [new FoodConsumer { Id = new("animal"), NutritionPerDay = 1d,
        HumanMeatAcceptable = false }] },
            Medicine = new() { Items = [new() { Id = "med", Definition = Medicine.Value, Count = 5, Forbidden = false, Rot = new RimGovernor.Brain.GameFacts.Observations.ItemRot.Never() }, new() { Id = "barred-med", Definition = Medicine.Value, Count = 100, Forbidden = true, Rot = new RimGovernor.Brain.GameFacts.Observations.ItemRot.Never() }] },
            Items = new() { Nutrition = new Dictionary<Resource, double> { [Meal] = .5 }, MedicalPotency = new Dictionary<Resource, double> { [Medicine] = 1 } }
        };
        var input = ProjectGraphTests.Colony() with { OtherNutritionPerDay = ForecastStockScope.OtherDemand(facts.Food, []), Stocks = new() { Materials = physical, Nutrition = ForecastStockScope.Nutrition(facts.Food), Medicine = [new(Medicine, 5)] }, Pawns = [] };
        return new() { Origin = new(0), HorizonDays = 1, Facts = facts, Coupled = new()
        {
            Activities = new() { Deadline = 0 }, Needs = NeedsFixtures.Empty, ModelBasis = new(input, new() { MedicineTendsPerDay = 0 }),
            FoodUses = [new("animal", Meal.Value, new(1, 1), true, .5, 10d, 1, new FoodFreshness.Rots(120000d)) { Ingredient = "", Thoughts = [] }],
            Resources = new(new() { Stock = physical, Standing = new([], new Dictionary<Resource, long>()), Reservations = [], Promises = AdmissionAssertions.MaterialPromises([]) }, new([], [], [], []), new MaterialAvailability.Blocked(MaterialBudgetBlock.AwaitingReview))
        } };
    }
    private static ForecastCoupledOutcome Evaluate(ColonyForecastRequest request, params ForecastCommitment[] changes) =>
        new ForecastCoupled.CoupledSearchCache().Evaluate(request, changes.ToDictionary(c => c.Id), request.Coupled!.Activities, new([], 0, 0));

    [Fact]
    public void ScheduledTreatmentReadsActualDetailedLotsAndCarriesItsSingleHistoryIntoContinuation()
    {
        var request = Request(Stock("food", 10));
        var basis = request.Coupled!.ModelBasis!;
        var policy = basis.Policy with
        {
            Scheduled = [new() { Day = .25, Kind = ModelEventKind.MedicineTreatment, Stock = ModelStock.Medicine, Id = "native/disease",
                MedicineDemand = (history, _) => history.Events[^1].Medicine.Sum() > 3 ? 2 : 1 },
                new() { Day = 2, Kind = ModelEventKind.MedicineTreatment, Stock = ModelStock.Medicine, Id = "native/retend",
                    MedicineDemand = (history, _) => history.TreatmentHistory is null ? 2 : 1 }],
        };
        request = request with { Coupled = request.Coupled with { ModelBasis = basis with { Policy = policy } } };
        var outcome = Evaluate(request);
        var full = ForecastHandoffPolicy.Project(outcome)!;
        var spent = ForecastHandoffPolicy.Project(Evaluate(request, new ForecastCommitment("surgery", [new(Medicine, .1, -3d)])))!;
        Assert.Empty(full.Gaps);
        Assert.Empty(spent.Gaps);
        Assert.Equal(3, Assert.Single(full.State!.Medicine).Count);
        Assert.Equal(1, Assert.Single(spent.State!.Medicine).Count);
        var arrived = ForecastHandoffPolicy.Project(Evaluate(request, new ForecastCommitment("delivery", [new(Medicine, .25, 2d)])))!;
        Assert.Equal(5, Assert.Single(arrived.State!.Medicine).Count);
        Assert.Same(outcome.Terminal!.TreatmentHistory, full.State.TreatmentHistory);
        var history = Assert.IsType<ColonyTrajectory>(full.State.TreatmentHistory);
        Assert.Equal(2, Assert.Single(history.Events, e => e.Kind == ModelEventKind.MedicineTreatment).MedicineConsumed.Sum());
        Assert.Equal(100, Assert.Single(Evaluate(request).Terminal!.Lots, l => l.Id == "barred-med").Count);
        var continued = ColonyModel.Run(basis.Input, full.State with { EndDay = 3 }, [], policy);
        Assert.Equal(2, continued.Events[^1].Medicine.Sum());
    }

    [Fact]
    public void FullEligibleCensusConsumesOtherPawnDemandAndExpiresOnlyRemainingUnits()
    {
        var request = Request(new HashSet<string> { "reserve" }, Stock("early", 4, 30000), Stock("late", 10), Stock("reserve", 100),
            Stock("held", 2) with { Holder = new PawnId("animal") });
        var result = Evaluate(request);
        var handoff = ForecastHandoffPolicy.Project(result)!;
        Assert.Empty(handoff.Gaps);
        Assert.Equal(0, Assert.Single(result.Terminal!.Lots, l => l.Id == "early").Count);
        Assert.Equal(10, Assert.Single(result.Terminal.Lots, l => l.Id == "late").Count);
        Assert.Equal(5.5, handoff.State!.Nutrition);
        Assert.Equal(5, Assert.Single(handoff.State!.Medicine).Count);
        Assert.Equal(10, Assert.Single(handoff.State!.Materials, r => r.Resource == Meal).Count);
        var basis = request.Coupled!.ModelBasis!;
        var continued = ColonyModel.Run(basis.Input, handoff.State with { EndDay = 2 }, [], basis.Policy);
        Assert.Equal(4.5, continued.Events[^1].Levels[(int)ModelStock.Nutrition], 8);
    }

    [Fact]
    public void ExternalOutputsAndMultiLotDebitsCarryOnceAndRemainUsable()
    {
        var request = Request(Stock("first", 4), Stock("second", 6));
        var outcome = Evaluate(request, new("debit", [new(Meal, .25, -3d)]), new("harvest", [new(Meal, .5, 8d)]),
            new("tend", [new(Medicine, .5, -2d)]));
        Assert.True(outcome.Feasible);
        var handoff = ForecastHandoffPolicy.Project(outcome)!;
        Assert.Empty(handoff.Gaps);
        Assert.Equal(6.5, handoff.State!.Nutrition);
        Assert.Equal(13, Assert.Single(handoff.State!.Materials, r => r.Resource == Meal).Count);
        Assert.Equal(3, Assert.Single(handoff.State!.Medicine).Count);
    }

    [Fact]
    public void CompletedProductionAndTypedScheduledOutputUseTheSamePhysicalLedger()
    {
        var request = Request(Stock("source", 10));
        var task = new ForecastActivity("cook", "census", "kitchen", [])
        {
            Inputs = [new(Meal, 4d) { Lot = "source" }],
            Outputs = [new(Meal, 6d) { Lot = "cooked", Perishable = true, FreshTicks = 120000d }]
        };
        request = request with { Coupled = request.Coupled! with
        {
            Activities = new() { Deadline = 0, Activities = [task] }, ModelBasis = request.Coupled.ModelBasis! with { Policy = new()
            { MedicineTendsPerDay = 0, Scheduled = [new() { Day = .5, Kind = ModelEventKind.Harvest, Id = "medicine", Stock = ModelStock.Medicine, Resource = Medicine, Amount = 2 },
                new() { Day = .5, Kind = ModelEventKind.Harvest, Id = "food", Stock = ModelStock.Nutrition, Resource = Meal, Amount = 1 },
                new() { Day = .5, Kind = ModelEventKind.ProjectCompletion, Id = "research", Stock = ModelStock.ResearchPoints, Amount = 3 }] } }
        } };
        var plan = new ForecastActivityPlan([new("cook", "worker", 0, 0, 0, 100, 0, 0, 100, 0, 0)], 100, 0);
        var outcome = new ForecastCoupled.CoupledSearchCache().Evaluate(request, new Dictionary<string, ForecastCommitment>(), request.Coupled.Activities, plan);
        Assert.True(outcome.Feasible);
        var handoff = ForecastHandoffPolicy.Project(outcome)!;
        Assert.Empty(handoff.Gaps);
        Assert.Equal(6, handoff.State!.Nutrition, 8);
        Assert.Equal(12, Assert.Single(handoff.State!.Materials, r => r.Resource == Meal).Count, 8);
        Assert.Equal(7, Assert.Single(handoff.State!.Medicine).Count);
        Assert.Equal(7, Assert.Single(handoff.State!.Materials, r => r.Resource == Medicine).Count);
        Assert.Equal(3, handoff.State!.ResearchPoints);
        var continued = ColonyModel.Run(request.Coupled.ModelBasis!.Input, handoff.State, [], request.Coupled.ModelBasis.Policy);
        Assert.Equal(3, continued.Events[^1].Levels[(int)ModelStock.ResearchPoints]);
    }

    [Fact]
    public void LivePendingStockRowsMatchActualEligibleResourceCensusWithoutModelBasis()
    {
        var request = Request(Stock("initial", 10));
        request = request with { Coupled = request.Coupled! with { ModelBasis = null } };
        var outcome = Evaluate(request);
        Assert.Null(ForecastHandoffPolicy.Project(outcome));
        var vector = ForecastCoupledSelection.CoupledConsequences(outcome, []);
        var selection = new ForecastCoupledSelection(new([], outcome.Plan, [], 0, false), outcome, outcome, vector, vector, [], 60000, false);
        var predictions = new List<ForecastPrediction>(); var observations = new List<ForecastObservation>();
        var pending = new ForecastEvidencePending(predictions.Add, observations.Add);
        var scope = new GenerationSnapshot(new("colony"), new(1), new("load"), new("root"), new(1), new(1));
        pending.Review(scope, 0, null, request.Coupled, request.Facts.Food);
        pending.Capture(scope, 0, 1, selection, request.Coupled, ForecastAdmission.Continued, [], "");
        var actual = request.Facts.Food with { Reserved = new HashSet<string> { "barred" }, Stocks = [Stock("actual", 7), Stock("barred", 100)] };
        pending.Review(scope, 60000, null, null, actual);
        var predicted = Assert.Single(predictions, p => p.Key.Metric == ForecastMetric.FoodStock);
        var observed = Assert.Single(observations, p => p.Key.Metric == ForecastMetric.FoodStock);
        Assert.Equal(new EvidenceValue.Valued(8), predicted.Value);
        Assert.Equal(new EvidenceValue.Valued(7), observed.Value);
        Assert.Equal(new ResidualError.Scalar(-1), ForecastResiduals.Match(predicted, observed).Error);
        Assert.Equal(0, pending.Count);
    }

    [Fact]
    public void ADifferentCensusScopeRemainsExplicit()
    {
        var supply = Request(Stock("food", 5)).Facts.Food;
        var key = new ForecastEvidenceKey("r", "l", "m", "review", "sample", ForecastMetric.FoodStock, Meal.Value, "total", "items", "", "", 0);
        var prediction = new ForecastPrediction(key, 0, 1, new EvidenceValue.Valued(5d), ForecastAdmission.Continued);
        Assert.Equal("food_stock_scope_mismatch", ForecastLiveEvidence.Observe(prediction, 1, null, null, ForecastExecution.Unconfirmed, supply).Value is EvidenceValue.Unavailable(var reason) ? reason : "");
    }

    [Fact]
    public void UnreachableMedicineDoesNotBecomeUsableOrDebitReachableStockWhenItRots()
    {
        var request = Request(Stock("food", 10));
        request = request with { Facts = request.Facts with { Medicine = request.Facts.Medicine with
        { Items = EquatableArray.From(request.Facts.Medicine.Items.Append(new RimGovernor.Brain.GameFacts.Observations.UpkeepItem
        { Id = "unreachable", Definition = Medicine.Value, Count = 100, Forbidden = false, Rot = new RimGovernor.Brain.GameFacts.Observations.ItemRot.Rotting(30000) })) } } };
        var handoff = ForecastHandoffPolicy.Project(Evaluate(request))!;
        Assert.Empty(handoff.Gaps);
        Assert.Equal(5, Assert.Single(handoff.State!.Medicine).Count);
        Assert.Equal(5, Assert.Single(handoff.State!.Materials, r => r.Resource == Medicine).Count);
    }

    [Fact]
    public void AggregateScheduledNutritionFeedsConsumersOnlyAfterArrivalAndIsNotRepaidAtHandoff()
    {
        var request = Request();
        request = request with { Coupled = request.Coupled! with { ModelBasis = request.Coupled.ModelBasis! with { Policy = new()
        { MedicineTendsPerDay = 0, Scheduled = [new() { Day = .5, Kind = ModelEventKind.Harvest, Id = "arrival", Stock = ModelStock.Nutrition, Amount = 2 }] } } } };
        var outcome = Evaluate(request);
        var handoff = ForecastHandoffPolicy.Project(outcome)!;
        Assert.Empty(handoff.Gaps);
        Assert.Equal(1.5, handoff.State!.Nutrition);
        Assert.DoesNotContain(handoff.State!.Materials, r => string.IsNullOrEmpty(r.Resource.Value));
        var basis = request.Coupled.ModelBasis!;
        var continued = ColonyModel.Run(basis.Input, handoff.State with { EndDay = 2 }, [], basis.Policy);
        Assert.Equal(.5, continued.Events[^1].Levels[(int)ModelStock.Nutrition], 8);
    }
}
