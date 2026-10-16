using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;
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

    private static readonly FoodStoragePolicy Policy = DefaultFoodStoragePolicy();

    private static FoodStorageStock Stock(string id, double nutrition, bool roofed, string def = "MealSimple") => new(new FoodStock
    {
        Id = id,
        DefName = new(def),
        Nutrition = Known(nutrition),
        Perishable = Known(true),
        RotTicks = Known((long)GameTime.TicksPerDay),
        Roofed = Known(roofed),
        TemperatureC = Known(roofed ? 0.0 : 20.0),
        Forbidden = Known(false),
    });

    /// <summary>A forbidden warm corpse the larder allows again, lying unroofed at (3, 4).</summary>
    private static FoodStorageStock Corpse() => new(new FoodStock
    {
        Id = "corpse-1",
        DefName = new("Corpse_Muffalo"),
        Corpse = true,
        Nutrition = Known(10.0),
        Perishable = Known(true),
        RotTicks = Known((long)GameTime.TicksPerDay),
        Roofed = Known(false),
        TemperatureC = Known(20.0),
        Forbidden = Known(true),
        MeatAmount = Known(100.0),
        BodySize = Known(1.0),
    });

    private static FoodStorageObservation Census(params FoodStorageStock[] stocks) => new()
    {
        Stocks = Known(EquatableArray.From(stocks)),
        Larder = Known(new FoodLarder { Corpses = stocks.Any(s => s.Stock.Corpse) ? [new CorpseHandling { Id = "corpse-1", Cell = new(3, 4) }] : [] }),
        ChilledMaxC = 10,
    };

    private static FoodReserveReview Reserve(params string[] hold) => new() { TargetNutrition = 10, StockNutrition = 10, Hold = hold };

    private static Need Derive(FoodStoresFacts facts) => Assert.Single(new FoodStoresFamily(Policy, true, false).Derive(facts));

    /// <summary>A roofed perishable stock in room r1 measured at <paramref name="celsius"/>, two days from rot.</summary>
    private static FoodStorageStock Warm(string id, double nutrition, double celsius) => new(new FoodStock
    {
        Id = id,
        DefName = new("RawFungus"),
        Nutrition = Known(nutrition),
        Perishable = Known(true),
        RotTicks = Known(2L * GameTime.TicksPerDay),
        Roofed = Known(true),
        TemperatureC = Known(celsius),
        Forbidden = Known(false),
        Room = Known("r1"),
    });

    private static Need Refrigeration(FoodStoresFacts facts) => Assert.Single(new FoodStoresFamily(Policy, false, true).Derive(facts));

    [Fact]
    public void TheRefrigerationNeedIsAPlainThresholdAtTheExitBarAndAnUnknownCensusIsUnknown()
    {
        // Warm at-risk nutrition at the floor, colder than the old 10 C entry bar but warmer than the 5 C exit bar:
        // with no latch it is unmet on its own.
        var warm = Refrigeration(new() { Storage = Census(Warm("a", Policy.AtRiskNutritionThreshold, Policy.ChilledExitC + 2)), WarmRooms = Known<EquatableArray<string>>([]), MealClosetOwed = Known(false) });
        Assert.Equal((RefrigerationNeed, NeedTier.Wellbeing, Satisfaction.Unmet), (warm.Id, warm.Tier, warm.Satisfaction));
        Assert.True(warm.Deficit.TryGetValue(out var deficit) && deficit > 0 && deficit <= 1);

        // The same stock at the exit bar is met, and so is warm stock under the floor.
        var chilled = Refrigeration(new() { Storage = Census(Warm("a", Policy.AtRiskNutritionThreshold, Policy.ChilledExitC)), WarmRooms = Known<EquatableArray<string>>([]), MealClosetOwed = Known(false) });
        Assert.Equal(Satisfaction.Met, chilled.Satisfaction);
        var little = Refrigeration(new() { Storage = Census(Warm("a", Policy.AtRiskNutritionThreshold / 2, Policy.ChilledExitC + 2)) });
        Assert.Equal(Satisfaction.Met, little.Satisfaction);

        // A warm cooling room or an owed meal closet is unmet on a cold census.
        Assert.Equal(Satisfaction.Unmet, Refrigeration(new() { Storage = Census(), WarmRooms = Known<EquatableArray<string>>(["tomb-1"]) }).Satisfaction);
        var closet = Refrigeration(new() { Storage = Census(), MealClosetOwed = Known(true) });
        Assert.Equal(Satisfaction.Unmet, closet.Satisfaction);
        Assert.True(closet.Deficit.TryGetValue(out var owed) && owed >= 0.5);

        // An unread census, or a warm stock of unknown temperature, is unknown, never met.
        var unread = Refrigeration(new());
        Assert.Equal(Satisfaction.Unknown, unread.Satisfaction);
        Assert.False(unread.Deficit.TryGetValue(out _));
        var unmeasured = Census(new FoodStorageStock(Warm("a", Policy.AtRiskNutritionThreshold, 20).Stock with { TemperatureC = Unknown<double>() }));
        Assert.Equal(Satisfaction.Unknown, Refrigeration(new() { Storage = unmeasured }).Satisfaction);
    }

    [Fact]
    public void EachFoodStoresNeedIsDerivedOnlyWhenItsServiceIsComposed()
    {
        var facts = new FoodStoresFacts { Storage = Census() };
        Assert.Equal([FoodStorageNeed, RefrigerationNeed], new FoodStoresFamily(Policy, true, true).Derive(facts).Select(n => n.Id));
        Assert.Equal([FoodStorageNeed], new FoodStoresFamily(Policy, true, false).Derive(facts).Select(n => n.Id));
        Assert.Equal([RefrigerationNeed], new FoodStoresFamily(Policy, false, true).Derive(facts).Select(n => n.Id));
        Assert.Equal([FactFamily.Rooms, FactFamily.Colony], new FoodStoresFamily(Policy, false, true).Reads);
    }

    [Fact]
    public void TheStorageNeedIsAPlainThresholdAtTheTargetShareAndAnUnknownCensusIsUnknown()
    {
        // 85% stored: met under the old entry share, unmet now that the latch is gone.
        var short85 = Derive(new() { Storage = Census(Stock("a", 85, roofed: true), Stock("b", 15, roofed: false)), Stockpile = Known(true) });
        Assert.Equal((FoodStorageNeed, NeedTier.Wellbeing, Satisfaction.Unmet), (short85.Id, short85.Tier, short85.Satisfaction));
        Assert.True(short85.Deficit.TryGetValue(out var deficit) && deficit > 0 && deficit <= 1);

        var stored = Derive(new() { Storage = Census(Stock("a", 95, roofed: true), Stock("b", 5, roofed: false)), Stockpile = Known(true) });
        Assert.Equal(Satisfaction.Met, stored.Satisfaction);
        Assert.Equal(Known(0.0), stored.Deficit);

        var unread = Derive(new() { Stockpile = Known(true) });
        Assert.Equal(Satisfaction.Unknown, unread.Satisfaction);
        Assert.False(unread.Deficit.IsKnown);
        Assert.Equal(ServiceCoverage.Unknown, NeedCoverage(unread));
    }

    [Fact]
    public void PendingReserveAccessAMissingStockpileOrAShortReserveLeaveTheNeedUnmet()
    {
        var census = Census(Stock("a", 100, roofed: true));
        Assert.Equal(Known(1.0), Derive(new() { Storage = census, Stockpile = Known(true), Reserve = Known(Reserve("pem-1")) }).Deficit);
        Assert.Equal(Satisfaction.Unmet, Derive(new() { Storage = census, Stockpile = Known(false) }).Satisfaction);
        var refill = Derive(new() { Storage = census, Stockpile = Known(true), Reserve = Known(new FoodReserveReview { TargetNutrition = 10, DeficitNutrition = 4 }) });
        Assert.Equal((Satisfaction.Unmet, Known(0.4)), (refill.Satisfaction, refill.Deficit));
        // An unread stockpile is not missing, and pending access is unmet even with the census unread.
        Assert.Equal(Satisfaction.Met, Derive(new() { Storage = census }).Satisfaction);
        Assert.Equal(Satisfaction.Unmet, Derive(new() { Reserve = Known(Reserve("pem-1")) }).Satisfaction);
    }

    private sealed class Colony : IDisposable
    {
        public required Journal Journal { get; init; }
        public required FamilyRunner<FoodStoresFacts> Runner { get; init; }
        public FoodStoresFacts Facts { get; set; } = new();

        public void Dispose() => Journal.Dispose();

        public FamilyRun Run() =>
            Runner.RunAsync(new FamilyStep { Journal = Journal, World = World }, TestContext.Current.CancellationToken).GetAwaiter().GetResult();

        public static Verdict Report(FamilyRun run) => Assert.Single(run.Reports, r => r.Need == FoodStorageNeed).Verdict;
    }

    /// <summary>A reviewed colony whose pemmican pem-1 the reserve holds and whose forbidden corpse the larder allows.</summary>
    private static Colony Open(bool larder = true)
    {
        var ct = TestContext.Current.CancellationToken;
        var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-food-stores-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        var census = larder ? Census(Stock("pem-1", 10, roofed: true, "Pemmican"), Corpse()) : Census(Stock("pem-1", 10, roofed: true, "Pemmican"));
        var facts = WorkResourceTestFacts.Create("a", "b", "c") with
        {
            Colonists = Known(3L), IndoorCapacity = Known(3L), BedCapacity = Known(3L), FieldCoverage = Known(1.0),
            FoodStorageUpkeep = census,
            FoodReserve = Known(Reserve("pem-1")),
        };
        journal.ReviewRounds(new() { Current = World, Tick = new(100), Enabled = true, Policy = DefaultRoundsPolicy(), Facts = facts }, ct);
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
                Reserve = Known(Reserve("pem-1")),
                Stockpile = Known(true),
                StockProtection = true,
                ReserveTargets = Known<EquatableArray<SupplyAllow>>([SupplyAllow.NewSupplyForbid("pem-1", "Pemmican", new(1, 1)), new SupplyAllow("pem-2", "Pemmican", new(1, 2))]),
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
        Assert.Equal(FoodStorageNeed.FilingKey, colony.Journal.PlanMethod(method.Plan, ct)!.Concern);

        var again = colony.Run();
        Assert.Equal(BuildingRuntimePackage.BuildingReasonExistingWork, Colony.Report(again));
        Assert.Single(colony.Journal.LoadNeed(FoodStorageNeed, World, ct).Methods);
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
        colony.Journal.ReviewRounds(new() { Current = World, Tick = new(200), Enabled = true, Policy = DefaultRoundsPolicy(), Revision = colony.Journal.LoadRounds(ct).Revision,
            Facts = WorkResourceTestFacts.Create("a", "b", "c") with { Colonists = Known(3L), IndoorCapacity = Known(3L), BedCapacity = Known(3L), FieldCoverage = Known(1.0) } }, ct);
        Assert.Equal(row, Row(colony));
        colony.Journal.RecordPlannerReasons(new Dictionary<ConcernId, PlannerNote>(), [new FamilyProgress("food-stores", true, [])], ct);
        Assert.Null(Row(colony));
    }

    private static void File(Colony colony, FamilyRun run) =>
        colony.Journal.RecordPlannerReasons(new Dictionary<ConcernId, PlannerNote>(), [PlannerWave.NeedProgressOf("food-stores", run)!], TestContext.Current.CancellationToken);

    private static ConcernProgress? Row(Colony colony) =>
        colony.Journal.LoadRounds(TestContext.Current.CancellationToken).Progress.SingleOrDefault(p => p.Concern == FoodStorageNeed.FilingKey);

    [Fact]
    public void TheLarderMethodIsAdmittedBesideAnOpenReservePlan()
    {
        var ct = TestContext.Current.CancellationToken;
        using var colony = Open();
        Assert.Equal(Outcome.Admitted, Colony.Report(colony.Run()).Outcome);

        // The reserve is held now; the larder's corpse change no longer waits behind the open reserve plan.
        colony.Facts = colony.Facts with { Reserve = Known(new FoodReserveReview { TargetNutrition = 10, StockNutrition = 10 }) };
        var run = colony.Run();
        Assert.Equal(Outcome.Admitted, Colony.Report(run).Outcome);
        Assert.Equal("corpse-larder-allow-corpse-1", Assert.Single(run.Methods).Method.Value);
        Assert.Equal(2, colony.Journal.LoadNeed(FoodStorageNeed, World, ct).Methods.Count);
    }

    [Fact]
    public void AnOverlappingSupplyStackIsAClaimConflict()
    {
        var ct = TestContext.Current.CancellationToken;
        using var colony = Open(larder: false);
        var other = PlanIds.MintPlanId();
        var held = colony.Journal.CommitClaimedMethod(FoodStorageNeed, World, new("held-pem-1"), "",
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
        Assert.ThrowsAny<Exception>(() => colony.Journal.CommitClaimedMethod(FoodStorageNeed, World, new("stray"), "",
            new PlanSpec(id, new(1), [DomainAction.NewSupplyAllowAction(new(id.Value + "-0"), new SupplyAllow("pem-9", "Pemmican", new(1, 1)))]), ct));
        Assert.Empty(colony.Journal.LoadNeed(FoodStorageNeed, World, ct).Methods);
    }

    [Fact]
    public void WithoutStockProtectionOrReserveStacksTheUnmetNeedHasNoMethod()
    {
        using var colony = Open(larder: false);
        colony.Facts = colony.Facts with { StockProtection = false };
        var run = colony.Run();
        Assert.Empty(run.Methods);
        Assert.True(Colony.Report(run).Is(Cause.AwaitingPlan));

        colony.Facts = colony.Facts with { StockProtection = true, ReserveTargets = default };
        Assert.Empty(colony.Run().Methods);
    }
}
