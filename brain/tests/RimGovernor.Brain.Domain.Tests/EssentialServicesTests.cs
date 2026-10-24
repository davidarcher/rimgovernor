using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class EssentialServicesTests
{
    private static RoundsFacts Facts() => new() { Tick = new(100), Colonists = 2L, Fuel = Known<EquatableArray<FuelConsumer>>([]) };

    /// <summary>Sleeping rooms inside the comfort band: the shelter/temperature Need is met (#3257).</summary>
    private static RoundsFacts Warm() => Facts() with { SleepingMin = Known(20.0), SleepingMax = Known(22.0) };
    private static EssentialServiceReview Review(RoundsFacts facts, int budget = 128) =>
        EssentialServicesRules.ReviewEssentialServices(facts, RoundsRules.DefaultRoundsPolicy(), new() { Work = new() { Tick = facts.Tick },
            MaterialAvailability = Known<IReadOnlyDictionary<Resource, Fact<double>>>(new Dictionary<Resource, Fact<double>> { [new("grain")] = Known(20.0) }) }, budget);

    [Fact]
    public void NormalInspectionExposesEveryServiceWithoutTreatingUnknownStressAsBaselineFailure()
    {
        var normal = RoundsRules.InspectRounds(new(), new(), RoundsRules.DefaultRoundsPolicy());
        Assert.NotNull(normal.EssentialServices);
        Assert.Equal(Enum.GetValues<EssentialService>(), normal.EssentialServices.Coverage.Select(c => c.Service).Distinct().Order().ToArray());
        var known = Review(Warm());
        Assert.Contains(known.Coverage, c => c.Target.Owner == ShelterFamily.Temperature.FilingKey && c.Coverage == ServiceCoverage.Adequate);
        Assert.Contains(Review(Facts()).Coverage, c => c.Target.Owner == ShelterFamily.Temperature.FilingKey && c.Coverage == ServiceCoverage.Unknown);
        Assert.Contains(known.Stress, c => c.Target.Owner == ShelterFamily.Temperature.FilingKey && c.Coverage == ServiceCoverage.Unknown);
        Assert.DoesNotContain(known.Proposals, p => p.Target.Owner == ShelterFamily.Temperature.FilingKey);
    }

    [Fact]
    public void HeatedShelterUsesExistingOwnerPredicatesAndStableIdentityUntilObservedRecovery()
    {
        var cold = Review(Warm() with { SleepingMin = Known(-5.0) });
        var later = Review(Warm() with { Tick = new(200), SleepingMin = Known(-20.0) });
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
        var inactive = Review(Facts() with { Colonists = 0L });
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
        var facts = Facts() with { MedicalPawns = Known<EquatableArray<CarePawn>>([Patient(("expert", .98), ("novice", .50))]) };
        var review = Review(facts);
        var baseline = Assert.Single(review.Coverage, c => c.Target.Predicate == "served_procedure_feasible");
        Assert.Equal(ServiceCoverage.Adequate, baseline.Coverage);
        Assert.Contains(review.Stress, s => s.Unavailable == "pawn/expert" && s.Coverage == ServiceCoverage.Shortfall);
        var proposal = Assert.Single(review.Proposals, p => p.Target.Subject == "patient/2");
        Assert.Single(proposal.StressEvidence);
        Assert.Equal(DevelopmentReviewState.Unknown, EssentialServicesRules.ReassessDevelopment(proposal, Review(facts, 0)));
        Assert.Contains("stress_shortfall:served_procedure_feasible", proposal.Evidence);
        var recovered = Review(facts with { MedicalPawns = Known<EquatableArray<CarePawn>>([Patient(("expert", .98), ("novice", .90))]) });
        Assert.DoesNotContain(recovered.Proposals, p => p.Id == proposal.Id);
    }

    [Fact]
    public void QuestProtectedPatientsDoNotGenerateSurgeryPracticeGoals()
    {
        var review = Review(Facts() with { MedicalPawns = Known<EquatableArray<CarePawn>>([Patient(("doctor", .9)) with { QuestProtected = true }]) });
        Assert.DoesNotContain(review.Coverage, c => c.Target.Predicate == "served_procedure_feasible");
    }

    private static RoundsFacts Kitchen(bool secondChain = false) => Facts() with
    {
        FoodPlan = Known(new FoodPlan { Portfolio = [new() { Channel = new() { Kind = CandidateKind.Cook, Id = "cooking-capacity" }, Decision = FoodPlanDecision.Open }] }),
        ForecastObserved = new() { WorkPawns = Known<EquatableArray<WorkPawn>>([
            Cook("expert", 8), Cook("learner", 1)]) },
        WorkClaims = new([], [], [], []),
        WorkAdmission = new() { Recipes = Known<EquatableArray<ProductionRecipeInput>>([
            new("advanced-meal", [[new(new("grain"), 5)]], false) { Products = [], WorkAmount = 0 },
            new("basic-meal", [[new(new("grain"), 5)]], false) { Products = [], WorkAmount = 0 }]) },
        EssentialInputs = new() { Benches = Known<EquatableArray<ProductionBench>>([
            new() { Id = "stove", Token = "stove-token", Usable = true, Recipes = secondChain ? [Recipe("advanced-meal", 6), Recipe("basic-meal", 0)] : [Recipe("advanced-meal", 6)] }]) },
    };
    private static WorkPawn Cook(string id, int level) => PawnFixtures.Worker(id) with {
        Work = new PawnWorkTable.Works(true, [new() { Work = WorkType.Cooking, Skill = "Cooking", Priority = 1 }]),
        Skills = [new() { Name = "Cooking", Level = level }] };
    private static ProductionRecipe Recipe(string id, int floor) => new() { Name = id, Role = RecipeRole.OrdinaryMeal, Available = true,
        Food = new RecipeFood.Food(0, 0, new RecipeCooking.FromRaw(1, [], floor)) };

    [Fact]
    public void FoodAlternateRecipeResolvesEligibilityButDoesNotCertifyReplacementThroughput()
    {
        var fragile = Review(Kitchen());
        Assert.Contains(fragile.Stress, s => s.Unavailable == "pawn/expert" && s.Coverage == ServiceCoverage.Shortfall);
        var alternate = Review(Kitchen(true));
        Assert.Contains(alternate.Stress, s => s.Unavailable == "pawn/expert" && s.Coverage == ServiceCoverage.Unknown
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
        var review = Review(Kitchen() with { FoodPlan = Known(new FoodPlan()), WorkClaims = new([], ["expert"], [], []) });
        Assert.Contains(review.Coverage, c => c.Target.Predicate == "recipe_eligibility_and_inputs" && c.Coverage == ServiceCoverage.NotApplicable);
        Assert.DoesNotContain(review.Proposals, p => p.Target.Subject == "recipe-capability");
    }

    [Fact]
    public void GeneratorRemovalUsesItsNetworkAndDoesNotClaimEssentialConsumerMembership()
    {
        PowerSite Site(string id, string network, double watts) => new() { Id = id, Network = network, BaseW = watts, Fuel = new RimGovernor.Contracts.Wire.Observations.BuildingFuel.NotRefuelable(),
            OutputW = watts, Connected = true, Powered = true, Forbidden = false, SwitchedOn = true };
        var facts = Facts() with { PowerRequired = Known(true), EssentialInputs = new() { Power = Known(new PowerTopology {
            Buildings = [Site("generator", "home", 100), Site("heater", "home", -80), Site("remote", "remote", 100)] }) } };
        var review = Review(facts);
        Assert.Contains(review.Stress, s => s.Unavailable == "facility/generator" && s.Coverage == ServiceCoverage.Shortfall);
        Assert.Contains(review.Stress, s => s.Unavailable == "facility/remote" && s.Coverage == ServiceCoverage.Adequate);
        Assert.Contains(review.Stress, s => s.MissingEvidence.Contains("power_consumer_service_membership"));
    }

    [Fact]
    public void LogisticsUsesObservedReachabilityAndAlternatePawns()
    {
        var facts = Facts() with { Upkeep = new() { Routes = Known(new RoutesObservation { Pawns = ["a", "b"],
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
        var facts = Facts() with { Resources = Known<EquatableArray<Amount>>([]), Fuel = Known<EquatableArray<FuelConsumer>>([
            new() { Id = "heater", Fuel = Known(0.0), Target = Known(10.0), OutOfFuel = Known(true), Fuels = [new("fuel")],
                Burn = Known(new FuelBurn { PerDay = 1, UnitsPerItem = 1 }) }]) };
        // The materials row is the economy/resource Need's, unmet while a derived target is short of stock (#3303).
        var review = EssentialServicesRules.ReviewEssentialServices(facts, RoundsRules.DefaultRoundsPolicy(), new()
        {
            Work = new() { Tick = facts.Tick },
            MaterialAvailability = Known<IReadOnlyDictionary<Resource, Fact<double>>>(new Dictionary<Resource, Fact<double>> { [new("grain")] = Known(20.0) }),
            Needs = new Dictionary<Resource, long> { [new("fuel")] = 10 },
        });
        var proposal = Assert.Single(review.Proposals, p => p.Target.Owner == EconomyFamily.Resource.FilingKey);
        Assert.Contains(EssentialService.Fuel, proposal.Services);
        Assert.Contains(EssentialService.Materials, proposal.Services);
        Assert.Equal(2, proposal.Requirements.Count);
    }
}
