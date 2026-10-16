using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class EssentialServicesTests
{
    private static readonly ConcernId[] Owners = [EnsureFoodSupply, EnsureCooking, CriticalMedicine, MaintainSurgery,
        MaintainMedicalReserves, MaintainHousing, MaintainShelter, EnsureTemperatureSafety,
        MaintainRefrigeration, EnsureBasicPower, MaintainRoutes, MaintainResource];
    private static RoundsFacts Facts() => new() { Tick = new(100), Colonists = Known(2L), Fuel = Known<EquatableArray<FuelConsumer>>([]) };
    private static EssentialServiceReview Review(RoundsFacts facts, int budget = 128, params ConcernId[] shortfalls) =>
        ReviewEssentialServices(facts, DefaultRoundsPolicy(), new() { Work = new() { Tick = facts.Tick },
            MaterialAvailability = Known<IReadOnlyDictionary<Resource, Fact<double>>>(new Dictionary<Resource, Fact<double>> { [new("grain")] = Known(20.0) }) },
            Owners.Select(o => new RoundsAssessment { Id = o, Finding = shortfalls.Contains(o) ? Finding.Unmet : Finding.Met }).ToArray(), budget);

    [Fact]
    public void NormalInspectionExposesEveryServiceWithoutTreatingUnknownStressAsBaselineFailure()
    {
        var normal = InspectRounds(new(), new(), DefaultRoundsPolicy());
        Assert.NotNull(normal.EssentialServices);
        Assert.Equal(Enum.GetValues<EssentialService>(), normal.EssentialServices.Coverage.Select(c => c.Service).Distinct().Order().ToArray());
        var known = Review(Facts());
        Assert.Contains(known.Coverage, c => c.Target.Owner == EnsureTemperatureSafety && c.Coverage == ServiceCoverage.Adequate);
        Assert.Contains(known.Stress, c => c.Target.Owner == EnsureTemperatureSafety && c.Coverage == ServiceCoverage.Unknown);
        Assert.DoesNotContain(known.Proposals, p => p.Target.Owner == EnsureTemperatureSafety);
    }

    [Fact]
    public void HeatedShelterUsesExistingOwnerPredicatesAndStableIdentityUntilObservedRecovery()
    {
        var cold = Review(Facts(), 128, EnsureTemperatureSafety);
        var later = Review(Facts() with { Tick = new(200), SleepingMin = Known(-20.0) }, 128, EnsureTemperatureSafety);
        var proposal = Assert.Single(cold.Proposals, p => p.Target.Owner == EnsureTemperatureSafety);
        Assert.Equal(proposal.Id, Assert.Single(later.Proposals, p => p.Target.Owner == EnsureTemperatureSafety).Id);
        Assert.DoesNotContain(Review(Facts()).Proposals, p => p.Id == proposal.Id);
        Assert.Equal(DevelopmentReviewState.Satisfied, ReassessDevelopment(proposal, Review(Facts())));
        Assert.Equal("owner_observed_recovery", proposal.Target.Predicate);
    }

    [Fact]
    public void InactiveColonyNeedsNoFictionalBackupAndBudgetIsVisibleDeterministic()
    {
        var inactive = Review(Facts() with { Colonists = Known(0L) });
        Assert.Empty(inactive.Proposals);
        Assert.Empty(inactive.Stress);
        var limited = Review(Facts(), 1);
        Assert.True(limited.Truncated);
        Assert.Single(limited.Stress);
        Assert.Equal(limited, Review(Facts(), 1));
        Assert.Throws<DomainException>(() => ReviewEssentialServices(Facts(), DefaultRoundsPolicy(), new(), [], 1));
    }

    private static CarePawn Patient(params (string Id, double Chance)[] doctors) => new()
    {
        Id = new("patient"), Dead = Known(false), Operations = Known<EquatableArray<SurgeryOperation>>([
            new() { Kind = SurgeryKind.Restore, Recipe = Known("restore-leg"), PartIndex = Known(2), PartDefName = Known("Leg"),
                IngredientsOnMap = Known(true), EligibleDoctors = Known(doctors.Length),
                SuccessChance = doctors.Length == 0 ? default : Known(doctors.Max(d => d.Chance)),
                Violation = Known(false), Lethal = Known(false),
                DoctorChances = doctors.ToDictionary(d => new PawnId(d.Id), d => d.Chance) }])
    };

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
        Assert.Equal(DevelopmentReviewState.Unknown, ReassessDevelopment(proposal, Review(facts, 0)));
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
            new("advanced-meal", Known<EquatableArray<EquatableArray<Amount>>>([[new(new("grain"), 5)]]), Known(false)),
            new("basic-meal", Known<EquatableArray<EquatableArray<Amount>>>([[new(new("grain"), 5)]]), Known(false))]) },
        EssentialInputs = new() { Benches = Known<EquatableArray<ProductionBench>>([
            new() { Id = "stove", Usable = Known(true), Recipes = secondChain ? [Recipe("advanced-meal", 6), Recipe("basic-meal", 0)] : [Recipe("advanced-meal", 6)] }]) },
    };
    private static WorkPawn Cook(string id, int level) => new() { Id = new(id), Applies = Known(true), Available = Known(true),
        Work = Known<EquatableArray<WorkPriority>>([new() { Work = WorkType.Cooking, Skill = "Cooking", Priority = 1 }]),
        Skills = Known<EquatableArray<WorkSkill>>([new() { Name = "Cooking", Level = level }]) };
    private static ProductionRecipe Recipe(string id, int floor) => new() { Name = id, Role = RecipeRole.OrdinaryMeal, Available = Known(true), CookSkillFloor = Known(floor) };

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
    public void ClaimsAndUnknownRecipeFloorCannotCertifyAvailableCapability()
    {
        var kitchen = Kitchen();
        var held = Review(kitchen with { WorkClaims = new([], ["expert"], [], []) });
        Assert.Contains(held.Coverage, c => c.Target.Predicate == "recipe_eligibility_and_inputs" && c.Coverage == ServiceCoverage.Shortfall);
        var unknown = Review(kitchen with { EssentialInputs = new() { Benches = Known<EquatableArray<ProductionBench>>([
            new() { Id = "stove", Usable = Known(true), Recipes = [Recipe("advanced-meal", 6) with { CookSkillFloor = default }] }]) } });
        Assert.Contains(unknown.Coverage, c => c.Target.Predicate == "recipe_eligibility_and_inputs" && c.Coverage == ServiceCoverage.Unknown);
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
        PowerSite Site(string id, string network, double watts) => new() { Id = id, Network = Known(network), BaseW = Known(watts),
            OutputW = Known(watts), Connected = Known(true), Powered = Known(true), Forbidden = Known(false), SwitchedOn = Known(true) };
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
            Facilities = [new() { Id = "medicine-store", Kind = "storage", Travel = [new() { Pawn = "a", Reachable = true }, new() { Pawn = "b", Reachable = false }] }] }) } };
        var review = Review(facts);
        Assert.Contains(review.Stress, s => s.Unavailable == "pawn/a" && s.Coverage == ServiceCoverage.Shortfall);
        Assert.Contains(review.Proposals, p => p.Target.Subject == "medicine-store");
    }

    [Fact]
    public void SharedFuelAndMaterialShortfallProducesOneOwnedProposal()
    {
        var facts = Facts() with { Resources = Known<EquatableArray<Amount>>([]), Fuel = Known<EquatableArray<FuelConsumer>>([
            new() { Id = "heater", Fuel = Known(0.0), Target = Known(10.0), OutOfFuel = Known(true), Fuels = [new("fuel")],
                Burn = Known(new FuelBurn { PerDay = 1, UnitsPerItem = 1 }) }]) };
        var review = Review(facts, 128, MaintainResource);
        var proposal = Assert.Single(review.Proposals, p => p.Target.Owner == MaintainResource);
        Assert.Contains(EssentialService.Fuel, proposal.Services);
        Assert.Contains(EssentialService.Materials, proposal.Services);
        Assert.Equal(2, proposal.Requirements.Count);
    }
}
