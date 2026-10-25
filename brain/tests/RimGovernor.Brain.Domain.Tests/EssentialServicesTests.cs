using O = RimGovernor.Contracts.Wire.Observations;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;
using SleepingTemperature = RimGovernor.Contracts.Wire.Observations.SleepingTemperature;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class EssentialServicesTests
{
    private static RoundsFacts Facts() => new() { Research = ResearchTestFacts.Read(), Gear = ColonyModelFixtures.NoGear(), CurrentConstruction = CurrentConstruction.Empty, MedicalReserve = new() { Catalog = ItemFactsFixtureRules.CoreItemFacts() }, Upkeep = new() { Rooms = RoomTestFacts.NoRooms, Routes = new() { PawnIds = [], Facilities = [], Traffic = [], TrafficSamples = 0 } }, Tick = new(100), Census = ColonyFactsCoreTests.Snapshot() with { ColonistCount = 2 }, Fuel = [] };

    /// <summary>Sleeping rooms inside the comfort band: the shelter/temperature Need is met (#3257).</summary>
    private static RoundsFacts Warm() => Facts() with { Census = (Facts()).Census with { SleepingTemperature = new SleepingTemperature.Indoor(20.0, 22.0) } };
    private static EssentialServiceReview Review(RoundsFacts facts, int budget = 128) =>
        EssentialServicesRules.ReviewEssentialServices(facts, RoundsRules.DefaultRoundsPolicy(), new() { Work = new() { Tick = facts.Tick },
            MaterialAvailability = new MaterialAvailability.Assessed(new Dictionary<Resource, MaterialUnits> { [new("grain")] = new MaterialUnits.Exact(20.0) }) }, budget);

    [Fact]
    public void NormalInspectionExposesEveryServiceWithoutTreatingUnknownStressAsBaselineFailure()
    {
        var census = ColonyFactsCoreTests.Snapshot();
        var normal = RoundsRules.InspectRounds(new() { Census = census, Gear = ColonyModelFixtures.NoGear(), CurrentConstruction = CurrentConstruction.Empty, MedicalReserve = new() { Census = census, Catalog = ItemFactsFixtureRules.CoreItemFacts() }, Upkeep = new() { Rooms = RoomTestFacts.NoRooms, Routes = new() { PawnIds = [], Facilities = [], Traffic = [], TrafficSamples = 0 } } }, new(), RoundsRules.DefaultRoundsPolicy());
        Assert.NotNull(normal.EssentialServices);
        Assert.Equal(Enum.GetValues<EssentialService>(), normal.EssentialServices.Coverage.Select(c => c.Service).Distinct().Order().ToArray());
        var known = Review(Warm());
        Assert.Contains(known.Coverage, c => c.Target.Owner == ShelterFamily.Temperature.FilingKey && c.Coverage == ServiceCoverage.Adequate);
        Assert.Contains(Review(Facts()).Coverage, c => c.Target.Owner == ShelterFamily.Temperature.FilingKey && c.Coverage == ServiceCoverage.Shortfall);
        Assert.Contains(known.Stress, c => c.Target.Owner == ShelterFamily.Temperature.FilingKey && c.Coverage == ServiceCoverage.EvidenceGap);
        Assert.DoesNotContain(known.Proposals, p => p.Target.Owner == ShelterFamily.Temperature.FilingKey);
    }

    [Fact]
    public void HeatedShelterUsesExistingOwnerPredicatesAndStableIdentityUntilObservedRecovery()
    {
        var cold = Review(Warm() with { Census = (Warm()).Census with { SleepingTemperature = new SleepingTemperature.Indoor(-5.0, 22.0) } });
        var later = Review(Warm() with { Tick = new(200), Census = (Warm()).Census with { SleepingTemperature = new SleepingTemperature.Indoor(-20.0, 22.0) } });
        var proposal = Assert.Single(cold.Proposals, p => p.Target.Owner == ShelterFamily.Temperature.FilingKey);
        Assert.Equal(proposal.Id, Assert.Single(later.Proposals, p => p.Target.Owner == ShelterFamily.Temperature.FilingKey).Id);
        Assert.DoesNotContain(Review(Warm()).Proposals, p => p.Id == proposal.Id);
        Assert.Equal(DevelopmentReviewState.Satisfied, EssentialServicesRules.ReassessDevelopment(proposal, Review(Warm())));
        Assert.Equal("need_satisfied", proposal.Target.Predicate);
        Assert.Equal(ShelterFamily.Temperature, proposal.Target.OwnerNeed);
    }

    [Fact]
    public void InactiveColonyNeedsNoFictionalBackupAndBudgetIsVisibleDeterministic()
    {
        var inactive = Review(Facts() with { Census = (Facts()).Census with { ColonistCount = 0 } });
        Assert.Empty(inactive.Proposals);
        Assert.Empty(inactive.Stress);
        var limited = Review(Facts(), 1);
        Assert.True(limited.Truncated);
        Assert.Single(limited.Stress);
        Assert.Equal(limited, Review(Facts(), 1));
        Assert.Throws<DomainException>(() => EssentialServicesRules.ReviewEssentialServices(Facts(), RoundsRules.DefaultRoundsPolicy(), new(), 1));
    }

    private static CarePawn Patient(params (string Id, double Chance)[] doctors) => CareFixtures.Patient("patient", operations: [
        CareFixtures.Operation(SurgeryKind.Restore, "restore-leg", 2, "Leg", stocked: true, doctors: doctors.Length,
            chance: doctors.Length == 0
                ? new SurgeryChance.NoEligibleDoctor()
                : new SurgeryChance.Rated(doctors.Max(d => d.Chance), doctors.Max(d => d.Chance), doctors.ToDictionary(d => d.Id, d => d.Chance)))]);

    [Fact]
    public void MedicalResilienceUsesProcedureChanceNotGenericDoctorCount()
    {
        var facts = Facts() with { MedicalPawns = [Patient(("expert", .98), ("novice", .50))] };
        var review = Review(facts);
        var baseline = Assert.Single(review.Coverage, c => c.Target.Predicate == "served_procedure_feasible");
        Assert.Equal(ServiceCoverage.Adequate, baseline.Coverage);
        Assert.Contains(review.Stress, s => s.Unavailable == "pawn/expert" && s.Coverage == ServiceCoverage.Shortfall);
        var proposal = Assert.Single(review.Proposals, p => p.Target.Subject == "patient/2");
        Assert.Single(proposal.StressEvidence);
        Assert.Equal(DevelopmentReviewState.RetirementUnproven, EssentialServicesRules.ReassessDevelopment(proposal, Review(facts, 0)));
        Assert.Contains("stress_shortfall:served_procedure_feasible", proposal.Evidence);
        var recovered = Review(facts with { MedicalPawns = [Patient(("expert", .98), ("novice", .90))] });
        Assert.DoesNotContain(recovered.Proposals, p => p.Id == proposal.Id);
    }

    [Fact]
    public void QuestProtectedPatientsDoNotGenerateSurgeryPracticeGoals()
    {
        var review = Review(Facts() with { MedicalPawns = [Patient(("doctor", .9)) with { QuestProtected = true }] });
        Assert.DoesNotContain(review.Coverage, c => c.Target.Predicate == "served_procedure_feasible");
    }

    private static RoundsFacts Kitchen(bool secondChain = false) => Facts() with
    {
        FoodPlan = new FoodPlan { Portfolio = [new() { Channel = CandidateRules.FoodCandidate(CandidateKind.Cook, "cooking-capacity", new YieldSupply.Steady(0), CandidateState.Closed, new CandidateLead.Days(0), new CandidateWork.PerDay(0)), Decision = FoodPlanDecision.Open }] },
        ForecastObserved = new() { WorkPawns = new WorkPawnsRead([
            Cook("expert", 8), Cook("learner", 1)]) },
        WorkClaims = new([], [], [], []),
        WorkAdmission = new() { Recipes = [
            new("advanced-meal", [[new(new("grain"), 5)]], false) { Products = [], WorkAmount = 0 },
            new("basic-meal", [[new(new("grain"), 5)]], false) { Products = [], WorkAmount = 0 }] },
        EssentialInputs = new() { Benches = EquatableArray.From<ProductionBench>([
            ProductionBenchFixtures.Cooking("stove", "ElectricStove", "stove-token", true) with { Recipes = secondChain ? [Recipe("advanced-meal", 6), Recipe("basic-meal", 0)] : [Recipe("advanced-meal", 6)] }]) },
    };
    private static WorkPawn Cook(string id, int level) => PawnFixtures.Worker(id) with { Work = PawnFixtures.WorkTable(true, [new() { Observed = new O.WorkSetting { DefName = "Cooking", Priority = 1, Disabled = false }, Skill = "Cooking" }]), Observed = PawnFixtures.Row(id) with { Biography = PawnFixtures.Row(id).Biography with { Skills = [new() { Passion = O.Passion.None, DefName = "Cooking", Level = level }] } } };
    private static ProductionRecipe Recipe(string id, int floor) => new() { Access = ProductionBenchFixtures.Recipe(id), Role = RecipeRole.OrdinaryMeal,
        Food = new RecipeFood.Food(0, 0, new RecipeCooking.FromRaw(1, [], floor)) };

    [Fact]
    public void FoodAlternateRecipeResolvesEligibilityButDoesNotCertifyReplacementThroughput()
    {
        var fragile = Review(Kitchen());
        Assert.Contains(fragile.Stress, s => s.Unavailable == "pawn/expert" && s.Coverage == ServiceCoverage.Shortfall);
        var alternate = Review(Kitchen(true));
        Assert.Contains(alternate.Stress, s => s.Unavailable == "pawn/expert" && s.Coverage == ServiceCoverage.EvidenceGap
            && s.MissingEvidence.Contains("replacement_throughput_not_established_by_recipe_eligibility"));
        Assert.DoesNotContain(alternate.Proposals.SelectMany(p => p.StressEvidence), s => s.Unavailable == "pawn/expert");
        Assert.Contains(alternate.Stress, s => s.Unavailable == "facility/stove" && s.Coverage == ServiceCoverage.Shortfall);
    }

    [Fact]
    public void ClaimsCannotCertifyAvailableCapability()
    {
        var held = Review(Kitchen() with { WorkClaims = new([], ["expert"], [], []) });
        Assert.Contains(held.Coverage, c => c.Target.Predicate == "recipe_eligibility_and_inputs" && c.Coverage == ServiceCoverage.Shortfall);
    }

    [Fact]
    public void NoRequiredCookingChainDoesNotCreateDevelopmentFromAnIdleKitchen()
    {
        var review = Review(Kitchen() with { FoodPlan = new FoodPlan(), WorkClaims = new([], ["expert"], [], []) });
        Assert.Contains(review.Coverage, c => c.Target.Predicate == "recipe_eligibility_and_inputs" && c.Coverage == ServiceCoverage.NotApplicable);
        Assert.DoesNotContain(review.Proposals, p => p.Target.Subject == "recipe-capability");
    }

    [Fact]
    public void GeneratorRemovalUsesItsNetworkAndDoesNotClaimEssentialConsumerMembership()
    {
        PowerSite Site(string id, string network, double watts) => PowerBuildingFixtures.Trader(id, id, new(1, 1), watts, watts, true, network);
        var facts = Facts() with { PowerRequired = true, EssentialInputs = new() { Power = new PowerTopology {
            Buildings = [Site("generator", "home", 100), Site("heater", "home", -80), Site("remote", "remote", 100)] } } };
        var review = Review(facts);
        Assert.Contains(review.Stress, s => s.Unavailable == "facility/generator" && s.Coverage == ServiceCoverage.Shortfall);
        Assert.Contains(review.Stress, s => s.Unavailable == "facility/remote" && s.Coverage == ServiceCoverage.Adequate);
        Assert.Contains(review.Stress, s => s.MissingEvidence.Contains("power_consumer_service_membership"));
    }

    [Fact]
    public void LogisticsUsesObservedReachabilityAndAlternatePawns()
    {
        var facts = Facts() with { Upkeep = new() { Rooms = RoomTestFacts.NoRooms, Routes = (new RimGovernor.Contracts.Wire.Observations.RoutesFacts { PawnIds = ["a", "b"], Traffic = [], TrafficSamples = 0,
            Facilities = [new() { Id = "medicine-store", Kind = RouteFacilityKind.Storage, Cell = new(4, 4), Travel = [new RouteTravel.Measured { Pawn = "a", Cost = 30, Cells = 12 }, new RouteTravel.Unreachable { Pawn = "b" }], Breaches = [] }] }) } };
        var review = Review(facts);
        Assert.Contains(review.Stress, s => s.Unavailable == "pawn/a" && s.Coverage == ServiceCoverage.Shortfall);
        Assert.Contains(review.Proposals, p => p.Target.Subject == "medicine-store");
        // The logistics owner row is the layout/routes Need (#3265): reachable by some pawn, so met.
        Assert.Contains(review.Coverage, c => c.Service == EssentialService.Logistics && c.Target == ServiceTarget.ForNeed(LayoutFamily.Routes)
            && c.Coverage == ServiceCoverage.Adequate);
    }

    [Fact]
    public void SharedFuelAndMaterialShortfallProducesOneOwnedProposal()
    {
        var facts = Facts() with { Census = (Facts()).Census with { Resources = [] }, Fuel = [
            PowerBuildingFixtures.Trader("heater", "Heater", new(1, 1), 0, 0, false) with
            {
                FuelDefinitions = ["fuel"],
                Service = BuildingServiceWire.NoService with { Fuel = new RimGovernor.Contracts.Wire.Observations.BuildingFuel.Refuelable
                {
                    Fuel = 0, TargetFuel = 10, OutOfFuel = true,
                    Burn = new() { PerDay = 1, UnitsPerItem = 1, UseDriven = false, InRain = false, WhenPowered = false, Flickable = false },
                } },
            }] };
        // The materials row is the economy/resource Need's, unmet while a derived target is short of stock (#3303).
        var review = EssentialServicesRules.ReviewEssentialServices(facts, RoundsRules.DefaultRoundsPolicy(), new()
        {
            Work = new() { Tick = facts.Tick },
            MaterialAvailability = new MaterialAvailability.Assessed(new Dictionary<Resource, MaterialUnits> { [new("grain")] = new MaterialUnits.Exact(20.0) }),
            Needs = new Dictionary<Resource, long> { [new("fuel")] = 10 },
        });
        var proposal = Assert.Single(review.Proposals, p => p.Target.Owner == EconomyFamily.Resource.FilingKey);
        Assert.Contains(EssentialService.Fuel, proposal.Services);
        Assert.Contains(EssentialService.Materials, proposal.Services);
        Assert.Equal(2, proposal.Requirements.Count);
    }
}
