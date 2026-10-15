using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Common;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;

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
        var facts = Review() with { AvailableMethods = Known(EquatableArray.From(capabilities.Methods)) };
        var generated = GenerateCoupledActivities(facts, Horizon);
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
                    Needs = [new("food", Known(level), Known(fall), Known(.3), Known(1d)),
                        new("rest", Known(1d), Known(0d), Known(.3), Known(1d))],
                }] },
                Coupled = new()
                {
                    Tick = 100,
                    Food = new() { Context = Context, Known = true, Options = EquatableArray.From(sources.Select(source =>
                        new FoodActivityOption("pawn", source, "Rice", new(1, 1), new(1, 1), "Harvest", 2, 10, harvest)
                        { ApproachTicks = Known(0d), DeliveryTicks = Known(10d), HarvestAllowed = unknownHarvest ? default : Known(true),
                            SourceCount = Known(2d), NutritionPerUnit = Known(.2) })) },
                    FoodUses = [new("pawn", "Rice", new(1, 1), Known(true), Known(.2), Known(10d), 2, Known(expiry))],
                },
            },
        };
        facts = ReviewWorkResources(facts, Known<EquatableArray<ProductionBatch>>([]), new([]));
        return facts with { CoupledAlternatives = GenerateCoupledActivities(facts, Horizon) };
    }
    private static string SourceTask(RoundsFacts facts, string target) => facts.CoupledAlternatives!.Context.Activities.Activities
        .Single(t => t.Inputs.Count == 0 && t.Outputs.Any(o => o.Lot == "harvest/" + target)).Id;
    private static ForecastCoupledSelection Select(RoundsFacts facts, int budget = 16384, params ForecastCommitment[] incumbent)
    {
        var request = CoupledForecastRequestOf(new("world/map/load", 100), Horizon / GameTime.TicksPerDay,
            facts, new(), new() { Work = ForecastWorkInputsOf(facts) }, EquatableArray.From(incumbent), new());
        return CompareActivityOutcomes(request, facts.CoupledAlternatives!, budget);
    }

    [Fact]
    public void OrdinaryReviewCombinesIndependentFoodSourcesForOneHungryBeneficiary()
    {
        var facts = Review();
        var result = Select(facts);
        Assert.Equal(Known(true), result.Selected.Feasible);
        Assert.True(result.Selected.Plan.Allocations.Length > 0, string.Join(";", result.Selection.Findings));
        Assert.Contains(result.Selected.Plan.Allocations, a => a.Activity == SourceTask(facts, "plant-a"));
        Assert.Contains(result.Selected.Plan.Allocations, a => a.Activity == SourceTask(facts, "plant-b"));
        Assert.Equal(2, result.Selected.Plan.Allocations.Count(a => facts.CoupledAlternatives!.Context.NeedTasks.Any(n => n.Task == a.Activity && n.Kind == "food")));
        Assert.True(result.SelectedVector.Urgent[0].Value < result.BaselineVector.Urgent[0].Value);
        Assert.True(result.Selected.Needs["pawn"].Needs.Single(n => n.Kind == "food").FinalLevel.Value > 0);
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
