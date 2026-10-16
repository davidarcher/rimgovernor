using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The food family (#3254): the food/cooking Need is a plain threshold over what can be cooked now, it reopens when
/// a kitchen is lost, and its building Methods are admitted under the Need owner side by side, held only by their
/// claims.
/// </summary>
public sealed class FoodFamilyTests
{
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));

    private static Need Cooking(FoodFacts facts) => Assert.Single(new FoodFamily(true).Derive(facts));

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
        Assert.Empty(new FoodFamily(false).Derive(facts));
        var family = new FoodFamily(true);
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

        public void Dispose() => Journal.Dispose();

        public FamilyRun Run() =>
            Runner.RunAsync(new FamilyStep { Journal = Journal, World = World }, TestContext.Current.CancellationToken).GetAwaiter().GetResult();

        public static Verdict Report(FamilyRun run) => Assert.Single(run.Reports, r => r.Need == CookingNeed).Verdict;
    }

    private static PlanSpec Build(string definition, Cell cell)
    {
        var id = PlanIds.MintPlanId();
        return new PlanSpec(id, new(1), [DomainAction.NewBuildingAction(new(id.Value + "-0"), new Building(definition, cell, Rotation.North, ""), PlannerTier(CookingNeed.FilingKey, Phase.None))]);
    }

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
        var runner = new FamilyRunner<FoodFacts>(new FoodFamily(true), (_, _) => Task.FromResult(colony!.Facts),
            new FamilySchedule { Kinds = [ActionKind.Building] },
            (work, _, call) =>
            {
                var verdict = Verdict.AwaitingPlan(work.Need.Id.Value, "no_method");
                foreach (var (method, cell) in colony!.Builds)
                {
                    var commit = journal.CommitOwnerWork(work.Owner, new MethodId(method), "", Build("Campfire", cell), call);
                    verdict = RoundsBuildingPlanner.OwnerCommitVerdict(commit);
                }
                return Task.FromResult(verdict);
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
}
