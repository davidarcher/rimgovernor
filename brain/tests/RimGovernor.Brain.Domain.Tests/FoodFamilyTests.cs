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
            ButcherBenches = Known<EquatableArray<ButcherBench>>([new() { Id = "bench", Definition = "TableButcher" }]),
        };
        foreach (var f in new[] { facts, met, met with { FoodDays = Known(0.0) }, met with { ButcherBenches = Known<EquatableArray<ButcherBench>>([]) } })
        {
            var row = Assert.Single(ReviewEssentialServices(f, policy, new() { Work = new() { Tick = f.Tick } }, []).Coverage,
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
            ReviewEssentialServices(f, DefaultRoundsPolicy(), new() { Work = new() { Tick = f.Tick } }, []).Coverage,
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
}
