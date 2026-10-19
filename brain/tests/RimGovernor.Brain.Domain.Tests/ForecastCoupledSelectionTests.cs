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
    private static readonly ObservationContext Context = new()
        { Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 100 };
    internal static RoundsFacts Review(double level = .2, double fall = .003, double harvest = 10, double expiry = 1000,
        bool reverse = false, bool unknownHarvest = false)
    {
        var facts = WorkResourceTestFacts.Create("pawn");
        var sources = new[] { "plant-a", "plant-b" };
        if (reverse) Array.Reverse(sources);
        facts = facts with
        {
            Resources = Known<EquatableArray<RimGovernor.Brain.Policy.Amount>>([]),
            ForecastObserved = facts.ForecastObserved with
            {
                Needs = new() { Tick = 100, Pawns = [new()
                {
                    Pawn = "pawn", State = "eligible", Mood = Known(.5), MoodTarget = Known(.5),
                    MoodUnclampedTarget = Known(.5), LocalHour = Known(6d), Awake = Known(true),
                    MoodRisePerTick = Known(.12 / GameTime.TicksPerHour), MoodFallPerTick = Known(.08 / GameTime.TicksPerHour),
                    MoodIntervalTicks = Known(150d), MoodFreezesWhileAsleep = Known(true), BreakThreshold = Known(.3),
                    ConditionalThoughts = Known<EquatableArray<NeedConditionalThought>>([]),
                    Memories = Known<EquatableArray<NeedMemory>>([]),
                    Needs = [new("food", Known(level), Known(fall), Known(.3)),
                        new("rest", Known(1d), Known(0d), Known(.3))],
                }] },
                Coupled = new()
                {
                    Tick = 100,
                    Food = new() { Options = EquatableArray.From(sources.Select(source =>
                        new FoodActivityOption("pawn", source, "Rice", new(1, 1), new(1, 1), "Harvest", 2, harvest)
                        { ApproachTicks = Known(0d), DeliveryTicks = Known(10d), HarvestAllowed = unknownHarvest ? default : Known(true),
                            SourceCount = Known(2d) })) },
                    FoodUses = [new("pawn", "Rice", new(1, 1), Known(true), Known(.2), Known(10d), 2, Known(expiry))],
                },
            },
        };
        facts = WorkRules.ReviewWorkResources(facts, Known<EquatableArray<ProductionBatch>>([]), new([]));
        return facts with { CoupledAlternatives = ForecastCoupledCandidates.GenerateCoupledActivities(facts, Horizon)?.Priced() };
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
            Needs = [new("joy", Known(.2), Known(.003), Known(.3))],
            Targets = [new("shared-service", "joy", "", 1, Known(0d), Known(10d), Known(0d), Known(.01))
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
    private static ForecastCoupledSelection Select(RoundsFacts facts, int budget = 16384, params ForecastCommitment[] incumbent)
    {
        var request = ForecastCoupled.CoupledForecastRequestOf(new("world/map/load", 100), Horizon / GameTime.TicksPerDay,
            facts,  new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, EquatableArray.From(incumbent), new());
        return ForecastCoupledSelection.CompareActivityOutcomes(request, facts.CoupledAlternatives!, budget);
    }
    private static readonly string[] NoSuchRoot = ["no/such/root"];
    private static ForecastCoupledSelection SelectWarm(RoundsFacts facts, IReadOnlyList<string> warmStart)
    {
        var request = ForecastCoupled.CoupledForecastRequestOf(new("world/map/load", 100), Horizon / GameTime.TicksPerDay,
            facts, new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, [], new());
        return ForecastCoupledSelection.CompareActivityOutcomes(request, facts.CoupledAlternatives!, warmStart: warmStart);
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
        Assert.True(cold.Selection.Expansions < 65536);
        Assert.True(cold.Selection.SearchTruncated);
        Assert.Contains(cold.SelectedPhase, cold.SearchPhases.Select(p => p.Phase));
        Assert.Equal("", cold.WarmStart);
        // A budget the sweep can finish in runs it to the end: the eight-root productive fixture exhausts its subsets at
        // 65,536 with budget left (the review fixture above has too many roots for any budget to finish its sweep).
        var original = ProductiveLearningTests.NormalFoodFacts();
        var generated = ForecastCoupledCandidates.GenerateCoupledActivities(original, 700)!.Priced(700);
        var exhaustive = ForecastCoupledSelection.CompareActivityOutcomes(PortfolioAllocationTests.Request(original, generated), generated, 65536);
        Assert.Equal("sweep", exhaustive.SearchPhases[^1].Phase);
        Assert.True(exhaustive.Selection.Expansions < 65536);

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
                Harm = new() { Schedule = new() { MajorThreatsPerDay = Known(0d), RaidFraction = Known(1d) } },
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
    public void AColonistWithNoChangeDueIsDeferredToStandby(double level, double fall, bool deferred)
    {
        var facts = Review(level, fall);
        var candidates = facts.CoupledAlternatives!;
        var pawn = candidates.Context.Needs.Pawns[0] with { Location = new(8, 9) };
        candidates = candidates with { Alternatives = [], Context = candidates.Context with
        { Needs = candidates.Context.Needs with { Pawns = [pawn] } } };
        facts = facts with { CoupledAlternatives = candidates };
        var choice = Select(facts);
        if (!deferred) { Assert.Empty(choice.Deferred); return; }
        // #3624: the deferred colonist is named for standby admission; the coupled Method itself binds nothing for it.
        Assert.Equal("pawn", Assert.Single(choice.Deferred));
        Assert.Null(ForecastCoupledSelection.StartCoupledActivity(choice, candidates, 1, "method", 100));
    }

    [Fact]
    public void UnknownNeedsAndExistingClaimsNeverBecomeIdlePermission()
    {
        var facts = Review(1, 0);
        var candidates = facts.CoupledAlternatives!;
        var pawn = candidates.Context.Needs.Pawns[0] with { Location = new(8, 9), Needs = [new("food", default, Known(0d), Known(.3))] };
        facts = facts with { CoupledAlternatives = candidates with { Alternatives = [], Context = candidates.Context with
        { Needs = candidates.Context.Needs with { Pawns = [pawn] } } } };
        Assert.Empty(Select(facts).Deferred);
        pawn = pawn with { Needs = [new("food", Known(1d), Known(0d), Known(.3))] };
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
            pawn with { Needs = [new("food", Known(1d), Known(double.NaN), Known(.3))] },
            pawn with { Needs = [new("food", Known(1d), Known(0d), Known(double.PositiveInfinity))] },
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
            Needs = [new("food", Known(.02), Known(.003), Known(.3)), new("rest", Known(1d), Known(0d), Known(.3))],
            Targets = [new("meal", "food", "MealSimple", 1, Known(0d), Known(10d), Known(.9), default)
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
    [InlineData(.28, true, true)]
    [InlineData(.9, true, false)]
    [InlineData(.28, false, false)]
    public void MoodMemoryWinsOnlyWhereItLowersBreakRisk(double mood, bool thresholdKnown, bool wins)
    {
        // Two equal-labor harvests; one grants a +4, eight-day memory at completion (the sarcophagus thought).
        var facts = Review(level: 1, fall: 0);
        var candidates = facts.CoupledAlternatives!;
        var pawn = candidates.Context.Needs.Pawns[0] with { Mood = Known(mood), MoodTarget = Known(mood), MoodUnclampedTarget = Known(mood),
            BreakThreshold = thresholdKnown ? Known(.3) : default };
        var harvest = new ForecastCommitment("plain", []) { ActivityCompletions = [SourceTask(facts, "plant-a")] };
        var memory = new NeedMemory("KnowBuriedInSarcophagus", 4, Known(8d * GameTime.TicksPerDay), false);
        facts = facts with { CoupledAlternatives = candidates with
        {
            Alternatives = [harvest with { Id = "mood", MoodMemories = [new("pawn", memory)] }, harvest],
            Context = candidates.Context with { Needs = candidates.Context.Needs with { Pawns = [pawn] }, MoodTrajectory = true }
        } };
        var result = Select(facts);
        Assert.Equal(Known(true), result.Selected.Feasible);
        Assert.Equal(thresholdKnown, result.BaselineVector.BreakRisk.IsKnown);
        Assert.Equal(!thresholdKnown, result.BaselineVector.Unknown.Contains("mental_break_probability_unassessed"));
        Assert.Equal(!thresholdKnown, result.Baseline.Needs["pawn"].Findings.Contains("mental_break_probability_unassessed"));
        if (wins)
        {
            Assert.Equal(["mood"], result.Selection.Selected);
            Assert.True(result.SelectedVector.BreakRisk.Value < result.BaselineVector.BreakRisk.Value);
            return;
        }
        // No break risk to remove (or none assessed): the memory ties no-change and the incumbent is kept.
        Assert.Empty(result.Selection.Selected);
        if (thresholdKnown) Assert.Equal(0, result.BaselineVector.BreakRisk.Value);
        // The memory still lowers raw mood pressure; that is not break risk and does not rank.
        var evaluated = result.Rejected.First(r => r.Commitments.SequenceEqual(["mood"])).Consequences!;
        Assert.True(!thresholdKnown || evaluated.MoodDepth.Value < result.BaselineVector.MoodDepth.Value);
    }

    [Theory]
    [InlineData(.28, true)]
    [InlineData(.9, false)]
    public void SituationalChangeWinsOnlyWhereItLowersBreakRisk(double mood, bool wins)
    {
        // A labor-free commitment removing an active -11 situational thought (a dead man's parka) from the forecast start.
        var facts = Review(level: 1, fall: 0);
        var candidates = facts.CoupledAlternatives!;
        var pawn = candidates.Context.Needs.Pawns[0] with { Mood = Known(mood), MoodTarget = Known(mood), MoodUnclampedTarget = Known(mood) };
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
        var pawn = candidates.Context.Needs.Pawns[0] with { Mood = Known(.28), MoodTarget = Known(.28), MoodUnclampedTarget = Known(.28) };
        var memory = new NeedMemory("KnowBuriedInSarcophagus", 4, Known(8d * GameTime.TicksPerDay), false);
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
        { Mood = Known(mood), MoodTarget = Known(mood), MoodUnclampedTarget = Known(mood), ComfortableMin = Known(10.0), ComfortableMax = Known(30.0) };
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
                Thermal = nearBreak ? candidates.Context.Thermal : new()
                {
                    Cells = EquatableArray.From(cells.Select(c => new ThermalCell(c, Known(-10.0), "none", false, false, false, "", false, false, 0, false))),
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
        { Mood = Known(mood), MoodTarget = Known(mood), MoodUnclampedTarget = Known(mood), ComfortableMin = Known(10.0), ComfortableMax = Known(30.0) };
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
                Thermal = new() { Cells = EquatableArray.From(cells.Select(c => new ThermalCell(c, Known(mild ? 20.0 : -10.0), "none", false, false, false, "", false, false, 0, false))) },
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
        // The work covers part of the horizon: the thought stays at the observed constant elsewhere, and says so.
        Assert.Contains("situational_thought_constant_outside_exposure:EnvironmentCold", warm.Findings);
    }

    [Theory]
    [InlineData(.28, true)]
    [InlineData(.9, false)]
    public void TheComparatorTakesTheWarmerOutfitOnlyNearABreak(double mood, bool wins)
    {
        var (facts, work, _) = ColdWorkReview(mood);
        var result = Select(facts, 16384, work);
        Assert.Contains(result.Selected.Plan.Allocations, a => work.ActivityCompletions.Contains(a.Activity));
        if (!wins)
        {
            Assert.Empty(result.Selection.Selected);
            return;
        }
        Assert.Equal(["gear"], result.Selection.Selected);
        Assert.True(result.SelectedVector.BreakRisk.Value < result.BaselineVector.BreakRisk.Value);
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
        var result = Select(facts, 16384, work);
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
        // No outdoor work: the shift has no span to act on; the mood is today's and names the constant thought.
        var idle = Outcome(facts, gear).Needs["pawn"].Mood!;
        var plain = Outcome(today, gear).Needs["pawn"].Mood!;
        Assert.Equal(0, Outcome(facts, gear).ThermalExposure.Single().AssessedTicks);
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
        return (Select(facts, 16384, work), parkas);
    }

    private static (RoundsFacts Facts, ForecastCommitment[] Parkas, ForecastCommitment Work) ColdParkaReview(double mood,
        MechanicCardStatus status = MechanicCardStatus.Verified, bool shirt = true)
    {
        static GearOption Garment(string product, GearSlot slot, string layer, double cold, GearSource source) => new()
        {
            ID = product, Definition = new(product), Stuff = new("Cloth"), Slot = slot, Layers = [layer], Groups = ["Torso"],
            Source = source, Quality = 2, Condition = 1, Cold = cold, Cost = 20
        };
        var census = new GearObservation
        {
            Stored = Known(default(EquatableArray<RimGovernor.Brain.Policy.GearStock>)),
            Pawns = [new() { Pawn = new("pawn"), Loadout = "load",
                Candidates = Known(default(EquatableArray<RimGovernor.Brain.Policy.GearCandidate>)),
                LoadoutModel = Known(new GearLoadoutInput { Ambient = -10, ComfortableMin = 10, ComfortableMax = 30,
                    Worn = shirt ? [Garment("Shirt", GearSlot.SkinTorso, "OnSkin", 2, GearSource.Worn), Garment("Jacket", GearSlot.Outer, "Shell", 5, GearSource.Worn)] : [],
                    Options = [Garment("Parka", GearSlot.Outer, "Shell", 20, GearSource.Bill)] }) }]
        };
        var stack = new RecipeIngredientObservation("cloth-stack", "Cloth", 10, new(5, 5), 10)
        { Capacity = 75, ApproachTicks = Known(5.0), CarryTicks = Known(5.0), ReturnTicks = Known(5.0) };
        var recipe = new RecipeActivityOption("parka-tailor", "tailor", "bench", "Tailor", "MakeParka", "Parka", "Cloth", "", "", new(10, 10),
            [stack], Known(0.0), Known(20.0), Known(5.0), new(15, 15)) { RequiredCount = 10 };
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
                Coupled = facts.ForecastObserved.Coupled! with { Clothing = new() { Known = true, Context = Context, Options = [recipe] } }
            }
        };
        var candidates = ForecastCoupledCandidates.GenerateCoupledActivities(facts, Horizon)!;
        var wearer = template with
        { Mood = Known(mood), MoodTarget = Known(mood), MoodUnclampedTarget = Known(mood), ComfortableMin = Known(10.0), ComfortableMax = Known(30.0) };
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
                Needs = candidates.Context.Needs with { Pawns = [wearer, template with { Pawn = "tailor", Mood = Known(.9), MoodTarget = Known(.9), MoodUnclampedTarget = Known(.9) }] },
                Shelters = [],
                Thermal = new() { Cells = EquatableArray.From(cells.Select(c => new ThermalCell(c, Known(-10.0), "none", false, false, false, "", false, false, 0, false))) },
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
            Assert.Empty(parka.Outcome!.Requirements!.Relief);
        });
        Assert.All(ColdParka(.28, shirt: false).Parkas, bare =>
        {
            Assert.Equal([new ForecastComfortShift("pawn", -20, 0)], bare.ComfortShifts);
            Assert.Equal(["clothing/pawn/coverage/Torso"], bare.Outcome!.Requirements!.Relief.Select(r => r.Need));
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
    public void AContentPawnGetsNoParkaForWarmthAloneButStillForCoverage()
    {
        Assert.Empty(ColdParka(.9).Result.Selection.Selected);
        var (covered, parkas) = ColdParka(.9, shirt: false);
        Assert.Contains(Assert.Single(covered.Selection.Selected), parkas.Select(p => p.Id));
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
            Gear = Known(new GearObservation { Stored = Known<EquatableArray<RimGovernor.Brain.Policy.GearStock>>([]), Pawns = [new GearPawn { Pawn = new("pawn"), LoadoutModel = Known(model), Candidates = Known<EquatableArray<RimGovernor.Brain.Policy.GearCandidate>>([]) }] }),
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
        var result = Select(facts, 0, current);
        Assert.True(result.Selection.SearchTruncated);
        Assert.Contains("portfolio_search_truncated", result.Selection.Findings);
        Assert.Empty(result.Selection.Selected);
        Assert.Equal(Known(true), result.Selected.Feasible);
        Assert.Equal(result.Baseline.Plan, result.Selected.Plan);
        Assert.Contains(result.Selected.Plan.Allocations, a => a.Activity == SourceTask(facts, "plant-a"));
    }
}
