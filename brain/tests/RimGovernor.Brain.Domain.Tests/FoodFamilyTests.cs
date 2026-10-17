using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The food family (#3254): food/supply and food/cooking are plain thresholds (supply over its six checks, cooking over
/// what can be cooked now, reopening when a kitchen is lost), and their Methods are admitted under the Need owner side
/// by side, held only by their claims.
/// </summary>
public sealed class FoodFamilyTests
{
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));

    private static Need Cooking(FoodFacts facts) => Assert.Single(new FoodFamily(false, true).Derive(facts));

    private static Need Supply(FoodFacts facts) => Assert.Single(new FoodFamily(true, false).Derive(facts));

    /// <summary>Every supply check read and met, the runway at the season's target as <paramref name="reserve"/> says.</summary>
    private static FoodFacts SupplyFacts(bool reserve = true) => new()
    {
        HumanFoodSettled = Known(true), FootholdFood = Known(true), FootholdProduction = Known(true),
        FieldsCovered = Known(true), ButcherFacility = Known(true), ReserveCovered = Known(reserve),
    };

    [Fact]
    public void TheSupplyNeedIsAPlainThresholdOverItsChecks()
    {
        var met = Supply(SupplyFacts());
        Assert.Equal((FoodSupplyNeed, NeedTier.Wellbeing, Satisfaction.Met, Known(0.0)), (met.Id, met.Tier, met.Satisfaction, met.Deficit));

        // A runway short of the season's target reopens the Need: no latch holds it open or shut.
        var shortRunway = Supply(SupplyFacts(reserve: false));
        Assert.Equal((Satisfaction.Unmet, Known(1.0)), (shortRunway.Satisfaction, shortRunway.Deficit));
        Assert.Contains(new NeedEvidence("reserve_days", Known("short")), shortRunway.Evidence);

        // A known shortfall decides the Need while other checks are unread.
        Assert.Equal(Satisfaction.Unmet, Supply(new() { ButcherFacility = Known(false) }).Satisfaction);
        Assert.Equal(Satisfaction.Unmet, Supply(SupplyFacts() with { HumanFoodSettled = Known(false) }).Satisfaction);

        // An unread check with none short is unknown, never met.
        var unread = Supply(SupplyFacts() with { FieldsCovered = Unknown<bool>() });
        Assert.Equal((Satisfaction.Unknown, false), (unread.Satisfaction, unread.Deficit.IsKnown));
        Assert.Contains(new NeedEvidence("fields", Unknown<string>()), unread.Evidence);
    }

    [Fact]
    public void TheForecastFoodRowReadsTheSupplyNeedWithTheSameAssessmentForTheSameFacts()
    {
        var policy = DefaultRoundsPolicy();
        var facts = new RoundsFacts { Tick = new(100), Colonists = Known(2L), Fuel = Known<EquatableArray<FuelConsumer>>([]) };
        var met = facts with
        {
            FoodDays = Known(1000.0), GrowingCells = Known(10_000L), FieldCoverage = Known(1.0),
            ButcherBenches = Known<EquatableArray<ButcherBench>>([new() { Definition = "TableButcher" }]),
        };
        foreach (var f in new[] { facts, met, met with { FoodDays = Known(0.0) }, met with { ButcherBenches = Known<EquatableArray<ButcherBench>>([]) } })
        {
            var row = Assert.Single(ReviewEssentialServices(f, policy, new() { Work = new() { Tick = f.Tick } }).Coverage,
                c => c.Service == EssentialService.Food && c.Target == ServiceTarget.ForNeed(FoodSupplyNeed));
            var expected = FoodSupplyNeedOf(FoodFacts.From(f, policy)).Satisfaction switch
            {
                Satisfaction.Met => ServiceCoverage.Adequate,
                Satisfaction.Unmet => ServiceCoverage.Shortfall,
                _ => ServiceCoverage.Unknown,
            };
            Assert.Equal(expected, row.Coverage);
        }
        Assert.Equal(Satisfaction.Met, FoodSupplyNeedOf(FoodFacts.From(met, policy)).Satisfaction);
        Assert.Equal(Satisfaction.Unknown, FoodSupplyNeedOf(FoodFacts.From(facts, policy)).Satisfaction);
    }

    [Fact]
    public void TheCookingNeedIsMetWhileAMealCanBeMadeAndReopensWhenTheKitchenIsLost()
    {
        var met = Cooking(new() { Cooking = Known(true), CampfireRetireOwed = Known(false) });
        Assert.Equal((CookingNeed, NeedTier.Wellbeing, Satisfaction.Met, Known(0.0)), (met.Id, met.Tier, met.Satisfaction, met.Deficit));

        // A lost stove kitchen: nothing can be cooked on the next review, so the Need is unmet again.
        var lost = Cooking(new() { Cooking = Known(false), CampfireRetireOwed = Known(false) });
        Assert.Equal((Satisfaction.Unmet, Known(1.0)), (lost.Satisfaction, lost.Deficit));

        // A superseded cooking campfire keeps the Need unmet even while a stove cooks, and decides it unread cooking.
        Assert.Equal(Satisfaction.Unmet, Cooking(new() { Cooking = Known(true), CampfireRetireOwed = Known(true) }).Satisfaction);
        Assert.Equal(Satisfaction.Unmet, Cooking(new() { CampfireRetireOwed = Known(true) }).Satisfaction);

        // Unread cooking is unknown, never met; an unread campfire owes nothing.
        var unread = Cooking(new() { CampfireRetireOwed = Known(false) });
        Assert.Equal((Satisfaction.Unknown, false), (unread.Satisfaction, unread.Deficit.IsKnown));
        Assert.Equal(Satisfaction.Met, Cooking(new() { Cooking = Known(true) }).Satisfaction);
    }

    [Fact]
    public void TheCookingNeedIsDerivedOnlyWhenComposedAndProposesNothingPurely()
    {
        var facts = new FoodFacts { Cooking = Known(false) };
        Assert.Empty(new FoodFamily(false, false).Derive(facts));
        Assert.Equal([FoodSupplyNeed, CookingNeed], new FoodFamily(true, true).Derive(facts).Select(n => n.Id));
        var family = new FoodFamily(true, true);
        Assert.Empty(family.Propose(family.Derive(facts), facts));
    }

    [Fact]
    public void TheForecastCookingRowReadsTheNeedWithTheSameAssessmentForTheSameFacts()
    {
        var facts = new RoundsFacts { Tick = new(100), Colonists = Known(2L), Fuel = Known<EquatableArray<FuelConsumer>>([]) };
        ServiceCoverage Coverage(RoundsFacts f) => Assert.Single(
            ReviewEssentialServices(f, DefaultRoundsPolicy(), new() { Work = new() { Tick = f.Tick } }).Coverage,
            c => c.Service == EssentialService.Cooking && c.Target == ServiceTarget.ForNeed(CookingNeed)).Coverage;
        Assert.Equal(ServiceCoverage.Adequate, Coverage(facts with { Cooking = Known(true), CampfireRetireOwed = Known(false) }));
        Assert.Equal(ServiceCoverage.Shortfall, Coverage(facts with { Cooking = Known(false) }));
        Assert.Equal(ServiceCoverage.Shortfall, Coverage(facts with { Cooking = Known(true), CampfireRetireOwed = Known(true) }));
        Assert.Equal(ServiceCoverage.Unknown, Coverage(facts));
    }

    private sealed class Colony : IDisposable
    {
        public required Journal Journal { get; init; }
        public required FamilyRunner<FoodFacts> Runner { get; init; }
        public FoodFacts Facts { get; set; } = new() { Cooking = Known(false), CampfireRetireOwed = Known(false) };

        /// <summary>The building Methods the cooking step commits on the next run, each a (MethodId, cell) pair.</summary>
        public List<(string Method, Cell Cell)> Builds { get; } = [];

        /// <summary>The zone and acquisition Methods the supply step commits on the next run.</summary>
        public List<(string Method, DomainAction Action)> Supplies { get; } = [];

        public void Dispose() => Journal.Dispose();

        public FamilyRun Run() =>
            Runner.RunAsync(new FamilyStep { Journal = Journal, World = World }, TestContext.Current.CancellationToken).GetAwaiter().GetResult();

        public static Verdict Report(FamilyRun run, NeedId? need = null) => Assert.Single(run.Reports, r => r.Need == (need ?? CookingNeed)).Verdict;
    }

    private static PlanSpec Build(string definition, Cell cell)
    {
        var id = PlanIds.MintPlanId();
        return new PlanSpec(id, new(1), [DomainAction.NewBuildingAction(new(id.Value + "-0"), new Building(definition, cell, Rotation.North, ""), PlannerTier(CookingNeed.FilingKey, Phase.None))]);
    }

    private static PlanSpec Plan(DomainAction action)
    {
        var id = PlanIds.MintPlanId();
        return new PlanSpec(id, new(1), [action]);
    }

    private static DomainAction Field(string id, params (int X, int Z)[] cells) =>
        DomainAction.NewZoneCreateAction(new(id), new ZoneCreate(ZoneKind.Growing, "Plant_Rice", cells.Select(c => new Cell(c.X, c.Z))));

    private static DomainAction Forage(string id, string source) =>
        DomainAction.NewAcquisitionAction(new(id), new(source, "Plant_Berry", new(1, 1)));

    /// <summary>A reviewed colony whose food family works the cooking Need by committing the listed builds under the Need.</summary>
    private static Colony Open()
    {
        var journal = Reviewed();
        Colony? colony = null;
        var runner = new FamilyRunner<FoodFacts>(new FoodFamily(true, true), (_, _) => Task.FromResult(colony!.Facts),
            FoodRounds.Schedule,
            (work, _, call) =>
            {
                var verdicts = new List<Verdict>();
                if (work.Need.Id == CookingNeed)
                {
                    foreach (var (method, cell) in colony!.Builds)
                    {
                        verdicts.Add(RoundsBuildingPlanner.OwnerCommitVerdict(journal.CommitOwnerWork(work.Owner, new MethodId(method), "", Build("Campfire", cell), call)));
                    }
                }
                else
                {
                    foreach (var (method, action) in colony!.Supplies)
                    {
                        verdicts.Add(RoundsBuildingPlanner.OwnerCommitVerdict(journal.CommitOwnerWork(work.Owner, new MethodId(method), "", Plan(action), call)));
                    }
                }
                return Task.FromResult(verdicts.Count == 0 ? Verdict.AwaitingPlan(work.Need.Id.Value, "no_method") : verdicts[^1]);
            });
        colony = new Colony { Journal = journal, Runner = runner };
        return colony;
    }

    /// <summary>A journal holding an enabled review of <see cref="World"/> with three pawns' work resources.</summary>
    private static Journal Reviewed()
    {
        var ct = TestContext.Current.CancellationToken;
        var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-food-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        var facts = WorkResourceTestFacts.Create("a", "b", "c");
        facts = facts with
        {
            Resources = Known<EquatableArray<Amount>>([new(new("WoodLog"), 1000)]),
            Colonists = Known(3L), IndoorCapacity = Known(3L), BedCapacity = Known(3L), FieldCoverage = Known(1.0),
            WorkAdmission = facts.WorkAdmission with
            {
                Buildings = Known<EquatableArray<WorkBuildingCost>>([
                    new("Campfire", "", Known<EquatableArray<Amount>>([new(new("WoodLog"), 20)])) { Size = Known(new Cell(1, 1)) },
                    new("Cooler", "", Known<EquatableArray<Amount>>([new(new("WoodLog"), 10)])) { Size = Known(new Cell(1, 1)) }]),
            },
        };
        journal.ReviewRounds(new() { Current = World, Tick = new(100), Enabled = true, Policy = DefaultRoundsPolicy(), Facts = facts }, ct);
        return journal;
    }

    [Fact]
    public void TwoCookingBuildsAreAdmittedSideBySideAndTheSameMethodContinues()
    {
        var ct = TestContext.Current.CancellationToken;
        using var colony = Open();
        colony.Builds.AddRange([("cooking-campfire", new Cell(5, 5)), ("kitchen-stove", new Cell(9, 9))]);
        var run = colony.Run();
        Assert.Equal(Outcome.Admitted, Colony.Report(run).Outcome);
        Assert.Equal(2, run.Methods.Count);
        Assert.All(run.Methods, m => Assert.Equal((CookingNeed, ClaimOutcome.Admitted), (m.Need, m.Outcome)));
        Assert.All(run.Methods, m => Assert.Equal(CookingNeed.FilingKey, colony.Journal.PlanMethod(m.Plan, ct)!.Concern));

        // The same MethodIds again: the open plans continue, nothing new is committed.
        var again = colony.Run();
        Assert.Equal(BuildingRuntimePackage.BuildingReasonExistingWork, Colony.Report(again));
        Assert.Equal(2, colony.Journal.LoadNeed(CookingNeed, World, ct).Methods.Count);
    }

    [Fact]
    public void ABuildOnACellAnotherNeedHoldsIsAClaimConflict()
    {
        var ct = TestContext.Current.CancellationToken;
        using var colony = Open();
        var held = colony.Journal.CommitClaimedMethod(RefrigerationNeed, World, new("held-cooler"), "", Build("Cooler", new(5, 5)), ct);
        Assert.Equal(ClaimOutcome.Admitted, held.Outcome);

        colony.Builds.Add(("cooking-campfire", new Cell(5, 5)));
        var refused = Colony.Report(colony.Run());
        Assert.True(refused.Is(Cause.Claim), refused.ToString());
        Assert.Empty(colony.Journal.LoadNeed(CookingNeed, World, ct).Methods);
    }

    [Fact]
    public void TheCookingNeedReachesRoundsProgressAndIsPrunedOnceMet()
    {
        var ct = TestContext.Current.CancellationToken;
        using var colony = Open();
        colony.Builds.Add(("cooking-campfire", new Cell(5, 5)));
        File(colony, colony.Run());
        var row = Row(colony);
        Assert.NotNull(row);
        Assert.Equal(("cooking-campfire", true), (row.Method, row.Open));

        colony.Facts = colony.Facts with { Cooking = Known(true) };
        File(colony, colony.Run());
        Assert.Null(Row(colony));
    }

    private static void File(Colony colony, FamilyRun run) =>
        colony.Journal.RecordPlannerReasons(new Dictionary<ConcernId, PlannerNote>(), [PlannerWave.NeedProgressOf(FoodFamilyId, run)!], TestContext.Current.CancellationToken);

    private static ConcernProgress? Row(Colony colony) =>
        colony.Journal.LoadRounds(TestContext.Current.CancellationToken).Progress.SingleOrDefault(p => p.Concern == CookingNeed.FilingKey);

    [Fact]
    public void SupplyFieldsAndForageAreAdmittedSideBySideAndAFieldOnTheSameCellsIsAClaimConflict()
    {
        var ct = TestContext.Current.CancellationToken;
        using var colony = Open();
        colony.Facts = colony.Facts with { Cooking = Known(true) } with { FieldsCovered = Known(false) };
        // A second field batch and a forage beside the first field: no open-work gate holds them behind it.
        colony.Supplies.AddRange([
            ("fields-first", Field("z1", (10, 10), (10, 11))),
            ("fields-second", Field("z2", (12, 10), (12, 11))),
            ("acquire-berries", Forage("f1", "berry-1"))]);
        var run = colony.Run();
        Assert.Equal(3, run.Methods.Count);
        Assert.All(run.Methods, m => Assert.Equal((FoodSupplyNeed, ClaimOutcome.Admitted), (m.Need, m.Outcome)));
        Assert.Equal(3, colony.Journal.LoadNeed(FoodSupplyNeed, World, ct).Methods.Count);

        // A third batch on a cell the first holds is refused on that claim; the open Methods continue.
        colony.Supplies.Clear();
        colony.Supplies.Add(("fields-third", Field("z3", (10, 11), (10, 12))));
        var refused = Colony.Report(colony.Run(), FoodSupplyNeed);
        Assert.True(refused.Is(Cause.Claim), refused.ToString());
        Assert.Equal(3, colony.Journal.LoadNeed(FoodSupplyNeed, World, ct).Methods.Count);
    }

    [Fact]
    public void ASupplyCooldownOutlivesTheRunThatRebuildsTheNeedsProgressRow()
    {
        var ct = TestContext.Current.CancellationToken;
        using var colony = Open();
        colony.Facts = colony.Facts with { Cooking = Known(true) } with { ReserveCovered = Known(false) };
        colony.Supplies.Add(("acquire-berries", Forage("f1", "berry-1")));
        File(colony, colony.Run());
        var review = colony.Journal.LoadRounds(ct);
        colony.Journal.RecordProgressCooldown(review.Revision, FoodSupplyNeed.FilingKey, "acquire/berry-2", new(review.Tick.Value + 1000), ct);

        File(colony, colony.Run());
        var row = Assert.Single(colony.Journal.LoadRounds(ct).Progress, p => p.Concern == FoodSupplyNeed.FilingKey);
        Assert.True(row.Cooled("acquire/berry-2", review.Tick));
    }

    private static Need BabyFeedingOf(FoodFacts facts) => Assert.Single(new FoodFamily(false, false, babyFeeding: true).Derive(facts));

    private static Need BlightOf(FoodFacts facts) => Assert.Single(new FoodFamily(false, false, blight: true).Derive(facts));

    private static BlightedPlant Blighted(string id, int x, bool designated = false, bool eligible = true) =>
        new() { ID = id, Definition = "Plant_Rice", Cell = new(x, 3), Designated = designated, Eligible = eligible };

    private static FoodFacts Census(params BlightedPlant[] plants) => new() { Blight = Known<EquatableArray<BlightedPlant>>([.. plants]) };

    [Fact]
    public void TheBabyFeedingNeedIsUnmetWhileBabyFoodIsShortAndUnknownUnread()
    {
        var stocked = BabyFeedingOf(new() { BabyFeeding = Known(new BabyFeeding { Babies = 1, StockNutrition = 10, TargetNutrition = 5 }) });
        Assert.Equal((BabyFeedingNeed, NeedTier.Wellbeing, Satisfaction.Met, Known(0.0)), (stocked.Id, stocked.Tier, stocked.Satisfaction, stocked.Deficit));
        Assert.Contains(new NeedEvidence("baby_food", Known("stocked")), stocked.Evidence);

        var breastfed = BabyFeedingOf(new() { BabyFeeding = Known(new BabyFeeding { Babies = 2, Breastfed = true }) });
        Assert.Equal(Satisfaction.Met, breastfed.Satisfaction);
        Assert.Contains(new NeedEvidence("baby_food", Known("breastfed")), breastfed.Evidence);

        var shortFood = BabyFeedingOf(new() { BabyFeeding = Known(new BabyFeeding { Babies = 1, TargetNutrition = 5, IsShort = true }) });
        Assert.Equal((Satisfaction.Unmet, Known(1.0)), (shortFood.Satisfaction, shortFood.Deficit));
        Assert.Contains(new NeedEvidence("babies", Known("1")), shortFood.Evidence);

        var unread = BabyFeedingOf(new());
        Assert.Equal((Satisfaction.Unknown, false), (unread.Satisfaction, unread.Deficit.IsKnown));

        // The bill is the work ledger's: the family proposes nothing for the Need.
        var family = new FoodFamily(false, false, babyFeeding: true);
        Assert.Empty(family.Propose([shortFood], new() { BabyFeeding = Known(new BabyFeeding { IsShort = true }) }));
    }

    [Fact]
    public void TheBlightNeedIsUnmetWhileABlightedPlantStandsAndUnknownUnread()
    {
        var clear = BlightOf(Census());
        Assert.Equal((BlightNeed, NeedTier.Wellbeing, Satisfaction.Met, Known(0.0)), (clear.Id, clear.Tier, clear.Satisfaction, clear.Deficit));

        // A designated plant still stands: the Need is met by the cut, not the order.
        var standing = BlightOf(Census(Blighted("plant-1", 1, designated: true)));
        Assert.Equal((Satisfaction.Unmet, Known(1.0)), (standing.Satisfaction, standing.Deficit));
        Assert.Contains(new NeedEvidence("blighted_plants", Known("1")), standing.Evidence);

        var unread = BlightOf(new());
        Assert.Equal((Satisfaction.Unknown, false), (unread.Satisfaction, unread.Deficit.IsKnown));
        Assert.Equal([FoodSupplyNeed, CookingNeed, BabyFeedingNeed, BlightNeed], new FoodFamily(true, true, true, true).Derive(new()).Select(n => n.Id));
    }

    [Fact]
    public void TheBlightNeedProposesOneStableBatchOfEligibleUndesignatedUnrefusedPlants()
    {
        var family = new FoodFamily(false, false, blight: true);
        IReadOnlyList<NeedMethod> Propose(FoodFacts f) => family.Propose([.. family.Derive(f).Where(n => n.Satisfaction == Satisfaction.Unmet)], f);
        var plants = Enumerable.Range(0, 12).Select(i => Blighted($"plant-{i:D2}", i)).ToList();
        plants.Add(Blighted("plant-designated", 20, designated: true));
        plants.Add(Blighted("plant-ineligible", 21, eligible: false));
        var facts = Census([.. plants]) with { BlightRefused = ["plant-00"] };

        var method = Assert.Single(Propose(facts));
        Assert.Null(method.Hold);
        Assert.StartsWith("cut-", method.Method.Value, StringComparison.Ordinal);
        var plan = method.Plan(new PlanId("plan-1"));
        var cuts = plan.Actions.Select(a => a.CutPlant!.Plant).ToList();
        Assert.Equal(Enumerable.Range(1, FoodFamily.BlightCutLimit).Select(i => $"plant-{i:D2}"), cuts);
        Assert.Equal("plan-1-0", plan.Actions[0].Id.Value);

        // The same census proposes the same MethodId, so an open batch continues; another set is another batch.
        Assert.Equal(method.Method, Assert.Single(Propose(Census([.. plants]) with { BlightRefused = ["plant-00"] })).Method);
        Assert.NotEqual(method.Method, Assert.Single(Propose(Census([.. plants]))).Method);

        // Only designated plants left: their cuts are under way, nothing more to propose.
        Assert.Empty(Propose(Census(Blighted("plant-1", 1, designated: true), Blighted("plant-2", 2, eligible: false))));

        // Nothing designated and nothing eligible: the Need is held.
        var held = Assert.Single(Propose(Census(Blighted("plant-2", 2, eligible: false))));
        Assert.Equal(new NeedHold("blight_cut_targets", "no_eligible_plant"), held.Hold);

        Assert.Empty(Propose(Census()));
    }

    [Fact]
    public void BlightBatchesAreAdmittedSideBySideAndAnOverlappingBatchIsAClaimConflict()
    {
        var ct = TestContext.Current.CancellationToken;
        var journal = Reviewed();
        FoodFacts facts = Census(Blighted("plant-1", 1), Blighted("plant-2", 2));
        var runner = new FamilyRunner<FoodFacts>(new FoodFamily(false, false, blight: true), (_, _) => Task.FromResult(facts),
            FoodRounds.Schedule, (work, _, _) => Task.FromResult(Verdict.AwaitingPlan(work.Need.Id.Value, "no_method")));
        using var colony = new Colony { Journal = journal, Runner = runner };

        var first = colony.Run();
        Assert.Equal(Outcome.Admitted, Colony.Report(first, BlightNeed).Outcome);
        var batch = Assert.Single(first.Methods);
        Assert.Equal(BlightNeed.FilingKey, journal.PlanMethod(batch.Plan, ct)!.Concern);

        // The same census again continues the open batch.
        Assert.Equal(BuildingRuntimePackage.BuildingReasonExistingWork, Colony.Report(colony.Run(), BlightNeed));
        Assert.Single(journal.LoadNeed(BlightNeed, World, ct).Methods);

        // The first batch's plants designated, a new plant is a second batch beside it: no whole-goal gate holds it.
        facts = Census(Blighted("plant-1", 1, designated: true), Blighted("plant-2", 2, designated: true), Blighted("plant-3", 3));
        Assert.Equal(Outcome.Admitted, Colony.Report(colony.Run(), BlightNeed).Outcome);
        Assert.Equal(2, journal.LoadNeed(BlightNeed, World, ct).Methods.Count);

        // A batch naming a plant an open batch still holds is refused on that plant's claim.
        facts = Census(Blighted("plant-2", 2), Blighted("plant-4", 4));
        var refused = Colony.Report(colony.Run(), BlightNeed);
        Assert.True(refused.Is(Cause.Claim), refused.ToString());
        Assert.Equal(2, journal.LoadNeed(BlightNeed, World, ct).Methods.Count);
        Assert.Empty(BlightRefusedOf(journal));
    }

    [Fact]
    public void ANeedPastTwoHundredFiftySixReleasedAttemptsStillRunsAndNumbersItsNextAttempt()
    {
        var ct = TestContext.Current.CancellationToken;
        var journal = Reviewed();
        FoodFacts facts = Census(Blighted("plant-1", 1));
        var family = new FoodFamily(false, false, blight: true);
        var batch = Assert.Single(family.Propose([.. family.Derive(facts)], facts)).Method;
        var runner = new FamilyRunner<FoodFacts>(family, (_, _) => Task.FromResult(facts),
            FoodRounds.Schedule, (work, _, _) => Task.FromResult(Verdict.AwaitingPlan(work.Need.Id.Value, "no_method")));
        using var colony = new Colony { Journal = journal, Runner = runner };

        // 300 attempts committed and released under the Need: every third the batch's MethodId again, the rest each
        // its own MethodId.
        for (var i = 0; i < 300; i++)
        {
            _ = CommitReleased(journal, i % 3 == 0 ? batch : new MethodId($"forage-{i}"), i);
        }
        var owner = journal.LoadNeed(BlightNeed, World, ct);
        Assert.Empty(owner.Methods);
        var history = journal.OwnerMethodHistory(owner, ct);
        Assert.Equal(201, history.Count);
        Assert.Single(history, m => m.Method == batch);

        // The family run budgets and commits the batch as its 101st attempt.
        var run = colony.Run();
        Assert.Equal(Outcome.Admitted, Colony.Report(run, BlightNeed).Outcome);
        var admitted = Assert.Single(run.Methods);
        Assert.Equal(admitted.Plan, journal.OwnerMethodPlan(owner, batch, ct));
        Assert.Equal(101L, Assert.Single(journal.LoadNeed(BlightNeed, World, ct).Methods).Attempt);
        var latest = journal.OwnerMethodHistory(owner, ct);
        Assert.Equal(201, latest.Count);
        Assert.Equal((batch, admitted.Plan), (latest[^1].Method, latest[^1].Plan));
    }

    [Fact]
    public void ANeedsHistoryIsTheLatestAttemptOfEachOfItsMostRecentlyBoundMethodIds()
    {
        var ct = TestContext.Current.CancellationToken;
        using var journal = Reviewed();
        var first = CommitReleased(journal, new("forage-first"), 0);
        var once = CommitReleased(journal, new("forage-1"), 1);
        for (var i = 2; i <= 256; i++)
        {
            _ = CommitReleased(journal, new($"forage-{i}"), i);
        }
        var again = CommitReleased(journal, new("forage-1"), 300);
        var owner = journal.LoadNeed(BlightNeed, World, ct);
        // 257 MethodIds: the oldest falls outside the window, and forage-1 is its re-issued attempt, the newest row.
        var history = journal.OwnerMethodHistory(owner, ct);
        Assert.Equal(256, history.Count);
        Assert.DoesNotContain(history, m => m.Plan == first || m.Plan == once);
        Assert.Equal((new MethodId("forage-1"), again), (history[^1].Method, history[^1].Plan));
        Assert.Equal(new MethodId("forage-2"), history[0].Method);
        // The once-check reads any MethodId's latest attempt, inside the window or not.
        Assert.Equal(first, journal.OwnerMethodPlan(owner, new("forage-first"), ct));
        Assert.Equal(again, journal.OwnerMethodPlan(owner, new("forage-1"), ct));
    }

    /// <summary>
    /// Commits the blight cut of plant-1 as <paramref name="method"/> under the blight Need and cancels it; every
    /// hundredth commit a review retires the cancelled plans, as reviews do in play. The <c>need_methods</c> rows stay.
    /// </summary>
    private static PlanId CommitReleased(Journal journal, MethodId method, int i)
    {
        var ct = TestContext.Current.CancellationToken;
        var facts = Census(Blighted("plant-1", 1));
        var family = new FoodFamily(false, false, blight: true);
        var plan = Assert.Single(family.Propose([.. family.Derive(facts)], facts)).Plan(PlanIds.MintPlanId());
        Assert.Equal(ClaimOutcome.Admitted, journal.CommitClaimedMethod(BlightNeed, World, method, "", plan, ct).Outcome);
        foreach (var action in plan.Actions)
        {
            journal.Cancel(plan.Id, action.Id, ct);
        }
        if (i % 100 == 99)
        {
            var tick = new Tick(200 + i);
            journal.ReviewRounds(new()
            {
                Revision = journal.LoadRounds(ct).Revision, Current = World, Tick = tick, Enabled = true, Policy = DefaultRoundsPolicy(),
                Facts = WorkResourceTestFacts.Create("a", "b", "c") with { Tick = tick },
            }, ct);
        }
        return plan.Id;
    }

    private static EquatableArray<string> BlightRefusedOf(Journal journal) =>
        FoodRounds.BlightRefused(journal, World, TestContext.Current.CancellationToken);
}
