using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Common;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ForecastCoupledSelectionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ActualHostCapabilitiesEnableOrdinarySelectionByDefaultAndHonorDisabledWork(bool work)
    {
        Assert.True(new RimGovernor.Brain.Host.BrainFamilies().Work);
        var (_, capabilities) = RimGovernor.Brain.Host.BrainComposition.RoundsCapabilitiesFor(
            new() { Work = work }, false);
        var facts = Review() with { AvailableNeeds = Known(EquatableArray.From(capabilities.Needs)) };
        var generated = ForecastCoupledCandidates.GenerateCoupledActivities(facts, Horizon);
        if (work) Assert.NotEmpty(Assert.IsType<ForecastCoupledCandidates>(generated).Alternatives);
        else Assert.Null(generated);
    }

    private const double Horizon = 300;
    private static readonly string[] Plants = ["plant-a", "plant-b"];
    private static readonly ObservationContext Context = new()
        { Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 100 };
    internal static RoundsFacts Review(double level = .2, double fall = .003, double harvest = 10, double expiry = 1000,
        bool reverse = false, bool unknownHarvest = false, int sourceCount = 2, double horizon = Horizon)
    {
        var facts = WorkResourceTestFacts.Create("pawn");
        var sources = Plants.Take(sourceCount).ToArray();
        if (reverse) Array.Reverse(sources);
        facts = facts with
        {
            Resources = Known<EquatableArray<RimGovernor.Brain.Policy.Amount>>([]),
            ForecastObserved = facts.ForecastObserved with
            {
                Needs = NeedsFixtures.Empty with { Tick = new(100), Pawns = [NeedsFixtures.Pawn() with
                {
                    Pawn = "pawn", State = "eligible", Mood = NeedsFixtures.Mood with { Level = .5, Target = .5, UnclampedTarget = .5, RisePerTick = .12 / GameTime.TicksPerHour, FallPerTick = .08 / GameTime.TicksPerHour, FreezesWhileAsleep = true, BreakThreshold = .3, Conditional = new ConditionalMood.Modeled([]), Memories = [] }, LocalHour = 6d, Awake = true,
                    Needs = [NeedsFixtures.Rate("food", level, fall, .3),
                        NeedsFixtures.Rate("rest", 1d, 0d, .3)],
                }] },
                Coupled = new()
                {
                    Research = ActivityContinuationFixtures.NoResearch, Hunt = HuntFixtures.NoHunt, Clothing = ClothingFixtures.NoClothing, Construction = ConstructionFixtures.NoConstruction(),
                    Tick = 100,
                    Food = new() { Options = EquatableArray.From(sources.Select(source =>
                        new FoodActivityOption("pawn", source, "Rice", new(1, 1), new(1, 1), "Harvest", 2, harvest)
                        { ApproachTicks = Known(0d), DeliveryTicks = Known(10d), HarvestAllowed = unknownHarvest ? default : Known(true),
                            SourceCount = Known(2d) })) },
                    FoodUses = [new("pawn", "Rice", new(1, 1), Known(true), Known(.2), Known(10d), 2, Known(expiry)) { Thoughts = [] }],
                },
            },
        };
        facts = WorkRules.ReviewWorkResources(facts, Known<EquatableArray<ProductionBatch>>([]), new([]));
        return facts with { CoupledAlternatives = ForecastCoupledCandidates.GenerateCoupledActivities(facts, horizon)?.Priced() };
    }
    private static string SourceTask(RoundsFacts facts, string target) => facts.CoupledAlternatives!.Context.Activities.Activities
        .Single(t => t.Inputs.Count == 0 && t.Outputs.Any(o => o.Lot == "harvest/" + target)).Id;

    [Fact]
    public void TwoParticipantsCannotSelectTheSameObservedServiceTarget()
    {
        var facts = Review();
        var roster = WorkResourceTestFacts.Create("pawn", "helper");
        var pawn = facts.ForecastObserved.Needs!.Pawns[0] with
        {
            Location = new(1, 1),
            Needs = [NeedsFixtures.Rate("joy", .2, .003, .3)],
            Targets = [NeedsFixtures.Service("shared-service", "joy", 0d, .01) with
                { Location = new(1, 1) }]
        };
        facts = facts with { WorkResources = roster.WorkResources, ForecastObserved = facts.ForecastObserved with
        {
            WorkPawns = roster.ForecastObserved.WorkPawns,
            Needs = facts.ForecastObserved.Needs with { Pawns = [pawn, pawn with { Pawn = "helper" }] },
            Coupled = facts.ForecastObserved.Coupled! with { Food = facts.ForecastObserved.Coupled.Food with { Options = [] }, FoodUses = [] }
        } };
        facts = WorkRules.ReviewWorkResources(facts, Known<EquatableArray<ProductionBatch>>([]), new([]));
        facts = facts with { CoupledAlternatives = ForecastCoupledCandidates.GenerateCoupledActivities(facts, Horizon)?.Priced() };
        var selected = Select(facts);
        Assert.NotEmpty(selected.Selected.Plan.Allocations);
        Assert.Single(selected.Selected.Plan.Allocations.Select(a => a.Pawn).Distinct());
        Assert.Contains(selected.Selection.Findings, f => f.StartsWith("conflicting_selected_target_owners:", StringComparison.Ordinal));
        var batch = ForecastCoupledSelection.StartCoupledActivity(selected, facts.CoupledAlternatives!, 1, "service-method", 100);
        Assert.NotNull(batch);
        Assert.True(WorkRules.WorkResourcesFit(facts.WorkView!, new(new("selected-service"), new(1),
            [DomainAction.NewPawnActivitiesAction(new("service-action"), batch)])));
    }
    private static ForecastCoupledSelection Select(RoundsFacts facts, ForecastSearchBudget? budget = null, params ForecastCommitment[] incumbent)
    {
        var request = ForecastCoupled.CoupledForecastRequestOf(new("world/map/load", 100), Horizon / GameTime.TicksPerDay,
            facts,  new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, EquatableArray.From(incumbent), new());
        return ForecastCoupledSelection.CompareActivityOutcomes(request, facts.CoupledAlternatives!, budget);
    }
    private static readonly string[] NoSuchRoot = ["no/such/root"];
    private static ForecastCoupledSelection SelectWarm(RoundsFacts facts, IReadOnlyList<string> warmStart, ForecastSearchBudget? budget = null)
    {
        var request = ForecastCoupled.CoupledForecastRequestOf(new("world/map/load", 100), Horizon / GameTime.TicksPerDay,
            facts, new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, [], new());
        return ForecastCoupledSelection.CompareActivityOutcomes(request, facts.CoupledAlternatives!, budget, warmStart);
    }

    // #3723: the losing-stage hook. Evidence names the comparator stage a subset lost at; a floor loss carries no vector.
    [Fact]
    public void EvidenceNamesTheStageASubsetLostAtAndWhetherItWasAssessed()
    {
        var floor = new ForecastPortfolioEvidence(["a"], null, ForecastPortfolioEvidence.LostToLeaderPrefix + ForecastCoupledSelection.UrgentZeroStage);
        Assert.Equal(ForecastCoupledSelection.UrgentZeroStage, ForecastPortfolioEvidence.StageOf(floor.Reason));
        Assert.True(floor.Assessed);
        var infeasible = new ForecastPortfolioEvidence(["a"], null, ForecastPortfolioEvidence.InfeasibleOrUnassessed);
        Assert.Null(ForecastPortfolioEvidence.StageOf(infeasible.Reason));
        Assert.False(infeasible.Assessed);
        var unsupported = new ForecastPortfolioEvidence(["a"], null, "construction_without_supported_service_relief:relief=0");
        Assert.Null(ForecastPortfolioEvidence.StageOf(unsupported.Reason));
        Assert.True(unsupported.Assessed);

        // On a review: every ranked loser names its stage, a loser decided at a floor carries no vector, one decided at or after the ledger carries the complete one.
        var result = Select(Review());
        Assert.NotEmpty(result.Rejected);
        foreach (var rejected in result.Rejected.Where(r => ForecastPortfolioEvidence.StageOf(r.Reason) is not null))
        {
            var atFloor = ForecastPortfolioEvidence.StageOf(rejected.Reason) is ForecastCoupledSelection.UrgentZeroStage or ForecastCoupledSelection.DeathFloorStage or ForecastCoupledSelection.BreakRiskStage;
            Assert.Equal(!atFloor, rejected.Consequences is not null);
        }
        LazyEvaluationTests.AssertPaired(ForecastCoupledSelection.CompareActivityOutcomesEagerly(RequestOf(Review()), Review().CoupledAlternatives!), result);
    }

    private static ColonyForecastRequest RequestOf(RoundsFacts facts, double horizon = Horizon) => ForecastCoupled.CoupledForecastRequestOf(new("world/map/load", 100), horizon / GameTime.TicksPerDay,
        facts,  new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, [], new());

    /// <summary>Two pawns with their own food needs and, each, a harvest-and-eat root per plant (#3724): "pawn" starves at
    /// <paramref name="pawnLevel"/> and "worker" at <paramref name="workerLevel"/>, both falling .003 a tick against the .3 threshold,
    /// so a portfolio feeding only one of them keeps the other's baseline zero time. <paramref name="workerRest"/> gives the worker a
    /// bed and a falling rest need.</summary>
    internal static RoundsFacts TwoPawnReview(double pawnLevel = .2, double workerLevel = .5, bool workerRest = false,
        double harvest = 10, int sourceCount = 2, double horizon = Horizon)
    {
        var facts = Review(level: pawnLevel, harvest: harvest, sourceCount: sourceCount, horizon: horizon);
        var roster = WorkResourceTestFacts.Create("pawn", "worker");
        var pawn = facts.ForecastObserved.Needs!.Pawns[0] with { Location = new(1, 1) };
        var worker = pawn with
        {
            Pawn = "worker",
            Needs = [NeedsFixtures.Rate("food", workerLevel, .003, .3),
                NeedsFixtures.Rate("rest", workerRest ? .2 : 1d, workerRest ? .0004 : 0d, .3)],
            SleepThreshold = .75, WakeThreshold = 1d, SleepBlockedUntilTicks = 0d,
            Targets = workerRest ? [NeedsFixtures.Service("bed", "rest", 0d, .0005) with { Location = new(1, 1) }] : [],
        };
        var coupled = facts.ForecastObserved.Coupled!;
        facts = facts with
        {
            WorkResources = roster.WorkResources,
            ForecastObserved = facts.ForecastObserved with
            {
                WorkPawns = roster.ForecastObserved.WorkPawns,
                Needs = facts.ForecastObserved.Needs with { Pawns = [pawn, worker] },
                Coupled = coupled with
                {
                    Food = coupled.Food with { Options = EquatableArray.From(coupled.Food.Options.Concat(coupled.Food.Options.Select(o => o with { Pawn = "worker" }))) },
                    FoodUses = [.. coupled.FoodUses, coupled.FoodUses[0] with { Pawn = "worker" }],
                },
            },
        };
        facts = WorkRules.ReviewWorkResources(facts, Known<EquatableArray<ProductionBatch>>([]), new([]));
        return facts with { CoupledAlternatives = ForecastCoupledCandidates.GenerateCoupledActivities(facts, horizon)?.Priced() };
    }

    /// <summary>The pruned search returns the unpruned search's selection, plan, vectors and findings, and visits the same subsets: every
    /// subset the unpruned search ranked or refused is either reported the same way or covered by a pruned row (its own, or a sweep
    /// prefix it extends), and a subset the bound skipped lost at the urgent-zero stage, or was infeasible, in the unpruned search.</summary>
    internal static void AssertPrunedEquivalent(ForecastCoupledSelection unpruned, ForecastCoupledSelection pruned)
    {
        Assert.Equal(unpruned.Selection.Selected, pruned.Selection.Selected);
        Assert.Equal(unpruned.Selection.Plan!.Allocations, pruned.Selection.Plan!.Allocations);
        // A skipped subset's plans were never scheduled, so their per-commitment findings are absent; the search's own findings are the same.
        Assert.Subset(unpruned.Selection.Findings.ToHashSet(StringComparer.Ordinal), pruned.Selection.Findings.ToHashSet(StringComparer.Ordinal));
        Assert.Equal(unpruned.Selection.Findings.Where(f => !f.Contains(':')), pruned.Selection.Findings.Where(f => !f.Contains(':')));
        Assert.Equal(unpruned.SelectedVector, pruned.SelectedVector);
        Assert.Equal(unpruned.BaselineVector, pruned.BaselineVector);
        Assert.Equal(unpruned.Selected.Plan.Allocations, pruned.Selected.Plan.Allocations);
        Assert.Equal(unpruned.Deferred, pruned.Deferred);
        Assert.Equal(unpruned.ServiceInvestments, pruned.ServiceInvestments);
        Assert.Equal(unpruned.SearchPhases.Select(p => p.Phase), pruned.SearchPhases.Select(p => p.Phase));
        Assert.Empty(unpruned.BoundsUnavailable);
        Assert.All(unpruned.SearchPhases, p => Assert.Equal((0, 0), (p.Pruned, p.Unbounded)));
        Assert.True(pruned.OutcomeEvaluations <= unpruned.OutcomeEvaluations);
        Assert.True(pruned.Selection.Expansions <= unpruned.Selection.Expansions);
        Assert.Equal(pruned.SearchPhases.Sum(p => p.Pruned), pruned.Rejected.Count(r => ForecastPortfolioEvidence.BoundOf(r.Reason) is not null));

        // The two searches traverse the subsets in the same order: walked together, a pruned row stands for the unpruned rows of its
        // subset (one per feasible plan) or, for a sweep prefix, of the completions that follow it, each of which lost at the urgent-zero
        // stage, was infeasible or was held by the relief gate (none could have led); every other row is the same row. A subset skipped
        // at the screening bound is skipped again at the full bound (the unpruned search had cached its finished schedule at the full
        // bound and does not revisit it), so a second pruned row of the same subset stands for no unpruned row.
        static bool Covered(string reason) => reason == ForecastPortfolioEvidence.LostToLeaderPrefix + ForecastCoupledSelection.UrgentZeroStage
            || reason == ForecastPortfolioEvidence.InfeasibleOrUnassessed
            || reason.StartsWith("construction_without_supported_service_relief", StringComparison.Ordinal);
        var i = 0;
        for (var j = 0; j < pruned.Rejected.Count; j++)
        {
            var row = pruned.Rejected[j];
            var bound = ForecastPortfolioEvidence.BoundOf(row.Reason);
            if (bound is null)
            {
                Assert.True(i < unpruned.Rejected.Count, "a row the unpruned search did not report: " + string.Join(" ", row.Commitments) + " " + row.Reason);
                var same = unpruned.Rejected[i++];
                Assert.Equal(same.Commitments, row.Commitments);
                Assert.Equal(same.Reason, row.Reason);
                Assert.Equal(same.Consequences, row.Consequences);
                continue;
            }
            Assert.Equal(ForecastCoupledSelection.UrgentZeroUnservedBound, bound);
            Assert.Null(row.Consequences);
            Assert.True(row.Assessed);
            var branch = row.Reason.StartsWith(ForecastPortfolioEvidence.PrunedBranchPrefix, StringComparison.Ordinal);
            var covered = 0;
            while (i < unpruned.Rejected.Count && (branch
                ? unpruned.Rejected[i].Commitments.Count > row.Commitments.Count && row.Commitments.All(unpruned.Rejected[i].Commitments.Contains)
                : unpruned.Rejected[i].Commitments.SequenceEqual(row.Commitments)))
            {
                Assert.True(Covered(unpruned.Rejected[i].Reason), string.Join(" ", unpruned.Rejected[i].Commitments) + " " + unpruned.Rejected[i].Reason);
                i++; covered++;
            }
            if (!branch && covered == 0)
                Assert.True(pruned.Rejected.Take(j).Any(earlier => ForecastPortfolioEvidence.BoundOf(earlier.Reason) is not null && earlier.Commitments.SequenceEqual(row.Commitments)),
                    "a pruned subset the unpruned search did not visit: " + string.Join(" ", row.Commitments));
        }
        Assert.Equal(unpruned.Rejected.Count, i);
    }

    private static (ForecastCoupledSelection Unpruned, ForecastCoupledSelection Pruned) Both(RoundsFacts facts, ForecastSearchBudget? budget = null, double horizon = Horizon) =>
        (ForecastCoupledSelection.CompareActivityOutcomesUnpruned(RequestOf(facts, horizon), facts.CoupledAlternatives!, budget),
            ForecastCoupledSelection.CompareActivityOutcomes(RequestOf(facts, horizon), facts.CoupledAlternatives!, budget));

    // #3724: the urgent-zero unserved-pawn bound skips the subsets that leave a pawn starving longer than the leader's worst, without
    // changing the selection, and the search spends fewer evaluations and expansions for it.
    [Fact]
    public void TheBoundSkipsSubsetsLeavingAPawnStarvingPastTheLeaderAndReturnsTheUnprunedSelection()
    {
        var facts = TwoPawnReview();
        var roots = facts.CoupledAlternatives!.Alternatives;
        Assert.Contains(roots, r => r.Id.Contains("/eat/pawn/", StringComparison.Ordinal));
        Assert.Contains(roots, r => r.Id.Contains("/eat/worker/", StringComparison.Ordinal));
        var (unpruned, pruned) = Both(facts);
        AssertPrunedEquivalent(unpruned, pruned);
        // A partial portfolio that breaks the floor (feeding the worker alone) is not discarded on that: the selection feeds both pawns.
        Assert.Contains(pruned.Selection.Selected, id => id.Contains("/eat/pawn/", StringComparison.Ordinal));
        Assert.Contains(pruned.Selection.Selected, id => id.Contains("/eat/worker/", StringComparison.Ordinal));
        Assert.Equal(Known(true), pruned.Selected.Feasible);
        // Subsets were pruned (the ones feeding the worker alone, or nobody, named after the pawn left starving) in the seed pass and in the
        // extension; the 52-root graph affords no sweep at the default budget (the sweep's prefix pruning is shown on the eight-root fixture below).
        var prunedRows = pruned.Rejected.Where(r => ForecastPortfolioEvidence.BoundOf(r.Reason) is not null).ToArray();
        Assert.NotEmpty(prunedRows);
        Assert.All(prunedRows, r => Assert.Equal(ForecastCoupledSelection.UrgentZeroUnservedBound, ForecastPortfolioEvidence.BoundOf(r.Reason)));
        // Each names the pawn left starving (the pawn while the leader feeds the worker alone or nobody, the worker once it feeds the pawn),
        // and holds no root whose closure feeds that pawn.
        Assert.All(prunedRows, r => Assert.True(r.Reason[(r.Reason.LastIndexOf(':') + 1)..] is "pawn" or "worker", r.Reason));
        Assert.Contains(prunedRows, r => r.Reason.EndsWith(":pawn", StringComparison.Ordinal));
        Assert.All(prunedRows, r => Assert.All(r.Commitments, id => Assert.DoesNotContain("/eat/" + r.Reason[(r.Reason.LastIndexOf(':') + 1)..] + "/", id)));
        Assert.All(prunedRows, r => Assert.StartsWith(ForecastPortfolioEvidence.PrunedPrefix, r.Reason));
        Assert.Equal("baseline seed extend confirm", string.Join(" ", pruned.SearchPhases.Select(p => p.Phase)));
        Assert.True(pruned.SearchPhases.Single(p => p.Phase == "seed").Pruned > 0);
        Assert.True(pruned.SearchPhases.Single(p => p.Phase == "extend").Pruned > 0);
        Assert.Empty(pruned.BoundsUnavailable);
        Assert.True(pruned.OutcomeEvaluations < unpruned.OutcomeEvaluations, pruned.OutcomeEvaluations + " of " + unpruned.OutcomeEvaluations);
        Assert.True(pruned.Selection.Expansions < unpruned.Selection.Expansions, pruned.Selection.Expansions + " of " + unpruned.Selection.Expansions);
        Assert.Equal(pruned.BaselineEvaluations, unpruned.BaselineEvaluations);
        Assert.Equal(pruned.OutcomeEvaluations, pruned.SearchPhases.Sum(p => p.OutcomeEvaluations));
        // Repeating the comparison yields the same counts (#3722).
        var again = ForecastCoupledSelection.CompareActivityOutcomes(RequestOf(facts), facts.CoupledAlternatives!);
        Assert.Equal((pruned.OutcomeEvaluations, pruned.Selection.Expansions, pruned.SearchPhases), (again.OutcomeEvaluations, again.Selection.Expansions, again.SearchPhases));
    }

    // #3724: in the cardinality sweep a prefix none of whose completions can feed the starving pawn is skipped whole, with every completion.
    // One plant, a 150-tick horizon and a worker who gets hungry but never starves within it make a small graph (the harvest and a few
    // meals of each pawn, each in two compositions) whose sweep the default budget affords and finishes; the worker's roots sort last, so
    // the sweep's prefixes of them have only worker roots for completions. The pruned sweep is the exhaustive one: the same selection, the
    // same ranked rows, fewer evaluations and expansions.
    [Fact]
    public void TheSweepSkipsAPrefixWhoseEveryCompletionLeavesThePawnStarving()
    {
        var facts = TwoPawnReview(workerLevel: .5, sourceCount: 1, horizon: 150);
        var roots = facts.CoupledAlternatives!.Alternatives;
        Assert.True(roots.Count <= 12, string.Join(" ", roots.Select(r => r.Id)));
        Assert.True(roots.Count(r => r.Id.Contains("/eat/worker/", StringComparison.Ordinal)) >= 2, string.Join(" ", roots.Select(r => r.Id)));
        var (unpruned, pruned) = Both(facts, horizon: 150);
        AssertPrunedEquivalent(unpruned, pruned);
        Assert.Equal("sweep", pruned.SearchPhases[^1].Phase);
        Assert.False(pruned.Selection.SearchTruncated);
        Assert.False(unpruned.Selection.SearchTruncated);
        Assert.Empty(pruned.BoundsUnavailable);
        var branches = pruned.Rejected.Where(r => r.Reason.StartsWith(ForecastPortfolioEvidence.PrunedBranchPrefix, StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(branches);
        Assert.All(branches, r => Assert.Equal(ForecastPortfolioEvidence.PrunedBranchPrefix + ForecastCoupledSelection.UrgentZeroUnservedBound + ":pawn", r.Reason));
        Assert.All(branches, r => Assert.All(r.Commitments, id => Assert.DoesNotContain("/eat/pawn/", id)));
        Assert.True(pruned.SearchPhases[^1].Pruned > 0);
        Assert.True(pruned.OutcomeEvaluations < unpruned.OutcomeEvaluations, pruned.OutcomeEvaluations + " of " + unpruned.OutcomeEvaluations);
        Assert.True(pruned.Selection.Expansions < unpruned.Selection.Expansions, pruned.Selection.Expansions + " of " + unpruned.Selection.Expansions);
    }

    // #3724: the pruned search is the unpruned search on graphs where the bound holds nothing, where every choice breaks the floor, where
    // the two pawns' starvation is reversed, with unknown harvest legality, and under a budget that truncates the search.
    [Theory]
    [InlineData("one-pawn")]
    [InlineData("one-pawn-unknown-harvest")]
    [InlineData("all-violate")]
    [InlineData("reversed")]
    [InlineData("worker-safe")]
    [InlineData("tiny-budget")]
    [InlineData("seed-budget")]
    public void ThePrunedSearchIsTheUnprunedSearchOnAdversarialGraphs(string graph)
    {
        (RoundsFacts facts, ForecastSearchBudget? budget) = graph switch
        {
            "one-pawn" => (Review(), null),
            "one-pawn-unknown-harvest" => (Review(unknownHarvest: true), null),
            // Harvests too slow for either pawn to eat before starving: every choice breaks the floor and the least excess wins.
            "all-violate" => (TwoPawnReview(harvest: 400), null),
            "reversed" => (TwoPawnReview(pawnLevel: .5, workerLevel: .2), null),
            // The worker never starves within the horizon: its roots serve a pawn whose baseline zero time is 0.
            "worker-safe" => (TwoPawnReview(workerLevel: 1), null),
            "tiny-budget" => (TwoPawnReview(), new ForecastSearchBudget(2)),
            "seed-budget" => (TwoPawnReview(), new ForecastSearchBudget(6)),
            _ => throw new ArgumentOutOfRangeException(nameof(graph)),
        };
        var (unpruned, pruned) = Both(facts, budget);
        AssertPrunedEquivalent(unpruned, pruned);
        Assert.Equal(unpruned.Selection.SearchTruncated, pruned.Selection.SearchTruncated);
        Assert.Empty(pruned.BoundsUnavailable);
        // Only a root feeding nobody (a harvest without its meal) can be skipped on a one-pawn graph; every root feeding the pawn is evaluated.
        if (graph == "one-pawn" || graph == "one-pawn-unknown-harvest")
            Assert.All(pruned.Rejected.Where(r => ForecastPortfolioEvidence.BoundOf(r.Reason) is not null),
                r => Assert.All(r.Commitments, id => Assert.DoesNotContain("/eat/", id)));
        if (graph == "all-violate") Assert.True(pruned.SelectedVector.Value.UrgentZero.Excess > 0);
    }

    // #3724: where no sound bound exists the search evaluates within the #3722 allowance and names why: a rest service in the closure
    // (the plan's sleep can leave rest unassessed), a baseline zero time unknown, goal values (every evaluated outcome reports its routes).
    [Theory]
    [InlineData("rest", "rest_service_in_closure")]
    [InlineData("goals", "goal_routes")]
    public void AnUnavailableBoundIsReportedAndPrunesNothing(string graph, string reason)
    {
        var facts = graph switch
        {
            "rest" => TwoPawnReview(workerRest: true),
            "goals" => TwoPawnReview(),
            _ => throw new ArgumentOutOfRangeException(nameof(graph)),
        };
        if (graph == "goals")
            facts = facts with { CoupledAlternatives = facts.CoupledAlternatives! with { Context = facts.CoupledAlternatives!.Context with
                { GoalValues = [new("goals/worn-count/2", Importance.Normal, null)] } } };
        if (graph == "rest") Assert.Contains(facts.CoupledAlternatives!.Context.NeedTasks, t => t.Kind == "rest" && t.Pawn == "worker");
        var (unpruned, pruned) = Both(facts);
        AssertPrunedEquivalent(unpruned, pruned);
        Assert.Contains(ForecastCoupledSelection.UrgentZeroUnservedBound + ":" + reason, pruned.BoundsUnavailable);
        Assert.True(pruned.SearchPhases.Sum(p => p.Unbounded) > 0);
        if (graph != "rest")
        {
            // Unavailable for every subset: the pruned search is the unpruned search to the count.
            Assert.Equal(0, pruned.SearchPhases.Sum(p => p.Pruned));
            Assert.Equal((unpruned.OutcomeEvaluations, unpruned.Selection.Expansions, unpruned.Rejected.Count), (pruned.OutcomeEvaluations, pruned.Selection.Expansions, pruned.Rejected.Count));
        }
        else
        {
            // Unavailable for the subsets whose closure sleeps the worker, which are evaluated; the others are bounded as before.
            var restRoots = facts.CoupledAlternatives!.Alternatives.Where(c => c.ExclusiveOutcome.Contains("/rest", StringComparison.Ordinal)).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
            Assert.NotEmpty(restRoots);
            Assert.All(pruned.Rejected.Where(r => ForecastPortfolioEvidence.BoundOf(r.Reason) is not null), r => Assert.DoesNotContain(r.Commitments, restRoots.Contains));
            Assert.True(pruned.SearchPhases.Sum(p => p.Pruned) > 0);
        }
    }

    // #3724: a pruned row names its bound and the pawn left starving, carries no vector, and counts as assessed (it was ranked, by proof).
    [Fact]
    public void EvidenceNamesTheBoundThatSkippedASubsetOrABranch()
    {
        var subset = new ForecastPortfolioEvidence(["a"], null, ForecastPortfolioEvidence.PrunedPrefix + ForecastCoupledSelection.UrgentZeroUnservedBound + ":pawn");
        Assert.Equal(ForecastCoupledSelection.UrgentZeroUnservedBound, ForecastPortfolioEvidence.BoundOf(subset.Reason));
        Assert.Null(ForecastPortfolioEvidence.StageOf(subset.Reason));
        Assert.True(subset.Assessed);
        var branch = new ForecastPortfolioEvidence(["a"], null, ForecastPortfolioEvidence.PrunedBranchPrefix + ForecastCoupledSelection.UrgentZeroUnservedBound + ":pawn");
        Assert.Equal(ForecastCoupledSelection.UrgentZeroUnservedBound, ForecastPortfolioEvidence.BoundOf(branch.Reason));
        Assert.Null(ForecastPortfolioEvidence.BoundOf(ForecastPortfolioEvidence.LostToLeaderPrefix + ForecastCoupledSelection.UrgentZeroStage));
        Assert.Null(ForecastPortfolioEvidence.BoundOf(ForecastPortfolioEvidence.InfeasibleOrUnassessed));
    }

    // #3563: the search reports its expansions per phase and the phase that found the chosen subset; the previous selection seeds the next search.
    [Fact]
    public void SearchPhasesSumToExpansionsAndThePreviousSelectionSeedsTheNextSearch()
    {
        var facts = Review();
        var cold = Select(facts);
        Assert.NotEmpty(cold.Selection.Selected);
        Assert.Equal(cold.Selection.Expansions, cold.SearchPhases.Sum(p => p.Expansions));
        // The cardinality sweep is skipped when the budget left cannot finish it (#3563): the search ends converged at the
        // confirmed leader with budget unspent, truncated since the subsets were not all seen.
        Assert.Equal("baseline seed extend confirm", string.Join(" ", cold.SearchPhases.Select(p => p.Phase)));
        Assert.True(cold.OutcomeEvaluations - cold.BaselineEvaluations < ForecastSearchBudget.DefaultEvaluations);
        Assert.True(cold.Selection.Expansions < ForecastSearchBudget.DefaultTraversal);
        Assert.True(cold.Selection.SearchTruncated);
        Assert.DoesNotContain(ForecastCoupledSelection.OutcomeEvaluationsExhausted, cold.Selection.Findings);
        Assert.Contains(cold.SelectedPhase, cold.SearchPhases.Select(p => p.Phase));
        Assert.Equal("", cold.WarmStart);
        // The phases' outcome evaluations sum to the comparison's too (#3722), and the baseline phase holds exactly the mandatory share.
        Assert.Equal(cold.OutcomeEvaluations, cold.SearchPhases.Sum(p => p.OutcomeEvaluations));
        Assert.Equal(cold.BaselineEvaluations, cold.SearchPhases[0].OutcomeEvaluations);
        Assert.Equal(cold.BaselineExpansions, cold.SearchPhases[0].Expansions);
        Assert.Equal(ForecastSearchBudget.Default, cold.Budget);
        // A budget the sweep can finish in runs it to the end: the eight-root productive fixture exhausts its subsets within
        // 16,384 evaluations with budget left (the review fixture above has too many roots for any budget to finish its sweep).
        var original = ProductiveLearningTests.NormalFoodFacts();
        var generated = ForecastCoupledCandidates.GenerateCoupledActivities(original, 700)!.Priced(700);
        var exhaustive = ForecastCoupledSelection.CompareActivityOutcomes(PortfolioAllocationTests.Request(original, generated), generated, new(16384));
        Assert.Equal("sweep", exhaustive.SearchPhases[^1].Phase);
        Assert.True(exhaustive.OutcomeEvaluations - exhaustive.BaselineEvaluations < 16384);
        Assert.True(exhaustive.Selection.Expansions < ForecastSearchBudget.DefaultTraversal);

        var warm = SelectWarm(facts, cold.Selection.Selected);
        Assert.Equal("seeded", warm.WarmStart);
        Assert.Equal("warm", warm.SelectedPhase);
        Assert.Equal(cold.Selection.Selected, warm.Selection.Selected);
        Assert.Equal("baseline warm seed extend confirm", string.Join(" ", warm.SearchPhases.Select(p => p.Phase)));

        // A previous id whose composition number moved still finds its root; an unknown id seeds nothing and changes nothing.
        var renumbered = SelectWarm(facts, cold.Selection.Selected.Select(id => id.Contains("/composition/", StringComparison.Ordinal) ? id[..id.IndexOf("/composition/", StringComparison.Ordinal)] + "/composition/999" : id).ToArray());
        Assert.Equal("seeded", renumbered.WarmStart);
        var missing = SelectWarm(facts, NoSuchRoot);
        Assert.Equal("missing", missing.WarmStart);
        Assert.Equal(cold.Selection.Selected, missing.Selection.Selected);
        Assert.Equal(cold.Selection.Expansions, missing.Selection.Expansions);
    }

    // #3546: the selection reports harm_capability_unassessed, with the specific finding, only when the chosen outcome could not price harm.
    [Fact]
    public void HarmCapabilityUnassessedIsAbsentWhenHarmIsAssessedAndPresentWithTheSpecificFindingOtherwise()
    {
        var facts = Review(fall: .012);
        var unassessed = Select(facts).Selection.Findings;
        Assert.Contains("harm_capability_unassessed", unassessed);
        Assert.Contains("threat_schedule_unknown", unassessed);
        Assert.Contains("death_floor_unassessed", unassessed);

        var quiet = facts.CoupledAlternatives! with
        {
            Context = facts.CoupledAlternatives!.Context with
            {
                Harm = new() { Schedule = new() { Assessed = true, MajorThreatsPerDay = 0d, RaidFraction = 1 } },
            },
        };
        var assessed = Select(facts with { CoupledAlternatives = quiet }).Selection.Findings;
        Assert.DoesNotContain("harm_capability_unassessed", assessed);
        Assert.DoesNotContain("threat_schedule_unknown", assessed);
        Assert.DoesNotContain("death_floor_unassessed", assessed);
    }

    [Fact]
    public void OrdinaryReviewCombinesIndependentFoodSourcesForOneHungryBeneficiary()
    {
        // One source leaves the pawn starving part of the horizon: the second source's meal is capability-days of hunger.
        var facts = Review(fall: .012);
        var result = Select(facts);
        Assert.Equal(Known(true), result.Selected.Feasible);
        Assert.True(result.Selected.Plan.Allocations.Length > 0, string.Join(";", result.Selection.Findings));
        Assert.Contains(result.Selected.Plan.Allocations, a => a.Activity == SourceTask(facts, "plant-a"));
        Assert.Contains(result.Selected.Plan.Allocations, a => a.Activity == SourceTask(facts, "plant-b"));
        Assert.Equal(2, result.Selected.Plan.Allocations.Count(a => facts.CoupledAlternatives!.Context.NeedTasks.Any(n => n.Task == a.Activity && n.Kind == "food")));
        Assert.True(result.SelectedVector.Urgent[0].Value < result.BaselineVector.Urgent[0].Value);
        Assert.True(result.SelectedVector.Value.Ledger.LossAgainst(result.BaselineVector.Value.Ledger).Value < 0);
        // Unknown future meal thoughts must not veto known relief of urgent hunger.
        Assert.True(result.Baseline.Needs["pawn"].Mood!.PressureTicks.IsKnown);
    }

    [Fact]
    public void AdequateConstantNeedsRetainNoChangeInsteadOfProducingUnrequestedSurplus()
    {
        var result = Select(Review(level: 1, fall: 0));
        Assert.Empty(result.Selection.Selected);
        Assert.Empty(result.Selected.Plan.Allocations);
        Assert.Equal(result.BaselineVector, result.SelectedVector);
    }

    [Theory]
    [InlineData(1, 0, true)]
    [InlineData(.5, .001, true)]
    [InlineData(.2, 0, false)]
    public void AColonistWithNoChangeDueIsDeferred(double level, double fall, bool deferred)
    {
        var facts = Review(level, fall);
        var candidates = facts.CoupledAlternatives!;
        var pawn = candidates.Context.Needs.Pawns[0] with { Location = new(8, 9) };
        candidates = candidates with { Alternatives = [], Context = candidates.Context with
        { Needs = candidates.Context.Needs with { Pawns = [pawn] } } };
        facts = facts with { CoupledAlternatives = candidates };
        var choice = Select(facts);
        if (!deferred) { Assert.Empty(choice.Deferred); return; }
        // #3624: the deferred colonist is named and runs vanilla work (#3669); the coupled Method binds nothing for it.
        Assert.Equal("pawn", Assert.Single(choice.Deferred));
        Assert.Null(ForecastCoupledSelection.StartCoupledActivity(choice, candidates, 1, "method", 100));
    }

    [Fact]
    public void UnknownNeedsAndExistingClaimsNeverBecomeIdlePermission()
    {
        var facts = Review(1, 0);
        var candidates = facts.CoupledAlternatives!;
        var pawn = candidates.Context.Needs.Pawns[0] with { Location = new(8, 9), Needs = [NeedsFixtures.Rate("food", default, 0d, .3)] };
        facts = facts with { CoupledAlternatives = candidates with { Alternatives = [], Context = candidates.Context with
        { Needs = candidates.Context.Needs with { Pawns = [pawn] } } } };
        Assert.Empty(Select(facts).Deferred);
        pawn = pawn with { Needs = [NeedsFixtures.Rate("food", 1d, 0d, .3)] };
        facts = facts with { CoupledAlternatives = candidates with { Alternatives = [], Context = candidates.Context with
        { Needs = candidates.Context.Needs with { Pawns = [pawn] }, Resources = candidates.Context.Resources with
        { Claims = candidates.Context.Resources.Claims with { Pawns = ["pawn"] } } } } };
        Assert.Empty(Select(facts).Deferred);
    }
    [Fact]
    public void UnsupportedServiceAndNonfiniteNeedEstimatesNeverAuthorizeIdle()
    {
        var facts = Review(1, 0);
        var original = facts.CoupledAlternatives!;
        var pawn = original.Context.Needs.Pawns[0] with { Location = new(8, 9) };
        foreach (var blocked in new[]
        {
            pawn with { VoluntaryControlBlockers = ["unsupported_psyfocus"] },
            pawn with { Needs = [NeedsFixtures.Rate("food", 1d, double.NaN, .3)] },
            pawn with { Needs = [NeedsFixtures.Rate("food", 1d, 0d, double.PositiveInfinity)] },
        })
        {
            var candidates = original with { Alternatives = [], Context = original.Context with
                { Needs = original.Context.Needs with { Pawns = [blocked] } } };
            if (blocked.VoluntaryControlBlockers.Count > 0)
                Assert.Empty(Select(facts with { CoupledAlternatives = candidates }).Deferred);
            else Assert.Throws<DomainException>(() => Select(facts with { CoupledAlternatives = candidates }));
        }
    }

    [Fact]
    public void AHungryBlockedPawnIsFedRatherThanHeldOnStandby()
    {
        // #3557: two colonists with chemical needs stood on standby at food 0 beside 13.9 nutrition: a blocked pawn had no
        // need targets, no capacity and so no eat to select; its urgent-zero excess was the same in every outcome.
        var facts = Review(1, 0);
        var pawn = facts.ForecastObserved.Needs!.Pawns[0] with
        {
            State = "unsupported_voluntary_state", VoluntaryControlBlockers = ["chemical_need:Chemical_Smokeleaf"], Location = new(1, 1),
            Needs = [NeedsFixtures.Rate("food", .02, .003, .3), NeedsFixtures.Rate("rest", 1d, 0d, .3)],
            Targets = [NeedsFixtures.Food("meal", "MealSimple", 1, 0d, 10d, .9) with
                { Location = new(1, 1), StockCount = Known(1d), MaxPawns = Known(1), FreshTicks = Known(100000d) }]
        };
        facts = facts with { Resources = Known<EquatableArray<RimGovernor.Brain.Policy.Amount>>([new(new("MealSimple"), 1)]), ForecastObserved = facts.ForecastObserved with
        {
            Needs = facts.ForecastObserved.Needs with { Pawns = [pawn] },
            Coupled = facts.ForecastObserved.Coupled! with { Food = facts.ForecastObserved.Coupled.Food with { Options = [] }, FoodUses = [] }
        } };
        facts = WorkRules.ReviewWorkResources(facts, Known<EquatableArray<ProductionBatch>>([]), new([]));
        facts = facts with { CoupledAlternatives = ForecastCoupledCandidates.GenerateCoupledActivities(facts, Horizon)?.Priced() };
        var candidates = facts.CoupledAlternatives!;
        Assert.Contains("unsupported_voluntary_state:pawn:chemical_need:Chemical_Smokeleaf", candidates.Findings);
        var selected = Select(facts);
        var why = string.Join(";", candidates.Findings.Concat(selected.Selection.Findings))
            + " tasks=" + string.Join(",", candidates.Context.NeedTasks.Select(n => n.Task))
            + " selected=" + string.Join(",", selected.Selection.Selected);
        Assert.True(selected.Selected.Plan.Allocations.Any(a => a.Pawn == "pawn"
            && candidates.Context.NeedTasks.Any(n => n.Task == a.Activity && n.Kind == "food")), why);
        Assert.Empty(selected.Deferred);
        var batch = ForecastCoupledSelection.StartCoupledActivity(selected, candidates, 1, "fed-method", 100);
        Assert.NotNull(batch);
        Assert.Contains(batch.Orders, o => o.Pawn == "pawn" && !o.Release);
        Assert.True(selected.SelectedVector.Value.UrgentZero.Excess < selected.BaselineVector.Value.UrgentZero.Excess, why);
    }

    [Theory]
    [InlineData("graph", false)]
    [InlineData("cost", true)]
    [InlineData("claim", true)]
    public void RedundantGraphRootsAreSkippedButDirectEffectsAndClaimsAreEvaluated(string effect, bool evaluatePair)
    {
        var facts = Review();
        var candidates = facts.CoupledAlternatives!;
        var root = candidates.Alternatives.First(c => c.ActivityCompletions.Count == 1
            && candidates.Context.NeedTasks.Any(n => n.Task == c.ActivityCompletions[0]));
        root = root with { Outcome = null, ExclusiveOutcome = "",
            AlternativeLaborCost = effect == "cost" ? Known(0d) : default,
            MaterialClaims = effect == "claim" ? [new(new("Rice"), Known(0d), default)] : [] };
        facts = facts with { CoupledAlternatives = candidates with { Alternatives = [root with { Id = "a" }, root with { Id = "b" }] } };
        var selected = Select(facts);
        Assert.Equal(evaluatePair, selected.Rejected.Any(e => e.Commitments.Count == 2));
        Assert.Contains(selected.Selected.Plan.Allocations, a => a.Activity == root.ActivityCompletions[0]);
    }

    [Fact]
    public void DelayedRealizationsRetainPhysicalOutcomeExclusions()
    {
        var facts = Review();
        var candidates = facts.CoupledAlternatives!;
        var roots = candidates.Alternatives.Where(c => !c.Id.Contains("/after/", StringComparison.Ordinal)
            && c.ActivityCompletions.Count == 1 && candidates.Context.NeedTasks.Any(n => n.Task == c.ActivityCompletions[0]))
            .GroupBy(c => candidates.Context.NeedTasks.Single(n => n.Task == c.ActivityCompletions[0]).FreshFrom)
            .Select(g => g.First()).Take(2).ToArray();
        Assert.Equal(2, roots.Length);
        var alternatives = roots.Select((root, index) => root with
        {
            Id = root.Id + "/after/recovery",
            Outcome = new("physical-" + index, "shared-demand", root.ActivityCompletions[0], Known(Horizon), default)
                { Excludes = ["physical-" + (1 - index)] }
        }).ToArray();
        facts = facts with { CoupledAlternatives = candidates with { Alternatives = EquatableArray.From(alternatives) } };
        var selected = Select(facts);
        Assert.Single(selected.Selection.Selected);
    }

    [Theory]
    [InlineData(290, 1000, false)]
    [InlineData(10, 5, false)]
    [InlineData(10, 1000, true)]
    public void LateExpiredOrUnavailableHarvestCannotBuyEarlierNeedRelief(double work, double expiry, bool unknown)
    {
        var result = Select(Review(harvest: work, expiry: expiry, unknownHarvest: unknown));
        Assert.Empty(result.Selection.Selected);
        Assert.DoesNotContain(result.Selected.Plan.Allocations, a => a.Activity.Contains("/eat/", StringComparison.Ordinal));
        Assert.Equal(result.BaselineVector.Urgent, result.SelectedVector.Urgent);
    }

    [Fact]
    public void InputReorderingPreservesSelectedPhysicalEffectsAndReplayIsStable()
    {
        var normal = Select(Review());
        var reordered = Select(Review(reverse: true));
        Assert.Equal(normal.Selection.Selected.Order(StringComparer.Ordinal), reordered.Selection.Selected.Order(StringComparer.Ordinal));
        Assert.Equal(normal.SelectedVector.Urgent, reordered.SelectedVector.Urgent);
        Assert.Equal(normal.Selected.Plan.Allocations.Where(a => a.Activity.StartsWith("harvest/", StringComparison.Ordinal)).Select(a => a.Activity).Order(),
            reordered.Selected.Plan.Allocations.Where(a => a.Activity.StartsWith("harvest/", StringComparison.Ordinal)).Select(a => a.Activity).Order());
        var replay = Select(Review());
        Assert.Equal(normal.Selected.Plan.Allocations, replay.Selected.Plan.Allocations);
        Assert.Equal(normal.Selected.Plan.CompletedAt, replay.Selected.Plan.CompletedAt);
        Assert.Equal(normal.SelectedVector, replay.SelectedVector);
    }

    [Theory]
    [InlineData(.28, true)]
    [InlineData(.9, false)]
    public void MoodMemoryWinsOnlyWhereItLowersBreakRisk(double mood, bool wins)
    {
        // Two equal-labor harvests; one grants a +4, eight-day memory at completion (the sarcophagus thought).
        var facts = Review(level: 1, fall: 0);
        var candidates = facts.CoupledAlternatives!;
        var pawn = candidates.Context.Needs.Pawns[0] with { Mood = candidates.Context.Needs.Pawns[0].Mood! with { Level = mood, Target = mood, UnclampedTarget = mood, BreakThreshold = .3 }};
        var harvest = new ForecastCommitment("plain", []) { ActivityCompletions = [SourceTask(facts, "plant-a")] };
        var memory = NeedsFixtures.Memory("KnowBuriedInSarcophagus", 4, 8d * GameTime.TicksPerDay, false);
        facts = facts with { CoupledAlternatives = candidates with
        {
            Alternatives = [harvest with { Id = "mood", MoodMemories = [new("pawn", memory)] }, harvest],
            Context = candidates.Context with { Needs = candidates.Context.Needs with { Pawns = [pawn] }, MoodTrajectory = true }
        } };
        var result = Select(facts);
        Assert.Equal(Known(true), result.Selected.Feasible);
        Assert.True(result.BaselineVector.BreakRisk.IsKnown);
        Assert.DoesNotContain("mental_break_probability_unassessed", result.BaselineVector.Unknown);
        Assert.DoesNotContain("mental_break_probability_unassessed", result.Baseline.Needs["pawn"].Findings);
        if (wins)
        {
            Assert.Equal(["mood"], result.Selection.Selected);
            Assert.True(result.SelectedVector.BreakRisk.Value < result.BaselineVector.BreakRisk.Value);
            return;
        }
        // No break risk to remove: the memory ties no-change and the incumbent is kept.
        Assert.Empty(result.Selection.Selected);
        Assert.Equal(0, result.BaselineVector.BreakRisk.Value);
        // The memory still lowers raw mood pressure; that is not break risk and does not rank.
        var evaluated = result.Rejected.First(r => r.Commitments.SequenceEqual(["mood"])).Consequences!;
        Assert.True(evaluated.MoodDepth.Value < result.BaselineVector.MoodDepth.Value);
    }

    [Theory]
    [InlineData(.28, true)]
    [InlineData(.9, false)]
    public void SituationalChangeWinsOnlyWhereItLowersBreakRisk(double mood, bool wins)
    {
        // A labor-free commitment removing an active -11 situational thought (a dead man's parka) from the forecast start.
        var facts = Review(level: 1, fall: 0);
        var candidates = facts.CoupledAlternatives!;
        var pawn = candidates.Context.Needs.Pawns[0] with { Mood = candidates.Context.Needs.Pawns[0].Mood! with { Level = mood, Target = mood, UnclampedTarget = mood }};
        var change = new ForecastCommitment("gear", []) { SituationalChanges = [new("pawn", "DeadMansApparel", 11)] };
        facts = facts with { CoupledAlternatives = candidates with
        {
            Alternatives = [change],
            Context = candidates.Context with { Needs = candidates.Context.Needs with { Pawns = [pawn] }, MoodTrajectory = true }
        } };
        var result = Select(facts);
        var vector = wins ? result.SelectedVector : result.Rejected.First(r => r.Commitments.SequenceEqual(["gear"])).Consequences!;
        Assert.True(vector.BreakRisk.IsKnown);
        Assert.DoesNotContain("mental_break_probability_unassessed", vector.Unknown);
        if (!wins)
        {
            Assert.Empty(result.Selection.Selected);
            Assert.Equal(result.BaselineVector.BreakRisk, vector.BreakRisk);
            return;
        }
        Assert.Equal(["gear"], result.Selection.Selected);
        Assert.True(vector.BreakRisk.Value < result.BaselineVector.BreakRisk.Value);
        var moodResult = result.Selected.Needs["pawn"].Mood!;
        // The change counts from the forecast start; every other situational thought is still held constant.
        Assert.Equal(mood + .11, moodResult.Trajectory[0].Target.Value, 9);
        Assert.Contains("unmodeled_situational_thoughts_constant", moodResult.Findings);
    }

    [Fact]
    public void LaborFreeMoodMemoryCountsFromTheForecastStart()
    {
        var facts = Review(level: 1, fall: 0);
        var candidates = facts.CoupledAlternatives!;
        var pawn = candidates.Context.Needs.Pawns[0] with { Mood = candidates.Context.Needs.Pawns[0].Mood! with { Level = .28, Target = .28, UnclampedTarget = .28 }};
        var memory = NeedsFixtures.Memory("KnowBuriedInSarcophagus", 4, 8d * GameTime.TicksPerDay, false);
        facts = facts with { CoupledAlternatives = candidates with
        {
            Alternatives = [new ForecastCommitment("memory", []) { MoodMemories = [new("pawn", memory)] }],
            Context = candidates.Context with { Needs = candidates.Context.Needs with { Pawns = [pawn] }, MoodTrajectory = true }
        } };
        var result = Select(facts);
        Assert.Equal(["memory"], result.Selection.Selected);
        var needs = result.Selected.Needs["pawn"];
        Assert.DoesNotContain("commitment_mood_memory_unassessed", needs.Findings);
        Assert.Equal(.32, needs.Mood!.Trajectory[0].Target.Value, 9);
        Assert.True(result.SelectedVector.BreakRisk.Value < result.BaselineVector.BreakRisk.Value);
    }

    [Fact]
    public void ChangingOneSituationalThoughtTwiceIsUnassessed()
    {
        var pawn = Review().CoupledAlternatives!.Context.Needs.Pawns[0];
        var mood = ForecastMood.ForecastMoodTimeline(pawn, Horizon, [], situational: [("DeadMansApparel", 11, 0), ("DeadMansApparel", 11, 10)]);
        Assert.False(mood.BreakRisk.IsKnown);
        Assert.Equal(["unsupported_situational_change_stacking"], mood.Findings);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TaintedParkaFilterWinsNearABreakAndLosesForAContentPawnWorkingOutsideInTheCold(bool nearBreak)
    {
        // #3284: the generated "tainted not allowed" candidate (+5 mood, 20 less cold insulation). #3285 parity: near the break
        // the candidate is selected and the pawn's filter keeps tainted disallowed; the content pawn's is rejected, so allowed.
        var (facts, result, root) = TaintedParkaReview(nearBreak);
        var allowed = EffectSourcesRules.GearChoices(facts.CoupledAlternatives, result);
        if (nearBreak)
        {
            Assert.Equal([root.Id], result.Selection.Selected);
            Assert.True(result.SelectedVector.BreakRisk.Value < result.BaselineVector.BreakRisk.Value);
            // #3329: the selected pair keeps the worn insulation floor.
            Assert.Equal([new GearChoice("pawn", false, 30, 0)], allowed);
            return;
        }
        Assert.NotEmpty(result.Selection.Selected);
        Assert.DoesNotContain(root.Id, result.Selection.Selected);
        Assert.Equal([new GearChoice("pawn", true)], allowed);
        // The content pawn's break risk does not move, and with the selected outdoor work the thinner parka adds cold exposure.
        Assert.Equal(result.BaselineVector.BreakRisk, result.Rejected.First(r => r.Commitments.SequenceEqual([root.Id])).Consequences!.BreakRisk);
        var work = facts.CoupledAlternatives!.Alternatives.Where(c => result.Selection.Selected.Contains(c.Id)).ToArray();
        double Cold(params ForecastCommitment[] add) => ColonyForecastRules.ForecastColony(ForecastCoupled.CoupledForecastRequestOf(new("world", 100), Horizon / GameTime.TicksPerDay,
                facts,  new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, [], new() { Add = EquatableArray.From(add) }))
            .Proposed.Coupled!.Alternatives.First(p => p.Feasible == Known(true)).ThermalExposure.Single().AssessedColdDegreeTicks;
        Assert.True(Cold([.. work, root]) > Cold(work));
    }

    [Fact]
    public void AReviewWithNoCoupledSelectionKeepsTheLastGearFilterOutcome()
    {
        // #3325: allowed in review 1; review 2 compared nothing, so the pawn stays allowed until a review with a selection
        // replaces it. Another world (or a restart's empty memory) still reads the default.
        var (facts, result, _) = TaintedParkaReview(false);
        var world = new GenerationSnapshot(new("colony"), new(0), new("load"), new("plan"), new(1), new(1));
        var memory = new RimGovernor.Brain.BuildingRuntime.ReviewMemory<EquatableArray<GearChoice>>([]);
        RimGovernor.Brain.BuildingRuntime.Rounder.NoteGearChoices(memory, world, facts.CoupledAlternatives, result);
        Assert.Equal([new GearChoice("pawn", true)], memory.Get(world));
        RimGovernor.Brain.BuildingRuntime.Rounder.NoteGearChoices(memory, world, facts.CoupledAlternatives, null);
        Assert.Equal([new GearChoice("pawn", true)], memory.Get(world));
        Assert.Empty(memory.Get(world with { Native = new(2) }));
        var (nearFacts, nearResult, _) = TaintedParkaReview(true);
        RimGovernor.Brain.BuildingRuntime.Rounder.NoteGearChoices(memory, world, nearFacts.CoupledAlternatives, nearResult);
        Assert.Equal([new GearChoice("pawn", false, 30, 0)], memory.Get(world));
    }

    private static (RoundsFacts Facts, ForecastCoupledSelection Result, ForecastCommitment Root) TaintedParkaReview(bool nearBreak)
    {
        var root = Assert.Single(EffectSourcesRules.GenerateEffectSources(EffectSourceCandidatesTests.Facts(),
            new Dictionary<string, MechanicCard> { ["DeadMansApparel"] = MechanicCards.ByDef["DeadMansApparel"] with { Status = MechanicCardStatus.Verified } }).Roots);
        // Near the break the pawn is fed; the content pawn is hungry, so it takes the outdoor harvest work.
        var facts = nearBreak ? Review(level: 1, fall: 0) : Review();
        var candidates = facts.CoupledAlternatives!;
        var mood = nearBreak ? .28 : .9;
        var pawn = candidates.Context.Needs.Pawns[0] with
        { Mood = candidates.Context.Needs.Pawns[0].Mood! with { Level = mood, Target = mood, UnclampedTarget = mood }, ComfortableMin = 10.0, ComfortableMax = 30.0 };
        // The mild forecast has no thermal cells; the cold one is -10 C at every task cell, with the outdoor work on offer.
        var cells = candidates.Context.Activities.Activities.SelectMany(t => new[] { t.Location, t.EndLocation })
            .Where(l => l.StartsWith("cell/", StringComparison.Ordinal)).Distinct().Select(l => l.Split('/'))
            .Select(p => new Cell(int.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture), int.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture)));
        var outdoor = candidates.Alternatives.Where(c => c.ActivityCompletions.Count > 0);
        facts = facts with { CoupledAlternatives = candidates with
        {
            Alternatives = nearBreak ? [root] : [.. outdoor, root],
            Context = candidates.Context with
            {
                Needs = candidates.Context.Needs with { Pawns = [pawn] },
                Shelters = nearBreak ? candidates.Context.Shelters : [],
                Thermal = nearBreak ? candidates.Context.Thermal : CoupledThermalTests.NoThermal with
                {
                    Cells = EquatableArray.From(cells.Select(c => new ThermalCell(c, -10.0, "none", false, false, "", false, false, 0, false))),
                },
            }
        } };
        return (facts, Select(facts), root);
    }

    // #3327: a pawn comfortable from 10 C at -10 C outdoor work observes EnvironmentCold at -12 (a deficit of 20). A labor-free
    // -20 comfort shift (a warmer outfit) makes the deficit 0 during the work, so the card's offset there is 0 (+12).
    // #3329: a mild forecast (20 C at every task cell) observes no EnvironmentCold.
    private static (RoundsFacts Facts, ForecastCommitment Work, ForecastCommitment Gear) ColdWorkReview(double mood, MechanicCardStatus status = MechanicCardStatus.Verified,
        ForecastCommitment? generated = null, bool mild = false)
    {
        var facts = Review(level: 1, fall: 0);
        var candidates = facts.CoupledAlternatives!;
        var pawn = candidates.Context.Needs.Pawns[0] with
        { Mood = candidates.Context.Needs.Pawns[0].Mood! with { Level = mood, Target = mood, UnclampedTarget = mood }, ComfortableMin = 10.0, ComfortableMax = 30.0 };
        var cells = candidates.Context.Activities.Activities.SelectMany(t => new[] { t.Location, t.EndLocation })
            .Where(l => l.StartsWith("cell/", StringComparison.Ordinal)).Distinct().Select(l => l.Split('/'))
            .Select(p => new Cell(int.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture), int.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture)));
        var gear = generated ?? new ForecastCommitment("gear", []) { ComfortShifts = [new("pawn", -20, 0)] };
        facts = facts with { CoupledAlternatives = candidates with
        {
            Alternatives = [gear],
            Context = candidates.Context with
            {
                Needs = candidates.Context.Needs with { Pawns = [pawn] },
                Shelters = [],
                Thermal = CoupledThermalTests.NoThermal with { Cells = EquatableArray.From(cells.Select(c => new ThermalCell(c, mild ? 20.0 : -10.0, "none", false, false, "", false, false, 0, false))) },
                Cards = new Dictionary<string, MechanicCard> { ["EnvironmentCold"] = MechanicCards.ByDef["EnvironmentCold"] with { Status = status } },
                CardPawns = new Dictionary<string, CardPawnFacts> { ["pawn"] = new()
                {
                    Traits = Known<EquatableArray<string>>([]), Hediffs = Known<EquatableArray<string>>([]),
                    IdeologyActive = Known(false), Precepts = Known<EquatableArray<string>>([])
                } },
                ObservedThoughts = new Dictionary<string, Fact<EquatableArray<MoodThought>>>
                { ["pawn"] = Known<EquatableArray<MoodThought>>(mild ? [] : [new("EnvironmentCold", -12)]) },
            }
        } };
        var work = candidates.Alternatives.Where(c => c.ActivityCompletions.Count > 0)
            .First(c => Outcome(facts, c).ThermalExposure.Single().AssessedTicks > 0);
        return (facts, work, gear);
    }

    private static ForecastCoupledOutcome Outcome(RoundsFacts facts, params ForecastCommitment[] add) =>
        ColonyForecastRules.ForecastColony(ForecastCoupled.CoupledForecastRequestOf(new("world", 100), Horizon / GameTime.TicksPerDay,
            facts with { CoupledAlternatives = facts.CoupledAlternatives! with { Context = facts.CoupledAlternatives.Context with { MoodTrajectory = true } } },  new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) },
            [], new() { Add = EquatableArray.From(add) })).Proposed.Coupled!.Alternatives.First(p => p.Feasible == Known(true));

    [Fact]
    public void AComfortShiftRaisesTemperatureMoodAlongTheOutdoorWork()
    {
        var (facts, work, gear) = ColdWorkReview(.28);
        var cold = Outcome(facts, work).Needs["pawn"].Mood!;
        var warm = Outcome(facts, work, gear).Needs["pawn"].Mood!;
        // Without the shift the card along the work matches the observed -12: today's constant.
        Assert.All(cold.Trajectory, p => Assert.Equal(.28, p.Target.Value, 9));
        Assert.Contains(warm.Trajectory, p => Math.Abs(p.Target.Value - .40) < 1e-9);
        Assert.All(warm.Trajectory, p => Assert.True(p.Target.Value >= .28 - 1e-9));
        Assert.True(warm.BreakRisk.Value < cold.BreakRisk.Value);
        // Every tick after the work is at its temperature (#3673), so the exposure covers the horizon the card is read over.
        Assert.DoesNotContain("situational_thought_constant_outside_exposure:EnvironmentCold", warm.Findings);
    }

    // Near a break the outfit lowers break risk; content, it is still taken for the cold exposure it removes, every tick
    // after the outdoor work assessed at the work's temperature (#3673).
    [Theory]
    [InlineData(.28, true)]
    [InlineData(.9, false)]
    public void TheComparatorTakesTheWarmerOutfitForTheColdAndNearABreakLowersBreakRisk(double mood, bool nearBreak)
    {
        var (facts, work, _) = ColdWorkReview(mood);
        var result = Select(facts, null, work);
        Assert.Contains(result.Selected.Plan.Allocations, a => work.ActivityCompletions.Contains(a.Activity));
        Assert.Equal(["gear"], result.Selection.Selected);
        Assert.True(result.Selected.ThermalExposure.Single().AssessedColdDegreeTicks < result.Baseline.ThermalExposure.Single().AssessedColdDegreeTicks);
        Assert.Equal(nearBreak, result.SelectedVector.BreakRisk.Value < result.BaselineVector.BreakRisk.Value);
    }

    // #3329 acceptance: gear warmth chosen by the comparator. A pawn in a shirt and pants with a parka (cold 30) loose: the
    // generator's one pair raises its floor to the parka's insulation. Near a break at the outdoor task in the cold the warmer
    // floor is selected, stamped once on the census and the planner targets the parka; content in a mild forecast the pawn
    // keeps the worn level.
    [Theory]
    [InlineData(false, .28, true)]
    [InlineData(true, .9, false)]
    public void TheWarmerFloorIsSelectedNearABreakInTheColdAndTheWornLevelIsKeptWhenMild(bool mild, double mood, bool wins)
    {
        var pawn = EffectSourceCandidatesTests.WarmPawn(tainted: false);
        var root = Assert.Single(EffectSourceCandidatesTests.Generate(EffectSourceCandidatesTests.Facts(pawns: pawn), temperature: MechanicCardStatus.Verified).Roots);
        Assert.Equal("gear/pawn/disallow-tainted/cold30-heat0", root.Id);
        Assert.Equal([new ForecastComfortShift("pawn", -30, 0)], root.ComfortShifts);
        Assert.Empty(root.SituationalChanges);

        var (choices, target) = WarmthReview(pawn, root, mood, mild);
        Assert.Equal(wins ? [new GearChoice("pawn", false, 30, 0)] : [], choices);
        Assert.Equal(wins, target.Any(o => o.ID == "parka"));
    }

    // #3329 acceptance: the only warm option is a tainted parka, so the pair is (allow tainted, warm floor): +30 cold insulation
    // and the -5 dead man's apparel thought. Near a break, the cold outdoor task's mood outweighs the -5 in break risk and the
    // pair wins; in a mild forecast only the -5 is left and it loses, so the default (disallowed, worn floor) stands.
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void ATaintedWarmParkaWinsOnlyWhenColdMoodOutweighsTheTaint(bool mild, bool wins)
    {
        var pawn = EffectSourceCandidatesTests.WarmPawn(tainted: true);
        var root = Assert.Single(EffectSourceCandidatesTests.Generate(EffectSourceCandidatesTests.Facts(pawns: pawn), temperature: MechanicCardStatus.Verified).Roots);
        Assert.Equal("gear/pawn/allow-tainted/cold30-heat0", root.Id);
        Assert.Equal([new ForecastSituationalChange("pawn", "DeadMansApparel", -5)], root.SituationalChanges);

        var (choices, target) = WarmthReview(pawn, root, .28, mild);
        Assert.Equal(wins ? [new GearChoice("pawn", true, 30, 0)] : [], choices);
        Assert.Equal(wins, target.Any(o => o.ID == "parka"));
    }

    /// <summary>Compares the generated pair beside the outdoor work, then plans the pawn inside the stamped outcome.</summary>
    private static (EquatableArray<GearChoice> Choices, EquatableArray<RimGovernor.Brain.Policy.GearOption> Target) WarmthReview(
        RimGovernor.Brain.Policy.Methods.Generation.EffectSourcePawn pawn, ForecastCommitment root, double mood, bool mild)
    {
        var (facts, work, _) = ColdWorkReview(mood, generated: root, mild: mild);
        var result = Select(facts, null, work);
        Assert.Contains(result.Selected.Plan.Allocations, a => work.ActivityCompletions.Contains(a.Activity));
        var choices = EffectSourcesRules.GearChoices(facts.CoupledAlternatives, result);
        var census = RimGovernor.Brain.BuildingRuntime.Rounder.WithGearChoices(new RoundsFacts { Gear = Known(EffectSourceCandidatesTests.Census(pawn)) }, choices).Gear;
        return (choices, GearLoadoutRules.PlanColonyGear(census.Value.Pawns)[0].Target);
    }

    [Fact]
    public void NoAssessedSpanOrNoLiveCardLeavesTemperatureMoodAsToday()
    {
        var (facts, work, gear) = ColdWorkReview(.28);
        var today = facts with { CoupledAlternatives = facts.CoupledAlternatives! with { Context = facts.CoupledAlternatives.Context with { Cards = new Dictionary<string, MechanicCard>() } } };
        // No thermal read: the shift has no span to act on; the mood is today's and names the constant thought.
        static RoundsFacts Unread(RoundsFacts f) => f with { CoupledAlternatives = f.CoupledAlternatives! with { Context = f.CoupledAlternatives.Context with { Thermal = null } } };
        var idle = Outcome(Unread(facts), gear).Needs["pawn"].Mood!;
        var plain = Outcome(Unread(today), gear).Needs["pawn"].Mood!;
        Assert.Equal(0, Outcome(Unread(facts), gear).ThermalExposure.Single().AssessedTicks);
        Assert.Equal(plain.Trajectory, idle.Trajectory);
        Assert.Equal(plain.BreakRisk, idle.BreakRisk);
        Assert.Contains("situational_thought_constant_outside_exposure:EnvironmentCold", idle.Findings);
        Assert.DoesNotContain(plain.Findings, f => f.StartsWith("situational_thought_constant_outside_exposure:", StringComparison.Ordinal));
        // A card no probe verified prices nothing, even along the work.
        var (extracted, _, _) = ColdWorkReview(.28, MechanicCardStatus.Extracted);
        Assert.Equal(Outcome(today, work, gear).Needs["pawn"].Mood, Outcome(extracted, work, gear).Needs["pawn"].Mood);
    }

    // #3330: the wearer harvests outdoors at -10 C, comfortable from 10 C and observing EnvironmentCold at -12. No warm garment
    // is physically available; a tailor can craft a parka (Cold 20, Normal quality). Replacing a worn jacket (Cold 5) over a
    // shirt, the parka relieves nothing, so only its comfort shift (-15 from the craft's completion, leaving a deficit of 5,
    // below the card's first band) can make the comparator select it. Bare, it also relieves torso coverage.
    private static (ForecastCoupledSelection Result, ForecastCommitment[] Parkas) ColdParka(double mood,
        MechanicCardStatus status = MechanicCardStatus.Verified, bool shirt = true)
    {
        var (facts, parkas, work) = ColdParkaReview(mood, status, shirt);
        return (Select(facts, null, work), parkas);
    }

    private static (RoundsFacts Facts, ForecastCommitment[] Parkas, ForecastCommitment Work) ColdParkaReview(double mood,
        MechanicCardStatus status = MechanicCardStatus.Verified, bool shirt = true)
    {
        static GearOption Garment(string product, GearSlot slot, string layer, double cold, GearSource source) => new()
        {
            ID = product, Definition = new(product), Stuff = new("Cloth"), Slot = slot, Layers = [layer], Groups = ["Torso"],
            Source = source, Quality = 2, Condition = 1, Cold = cold, Cost = 20
        };
        var census = ColonyModelFixtures.NoGear() with
        {
            Stored = default(EquatableArray<RimGovernor.Brain.Policy.GearStock>),
            Pawns = [new() { Pawn = new("pawn"), Loadout = "load",
                Candidates = default(EquatableArray<RimGovernor.Brain.Policy.GearCandidate>),
                LoadoutModel = Known(new GearLoadoutInput { Ambient = -10, ComfortableMin = 10, ComfortableMax = 30,
                    Worn = shirt ? [Garment("Shirt", GearSlot.SkinTorso, "OnSkin", 2, GearSource.Worn), Garment("Jacket", GearSlot.Outer, "Shell", 5, GearSource.Worn)] : [],
                    Options = [Garment("Parka", GearSlot.Outer, "Shell", 20, GearSource.Bill)] }) }]
        };
        var stack = new RecipeIngredientObservation("cloth-stack", "Cloth", 10, new(5, 5), 10)
        { Capacity = 75, ApproachTicks = 5.0, CarryTicks = 5.0, ReturnTicks = 5.0 };
        var recipe = new RecipeActivityOption("parka-tailor", "tailor", "bench", "Tailor", "MakeParka", "Parka", "Cloth", "", "", new(10, 10),
            [stack], 0.0, 20.0, 5.0, new(15, 15)) { RequiredCount = 10 };
        var facts = Review(level: 1, fall: 0, harvest: 200);
        var roster = WorkResourceTestFacts.Create("pawn", "tailor") with
        { Resources = Known<EquatableArray<RimGovernor.Brain.Policy.Amount>>([new(new("Cloth"), 10)]) };
        var template = facts.ForecastObserved.Needs!.Pawns[0];
        facts = facts with
        {
            Gear = Known(census),
            WorkView = WorkRules.WorkResourcesOf(ProductionRules.WorkAdmissionEvidenceOf(roster, Known<EquatableArray<ProductionBatch>>([])), Known<EquatableArray<ProductionBatch>>([]), []),
            ForecastObserved = facts.ForecastObserved with
            {
                Needs = facts.ForecastObserved.Needs with { Pawns = [template, template with { Pawn = "tailor" }] },
                Coupled = facts.ForecastObserved.Coupled! with { Clothing = new() { Context = Context, Options = [recipe], Retained = [] } }
            }
        };
        var candidates = ForecastCoupledCandidates.GenerateCoupledActivities(facts, Horizon)!;
        var wearer = template with
        { Mood = template.Mood! with { Level = mood, Target = mood, UnclampedTarget = mood }, ComfortableMin = 10.0, ComfortableMax = 30.0 };
        var cells = candidates.Context.Activities.Activities.SelectMany(t => new[] { t.Location, t.EndLocation })
            .Where(l => l.StartsWith("cell/", StringComparison.Ordinal)).Distinct().Select(l => l.Split('/'))
            .Select(p => new Cell(int.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture), int.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture)));
        // Each ingredient allocation is its own alternative.
        var parkas = candidates.Alternatives.Where(c => c.ExclusiveOutcome == "clothing/pawn/Parka").ToArray();
        Assert.NotEmpty(parkas);
        facts = facts with { CoupledAlternatives = (candidates with
        {
            Alternatives = EquatableArray.From(parkas),
            Context = candidates.Context with
            {
                Needs = candidates.Context.Needs with { Pawns = [wearer, template with { Pawn = "tailor", Mood = template.Mood! with { Level = .9, Target = .9, UnclampedTarget = .9 }}] },
                Shelters = [],
                Thermal = CoupledThermalTests.NoThermal with { Cells = EquatableArray.From(cells.Select(c => new ThermalCell(c, -10.0, "none", false, false, "", false, false, 0, false))) },
                Cards = new Dictionary<string, MechanicCard> { ["EnvironmentCold"] = MechanicCards.ByDef["EnvironmentCold"] with { Status = status } },
                CardPawns = new Dictionary<string, CardPawnFacts> { ["pawn"] = new()
                {
                    Traits = Known<EquatableArray<string>>([]), Hediffs = Known<EquatableArray<string>>([]),
                    IdeologyActive = Known(false), Precepts = Known<EquatableArray<string>>([])
                } },
                ObservedThoughts = new Dictionary<string, Fact<EquatableArray<MoodThought>>> { ["pawn"] = Known<EquatableArray<MoodThought>>([new("EnvironmentCold", -12)]) },
            }
        }).Priced(Horizon) };
        // The wearer's outdoor harvest is the incumbent work the parka would warm.
        var work = candidates.Alternatives.Where(c => c.ActivityCompletions.Count > 0 && c.ExclusiveOutcome.StartsWith("raw/", StringComparison.Ordinal))
            .First(c => Outcome(facts, c).ThermalExposure.Single(t => t.Pawn == "pawn").AssessedTicks > 0);
        return (facts, parkas, work);
    }

    [Fact]
    public void ACraftedGarmentsOutfitChangeTakesEffectAtItsDelivery()
    {
        // #3621: the parka overlays the worn outfit from the tick its delivery completes, which the outcome reports for the harm term.
        var (facts, parkas, _) = ColdParkaReview(.28);
        var parka = parkas[0];
        var change = Assert.Single(parka.OutfitChanges);
        Assert.True(change.Overlay);
        Assert.Equal("Parka", Assert.Single(change.Items).ID);
        var outcome = Outcome(facts, parka);
        var delivery = outcome.Plan.Allocations.Single(a => a.Activity == parka.ActivityCompletions[0]);
        Assert.Equal([new ForecastEffectiveTick(parka.Id, Known(delivery.End))], outcome.OutfitTicks);
        Assert.True(delivery.End > 0);
    }

    [Fact]
    public void ClothingCandidatesCarryTheCraftedGarmentsComfortShiftAndNoThermalRelief()
    {
        Assert.All(ColdParka(.28).Parkas, parka =>
        {
            Assert.Equal([new ForecastComfortShift("pawn", -15, 0)], parka.ComfortShifts);
            Assert.Null(parka.Outcome!.Requirements);
        });
        Assert.All(ColdParka(.28, shirt: false).Parkas, bare =>
        {
            Assert.Equal([new ForecastComfortShift("pawn", -20, 0)], bare.ComfortShifts);
            // #3816: coverage is no relief; the apparel thoughts the garment changes are its credit.
            Assert.Null(bare.Outcome!.Requirements);
        });
    }

    [Fact]
    public void APawnNearABreakAtColdOutdoorWorkGetsAParkaCrafted()
    {
        var (result, parkas) = ColdParka(.28);
        Assert.Contains(Assert.Single(result.Selection.Selected), parkas.Select(p => p.Id));
        Assert.True(result.SelectedVector.BreakRisk.Value < result.BaselineVector.BreakRisk.Value);
    }

    [Fact]
    public void AContentPawnGetsNoParkaForWarmthAlone()
    {
        Assert.Empty(ColdParka(.9).Result.Selection.Selected);
    }

    [Fact]
    public void ATemperatureCardThatIsNotLiveCraftsNothingForWarmth() =>
        Assert.Empty(ColdParka(.28, MechanicCardStatus.Extracted).Result.Selection.Selected);

    [Fact]
    public void TheCoupledGeneratorOffersTheShippedCardsFilterCandidateBesideTheClothingFamily()
    {
        // #3285: the shipped (verified) DeadMansApparel card over the gear census, behind the equipment Need's enablement.
        var model = EffectSourceCandidatesTests.Pawn().Gear;
        model = model with { Role = model.Role with { Work = model.Role.Work with { Traits = Known<EquatableArray<PawnTrait>>([]) } } };
        var facts = Review() with
        {
            Gear = Known(ColonyModelFixtures.NoGear() with { Stored = [], Pawns = [new GearPawn { Pawn = new("pawn"), LoadoutModel = Known(model), Candidates = [] }] }),
            MedicalPawns = Known<EquatableArray<CarePawn>>([new() { Id = new("pawn"), Conditions = Known<EquatableArray<CareCondition>>([]) }]),
        };
        var gear = ForecastCoupledCandidates.GenerateCoupledActivities(facts, Horizon)!.Alternatives.Where(a => a.ExclusiveOutcome == "gear/pawn").ToArray();
        Assert.Contains(gear, a => a.Id == "gear/pawn/disallow-tainted/cold30-heat0");
        var disabled = facts with { AvailableNeeds = Known<EquatableArray<NeedId>>([AdminFamily.WorkAssignments]) };
        Assert.DoesNotContain(ForecastCoupledCandidates.GenerateCoupledActivities(disabled, Horizon)!.Alternatives, a => a.ExclusiveOutcome.StartsWith("gear/", StringComparison.Ordinal));
    }

    [Fact]
    public void ABloodlustPawnHasNoCandidateSoItsFilterKeepsTheDefault()
    {
        // #3285 parity: the card is nullified, so nothing is compared and the pawn keeps tainted disallowed.
        var generated = EffectSourcesRules.GenerateEffectSources(EffectSourceCandidatesTests.Facts(pawns: EffectSourceCandidatesTests.Pawn(traits: ["Bloodlust"])),
            new Dictionary<string, MechanicCard> { ["DeadMansApparel"] = MechanicCards.ByDef["DeadMansApparel"] with { Status = MechanicCardStatus.Verified } });
        Assert.Empty(generated.Roots);
        var facts = Review();
        facts = facts with { CoupledAlternatives = facts.CoupledAlternatives! with { Alternatives = [.. facts.CoupledAlternatives.Alternatives, .. generated.Roots] } };
        Assert.Empty(EffectSourcesRules.GearChoices(facts.CoupledAlternatives, Select(facts)));
        Assert.Empty(EffectSourcesRules.GearChoices(null, null));
    }

    [Fact]
    public void ExhaustedSearchPreservesFeasibleCurrentCommitmentAndExposesBound()
    {
        var facts = Review(level: 1, fall: 0);
        var current = facts.CoupledAlternatives!.Alternatives.Single(c => c.ExclusiveOutcome == "raw/0" && !c.Id.Contains("/after/", StringComparison.Ordinal));
        var result = Select(facts, new(0), current);
        Assert.True(result.Selection.SearchTruncated);
        Assert.Contains("portfolio_search_truncated", result.Selection.Findings);
        Assert.Empty(result.Selection.Selected);
        Assert.Equal(Known(true), result.Selected.Feasible);
        Assert.Equal(result.Baseline.Plan, result.Selected.Plan);
        Assert.Contains(result.Selected.Plan.Allocations, a => a.Activity == SourceTask(facts, "plant-a"));
    }

    private static (string Phase, int Expansions, int ScheduleSearches, int OutcomeEvaluations)[] Spend(ForecastCoupledSelection selection) =>
        selection.SearchPhases.Select(p => (p.Phase, p.Expansions, p.ScheduleSearches, p.OutcomeEvaluations)).ToArray();

    // #3722: the allowance is a count of outcome evaluations, a unit per evaluation started and none for a cached schedule or outcome.
    // The search never exceeds it, reports its end exactly when it reached it (a search that converged first ends with units unspent),
    // and the same input spends the same units. A zero allowance evaluates the mandatory baseline alone; one evaluates one outcome.
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(7)] [InlineData(40)]
    public void TheEvaluationBudgetIsNeverExceededAndItsEndIsReported(int evaluations)
    {
        var facts = Review();
        var first = Select(facts, new(evaluations));
        var again = Select(facts, new(evaluations));
        var why = string.Join(",", Spend(first).Select(p => p.ToString())) + " findings=" + string.Join(";", first.Selection.Findings);
        Assert.Equal(new ForecastSearchBudget(evaluations), first.Budget);
        var spent = first.OutcomeEvaluations - first.BaselineEvaluations;
        Assert.True(spent <= evaluations, why);
        Assert.True((spent == evaluations) == first.Selection.Findings.Contains(ForecastCoupledSelection.OutcomeEvaluationsExhausted), why);
        if (evaluations <= 1) Assert.True(spent == evaluations, why);
        Assert.DoesNotContain(ForecastCoupledSelection.TraversalBoundExhausted, first.Selection.Findings);
        Assert.True(first.Selection.SearchTruncated);
        // The mandatory baseline is counted, reported as its own phase and charged to no allowance.
        Assert.True(first.BaselineEvaluations > 0);
        Assert.Equal(first.BaselineEvaluations, first.SearchPhases[0].OutcomeEvaluations);
        Assert.Equal(first.BaselineExpansions, first.SearchPhases[0].Expansions);
        Assert.Equal(first.OutcomeEvaluations, first.SearchPhases.Sum(p => p.OutcomeEvaluations));
        Assert.Equal(first.Selection.Expansions, first.SearchPhases.Sum(p => p.Expansions));
        Assert.True(first.Selection.Expansions - first.BaselineExpansions <= ForecastSearchBudget.DefaultTraversal);
        Assert.Equal(Spend(first), Spend(again));
        Assert.Equal(first.Selection.Selected, again.Selection.Selected);
        Assert.Equal(first.SelectedVector, again.SelectedVector);
    }

    [Fact]
    public void ANegativeBudgetIsRefused()
    {
        var facts = Review();
        Assert.Throws<DomainException>(() => Select(facts, new(-1)));
        Assert.Throws<DomainException>(() => Select(facts, new(1, Traversal: -1)));
    }

    // #3722: the traversal bound on scheduler expansions is never exceeded and ends the search on its own when it is reached, on a
    // plain graph and on one whose harvest is unknown, so a search of duplicate, refused or unknown-heavy roots terminates whatever
    // its evaluation allowance. A bound of zero runs the mandatory baseline alone.
    [Theory]
    [InlineData(false, 0)] [InlineData(false, 1)] [InlineData(false, 24)] [InlineData(false, 700)] [InlineData(true, 24)]
    public void TheTraversalBoundIsNeverExceededAndItsEndIsReported(bool unknownHarvest, int traversal)
    {
        var facts = Review(unknownHarvest: unknownHarvest);
        var budget = new ForecastSearchBudget(ForecastSearchBudget.DefaultEvaluations, Traversal: traversal);
        var first = Select(facts, budget);
        var again = Select(facts, budget);
        var why = string.Join(",", Spend(first).Select(p => p.ToString())) + " findings=" + string.Join(";", first.Selection.Findings);
        Assert.Equal(budget, first.Budget);
        var spent = first.Selection.Expansions - first.BaselineExpansions;
        Assert.True(spent <= traversal, why);
        Assert.True((spent == traversal) == first.Selection.Findings.Contains(ForecastCoupledSelection.TraversalBoundExhausted), why);
        if (traversal <= 1 && !unknownHarvest) Assert.True(spent == traversal, why);
        Assert.DoesNotContain(ForecastCoupledSelection.OutcomeEvaluationsExhausted, first.Selection.Findings);
        Assert.True(first.Selection.SearchTruncated);
        Assert.True(first.OutcomeEvaluations - first.BaselineEvaluations < ForecastSearchBudget.DefaultEvaluations);
        // The baseline was still evaluated, at its full bound.
        Assert.True(first.BaselineEvaluations > 0);
        Assert.Equal(first.Selection.Expansions, first.SearchPhases.Sum(p => p.Expansions));
        Assert.Equal(first.OutcomeEvaluations, first.SearchPhases.Sum(p => p.OutcomeEvaluations));
        Assert.Equal(Spend(first), Spend(again));
        Assert.Equal(first.Selection.Selected, again.Selection.Selected);
    }

    // #3722: the warm start's screen draws on the same allowance as every later phase; a unit it spends is not spent again.
    [Fact]
    public void TheWarmStartSpendsFromTheOneAllowance()
    {
        var facts = Review();
        var cold = Select(facts);
        var warm = SelectWarm(facts, cold.Selection.Selected, new(1));
        Assert.Equal("seeded", warm.WarmStart);
        Assert.Equal("baseline warm", string.Join(" ", warm.SearchPhases.Select(p => p.Phase)));
        Assert.Equal(1, Assert.Single(warm.SearchPhases, p => p.Phase == "warm").OutcomeEvaluations);
        Assert.Equal(1, warm.OutcomeEvaluations - warm.BaselineEvaluations);
        Assert.Contains(ForecastCoupledSelection.OutcomeEvaluationsExhausted, warm.Selection.Findings);
        // With the allowance the cold search had, the warm start leads, its screen is counted in the warm phase, and the phases still
        // sum to the totals.
        var afforded = SelectWarm(facts, cold.Selection.Selected, cold.Budget);
        Assert.Equal("warm", afforded.SelectedPhase);
        Assert.Equal(cold.Selection.Selected, afforded.Selection.Selected);
        Assert.True(Assert.Single(afforded.SearchPhases, p => p.Phase == "warm").OutcomeEvaluations > 0);
        Assert.Equal(afforded.OutcomeEvaluations, afforded.SearchPhases.Sum(p => p.OutcomeEvaluations));
        Assert.Equal(afforded.Selection.Expansions, afforded.SearchPhases.Sum(p => p.Expansions));
        Assert.DoesNotContain(ForecastCoupledSelection.OutcomeEvaluationsExhausted, afforded.Selection.Findings);
    }

    // #3722: the ledger every nested search draws on. Mandatory work is counted and reported but charged to no allowance; the
    // discretionary allowances are spent exactly and nothing a nested call does extends them.
    [Fact]
    public void TheLedgerChargesDiscretionaryWorkOnlyAndNeverExtendsItself()
    {
        var ledger = new ForecastSearchLedger(new(2, Traversal: 10));
        ledger.Mandatory = true;
        Assert.True(ledger.TryStartEvaluation());
        ledger.SpendExpansions(7);
        Assert.Equal(4096, ledger.SchedulerBound(4096));
        ledger.Mandatory = false;
        Assert.Equal((1, 7, 1, 7), (ledger.Evaluations, ledger.Expansions, ledger.BaselineEvaluations, ledger.BaselineExpansions));
        Assert.Equal((2, 10), (ledger.EvaluationsLeft, ledger.TraversalLeft));
        Assert.False(ledger.Exhausted);
        Assert.True(ledger.TryStartEvaluation());
        Assert.True(ledger.TryStartEvaluation());
        Assert.False(ledger.TryStartEvaluation());
        Assert.False(ledger.TryStartEvaluation());
        Assert.Equal(3, ledger.Evaluations);
        Assert.Equal(2, ledger.DiscretionaryEvaluations);
        Assert.True(ledger.EvaluationsExhausted);
        Assert.True(ledger.Exhausted);
        Assert.False(ledger.TraversalExhausted);
        Assert.Equal(10, ledger.SchedulerBound(64));
        ledger.SpendExpansions(6);
        Assert.Equal(4, ledger.SchedulerBound(64));
        ledger.SpendExpansions(4);
        Assert.Equal(0, ledger.SchedulerBound(64));
        Assert.True(ledger.TraversalExhausted);
        Assert.Equal((17, 10), (ledger.Expansions, ledger.DiscretionaryExpansions));
        // Mandatory work still runs once the allowances are spent, charged to the baseline, not to them.
        ledger.Mandatory = true;
        Assert.True(ledger.TryStartEvaluation());
        Assert.Equal(64, ledger.SchedulerBound(64));
        ledger.SpendExpansions(3);
        Assert.Equal((2, 10), (ledger.BaselineEvaluations, ledger.BaselineExpansions));
        Assert.Equal((2, 10), (ledger.DiscretionaryEvaluations, ledger.DiscretionaryExpansions));
        Assert.Equal((4, 20), (ledger.Evaluations, ledger.Expansions));
    }
}
