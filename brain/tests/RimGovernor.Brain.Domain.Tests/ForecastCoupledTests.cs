using System.Globalization;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using RimGovernor.Contracts.Wire.Common;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ForecastCoupledTests
{
    private static bool Original(ForecastCommitment commitment, string id) => commitment.Id.StartsWith(id + "/composition/", StringComparison.Ordinal)
        && !commitment.Id.Contains("/after/", StringComparison.Ordinal);
    private static readonly ObservationContext Context = new() { Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 100 };
    private static RoundsFacts Review(double harvest = 25, double build = 250, double foodLevel = .2, double expiry = 1000)
    {
        var facts = WorkResourceTestFacts.Create("pawn");
        facts = facts with
        {
            Resources = Known<EquatableArray<RimGovernor.Brain.Policy.Amount>>([]),
            ForecastObserved = facts.ForecastObserved with
            {
                Needs = new()
                {
                    Tick = 100,
                    Pawns = [new() { Pawn = "pawn", State = "eligible", Mood = Known(.5), MoodTarget = Known(.5),
                Memories = Known<EquatableArray<NeedMemory>>([]),
                Needs = [new("food", Known(foodLevel), Known(.001), Known(.3), Known(1.0)),
                    new("rest", Known(1.0), Known(0.0), Known(.3), Known(1.0))] }]
                },
                Coupled = new()
                {
                    Tick = 100,
                    Food = new()
                    {
                        Context = Context,
                        Known = true,
                        Options = [new("pawn", "plant", "Rice", new(1,1), new(1,1), "Harvest", 8, harvest)
                    { ApproachTicks = Known(0.0), DeliveryTicks = Known(10.0), HarvestAllowed = Known(true), SourceCount = Known(8.0) }]
                    },
                    FoodUses = [new("pawn", "Rice", new(1, 1), Known(true), Known(.1), Known(10.0), 4, Known(expiry))],
                    Construction = new()
                    {
                        Context = Context,
                        Known = true,
                        Options = [new("frame", "TestWall", new(1,1), "pawn", "", "", new(1,1), 0, 0,
                    "Finish", default, default, Known(build)) { SiteApproachTicks = Known(0.0) }]
                    },
                },
            }
        };
        return ReviewCoupledActivities(ReviewWorkResources(facts, Known<EquatableArray<ProductionBatch>>([]), new([])));
    }
    private static ColonyForecast Evaluate(RoundsFacts facts, params ForecastCommitment[] selected)
    {
        var demand = new DerivedDemand { Work = ForecastWorkInputsOf(facts) };
        return ForecastColony(CoupledForecastRequestOf(new("world/map/load", 100), 300.0 / GameTime.TicksPerDay,
            facts, new(), demand, [], new() { Add = EquatableArray.From(selected) }));
    }

    [Fact]
    public void OrdinaryReviewGeneratesRecoveryBeforeProductionAndValuesActualCompletedWork()
    {
        var facts = Review(); var generated = Assert.IsType<ForecastCoupledCandidates>(facts.CoupledAlternatives);
        var build = generated.Alternatives.Single(c => Original(c, "site-0-finish"));
        Assert.DoesNotContain(Evaluate(facts, build).Proposed.Coupled!.Alternatives, p => p.Feasible == Known(true));
        var combinations = generated.Alternatives.Where(c => c.Id.StartsWith("site-0-finish/composition/", StringComparison.Ordinal) && c.Id.Contains("/after/raw/", StringComparison.Ordinal)
            && c.Id.Contains("/eat/pawn/0/composition/", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(combinations);
        var compared = combinations.Select(c => Evaluate(facts, c)).First(f => f.Proposed.Coupled!.Alternatives.Any(p => p.Feasible == Known(true)));
        var useful = compared.Proposed.Coupled!.Alternatives.First(p => p.Feasible == Known(true));
        Assert.True(useful.Plan.CompletedAt <= 300);
        Assert.True(useful.Needs["pawn"].Needs.Single(n => n.Kind == "food").FinalLevel.Value > 0);
        Assert.Empty(compared.Baseline.Coupled!.Alternatives.Single().Plan.Allocations);
        Assert.DoesNotContain(compared.Baseline.Coupled.Alternatives.Single().Stocks, s => s.Resource.Value == "TestWall");
    }

    [Theory]
    [InlineData(300d, false)]
    [InlineData(690d, false)]
    [InlineData(700d, true)]
    public void ShortHorizonDoesNotCreateReversedMealUseWindows(double horizon, bool firstMealInWindow)
    {
        // Full food reaches the .3 threshold after 700 ticks, with 10 ticks of chewing.
        // A meal starting at 690 is usable only when its completion at 700 fits.
        // Recurring meals occur later still; they must not poison an otherwise valid request.
        var generated = Assert.IsType<ForecastCoupledCandidates>(GenerateCoupledActivities(Review(foodLevel: 1), horizon));
        Assert.All(generated.Context.Activities.Activities, task => Assert.True(task.NotBefore <= horizon));
        Assert.Equal(firstMealInWindow, generated.Context.NeedTasks.Any(n => n.Kind == "food"));
        Assert.All(generated.Context.NeedTasks.Where(need => need.Kind == "food"), need =>
        {
            var task = Assert.Single(generated.Context.Activities.Activities, task => task.Id == need.Task);
            var option = Assert.Single(generated.Context.Activities.Options,
                option => option.Activity == need.Task && option.Pawn == need.Pawn);
            Assert.True(option.WorkTicks.IsKnown);
            Assert.True(task.NotBefore + option.WorkTicks.Value <= horizon);
        });
        Assert.Contains(generated.Context.Activities.Activities, task => task.Id.StartsWith("harvest/", StringComparison.Ordinal));
    }
    [Fact]
    public void LateOrSpoiledFoodCannotFinanceARecoveryChain()
    {
        foreach (var facts in new[] { Review(harvest: 290), Review(expiry: 20) })
        {
            var candidates = facts.CoupledAlternatives!.Alternatives.Where(c => c.Id.StartsWith("site-0-finish/composition/", StringComparison.Ordinal) && c.Id.Contains("/after/raw/", StringComparison.Ordinal)
                && c.Id.Contains("/eat/pawn/0/composition/", StringComparison.Ordinal)).ToArray();
            Assert.All(candidates, candidate => Assert.DoesNotContain(Evaluate(facts, candidate).Proposed.Coupled!.Alternatives, p => p.Feasible == Known(true)));
        }
    }

    [Fact]
    public void MixedObservedTicksCannotProduceAFalseCoherentFrame()
    {
        var facts = Review();
        Assert.Null(GenerateCoupledActivities(facts with
        {
            ForecastObserved = facts.ForecastObserved with
            { Coupled = facts.ForecastObserved.Coupled! with { Tick = 101 } }
        }));
    }
    [Fact]
    public void ConstructionSiteWithUnknownFinishTimeIsNotAnAlternativeAndDoesNotThrow()
    {
        var facts = Review();
        var coupled = facts.ForecastObserved.Coupled!;
        var generated = GenerateCoupledActivities(facts with
        {
            ForecastObserved = facts.ForecastObserved with
            {
                Coupled = coupled with
                {
                    Construction = coupled.Construction with { Options = EquatableArray.From(coupled.Construction.Options.Select(o => o with { FinishTicks = default })) }
                }
            }
        });
        Assert.NotNull(generated);
        Assert.DoesNotContain(generated.Alternatives, c => c.ExclusiveOutcome.StartsWith("construction/", StringComparison.Ordinal));
        Assert.Contains(generated.Findings, f => f.StartsWith("construction_outcome_unbound:", StringComparison.Ordinal));
    }

    internal static RoundsFacts ConstructionReview(int sites = 1, int stock = 100, int reserved = 0)
    {
        var facts = Review(foodLevel: 1);
        facts = facts with
        {
            Resources = Known<EquatableArray<RimGovernor.Brain.Policy.Amount>>([new(new("Rice"), stock)]),
            ConstructionDeficit = Known<IReadOnlyDictionary<Resource, long>>(new Dictionary<Resource, long> { [new("Rice")] = sites * 100 }),
            BillReservations = Known<EquatableArray<RimGovernor.Brain.Policy.IngredientReservation>>(reserved == 0 ? []
                : [new() { Pawn = new("other-cook"), Bill = Known("other-bill"), Items = [new(new("Rice"), reserved)] }]),
            ForecastObserved = facts.ForecastObserved with
            {
                Coupled = facts.ForecastObserved.Coupled! with
                {
                    Construction = new()
                    {
                        Context = Context,
                        Known = true,
                        Options = EquatableArray.From(Enumerable.Range(0, sites).Select(i =>
                    new ConstructionActivityOption("frame" + i, "TestWall", new(1, 1), "pawn", "Rice", "stack", new(1, 1), 100, 100, "Finish",
                        Known(0.0), Known(20.0), Known(30.0))
                    { AvailableInput = Known((double)stock), SiteApproachTicks = Known(0.0) }))
                    }
                }
            }
        };
        return ReviewCoupledActivities(ReviewWorkResources(facts, Known<EquatableArray<ProductionBatch>>([]), new([])));
    }

    [Fact]
    public void ExactSelectedConstructionOwnsItsReservedMaterialOnce()
    {
        var facts = ConstructionReview();
        Assert.Equal(100, facts.WorkView!.Materials.Value[new("Rice")].Value); // Unselected sites do not reserve stock.
        var selected = facts.CoupledAlternatives!.Alternatives.Single(c => Original(c, "site-0-finish"));
        Assert.Contains(Evaluate(facts, selected).Proposed.Coupled!.Alternatives, p => p.Feasible == Known(true));
    }

    [Fact]
    public void DistinctSitesCanCombineButCannotSpendTheSamePhysicalStackTwice()
    {
        foreach (var stock in new[] { 100, 200 })
        {
            var facts = ConstructionReview(2, stock);
            var selected = facts.CoupledAlternatives!.Alternatives.Where(c => Original(c, "site-0-finish") || Original(c, "site-1-finish")).ToArray();
            var result = Evaluate(facts, selected).Proposed.Coupled!;
            Assert.Equal(stock == 200, result.Alternatives.Any(p => p.Feasible == Known(true)));
        }
    }

    [Fact]
    public void NativeHoldForAnotherBillCannotBeReleasedBySelectingConstruction()
    {
        var facts = ConstructionReview(reserved: 50);
        var selected = facts.CoupledAlternatives!.Alternatives.Single(c => Original(c, "site-0-finish"));
        Assert.DoesNotContain(Evaluate(facts, selected).Proposed.Coupled!.Alternatives, p => p.Feasible == Known(true));
    }
    [Theory]
    [InlineData(18, 17)]
    [InlineData(15, 18)]
    public void HeaterTargetNeverProvidesArtificialCooling(double initial, double outdoors)
    {
        var room = new ForecastShelterPlan(new(ForecastShelterSource.Planned, "1"), [new(1, 1)], [], Known(initial), Known(outdoors), Known(.001), Known(.35), Known(15.0), Known(1000.0), []);
        var result = ForecastShelter(room, new([], 0, 0), 300);
        Assert.True(result.TemperatureAtHorizon.Value >= Math.Min(initial, outdoors));
        Assert.True(result.TemperatureAtHorizon.Value > 15);
    }

    [Fact]
    public void TwoHeatersCannotIndependentlySpendOneObservedNetworkSurplus()
    {
        var first = new ForecastShelterPlan(new(ForecastShelterSource.Planned, "1"), [new(1, 1)], [], Known(-10.0), Known(-10.0), Known(.0001), Known(.35), Known(20.0), Known(1000.0), [])
        { PowerNetwork = "net", Source = "heater1", AdditionalWatts = Known(100.0), SurplusWatts = Known(150.0) };
        Assert.True(ForecastShelters([first], new([], 0, 0), 300).Single().TemperatureAtHorizon.IsKnown);
        var result = ForecastShelters([first, first with { Room = new(ForecastShelterSource.Planned, "2"), Source = "heater2" }], new([], 0, 0), 300);
        Assert.All(result, r => Assert.False(r.TemperatureAtHorizon.IsKnown));
        Assert.All(result, r => Assert.Contains("shared_power_capacity_shortfall", r.Findings));
    }

    [Fact]
    public void NormalReviewRequiresRoofLaborAndWarmupAfterObservedShellCompletion()
    {
        var facts = ConstructionReview(stock: 100);
        var cells = Enumerable.Range(0, 5).SelectMany(x => Enumerable.Range(0, 5).Select(z => new Cell(x, z))).ToArray();
        var inside = cells.Where(c => c.X is > 0 and < 4 && c.Z is > 0 and < 4).ToArray();
        var wall = facts.ForecastObserved.Coupled!.Construction.Options.Single() with { Location = new(0, 1) };
        var thermal = new CoupledThermalRead
        {
            Outdoors = Known(-10.0),
            ThinRoofExchangePerTick = Known(.00005),
            WallExchangePerTick = Known(.000017),
            Cells = EquatableArray.From(cells.Select(c => new ThermalCell(c, Known(-10.0), "none", !inside.Contains(c) && c != wall.Location, false, false,
                c == wall.Location ? wall.Site : "", c == wall.Location ? "TestWall" : "", c == wall.Location, false, 0, inside.Contains(c)))),
            Workers = [new("pawn", Known(5.0))],
            RoofRoutes = EquatableArray.From(inside.Select(c => new ThermalRoofRoute("pawn", c, Known(1.0)))),
            Sources = [new("heater", new(2, 2), true, Known(.35), Known(20.0), Known(60000.0))]
        };
        facts = facts with
        {
            PlannedShelters = [new() { Id = 1, Region = RectRegion(new(1, 1, 3, 3)) }],
            ForecastObserved = facts.ForecastObserved with
            {
                Needs = facts.ForecastObserved.Needs! with
                {
                    Pawns = [facts.ForecastObserved.Needs!.Pawns[0] with
                { Needs = [new("food",Known(1.0),Known(0.0),Known(.3),Known(1.0)),new("rest",Known(1.0),Known(0.0),Known(.3),Known(1.0))] }]
                },
                Coupled = facts.ForecastObserved.Coupled with
                {
                    Thermal = thermal,
                    Construction = facts.ForecastObserved.Coupled.Construction with { Options = [wall] },
                    Routes = EquatableArray.From(cells.SelectMany(a => cells.Where(b => a != b).Select(b => new CoupledRoute("pawn", a, b, Known(1.0)))))
                }
            }
        };
        facts = ReviewCoupledActivities(ReviewWorkResources(facts, Known<EquatableArray<ProductionBatch>>([]), new([])));
        var shelter = facts.CoupledAlternatives!.Alternatives.Single(c => c.ExclusiveOutcome == "shelter/planned/1");
        Assert.Equal(10, shelter.ActivityCompletions.Count);
        var request = CoupledForecastRequestOf(new("world", 100), 1000.0 / GameTime.TicksPerDay, facts, new(), new() { Work = ForecastWorkInputsOf(facts) }, [], new() { Add = [shelter] });
        var forecast = ForecastColony(request);
        Assert.False(forecast.Baseline.Coupled!.Alternatives.Single().Shelters.Single().TemperatureAtHorizon.IsKnown);
        var projected = forecast.Proposed.Coupled!.Alternatives.First(p => p.Feasible == Known(true)).Shelters.Single();
        Assert.True(projected.CompleteAt.Value >= 95);
        Assert.Equal(-10, projected.Trajectory[0].Temperature);
        Assert.True(projected.TemperatureAtHorizon.Value > 16);
    }
    [Fact]
    public void ExistingFoodBatchesPreserveEarlyMealSchedulesAndExactIterationOutputs()
    {
        var facts = Review();
        facts = facts with
        {
            Resources = Known<EquatableArray<RimGovernor.Brain.Policy.Amount>>([new(new("Rice"), 8)]),
            ForecastObserved = facts.ForecastObserved with
            {
                Coupled = facts.ForecastObserved.Coupled! with
                {
                    Food = new() { Context = Context, Known = true },
                    FoodSources = [new("rice-stack", "Rice", new(1, 1), 8, Known(1000.0))],
                    FoodConversions = [new("rice-stack","pawn","bench","CookMeal","Rice",4,"Meal",1,new(1,1),new(1,1),new(1,1),
                    Known(0.0),Known(10.0),Known(20.0),Known(10.0),8,8) { RequiresHarvest = false, Bill = "bill", WorkGiver = "Cook" }],
                    FoodUses = [new("pawn", "Meal", new(1, 1), Known(true), Known(.8), Known(10.0), 1, Known(1000.0))]
                }
            }
        };
        facts = ReviewCoupledActivities(ReviewWorkResources(facts, Known<EquatableArray<ProductionBatch>>([]), new([])));
        var generated = facts.CoupledAlternatives!;
        Assert.DoesNotContain(generated.Context.Activities.Activities, t => t.Id.StartsWith("harvest/", StringComparison.Ordinal));
        var batch = generated.Alternatives.Single(c => c.Id.StartsWith("food/", StringComparison.Ordinal) && c.ActivityCompletions.Count == 2);
        var firstMeal = generated.Context.NeedTasks.First(n => n.FoodReady == batch.ActivityCompletions[0]);
        var meal = generated.Alternatives.Single(c => c.ActivityCompletions.Contains(firstMeal.Task));
        var result = Evaluate(facts, batch, meal).Proposed.Coupled!.Alternatives.Where(p => p.Feasible == Known(true)).ToArray();
        Assert.NotEmpty(result);
        Assert.All(result, p => Assert.Equal(1, p.Stocks.Single(s => s.Resource.Value == "Meal").Points[^1].BalanceUnits.Value));
        var secondRecipe = generated.Context.Activities.Activities.Single(t => t.Id == batch.ActivityCompletions[1]).Prerequisites
            .Single(id => generated.Context.Activities.Activities.Single(t => t.Id == id).Outputs.Any(o => o.Resource.Value == "Meal"));
        Assert.Contains(result, p => p.Plan.Allocations.Single(a => a.Activity == firstMeal.Task).End < p.Plan.Allocations.Single(a => a.Activity == secondRecipe).End);
        Assert.Contains(result, p => p.Plan.Allocations.Single(a => a.Activity == firstMeal.Task).End > p.Plan.Allocations.Single(a => a.Activity == secondRecipe).End);
    }
    [Fact]
    public void WarmRoomCapabilityDoesNotProtectAnOutdoorWorker()
    {
        var facts = Review();
        var context = facts.CoupledAlternatives!.Context;
        var room = new ForecastShelterPlan(new(ForecastShelterSource.Planned, "1"), [new(1, 1)], [], Known(-10.0), Known(-10.0), Known(.0001), Known(.35), Known(20.0), Known(1000.0), []);
        context = context with
        {
            Shelters = [room],
            Thermal = new()
            {
                Cells = [
            new(new(1,1),Known(-10.0),"thin",false,false,false,"","",false,false,0,false),
            new(new(5,5),Known(-10.0),"none",false,false,false,"","",false,false,0,false)]
            },
            Needs = context.Needs with { Pawns = [context.Needs.Pawns[0] with { ComfortableMin = Known(10.0), ComfortableMax = Known(30.0) }] }
        };
        var projection = ForecastShelters([room], new([], 0, 0), 300);
        ForecastThermalExposure Exposure(string location, Dictionary<string, List<(double, double, double)>>? comfort = null) => ForecastThermalExposureOf(context,
            new() { Activities = [new("work", location, location, [])] },
            new([new("work", "pawn", 0, 0, 0, 300, 0, 0, 300, 300, 0)], 300, 300), projection, 300, comfort: comfort).Single();
        var outdoors = Exposure("cell/5/5"); var indoors = Exposure("cell/1/1");
        Assert.Equal(6000, outdoors.ColdDegreeTicks.Value);
        // A -20 comfort shift from tick 100 removes the cold for the remaining 200 ticks.
        Assert.Equal(2000, Exposure("cell/5/5", new() { ["pawn"] = [(-20, 0, 100)] }).ColdDegreeTicks.Value);
        Assert.True(indoors.ColdDegreeTicks.Value < outdoors.ColdDegreeTicks.Value);
        Assert.Equal(0, outdoors.UnknownTicks);
    }

    [Fact]
    public void ComfortShiftLowersColdExposureOnlyAtAStationaryOutdoorTask()
    {
        var facts = Review();
        var generated = facts.CoupledAlternatives!;
        var cells = generated.Context.Activities.Activities.SelectMany(t => new[] { t.Location, t.EndLocation })
            .Where(l => l.StartsWith("cell/", StringComparison.Ordinal)).Distinct()
            .Select(l => l.Split('/')).Select(p => new Cell(int.Parse(p[1], CultureInfo.InvariantCulture), int.Parse(p[2], CultureInfo.InvariantCulture)));
        generated = generated with
        {
            Context = generated.Context with
            {
                Shelters = [],
                Thermal = new() { Cells = EquatableArray.From(cells.Select(c => new ThermalCell(c, Known(-10.0), "none", false, false, false, "", "", false, false, 0, false))) },
                Needs = generated.Context.Needs with { Pawns = [generated.Context.Needs.Pawns[0] with { ComfortableMin = Known(10.0), ComfortableMax = Known(30.0) }] }
            }
        };
        facts = facts with { CoupledAlternatives = generated };
        ForecastCoupledOutcome Outcome(params ForecastCommitment[] c) => Evaluate(facts, c).Proposed.Coupled!.Alternatives.First(p => p.Feasible == Known(true));
        var outdoor = generated.Alternatives.Where(c => c.ActivityCompletions.Count > 0).Select(c => (Commitment: c, Outcome: Outcome(c)))
            .First(p => p.Outcome.ThermalExposure.Single().AssessedTicks > 0);
        // A labor-free outfit change applies from the forecast start, so it covers the outdoor work.
        var gear = new ForecastCommitment("gear", []) { ComfortShifts = [new("pawn", -20, 0)] };
        var plain = outdoor.Outcome.ThermalExposure.Single();
        var warm = Outcome(outdoor.Commitment, gear).ThermalExposure.Single();
        Assert.True(plain.AssessedColdDegreeTicks > 0);
        Assert.True(warm.AssessedColdDegreeTicks < plain.AssessedColdDegreeTicks);
        Assert.Equal(plain.AssessedTicks, warm.AssessedTicks);
        // No stationary outdoor task: the shift has no exposure to change.
        var idle = Outcome(gear).ThermalExposure.Single();
        Assert.Equal(Evaluate(facts).Baseline.Coupled!.Alternatives.Single().ThermalExposure.Single(), idle);
    }

    [Fact]
    public void ImpossibleHarvestCannotLeaveSpendableFoodOrRecovery()
    {
        var facts = Review(harvest: 250, foodLevel: .1);
        var meal = facts.CoupledAlternatives!.Alternatives.First(c => c.Id.StartsWith("raw/", StringComparison.Ordinal)
            && c.Id.Contains("/eat/pawn/0/composition/", StringComparison.Ordinal));
        var alternatives = Evaluate(facts, meal).Proposed.Coupled!.Alternatives;
        Assert.NotEmpty(alternatives);
        Assert.All(alternatives, p =>
        {
            Assert.Equal(Known(false), p.Feasible);
            Assert.DoesNotContain(p.Stocks, s => s.Resource.Value == "Rice" && s.Points.Any(t => t.BalanceUnits.IsKnown && t.BalanceUnits.Value > 0));
            Assert.True(p.Needs["pawn"].Needs.Single(n => n.Kind == "food").FinalLevel.Value <= .1);
        });
    }
    [Fact]
    public void RemovingAProjectedCommitmentDoesNotReleaseAnActiveJournalHold()
    {
        var facts = ConstructionReview(stock: 100);
        var held = new ForecastCommitment("active-supply", []) { MaterialClaims = [new(new("Rice"), Known(30.0), Known(""))] };
        var context = facts.CoupledAlternatives!.Context;
        context = context with { Resources = context.Resources with { Claims = context.Resources.Claims with { Materials = [held] } } };
        facts = facts with { CoupledAlternatives = facts.CoupledAlternatives with { Context = context } };
        var wall = facts.CoupledAlternatives.Alternatives.Single(c => Original(c, "site-0-finish"));
        var request = CoupledForecastRequestOf(new("world", 100), 300.0 / GameTime.TicksPerDay, facts, new(), new() { Work = ForecastWorkInputsOf(facts) },
            [held], new() { Remove = [held.Id], Add = [wall] });
        Assert.DoesNotContain(ForecastColony(request).Proposed.Coupled!.Alternatives, p => p.Feasible == Known(true));
    }

    [Theory]
    [InlineData(100, .4)]
    [InlineData(1000, .5)]
    public void OrdinaryReviewPreservesThoughtExpiryInCoupledOutcome(double expiry, double expected)
    {
        var facts = Review();
        facts = facts with
        {
            ForecastObserved = facts.ForecastObserved with
            {
                Needs = facts.ForecastObserved.Needs! with
                { Pawns = [facts.ForecastObserved.Needs!.Pawns[0] with { Memories = Known<EquatableArray<NeedMemory>>([new("happy", 10, Known(expiry), false)]) }] }
            }
        };
        facts = ReviewCoupledActivities(ReviewWorkResources(facts, Known<EquatableArray<ProductionBatch>>([]), new([])));
        Assert.Equal(expected, Evaluate(facts).Proposed.Coupled!.Alternatives.Single().Needs["pawn"].MemoryAdjustedMoodTarget.Value, 8);
    }

    [Fact]
    public void BeyondHorizonInvestmentRemainsUnassessedAndFreeTimeAloneCreatesNoProduction()
    {
        var facts = Review(build: 1000, foodLevel: 1);
        var wall = facts.CoupledAlternatives!.Alternatives.Single(c => Original(c, "site-0-finish"));
        var result = Evaluate(facts, wall);
        Assert.Empty(result.Proposed.Coupled!.Alternatives);
        Assert.Contains("unassessed_no_schedule_within_horizon", result.Proposed.Coupled.Findings);
        var baseline = result.Baseline.Coupled!.Alternatives.Single();
        Assert.True(baseline.Needs["pawn"].Capacity.FreeTicks.Value > 0);
        Assert.Empty(baseline.Plan.Allocations);
        Assert.DoesNotContain(baseline.Stocks, s => s.Resource.Value == "TestWall");
    }
    [Fact]
    public void ClothingAndConstructionKeepMaterialAlternativesUntilSharedEvaluation()
    {
        foreach (var rice in new[] { 100, 110 })
        {
            var facts = ConstructionReview(stock: rice);
            GearOption Garment(string stuff) => new()
            {
                ID = "Shirt" + stuff,
                Definition = new("Shirt"),
                Stuff = new(stuff),
                Slot = GearSlot.SkinTorso,
                Layers = ["OnSkin"],
                Groups = ["Torso"],
                Source = GearSource.Bill,
                Quality = 2,
                Condition = 1,
                Cold = stuff == "Rice" ? 15 : 5,
                Cost = 10,
                Sharp = .2
            };
            RecipeActivityOption Recipe(string stuff) => new("Shirt" + stuff, "pawn", "bench", "Tailor", "MakeShirt", "Shirt", stuff, "", "", new(1, 1),
                [new(stuff == "Rice" ? "stack" : "cloth-stack",stuff,10,new(1,1),stuff == "Rice" ? rice : 10)
                    { Capacity = 100, ApproachTicks = Known(0.0), CarryTicks = Known(0.0), ReturnTicks = Known(0.0) }],
                Known(0.0), Known(30.0), Known(5.0), new(1, 1))
            { RequiredCount = 10 };
            facts = facts with
            {
                Resources = Known<EquatableArray<RimGovernor.Brain.Policy.Amount>>([new(new("Rice"), rice), new(new("Cloth"), 10)]),
                Gear = Known(new GearObservation
                {
                    Stored = Known<EquatableArray<RimGovernor.Brain.Policy.GearStock>>([]),
                    Pawns = [new() { Pawn = new("wearer"), Loadout = "load",
                    Candidates = Known<EquatableArray<RimGovernor.Brain.Policy.GearCandidate>>([]), Apparel = Known<EquatableArray<GearApparel>>([]),
                    LoadoutModel = Known(new GearLoadoutInput { Ambient = 0, ComfortableMin = 10, ComfortableMax = 30, Options = [Garment("Rice"),Garment("Cloth")] }) }]
                }),
                ForecastObserved = facts.ForecastObserved with
                {
                    Coupled = facts.ForecastObserved.Coupled! with
                    { Clothing = new() { Context = Context, Known = true, Options = [Recipe("Rice"), Recipe("Cloth")] } }
                }
            };
            facts = ReviewCoupledActivities(ReviewWorkResources(facts, Known<EquatableArray<ProductionBatch>>([]), new([])));
            var generated = facts.CoupledAlternatives!;
            var wall = generated.Alternatives.Single(c => Original(c, "site-0-finish"));
            foreach (var stuff in new[] { "Rice", "Cloth" })
            {
                var recipe = generated.Context.Activities.Activities.First(t => t.Id.StartsWith("clothing-", StringComparison.Ordinal) && t.Inputs.Any(i => i.Resource.Value == stuff));
                var garment = generated.Alternatives.First(c => c.ActivityCompletions.Count == 1 && generated.Context.Activities.Activities
                    .Single(t => t.Id == c.ActivityCompletions[0]).Prerequisites.Contains(recipe.Id));
                Assert.NotNull(garment.Outcome!.Requirements);
                Assert.Equal(stuff == "Cloth" || rice == 110, Evaluate(facts, wall, garment).Proposed.Coupled!.Alternatives.Any(p => p.Feasible == Known(true)));
            }
        }
    }
    [Theory]
    [InlineData(4, 10, false, true)]
    [InlineData(1, 10, false, false)]
    [InlineData(4, 100, false, false)]
    [InlineData(4, 10, true, false)]
    public void GeneratedHelperSupplyReversesWithCarryingDistanceAndAnExistingObligation(int capacity, double travel, bool obligated, bool helperSupplies)
    {
        var facts = Review(foodLevel: 1); var roster = WorkResourceTestFacts.Create("pawn", "helper");
        var cells = new[] { new Cell(1, 1), new Cell(2, 1), new Cell(3, 1) };
        var cook = new CoupledFoodConversion("rice", "pawn", "bench", "CookMeal", "Rice", 4, "Meal", 1, cells[0], cells[1], cells[2],
            Known(0.0), Known(40.0), Known(20.0), Known(40.0), 4, 4)
        { RequiresHarvest = false, Bill = "bill", WorkGiver = "Cook" };
        facts = facts with
        {
            Resources = Known<EquatableArray<RimGovernor.Brain.Policy.Amount>>([new(new("Rice"), 4)]),
            WorkResources = roster.WorkResources,
            ForecastObserved = facts.ForecastObserved with
            {
                WorkPawns = roster.ForecastObserved.WorkPawns,
                Needs = facts.ForecastObserved.Needs! with { Pawns = [facts.ForecastObserved.Needs!.Pawns[0], facts.ForecastObserved.Needs.Pawns[0] with { Pawn = "helper" }] },
                Coupled = facts.ForecastObserved.Coupled! with
                {
                    Food = new() { Context = Context, Known = true },
                    FoodSources = [new("rice", "Rice", cells[0], 4, Known(1000.0))],
                    FoodConversions = [cook, cook with { Pawn = "helper", CanCook = false, CarryingCapacity = capacity, SupplyTicks = Known(travel), DeliveryTicks = Known(travel) }],
                    FoodUses = [new("pawn", "Meal", cells[2], Known(true), Known(.8), Known(10.0), 1, Known(1000.0))],
                    Routes = EquatableArray.From(roster.ForecastObserved.WorkPawns.Value.Select(p => p.Id.Value).SelectMany(p => cells.SelectMany(a => cells.Where(b => b != a)
                        .Select(b => new CoupledRoute(p, a, b, Known(p == "helper" && a == cells[1] && b == cells[0] ? travel : 1.0))))
                        .Concat(cells.Select(c => new CoupledRoute(p, new(0, 0), c, Known(1.0)) { FromStart = true }))))
                }
            }
        };
        facts = ReviewWorkResources(facts, Known<EquatableArray<ProductionBatch>>([]), new([]));
        if (obligated) facts = facts with { WorkView = facts.WorkView! with { Claims = facts.WorkView!.Claims with { Pawns = ["helper"] } } };
        facts = ReviewCoupledActivities(facts);
        var batch = facts.CoupledAlternatives!.Alternatives.Single(c => c.Id.StartsWith("food/", StringComparison.Ordinal) && !c.Id.Contains("/eat/", StringComparison.Ordinal) && !c.Id.Contains("/after/", StringComparison.Ordinal));
        var feasible = Evaluate(facts, batch).Proposed.Coupled!.Alternatives.Where(p => p.Feasible == Known(true)).ToArray();
        Assert.NotEmpty(feasible);
        var cheapest = feasible.OrderBy(p => p.Plan.LaborCost).First();
        Assert.Equal(helperSupplies, cheapest.Plan.Allocations.Single(a => a.Activity.Contains("/supply/", StringComparison.Ordinal)).Pawn == "helper");
        Assert.Equal("pawn", cheapest.Plan.Allocations.Single(a => a.Activity.Contains("/recipe/", StringComparison.Ordinal)).Pawn);
        if (obligated) Assert.All(feasible, p => Assert.DoesNotContain(p.Plan.Allocations, a => a.Pawn == "helper"));
    }
    [Fact]
    public void RequestCannotRelabelObservedReviewAsALaterSchedulingTick()
    {
        var facts = Review(); var demand = new DerivedDemand { Work = ForecastWorkInputsOf(facts) };
        Assert.Equal(100, facts.Tick.Value);
        Assert.Equal(facts.Tick, facts.WorkView!.Evidence.Tick);
        Assert.Equal(facts.Tick, demand.Work.Tick);
        Assert.Equal(facts.Tick.Value, facts.CoupledAlternatives!.Context.Needs.Tick);
        Assert.Throws<DomainException>(() => CoupledForecastRequestOf(new("world", 101), .01, facts, new(), demand, [], new()));
    }

    [Fact]
    public void UnknownNeedEffectsDoNotEraseKnownIndependentProduction()
    {
        var facts = Review(foodLevel: 1);
        facts = facts with
        {
            ForecastObserved = facts.ForecastObserved with
            {
                Needs = facts.ForecastObserved.Needs! with
                { Pawns = [facts.ForecastObserved.Needs!.Pawns[0] with { Memories = default }] }
            }
        };
        facts = ReviewCoupledActivities(ReviewWorkResources(facts, Known<EquatableArray<ProductionBatch>>([]), new([])));
        var wall = facts.CoupledAlternatives!.Alternatives.Single(c => Original(c, "site-0-finish"));
        var result = Evaluate(facts, wall).Proposed.Coupled!.Alternatives.Single(p => p.Feasible == Known(true));
        Assert.False(result.Needs["pawn"].MemoryAdjustedMoodTarget.IsKnown);
        Assert.Contains(result.Plan.Allocations, a => a.Activity == wall.ActivityCompletions[0]);
    }
    [Theory]
    [InlineData(100, false)]
    [InlineData(120, true)]
    public void ExternalTimedConsumptionSharesTheLedgerAndCreditsOnlyItsOwnHold(int stock, bool fits)
    {
        var facts = ConstructionReview(stock: stock);
        var legacy = new ForecastCommitment("external", [new(new("Rice"), Known(0.0), Known(-20.0))]);
        var held = new ForecastCommitment("external", []) { MaterialClaims = [new(new("Rice"), Known(20.0), Known(""))] };
        var context = facts.CoupledAlternatives!.Context;
        context = context with { Resources = context.Resources with { Evidence = context.Resources.Evidence with { Promises = Known<EquatableArray<ProductionMaterialPromise>>([new(held.Id, [new(new("Rice"), Known(20.0), Known(""))])]) } } };
        facts = facts with { CoupledAlternatives = facts.CoupledAlternatives with { Context = context } };
        var wall = facts.CoupledAlternatives.Alternatives.Single(c => Original(c, "site-0-finish"));
        var request = CoupledForecastRequestOf(new("world", 100), 300.0 / GameTime.TicksPerDay, facts, new(), new() { Work = ForecastWorkInputsOf(facts) }, [legacy], new() { Add = [wall] });
        var result = ForecastColony(request).Proposed.Coupled!;
        Assert.Equal(fits, result.Alternatives.Any(p => p.Feasible == Known(true)));
        if (fits) Assert.All(result.Alternatives, p => Assert.Equal(0, p.Stocks.Single(s => s.Resource.Value == "Rice").Points[^1].BalanceUnits.Value));
    }

    [Fact]
    public void UnboundExternalOutputCannotFinanceKnownPhysicalInputs()
    {
        var facts = ConstructionReview(stock: 0);
        Assert.DoesNotContain(facts.CoupledAlternatives!.Alternatives, c => Original(c, "site-0-finish"));
        var request = CoupledForecastRequestOf(new("world", 100), 300.0 / GameTime.TicksPerDay, facts, new(), new() { Work = ForecastWorkInputsOf(facts) },
            [new("external", [new(new("Rice"), Known(0.0), Known(100.0))])], new());
        var result = ForecastColony(request).Proposed.Coupled!.Alternatives;
        Assert.All(result, p => Assert.Empty(p.Plan.Allocations));
        Assert.All(result, p => Assert.Contains(p.Findings, f => f.Reason == "unbound_external_output"));
    }
    [Fact]
    public void OmittedSheltersRemainVisibleAsGenerationAndScenarioTruncation()
    {
        var facts = Review();
        facts = facts with
        {
            PlannedShelters = EquatableArray.From(Enumerable.Range(1, 5).Select(id =>
            new PlannedRoom { Id = id, Region = RectRegion(new(id * 10, 1, 3, 3)) }))
        };
        facts = ReviewCoupledActivities(facts);
        Assert.True(facts.CoupledAlternatives!.Truncated);
        Assert.Contains("coupled_generation_truncated", facts.CoupledAlternatives.Findings);
        Assert.True(Evaluate(facts).Proposed.Coupled!.Truncated);
    }
    [Fact]
    public void ExternalConsumptionCannotSkipAnEarlierExpiryWhenThereAreNoTaskEvents()
    {
        var facts = Review();
        facts = facts with
        {
            Resources = Known<EquatableArray<RimGovernor.Brain.Policy.Amount>>([new(new("Rice"), 10)]),
            ForecastObserved = facts.ForecastObserved with
            {
                Coupled = facts.ForecastObserved.Coupled! with
                { FoodSources = [new("rice", "Rice", new(1, 1), 10, Known(10.0))] }
            }
        };
        facts = ReviewCoupledActivities(ReviewWorkResources(facts, Known<EquatableArray<ProductionBatch>>([]), new([])));
        var request = CoupledForecastRequestOf(new("world", 100), 300.0 / GameTime.TicksPerDay, facts, new(), new() { Work = ForecastWorkInputsOf(facts) },
            [new("external", [new(new("Rice"), Known(20.0 / GameTime.TicksPerDay), Known(-10.0))])], new());
        var result = ForecastColony(request).Proposed.Coupled!.Alternatives.Single();
        Assert.Equal(Known(false), result.Feasible);
        Assert.Contains(result.Findings, f => f.Reason == "observed_lot_expired");
        Assert.Contains(result.Findings, f => f.Reason == "external_stock_shortfall");
    }
}
