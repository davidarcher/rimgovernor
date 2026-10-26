using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class BabyFeedingResponseTests
{
    private static readonly ServicePositionRead Here = new ServicePositionRead.Located(new(0, 0));
    private static BabyCareOpportunityRead Nursing(string carer = "pawn", double available = 1) =>
        new(carer, BabyCareKind.Breastfeed, available, Here, Here, ActivitySocialMode.Normal) { ReadyToFeed = true };
    private static BabyRead Baby(string id, params BabyCareOpportunityRead[] opportunities) => new()
    {
        Pawn = id, Care = new() { Food = new(.2, .00001, .0002) { WantsFeeding = true },
            Active = null, Play = null, Opportunities = [.. opportunities] },
        CribMood = null, Fits = [], Play = null, InNursery = false, Hearers = [], SleepHearers = [],
        SleepWithoutNursery = new("", false, true, .00003, 0, 21),
    };
    private static NeedsForecastRead Read(params BabyRead[] babies) => NeedsFixtures.Read(100, NeedsFixtures.Pawn()) with
    { Babies = NeedsFixtures.NoBabies with { Babies = [.. babies] } };
    private static BabyFeeding Review(NeedsForecastRead read, bool stocked = false)
    {
        var ids = read.Babies.Babies.Select(b => new PawnId(b.Pawn)).ToArray();
        return BabyFeedingRules.ReviewBabyFeeding(ids, BabyCareRules.AssessBabyCare(read), new FoodSupply
        {
            Larder = new() { RawMeatNutrition = 0, CookDemandNutrition = 0, Corpses = [] },
            Consumers = ids.Select(id => new FoodConsumer { Id = id, NutritionPerDay = 1, HumanMeatAcceptable = false }).ToArray(),
            Stocks = stocked ? [new FoodStock { Id = "milk", DefName = "Milk", Count = 75, Nutrition = 5, UnitMass = .1,
                IsHumanMeat = false, RawMeat = false, IsHumanlike = false, Vegetable = false, RawClass = FoodIngredientClass.None,
                Holder = new PawnId(""), Eaters = ids, Rot = new FoodRot.Keeps(), Place = new FoodPlace.Stands(true, "store"),
                TemperatureC = 20, RotRate = 1, Form = new FoodForm.Item(), Forbidden = false }] : [],
        }, 2);
    }

    [Fact]
    public void NativeBabyInteractionMemoriesSurviveTheTypedObservationBoundary()
    {
        var read = new BabyInteractionRead("Chitchat", 1, false, [new("KindWords", 5, 60000, 3, 1, .75, 0)]);
        var copy = OrdersJson.Deserialize<BabyInteractionRead>(OrdersJson.Serialize(read));
        Assert.Equal(read, copy);
        Assert.Single(copy.Moods);
    }

    [Fact]
    public void HungerAndCareOpportunityRoundTripWithoutBeingIntake()
    {
        var read = Read(Baby("baby", Nursing()));
        var copy = OrdersJson.Deserialize<NeedsForecastRead>(OrdersJson.Serialize(read));
        var care = Assert.Single(BabyCareRules.AssessBabyCare(copy));
        Assert.True(care.Hungry);
        Assert.Equal("pawn", care.Opportunity!.Carer);
        Assert.Null(care.Active);
        Assert.Equal(Satisfaction.Unmet, FoodRules.BabyFeedingNeedOf(new() { BabyFeeding = Review(copy) }).Satisfaction);
    }

    [Fact]
    public void DepletedNursingAndUnavailableCarersDoNotCoverProvision()
    {
        var read = Read(Baby("baby", Nursing(available: 0) with { FoodRefillPerTick = .00001 }));
        var care = Assert.Single(BabyCareRules.AssessBabyCare(read));
        Assert.Null(care.Opportunity);
        Assert.Equal("no_usable_feeding_opportunity", care.Blocker);
        Assert.True(Review(read).IsShort);
        read = Read(Baby("baby", Nursing())) with { Pawns = [NeedsFixtures.Pawn() with { State = "drafted" }] };
        Assert.Null(Assert.Single(BabyCareRules.AssessBabyCare(read)).Opportunity);
        var missing = Baby("baby") with { Care = Baby("baby").Care with { Food = null } };
        var unavailable = Assert.Single(BabyCareRules.AssessBabyCare(Read(missing)));
        Assert.True(unavailable.Hungry);
        Assert.Equal("baby_food_unobserved", unavailable.Blocker);
        // Native lactation can contain positive charge below its activation threshold.
        var unready = Nursing(available: .01) with { ReadyToFeed = false, FoodRefillPerTick = .00001 };
        Assert.True(Review(Read(Baby("baby", unready))).IsShort);
        var bottle = Nursing() with { Kind = BabyCareKind.BottleFeed };
        Assert.Equal(BabyCareKind.BottleFeed,
            Assert.Single(BabyCareRules.AssessBabyCare(Read(Baby("baby", unready, bottle)))).Opportunity!.Kind);
    }

    [Fact]
    public void AWorkingNurseDoesNotRequireBottleStockButHungerRemainsUnmet()
    {
        var baby = Baby("baby", Nursing());
        baby = baby with { Care = baby.Care with
            { Active = new("pawn", BabyCareKind.Breastfeed, new(0, 0), new(0, 0), Here, ActivitySocialMode.Normal) } };
        var review = Review(Read(baby));
        Assert.False(review.IsShort);
        Assert.Equal(Satisfaction.Unmet, FoodRules.BabyFeedingNeedOf(new() { BabyFeeding = review }).Satisfaction);
    }

    [Fact]
    public void SuitableBottleStockCoversProvisionWithoutClosingHunger()
    {
        var bottle = Nursing() with { Kind = BabyCareKind.BottleFeed };
        var review = Review(Read(Baby("baby", bottle)), stocked: true);
        Assert.False(review.IsShort);
        Assert.Equal(5, review.StockNutrition);
        Assert.Equal(Satisfaction.Unmet, FoodRules.BabyFeedingNeedOf(new() { BabyFeeding = review }).Satisfaction);
    }

    [Fact]
    public void OneNurseCannotCoverTwoHungryBabiesAndLostSupplyIsReassessed()
    {
        var read = Read(Baby("first", Nursing()), Baby("second", Nursing()));
        var care = BabyCareRules.AssessBabyCare(read);
        Assert.Single(care, c => c.Opportunity is not null);
        Assert.True(Review(read).IsShort);
        Assert.All(care, c => Assert.True(c.Hungry));
        var bottle = Nursing() with { Kind = BabyCareKind.BottleFeed };
        Assert.Equal(BabyCareKind.BottleFeed, Assert.Single(BabyCareRules.AssessBabyCare(Read(Baby("baby", bottle)))).Opportunity!.Kind);
        Assert.Null(Assert.Single(BabyCareRules.AssessBabyCare(Read(Baby("baby")))).Opportunity);
    }

    [Fact]
    public void BottleAccumulationIsActiveCareAndIntakeDoesNotEndSafeReturn()
    {
        var baby = Baby("baby") with { Care = Baby("baby").Care with
        { Active = new("pawn", BabyCareKind.BottleFeed, new(0, 0), new(0, 0), Here, ActivitySocialMode.Normal) } };
        var read = Read(baby);
        Assert.Contains("pawn", BabyCareRules.CarePawns(read));
        Assert.Equal("native_care_active", Assert.Single(BabyCareRules.AssessBabyCare(read)).Blocker);
        var returning = baby with { Care = baby.Care with
        { Food = baby.Care.Food! with { Level = 1, WantsFeeding = false }, Active = baby.Care.Active! with { Kind = BabyCareKind.Carry } } };
        Assert.Contains("pawn", BabyCareRules.CarePawns(Read(returning)));
        Assert.DoesNotContain("pawn", BabyCareRules.CarePawns(Read(returning with { Care = returning.Care with { Active = null } })));
        // An interruption retains actual partial intake and an unmet need; no predicted intake is replayed.
        var interrupted = baby with { Care = baby.Care with { Active = null, Food = baby.Care.Food! with { Level = .4 } } };
        var actual = Assert.Single(BabyCareRules.AssessBabyCare(Read(interrupted)));
        Assert.Equal(.4, interrupted.Care.Food!.Level);
        Assert.True(actual.Hungry);
    }

    [Fact]
    public void SharedAdmissionWithholdsCareCapacityAndDoesNotRefundCarriedInputs()
    {
        var facts = WorkResourceTestFacts.Create("pawn") with { AvailableNeeds = [FoodRules.BabyFeedingNeed, AdminFamily.WorkAssignments] };
        var carried = facts.Census.WorkResources[0] with { CarriedThing = "partial-input", CarriedResource = "Rice", CarriedCount = 3 };
        facts = facts with { Census = facts.Census with { WorkResources = [carried] },
            ForecastObserved = facts.ForecastObserved with { Needs = Read(Baby("baby", Nursing())) } };
        // Hunger and a possible nurse do not reserve a worker before vanilla chooses care.
        var possible = ProductionRules.WorkAdmissionEvidenceOf(facts, new ProductionBatchCensus.Read([]));
        Assert.True(Assert.Single(possible.Pawns).Available);
        var baby = Baby("baby", Nursing());
        baby = baby with { Care = baby.Care with
            { Active = new("pawn", BabyCareKind.Breastfeed, new(0, 0), new(0, 0), Here, ActivitySocialMode.Normal) } };
        facts = facts with { ForecastObserved = facts.ForecastObserved with { Needs = Read(baby) } };
        var evidence = ProductionRules.WorkAdmissionEvidenceOf(facts, new ProductionBatchCensus.Read([]));
        Assert.False(Assert.Single(evidence.Pawns).Available);
        Assert.Equal(carried, Assert.Single(evidence.WorkResources));
        var view = WorkRules.WorkResourcesOf(evidence, new ProductionBatchCensus.Read([]), []);
        var action = DomainAction.NewPawnActivitiesAction(new("competing"), new(1,
            [new("pawn", "new-owner", "new-method", 0, 1, false, [new(PawnActivityKind.Hold, "", "", "", 0, 500)])]));
        Assert.False(WorkRules.WorkResourcesFit(view, new PlanSpec(new("competing"), new(1), [action])));
    }
}
