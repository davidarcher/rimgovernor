using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The home-upkeep family (#3253): repairs and clearance are Needs with pure met, unmet and unknown predicates;
/// clearance steps are Need-owned Methods admitted by claims, side by side when independent.
/// </summary>
public sealed class HomeUpkeepFamilyTests
{
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));

    private static UpkeepStructure Structure(string id, long hp, long max = 100, bool home = true, int priority = 1) =>
        new() { Id = id, Cell = new(1, 1), Home = home, HitPoints = hp, MaxHitPoints = max, Priority = priority };

    private static Need Derive(HomeUpkeepFacts facts, NeedId id) => Assert.Single(new HomeUpkeepFamily().Derive(facts), n => n.Id == id);

    [Fact]
    public void RepairsIsMetUnmetOrUnknownByTheDamagedHomeStructures()
    {
        var met = Derive(new HomeUpkeepFacts { Structures = Known<EquatableArray<UpkeepStructure>>([Structure("whole", 100), Structure("outside", 10, home: false)]) }, HomeUpkeepFamily.Repairs);
        Assert.Equal((Satisfaction.Met, 0.0, NeedTier.Wellbeing), (met.Satisfaction, met.Deficit.Value, met.Tier));

        var unmet = Derive(new HomeUpkeepFacts { Structures = Known<EquatableArray<UpkeepStructure>>([Structure("wall", 60), Structure("door", 90)]) }, HomeUpkeepFamily.Repairs);
        Assert.Equal((Satisfaction.Unmet, 50.0), (unmet.Satisfaction, unmet.Deficit.Value));
        Assert.Contains(unmet.Evidence, e => e.Fact == "damaged_structures" && e.Value.Value == "2");

        var unknown = Derive(new HomeUpkeepFacts(), HomeUpkeepFamily.Repairs);
        Assert.Equal(Satisfaction.Unknown, unknown.Satisfaction);
        Assert.False(unknown.Deficit.IsKnown);
    }

    [Fact]
    public void DamagedStructuresRankByPriorityThenHealthAndRefuseAnInvalidRow()
    {
        var (targets, missing) = HomeUpkeepFamily.DamagedStructures([Structure("b", 50, priority: 2), Structure("c", 80), Structure("a", 20)]);
        Assert.Equal<string>(["a", "c", "b"], targets);
        Assert.Equal(150.0, missing);
        Assert.Throws<DomainException>(() => HomeUpkeepFamily.DamagedStructures([Structure("x", 120)]));
        Assert.Throws<DomainException>(() => HomeUpkeepFamily.DamagedStructures([Structure("x", 10), Structure("x", 20)]));
    }

    [Fact]
    public void ClearanceIsMetUnmetOrUnknownByTheCensusAndPlannedGround()
    {
        var held = new ClearanceTarget { EntityId = "ruin", Deconstructible = false };
        var met = Derive(new HomeUpkeepFacts { Clearance = Known<EquatableArray<ClearanceTarget>>([held]) }, HomeUpkeepFamily.Clearance);
        Assert.Equal((Satisfaction.Met, NeedTier.Development), (met.Satisfaction, met.Tier));

        var unmet = Derive(new HomeUpkeepFacts
        {
            Clearance = Known<EquatableArray<ClearanceTarget>>([held, new ClearanceTarget { EntityId = "wall", Deconstructible = true }]),
            Ground = Known<EquatableArray<string>>(["floor"]),
        }, HomeUpkeepFamily.Clearance);
        Assert.Equal((Satisfaction.Unmet, 2.0), (unmet.Satisfaction, unmet.Deficit.Value));

        Assert.Equal(Satisfaction.Unknown, Derive(new HomeUpkeepFacts { Ground = Known<EquatableArray<string>>(["floor"]) }, HomeUpkeepFamily.Clearance).Satisfaction);
    }

    [Fact]
    public void RepairsAndAWallSwapAreHeldWhileOtherClearanceIsProposed()
    {
        var family = new HomeUpkeepFamily();
        var ready = new[]
        {
            new Need { Id = HomeUpkeepFamily.Repairs, Tier = NeedTier.Wellbeing, Satisfaction = Satisfaction.Unmet },
            new Need { Id = HomeUpkeepFamily.Clearance, Tier = NeedTier.Development, Satisfaction = Satisfaction.Unmet },
        };
        var swap = family.Propose(ready, new HomeUpkeepFacts { Candidate = Step("swap-door-d", "d", wall: true) });
        Assert.Equal([HomeUpkeepFamily.UnsupportedRepair, HomeUpkeepFamily.UnsupportedWallReplacement], swap.Select(m => m.Hold!.Subject));

        var cut = Assert.Single(family.Propose(ready, new HomeUpkeepFacts { Candidate = Step("deconstruct-a", "a") }), m => m.Hold is null);
        Assert.Equal((HomeUpkeepFamily.Clearance, "deconstruct-a"), (cut.Need, cut.Method.Value));

        // Clearance no longer waits on repairs: the old hold only labelled the progress strip.
        Assert.Empty(new HomeUpkeepFamily().Derive(new HomeUpkeepFacts()).SelectMany(n => n.Preconditions));
    }

    private static ClearanceCandidate Step(string method, string target, int x = 5, bool wall = false) =>
        new(new MethodId(method), wall, id =>
        {
            var value = new Deconstruction(target, "Wall", new(x, 5));
            return [DomainAction.NewDeconstructionAction(new(id.Value + "-0"), wall ? value.WithWallReplacement() : value)];
        });

    private sealed class Colony : IDisposable
    {
        public required Journal Journal { get; init; }
        public required FamilyRunner<HomeUpkeepFacts> Runner { get; init; }
        public required RoundsRequest Review { get; init; }
        public HomeUpkeepFacts Facts { get; set; } = new();

        /// <summary>Files a run's Need progress as the scheduler does after a wave.</summary>
        public void File(FamilyRun run) =>
            Journal.RecordPlannerReasons(new Dictionary<ConcernId, PlannerNote>(), [PlannerWave.NeedProgressOf("home-upkeep", run)!], TestContext.Current.CancellationToken);

        public ConcernProgress? Row(NeedId need) => Journal.LoadRounds(TestContext.Current.CancellationToken).Progress.SingleOrDefault(p => p.Concern == need.FilingKey);

        public void Dispose() => Journal.Dispose();

        public FamilyRun Run() =>
            Runner.RunAsync(new FamilyStep { Journal = Journal, World = World }, TestContext.Current.CancellationToken).GetAwaiter().GetResult();

        public static Verdict Report(FamilyRun run, NeedId need) => Assert.Single(run.Reports, r => r.Need == need).Verdict;
    }

    private static Colony Open()
    {
        // The host registers the intent-mode kinds at startup; without it a refused deconstruction would stay
        // pending, and the result would depend on which test registered them first in this process.
        BridgeFunctions.RegisterIntentKinds();
        var ct = TestContext.Current.CancellationToken;
        var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-home-upkeep-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        var facts = WorkResourceTestFacts.Create("a", "b", "c");
        facts = facts with { Colonists = Known(3L), IndoorCapacity = Known(3L), BedCapacity = Known(3L), FieldCoverage = Known(1.0) };
        var review = new RoundsRequest { Current = World, Tick = new(100), Enabled = true, Policy = DefaultRoundsPolicy(), Facts = facts };
        journal.ReviewRounds(review, ct);
        Colony? colony = null;
        var runner = new FamilyRunner<HomeUpkeepFacts>(new HomeUpkeepFamily(), (_, _) => Task.FromResult(colony!.Facts),
            new FamilySchedule { Kinds = [ActionKind.Deconstruction] });
        colony = new Colony { Journal = journal, Runner = runner, Review = review };
        return colony;
    }

    private static HomeUpkeepFacts Unmet(ClearanceCandidate? step) => new()
    {
        Structures = Known<EquatableArray<UpkeepStructure>>([Structure("wall", 40)]),
        Clearance = Known<EquatableArray<ClearanceTarget>>([new ClearanceTarget { EntityId = "a", Deconstructible = true }]),
        Candidate = step,
    };

    [Fact]
    public void RepairsIsHeldAndTheClearanceStepIsAdmittedUnderItsNeed()
    {
        var ct = TestContext.Current.CancellationToken;
        using var colony = Open();
        colony.Facts = Unmet(Step("deconstruct-a", "a"));
        var run = colony.Run();

        var repairs = Colony.Report(run, HomeUpkeepFamily.Repairs);
        Assert.True(repairs.Is(Cause.AwaitingPlan));
        Assert.Equal(HomeUpkeepFamily.UnsupportedRepair, repairs.Refusal.Subject);
        Assert.Empty(colony.Journal.LoadNeed(HomeUpkeepFamily.Repairs, World, ct).Methods);

        Assert.Equal(Outcome.Admitted, Colony.Report(run, HomeUpkeepFamily.Clearance).Outcome);
        var method = Assert.Single(run.Methods);
        Assert.Equal("deconstruct-a", method.Method.Value);
        Assert.Equal(HomeUpkeepFamily.Clearance.FilingKey, colony.Journal.PlanMethod(method.Plan, ct)!.Concern);
        Assert.Equal("Clear home obstructions: deconstruct a", OperationIntent.Of(colony.Journal.PlanMethod(method.Plan, ct)!));

        // The same step continues its open Method.
        var again = colony.Run();
        Assert.Equal(BuildingRuntimePackage.BuildingReasonExistingWork, Colony.Report(again, HomeUpkeepFamily.Clearance));
        Assert.Equal(ClaimOutcome.Continued, Assert.Single(again.Methods).Outcome);
    }

    [Fact]
    public void IndependentStepsRunSideBySideAndAnOverlappingOneIsHeldByItsClaim()
    {
        var ct = TestContext.Current.CancellationToken;
        using var colony = Open();
        colony.Facts = Unmet(Step("deconstruct-a", "a", x: 5));
        Assert.Equal(Outcome.Admitted, Colony.Report(colony.Run(), HomeUpkeepFamily.Clearance).Outcome);

        colony.Facts = Unmet(Step("deconstruct-b", "b", x: 9));
        Assert.Equal(Outcome.Admitted, Colony.Report(colony.Run(), HomeUpkeepFamily.Clearance).Outcome);
        Assert.Equal(2, colony.Journal.LoadNeed(HomeUpkeepFamily.Clearance, World, ct).Methods.Count);

        // Another step on a's cell overlaps the open Method's claim and is refused with nothing written.
        colony.Facts = Unmet(Step("deconstruct-c", "c", x: 5));
        var held = Colony.Report(colony.Run(), HomeUpkeepFamily.Clearance);
        Assert.True(held.Is(Cause.Claim));
        Assert.Equal("cell/5/5", held.Refusal.Subject);
        Assert.Equal(2, colony.Journal.LoadNeed(HomeUpkeepFamily.Clearance, World, ct).Methods.Count);
    }

    [Fact]
    public void ARefusedStepSpendsItsRetryBudgetBeforeItIsAdmittedAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        using var colony = Open();
        colony.Facts = Unmet(Step("deconstruct-a", "a"));
        var first = Assert.Single(colony.Run().Methods);
        var action = new ActionId(first.Plan.Value + "-0");
        var scope = World with { Plan = first.Plan };
        colony.Journal.Prepare(first.Plan, action, scope, new(110), ct);
        var sent = colony.Journal.Dispatch(first.Plan, action, scope, new(110), ct);
        colony.Journal.RecordReceipts([new BatchReceipt(first.Plan, action, sent.View.Attempt, Receipt.Refused)
        {
            Refusal = new NativeRefusal("forbidden", "not deconstructible", RefusalClass.Permanent),
        }], ct);

        // Deconstruction is intent-mode (as the host registers it): the refused receipt ends the attempt, so the
        // Method is no longer open work and the permanent refusal on record spends the step's budget instead of
        // admitting it again.
        Assert.Equal(Stage.Unsuccessful, colony.Journal.LoadPlan(first.Plan, ct).Progress[0].View.Stage);
        var refused = Colony.Report(colony.Run(), HomeUpkeepFamily.Clearance);
        Assert.True(refused.Is(Cause.RetriesSpent));
        Assert.Equal("deconstruct-a", refused.Refusal.Subject);
    }

    [Fact]
    public void AReadyNeedWithoutAStepWaitsOnItsOpenWorkOrHasNoMethod()
    {
        using var colony = Open();
        colony.Facts = Unmet(null);
        var idle = Colony.Report(colony.Run(), HomeUpkeepFamily.Clearance);
        Assert.True(idle.Is(Cause.AwaitingPlan));
        Assert.Equal("no_method", idle.Refusal.Detail);

        colony.Facts = Unmet(Step("deconstruct-a", "a"));
        _ = colony.Run();
        colony.Facts = Unmet(null);
        Assert.Equal(BuildingRuntimePackage.BuildingReasonExistingWork, Colony.Report(colony.Run(), HomeUpkeepFamily.Clearance));
    }

    [Fact]
    public void NeedRowsReachRoundsProgressWithTheirStatusAndSurviveTheReviewRebuild()
    {
        var ct = TestContext.Current.CancellationToken;
        using var colony = Open();
        colony.Facts = Unmet(Step("deconstruct-a", "a"));
        colony.File(colony.Run());

        var repairs = colony.Row(HomeUpkeepFamily.Repairs)!;
        Assert.Equal((BlockedReason.FromCause(Cause.AwaitingPlan), Cause.AwaitingPlan, HomeUpkeepFamily.UnsupportedRepair, ""),
            (repairs.Blocked, repairs.Planner, repairs.PlannerSubject, repairs.Method));
        var clearance = colony.Row(HomeUpkeepFamily.Clearance)!;
        Assert.Equal(("deconstruct-a", true), (clearance.Method, clearance.Open));
        Assert.True(clearance.Blocked.IsEmpty);

        // The review rebuild keeps the family's rows as they stand.
        colony.Journal.ReviewRounds(colony.Review with { Tick = new(200), Revision = colony.Journal.LoadRounds(ct).Revision }, ct);
        Assert.Equal(repairs, colony.Row(HomeUpkeepFamily.Repairs));
        Assert.Equal(clearance, colony.Row(HomeUpkeepFamily.Clearance));
    }

    [Fact]
    public void ANeedRowIsPrunedWhenItsNeedIsMetOrItsFamilyStopsReportingIt()
    {
        var ct = TestContext.Current.CancellationToken;
        using var colony = Open();
        colony.Facts = Unmet(Step("deconstruct-a", "a"));
        colony.File(colony.Run());
        Assert.NotNull(colony.Row(HomeUpkeepFamily.Repairs));

        // Repairs met: its row goes, clearance's stays.
        colony.Facts = Unmet(Step("deconstruct-a", "a")) with { Structures = Known<EquatableArray<UpkeepStructure>>([Structure("wall", 100)]) };
        colony.File(colony.Run());
        Assert.Null(colony.Row(HomeUpkeepFamily.Repairs));
        Assert.NotNull(colony.Row(HomeUpkeepFamily.Clearance));

        // A partial run (the immediate pass) prunes nothing; a complete run that no longer derives a Need does.
        colony.Journal.RecordPlannerReasons(new Dictionary<ConcernId, PlannerNote>(), [new FamilyProgress("home-upkeep", false, [])], ct);
        Assert.NotNull(colony.Row(HomeUpkeepFamily.Clearance));
        colony.Journal.RecordPlannerReasons(new Dictionary<ConcernId, PlannerNote>(), [new FamilyProgress("home-upkeep", true, [])], ct);
        Assert.Null(colony.Row(HomeUpkeepFamily.Clearance));
    }

    [Fact]
    public void TheFamilyIsOneDueQueueEntryWakingOnItsReadsAndMethodKinds()
    {
        var entry = ClockPlannerCatalog.FamilyEntry(HomeUpkeepReaderRunner());
        Assert.Equal("home-upkeep", entry.Name);
        Assert.Contains(ActionKind.Deconstruction, entry.Kinds);
        Assert.Contains(ActionKind.RemoveRoof, entry.Kinds);
        Assert.Contains(RimGovernor.Brain.Facts.Section.Colony, entry.Sections);
        Assert.Contains(RimGovernor.Brain.Facts.Section.Rooms, entry.Sections);
        Assert.DoesNotContain(ClockPlannerCatalog.Entries, e => e.Name is "repair" or "clearance");
        Assert.False(entry.Immediate);
    }

    private static FamilyRunner<HomeUpkeepFacts> HomeUpkeepReaderRunner() =>
        new(new HomeUpkeepFamily(), (_, _) => Task.FromResult(new HomeUpkeepFacts()), HomeUpkeepReader.Schedule);

    [Fact]
    public void RoofAndFloorRemovalClaimTheirCells()
    {
        var plan = new PlanId("p");
        var roof = DomainAction.NewRemoveRoofAction(new("p-0"), new RemoveRoof([new Cell(2, 3)]));
        var floor = DomainAction.NewFloorRemovalAction(new("p-1"), new FloorRemoval("WoodPlankFloor", new(4, 5)));
        var claims = MethodClaimsOf(new WorkAdmissionEvidence(),
            [new WorkAction(plan, roof, new ProgressView { Action = roof.Id, Stage = Stage.Pending }), new WorkAction(plan, floor, new ProgressView { Action = floor.Id, Stage = Stage.Pending })]);
        Assert.Contains("roof/2/3", claims.Targets);
        Assert.Contains("floor/4/5", claims.Targets);
    }
}
