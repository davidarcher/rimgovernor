using RimGovernor.Brain.Runtime;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using Xunit;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The food stores family, storage only (#3251): the food-stores/storage Need is a plain threshold over what
/// stands, and its reserve and larder Methods are admitted under the Need owner beside each other, with the
/// supply stacks as their claims.
/// </summary>
public sealed class FoodStoresFamilyTests
{
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));

    private static readonly FoodStoragePolicy Policy = FoodStorageUpkeepRules.DefaultFoodStoragePolicy();

    private static FoodStorageStock Stock(string id, double nutrition, bool roofed, string def = "MealSimple") => new(new FoodStock {
        Id = id,
        DefName = def,
        Nutrition = nutrition,
        Rot = new FoodRot.Rots((long)GameTime.TicksPerDay),
        Place = new FoodPlace.Stands(roofed, ""),
        TemperatureC = roofed ? 0.0 : 20.0, RotRate = roofed ? 0 : 1,
        Form = new FoodForm.Item(),
        IsHumanMeat = false, RawMeat = false, IsHumanlike = false, Vegetable = false, RawClass = FoodIngredientClass.None, Holder = new PawnId(string.Empty), Eaters = [], Count = 0, UnitMass = 0, Forbidden = false });

    /// <summary>A forbidden warm corpse the larder allows again, lying unroofed at (3, 4).</summary>
    private static FoodStorageStock Corpse() => new(new FoodStock {
        Id = "corpse-1",
        DefName = "Corpse_Muffalo",
        Nutrition = 10.0,
        Rot = new FoodRot.Rots((long)GameTime.TicksPerDay),
        Place = new FoodPlace.Stands(false, ""),
        TemperatureC = 20.0, RotRate = 1,
        Forbidden = true,
        Form = new FoodForm.Corpse(100.0, 1.0),
        IsHumanMeat = false, RawMeat = false, IsHumanlike = false, Vegetable = false, RawClass = FoodIngredientClass.None, Holder = new PawnId(string.Empty), Eaters = [], Count = 0, UnitMass = 0 });

    private static FoodStorageObservation Census(params FoodStorageStock[] stocks) => new()
    {
        Stocks = EquatableArray.From(stocks),
        Larder = new FoodLarder { Corpses = stocks.Any(s => s.Stock.IsCorpse()) ? [new CorpseHandling { Id = "corpse-1", Cell = new(3, 4),
        Hauler = new PawnId(string.Empty), FrozenDestination = false }] : [],
        RawMeatNutrition = 0, CookDemandNutrition = 0 },
        ChilledMaxC = 10,
    };

    private static FoodReserveReview Reserve(params string[] hold) => new() { TargetNutrition = 10, StockNutrition = 10, Hold = hold };

    private static Need Derive(FoodStoresFacts facts) => Assert.Single(new FoodStoresFamily(Policy, true, false).Derive(facts));

    /// <summary>A roofed perishable stock in room r1 measured at <paramref name="celsius"/>, two days from rot.</summary>
    private static FoodStorageStock Warm(string id, double nutrition, double celsius) => new(new FoodStock {
        Id = id,
        DefName = "RawFungus",
        Nutrition = nutrition,
        Rot = new FoodRot.Rots(2L * GameTime.TicksPerDay),
        Place = new FoodPlace.Stands(true, "r1"),
        TemperatureC = celsius, RotRate = celsius <= 0 ? 0 : 1,
        Form = new FoodForm.Item(),
        IsHumanMeat = false, RawMeat = false, IsHumanlike = false, Vegetable = false, RawClass = FoodIngredientClass.None, Holder = new PawnId(string.Empty), Eaters = [], Count = 0, UnitMass = 0, Forbidden = false });

    private static Need Refrigeration(FoodStoresFacts facts) => Assert.Single(new FoodStoresFamily(Policy, false, true).Derive(facts));

    [Fact]
    public void TheRefrigerationNeedIsAPlainThresholdAtTheExitBarAndAMalformedCensusIsUnreadable()
    {
        // Warm at-risk nutrition at the floor, colder than the old 10 C entry bar but warmer than the 5 C exit bar:
        // with no latch it is unmet on its own.
        var warm = Refrigeration(new() { Storage = Census(Warm("a", Policy.AtRiskNutritionThreshold, Policy.ChilledExitC + 2)), WarmRooms = [], MealClosetOwed = false });
        Assert.Equal((FoodStoresRules.RefrigerationNeed, NeedTier.Wellbeing, Satisfaction.Unmet), (warm.Id, warm.Tier, warm.Satisfaction));
        Assert.True(warm.Deficit > 0 && warm.Deficit <= 1);

        // The same stock at the exit bar is met, and so is warm stock under the floor.
        var chilled = Refrigeration(new() { Storage = Census(Warm("a", Policy.AtRiskNutritionThreshold, Policy.ChilledExitC)), WarmRooms = [], MealClosetOwed = false });
        Assert.Equal(Satisfaction.Met, chilled.Satisfaction);
        var little = Refrigeration(new() { Storage = Census(Warm("a", Policy.AtRiskNutritionThreshold / 2, Policy.ChilledExitC + 2)) });
        Assert.Equal(Satisfaction.Met, little.Satisfaction);

        // A warm cooling room or an owed meal closet is unmet on a cold census.
        Assert.Equal(Satisfaction.Unmet, Refrigeration(new() { Storage = Census(), WarmRooms = ["tomb-1"] }).Satisfaction);
        var closet = Refrigeration(new() { Storage = Census(), MealClosetOwed = true });
        Assert.Equal(Satisfaction.Unmet, closet.Satisfaction);
        Assert.True(closet.Deficit >= 0.5);

        // A malformed census is unreadable, never met.
        Assert.Equal("refrigeration census", Assert.Throws<RequiredFactException>(() => Refrigeration(new())).Fact);
    }

    [Fact]
    public void EachFoodStoresNeedIsDerivedOnlyWhenItsServiceIsComposed()
    {
        var facts = new FoodStoresFacts { Storage = Census() };
        Assert.Equal([FoodStoresRules.FoodStorageNeed, FoodStoresRules.RefrigerationNeed], new FoodStoresFamily(Policy, true, true).Derive(facts).Select(n => n.Id));
        Assert.Equal([FoodStoresRules.FoodStorageNeed], new FoodStoresFamily(Policy, true, false).Derive(facts).Select(n => n.Id));
        Assert.Equal([FoodStoresRules.RefrigerationNeed], new FoodStoresFamily(Policy, false, true).Derive(facts).Select(n => n.Id));
        Assert.Equal([FactFamily.Rooms, FactFamily.Colony], new FoodStoresFamily(Policy, false, true).Reads);
    }

    [Fact]
    public void TheStorageNeedIsAPlainThresholdAtTheTargetShareAndAMalformedCensusIsUnreadable()
    {
        // 85% stored: met under the old entry share, unmet now that the latch is gone.
        var short85 = Derive(new() { Storage = Census(Stock("a", 85, roofed: true), Stock("b", 15, roofed: false)), Stockpile = true });
        Assert.Equal((FoodStoresRules.FoodStorageNeed, NeedTier.Wellbeing, Satisfaction.Unmet), (short85.Id, short85.Tier, short85.Satisfaction));
        Assert.True(short85.Deficit > 0 && short85.Deficit <= 1);

        var stored = Derive(new() { Storage = Census(Stock("a", 95, roofed: true), Stock("b", 5, roofed: false)), Stockpile = true });
        Assert.Equal(Satisfaction.Met, stored.Satisfaction);
        Assert.Equal(0.0, stored.Deficit);

        // An empty census is whole and met; malformed rows are an unreadable census.
        Assert.Equal(Satisfaction.Met, Derive(new() { Stockpile = true }).Satisfaction);
        Assert.Equal("food storage census", Assert.Throws<RequiredFactException>(() => Derive(new() { Storage = Census(Stock("a", 5, roofed: true), Stock("a", 5, roofed: true)) })).Fact);
    }

    [Fact]
    public void PendingReserveAccessAMissingStockpileOrAShortReserveLeaveTheNeedUnmet()
    {
        var census = Census(Stock("a", 100, roofed: true));
        Assert.Equal(1.0, Derive(new() { Storage = census, Stockpile = true, Reserve = new FoodReserveState.Reviewed(Reserve("pem-1")) }).Deficit);
        Assert.Equal(Satisfaction.Unmet, Derive(new() { Storage = census, Stockpile = false }).Satisfaction);
        var refill = Derive(new() { Storage = census, Stockpile = true, Reserve = new FoodReserveState.Reviewed(new FoodReserveReview { TargetNutrition = 10, DeficitNutrition = 4 }) });
        Assert.Equal((Satisfaction.Unmet, 0.4), (refill.Satisfaction, refill.Deficit));
        // A present stockpile is not missing, and pending access is unmet even with the census unread.
        Assert.Equal(Satisfaction.Met, Derive(new() { Storage = census, Stockpile = true }).Satisfaction);
        Assert.Equal(Satisfaction.Unmet, Derive(new() { Stockpile = true, Reserve = new FoodReserveState.Reviewed(Reserve("pem-1")) }).Satisfaction);
    }

    private sealed class Colony : IDisposable
    {
        public required Journal Journal { get; init; }
        public required FamilyRunner<FoodStoresFacts> Runner { get; init; }
        public FoodStoresFacts Facts { get; set; } = new();

        public void Dispose() => Journal.Dispose();

        public FamilyRun Run() =>
            Runner.RunAsync(new FamilyStep { Journal = Journal, World = World }, TestContext.Current.CancellationToken).GetAwaiter().GetResult();

        public static Verdict Report(FamilyRun run) => Assert.Single(run.Reports, r => r.Need == FoodStoresRules.FoodStorageNeed).Verdict;
    }

    /// <summary>A reviewed colony whose pemmican pem-1 the reserve holds and whose forbidden corpse the larder allows.</summary>
    private static Colony Open(bool larder = true)
    {
        var ct = TestContext.Current.CancellationToken;
        var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-food-stores-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        var census = larder ? Census(Stock("pem-1", 10, roofed: true, "Pemmican"), Corpse()) : Census(Stock("pem-1", 10, roofed: true, "Pemmican"));
        var facts = WorkResourceTestFacts.Create("a", "b", "c") with
        {
            Census = (WorkResourceTestFacts.Create("a", "b", "c")).Census with { ColonistCount = 3, IndoorSleepingCapacity = 3, BedCapacity = 3 },   FieldCoverage = 1.0,
            FoodStorageUpkeep = census,
            FoodReserve = new FoodReserveState.Reviewed(Reserve("pem-1")),
        };
        journal.Review(new() { Current = World, Tick = new(100), Enabled = true, Policy = Rules.DefaultPlannerSettings(), Facts = facts }, ct);
        Colony? colony = null;
        var runner = new FamilyRunner<FoodStoresFacts>(new FoodStoresFamily(Policy, true, false), (_, _) => Task.FromResult(colony!.Facts),
            new FamilySchedule { Kinds = [ActionKind.Haul, ActionKind.SupplyAllow, ActionKind.SupplyForbid] });
        colony = new Colony
        {
            Journal = journal,
            Runner = runner,
            Facts = new FoodStoresFacts
            {
                Storage = census,
                Reserve = new FoodReserveState.Reviewed(Reserve("pem-1")),
                Stockpile = true,
                StockProtection = true,
                ReserveTargets = new ReserveSupplyRead.Targets([SupplyAllow.NewSupplyForbid("pem-1", "Pemmican", new(1, 1)), new SupplyAllow("pem-2", "Pemmican", new(1, 2))]),
            },
        };
        return colony;
    }

    [Fact]
    public void TheReserveMethodIsAdmittedUnderTheNeedOwnerAndContinuesWhileOpen()
    {
        var ct = TestContext.Current.CancellationToken;
        using var colony = Open();
        var run = colony.Run();
        Assert.Equal(Outcome.Admitted, Colony.Report(run).Outcome);
        var method = Assert.Single(run.Methods);
        Assert.Equal("food-reserve", method.Method.Value);
        var plan = colony.Journal.LoadPlan(method.Plan, ct);
        // Only the stack the review wants held, as a forbid.
        var action = Assert.Single(plan.Progress).Action;
        Assert.Equal((ActionKind.SupplyForbid, "pem-1"), (action.Kind, action.SupplyAllow!.Thing));
        Assert.Equal(FoodStoresRules.FoodStorageNeed.FilingKey, colony.Journal.PlanMethod(method.Plan, ct)!.Concern);

        var again = colony.Run();
        Assert.Equal(Verdict.BuildingReasonExistingWork, Colony.Report(again));
        Assert.Single(colony.Journal.LoadNeed(FoodStoresRules.FoodStorageNeed, World, ct).Methods);
    }

    [Fact]
    public void TheStorageNeedReachesRoundsProgressAndIsPrunedWhenItsFamilyStopsReportingIt()
    {
        var ct = TestContext.Current.CancellationToken;
        using var colony = Open();
        File(colony, colony.Run());
        var row = Row(colony);
        Assert.NotNull(row);
        Assert.Equal(("food-reserve", true), (row.Method, row.Open));

        // The review rebuild keeps the row; a complete run that no longer derives the Need prunes it.
        colony.Journal.Review(new() { Current = World, Tick = new(200), Enabled = true, Policy = Rules.DefaultPlannerSettings(), Revision = colony.Journal.LoadReview(ct).Revision,
            Facts = WorkResourceTestFacts.Create("a", "b", "c") with { Census = (WorkResourceTestFacts.Create("a", "b", "c")).Census with { ColonistCount = 3, IndoorSleepingCapacity = 3, BedCapacity = 3 },   FieldCoverage = 1.0} }, ct);
        Assert.Equal(row, Row(colony));
        colony.Journal.RecordPlannerReasons(new Dictionary<FilingKey, PlannerNote>(), [new FamilyProgress("food-stores", true, [])], ct);
        Assert.Null(Row(colony));
    }

    private static void File(Colony colony, FamilyRun run) =>
        colony.Journal.RecordPlannerReasons(new Dictionary<FilingKey, PlannerNote>(), [PlannerWave.NeedProgressOf("food-stores", run)!], TestContext.Current.CancellationToken);

    private static ConcernProgress? Row(Colony colony) =>
        colony.Journal.LoadReview(TestContext.Current.CancellationToken).Progress.SingleOrDefault(p => p.Concern == FoodStoresRules.FoodStorageNeed.FilingKey);

    [Fact]
    public void TheLarderMethodIsAdmittedBesideAnOpenReservePlan()
    {
        var ct = TestContext.Current.CancellationToken;
        using var colony = Open();
        Assert.Equal(Outcome.Admitted, Colony.Report(colony.Run()).Outcome);

        // The reserve is held now; the larder's corpse change no longer waits behind the open reserve plan.
        colony.Facts = colony.Facts with { Reserve = new FoodReserveState.Reviewed(new FoodReserveReview { TargetNutrition = 10, StockNutrition = 10 }) };
        var run = colony.Run();
        Assert.Equal(Outcome.Admitted, Colony.Report(run).Outcome);
        Assert.Equal("corpse-larder-allow-corpse-1", Assert.Single(run.Methods).Method.Value);
        Assert.Equal(2, colony.Journal.LoadNeed(FoodStoresRules.FoodStorageNeed, World, ct).Methods.Count);
    }

    [Fact]
    public void AnOverlappingSupplyStackIsAClaimConflict()
    {
        var ct = TestContext.Current.CancellationToken;
        using var colony = Open(larder: false);
        var other = PlanIds.MintPlanId();
        var held = colony.Journal.CommitClaimedMethod(FoodStoresRules.FoodStorageNeed, World, new("held-pem-1"), "",
            new PlanSpec(other, new(1), [DomainAction.NewSupplyAllowAction(new(other.Value + "-0"), SupplyAllow.NewSupplyForbid("pem-1", "Pemmican", new(1, 1)))]), ct);
        Assert.Equal(ClaimOutcome.Admitted, held.Outcome);

        var refused = Colony.Report(colony.Run());
        Assert.True(refused.Is(Cause.Claim));
        Assert.Equal("pem-1", refused.Refusal.Subject);
    }

    [Fact]
    public void TheNeedOwnerAdmitsOnlyStacksTheReviewLists()
    {
        var ct = TestContext.Current.CancellationToken;
        using var colony = Open(larder: false);
        var id = PlanIds.MintPlanId();
        Assert.ThrowsAny<Exception>(() => colony.Journal.CommitClaimedMethod(FoodStoresRules.FoodStorageNeed, World, new("stray"), "",
            new PlanSpec(id, new(1), [DomainAction.NewSupplyAllowAction(new(id.Value + "-0"), new SupplyAllow("pem-9", "Pemmican", new(1, 1)))]), ct));
        Assert.Empty(colony.Journal.LoadNeed(FoodStoresRules.FoodStorageNeed, World, ct).Methods);
    }

    [Fact]
    public void AReserveProposalIsCheckedAgainstTheCurrentCohortAgainAtCommit()
    {
        var ct = TestContext.Current.CancellationToken;
        using var colony = Open(larder: false);
        var family = new FoodStoresFamily(Policy, true, false);
        var candidate = Assert.Single(family.Propose(family.Derive(colony.Facts), colony.Facts));
        var plan = candidate.Plan(new("reserve-plan"));
        var reviewed = colony.Journal.LoadReview(ct);
        Assert.Null(FamilyAdmissionRules.SupplyConflict(candidate.Need, plan, true, reviewed.LarderSupplies,
            reviewed.ReserveSupplies, reviewed.EventLoot.Pending, reviewed.PollutionSupplies));

        // A new review releases the reserve while the old proposal is still waiting to commit.
        colony.Journal.Review(new() { Revision = reviewed.Revision, Current = World, Tick = new(101), Enabled = true,
            Policy = Rules.DefaultPlannerSettings(), Facts = WorkResourceTestFacts.Create("a", "b", "c") }, ct);
        var current = colony.Journal.LoadReview(ct);
        var reason = FamilyAdmissionRules.SupplyConflict(candidate.Need, plan, true, current.LarderSupplies,
            current.ReserveSupplies, current.EventLoot.Pending, current.PollutionSupplies);
        Assert.Equal("supply pem-1 at reserve-plan-0 is not the review's cohort row", reason);
        Assert.Equal(new ConflictException(reason!).Message, Assert.Throws<ConflictException>(() => colony.Journal.CommitClaimedMethod(
            candidate.Need, World, candidate.Method, candidate.Reason, plan, ct)).Message);
        Assert.Empty(colony.Journal.LoadNeed(candidate.Need, World, ct).Methods);
    }
    [Fact]
    public void WithoutStockProtectionOrReserveStacksTheUnmetNeedHasNoMethod()
    {
        using var colony = Open(larder: false);
        colony.Facts = colony.Facts with { StockProtection = false };
        var run = colony.Run();
        Assert.Empty(run.Methods);
        Assert.True(Colony.Report(run).Is(Cause.AwaitingPlan));

        colony.Facts = colony.Facts with { StockProtection = true, ReserveTargets = new ReserveSupplyRead.Targets([]) };
        Assert.Empty(colony.Run().Methods);
    }
}
