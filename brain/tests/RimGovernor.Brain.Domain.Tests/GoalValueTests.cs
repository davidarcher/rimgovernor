using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>Goal value in the comparator (#3503): importance and deadline in capability-days, the risk and pace tables, the infeasible status.</summary>
public sealed class GoalValueTests
{
    private const double Hour = GameTime.TicksPerHour;

    private static WorkResourceView Resources()
    {
        var facts = WorkResourceTestFacts.Create("pawn") with
        { Resources = [] };
        return WorkRules.WorkResourcesOf(ProductionRules.WorkAdmissionEvidenceOf(facts, new ProductionBatchCensus.Read([])),
            new ProductionBatchCensus.Read([]), []);
    }

    // A research goal on a 5,000-point project at 1 point per tick: one hour of work (2,500 points) is half the goal.
    private static ForecastCoupledContext Context(Importance importance, int? deadline = null, long tick = 0) => new()
    {
        Activities = new() { Deadline = 0, Activities = [new("research/pawn/bench", "bench", "bench", []) { ProjectProgress = new("Smithing", 1) }] },
        Needs = NeedsFixtures.Empty with { Tick = new(tick) }, Resources = Resources(),
        GoalValues = [new("goals/research-project/1", importance, GoalDeadline.Of(deadline)) { Research = [new("Smithing", 1.0 / 5000)] }],
    };

    private static ForecastCoupledOutcome Outcome(double researchTicks) =>
        new(new(researchTicks == 0 ? Array.Empty<ForecastActivityAllocation>() : [new("research/pawn/bench", "pawn", 0, 0, 0, researchTicks, 0, 0, researchTicks, 0, 0)], researchTicks, 0),
            true, new Dictionary<string, ForecastNeedsResult>(), []);

    private static ForecastValueResult Ledger(ForecastCoupledContext context, double researchTicks) =>
        ForecastValue.Of(Outcome(researchTicks), context, GameTime.TicksPerDay, Outcome(0));

    [Fact]
    public void AGoalWithoutADeadlineIsOpenAndOneWithADeadlineIsDueThatDay()
    {
        Assert.IsType<GoalDeadline.Open>(GoalDeadline.Of(null));
        Assert.Equal(new GoalDeadline.Due(12), GoalDeadline.Of(12));
        // An open goal keeps its whole worth however late; a due one loses worth only once its day has passed.
        var policy = new ForecastValuePolicy();
        Assert.Equal(policy.GoalWorthOn(Importance.Normal, new GoalDeadline.Open(), 0), policy.GoalWorthOn(Importance.Normal, new GoalDeadline.Open(), 400));
        Assert.Equal(policy.GoalWorthOn(Importance.Normal, new GoalDeadline.Open(), 5), policy.GoalWorthOn(Importance.Normal, new GoalDeadline.Due(12), 5));
        Assert.True(policy.GoalWorthOn(Importance.Normal, new GoalDeadline.Due(12), 40) < policy.GoalWorthOn(Importance.Normal, new GoalDeadline.Open(), 40));
    }

    [Theory]
    [InlineData(Importance.Low, 0.25)]
    [InlineData(Importance.Normal, 1.0)]
    [InlineData(Importance.High, 4.0)]
    public void AnHourOfResearchAtAGoalsProjectEarnsTheImportanceValuesShareOfTheGoal(Importance importance, double gained)
    {
        var context = Context(importance);
        var research = Ledger(context, Hour);
        var idle = Ledger(context, 0);
        Assert.Equal(-gained, research.Goals, 9);
        Assert.Equal(0, idle.Goals);
        // The portfolio that spends the hour outranks idle labor by exactly the goal's value.
        Assert.Equal(-gained, research.LossAgainst(idle), 9);
        var contribution = Assert.Single(research.GoalContributions);
        Assert.Equal(("goals/research-project/1", 0.5, gained * 2, gained), (contribution.Goal, contribution.Progress, contribution.Worth, contribution.Value));
    }

    [Fact]
    public void AHighGoalOutranksIdleLaborWhileALowOneLosesToAWellbeingGainTheCurrencyPricesAboveIt()
    {
        // Mood-days gained at the default wellbeing weight: 0.4 capability-days, between Low (0.25 for the hour) and High (4).
        var wellbeing = new ForecastValuePolicy().WellbeingWeight * 4;
        ForecastConsequenceVector Vector(ForecastValueResult goals, double wellbeingLoss) => new(
            EquatableArray.From(Enumerable.Repeat<ForecastTerm>(new ForecastTerm.Assessed(0d), 5).ToArray()), EquatableArray.From(Enumerable.Repeat<ForecastTerm>(new ForecastTerm.Assessed(0d), 6).ToArray()),
            new ForecastTerm.Assessed(0d), new ForecastTerm.Assessed(0d), 0, 0, [])
        { Value = new(goals with { Wellbeing = new LedgerTerm.Priced(wellbeingLoss) }, new()) };

        var high = Vector(Ledger(Context(Importance.High), Hour), 0);
        var low = Vector(Ledger(Context(Importance.Low), Hour), 0);
        var idle = Vector(Ledger(Context(Importance.High), 0), 0);
        var happier = Vector(Ledger(Context(Importance.Low), 0), -wellbeing);

        Assert.True(ForecastCoupledSelection.CompareConsequences(high, idle) < 0);
        Assert.True(ForecastCoupledSelection.CompareConsequences(low, idle) < 0);
        Assert.True(ForecastCoupledSelection.CompareConsequences(happier, low) < 0);
        Assert.True(ForecastCoupledSelection.CompareConsequences(high, happier) < 0);
    }

    [Fact]
    public void ThePastDeadlineValueDecaysPerDayToTheFloor()
    {
        var policy = new ForecastValuePolicy();
        const long Day10 = 10L * GameTime.TicksPerDay;
        Assert.Equal(8, policy.GoalWorth(Importance.High, new GoalDeadline.Open(), Day10 * 50));
        Assert.Equal(8, policy.GoalWorth(Importance.High, new GoalDeadline.Due(10), Day10));
        Assert.Equal(8, policy.GoalWorth(Importance.High, new GoalDeadline.Due(11), Day10));
        Assert.Equal(8 * policy.GoalDeadlineDecay, policy.GoalWorth(Importance.High, new GoalDeadline.Due(9), Day10), 9);
        Assert.Equal(8 * Math.Pow(policy.GoalDeadlineDecay, 3), policy.GoalWorth(Importance.High, new GoalDeadline.Due(7), Day10), 9);
        Assert.Equal(8 * policy.GoalDeadlineFloor, policy.GoalWorth(Importance.High, new GoalDeadline.Due(1), 30L * GameTime.TicksPerDay), 9);
        // The same hour of research is worth the decayed share, read from the observed tick.
        Assert.Equal(-4 * policy.GoalDeadlineDecay, Ledger(Context(Importance.High, deadline: 9, tick: Day10), Hour).Goals, 9);
    }

    [Fact]
    public void ARiskProfileAndPaceHaveARowEachAndCautionIsStricterThanBoldness()
    {
        Assert.Equal(Enum.GetValues<RiskProfile>().Order(), ForecastValuePolicy.RiskRows.Keys.Order());
        Assert.Equal(Enum.GetValues<Pace>().Order(), ForecastValuePolicy.PaceWellbeingWeight.Keys.Order());
        Assert.Equal(new ForecastValuePolicy(), ForecastValuePolicy.For(new PreferencesRecord()));

        var cautious = ForecastValuePolicy.For(new() { RiskProfile = RiskProfile.Cautious });
        var normal = ForecastValuePolicy.For(new());
        var bold = ForecastValuePolicy.For(new() { RiskProfile = RiskProfile.Bold });
        Assert.True(cautious.DeathRiskFloor < normal.DeathRiskFloor && normal.DeathRiskFloor < bold.DeathRiskFloor);
        Assert.True(cautious.HarmRiskWeight > normal.HarmRiskWeight && normal.HarmRiskWeight > bold.HarmRiskWeight);
        Assert.Equal(normal.BreakRiskCap, cautious.BreakRiskCap);

        Assert.True(ForecastValuePolicy.For(new() { Pace = Pace.Wellbeing }).WellbeingWeight > normal.WellbeingWeight);
        Assert.True(ForecastValuePolicy.For(new() { Pace = Pace.Development }).WellbeingWeight < normal.WellbeingWeight);
    }

    [Fact]
    public void AGoalsWearerIsPaidWhenACommitmentDressesThemAndTheDeliveryIsNoDevelopmentUnit()
    {
        // #3816: the worn-count goal pays the wearer's share when a selected commitment's outfit change dresses that pawn in the
        // garment, a crafted overlay or a swap into a stored one alike; the delivery adds nothing to the development count.
        var duster = new GearOption { ID = "duster", Definition = new("Duster"), Stuff = new("Cloth"), Slot = GearSlot.Outer };
        var crafted = new ForecastCommitment("clothing", [])
        {
            ActivityCompletions = ["done"], Outcome = new("clothing", "clothing/pawn/Duster", "done"),
            OutfitChanges = [new("pawn", [duster]) { Overlay = true }],
        };
        var swap = new ForecastCommitment("gear/pawn/a", []) { OutfitChanges = [new("pawn", [duster with { ID = "stored-duster", Source = GearSource.Stored }])] };
        var other = new ForecastCommitment("gear/other/a", []) { OutfitChanges = [new("other", [duster])] };
        var context = Context(Importance.Normal) with { GoalValues = [new("goals/worn-count/2", Importance.Normal, new GoalDeadline.Open()) { Wearers = [new("pawn", "Duster", "", .5)] }] };
        var outcome = Outcome(0);

        Assert.Equal(0, ForecastCoupledSelection.CoupledConsequences(outcome, [crafted]).Development);
        Assert.Equal(-1, ForecastValue.Of(outcome, context, GameTime.TicksPerDay, Outcome(0), [crafted]).Goals, 9);
        Assert.Equal(-1, ForecastValue.Of(outcome, context, GameTime.TicksPerDay, Outcome(0), [swap]).Goals, 9);
        // Once per wearer, and only the wearer the goal names; a goal on another stuff is not met by cloth.
        Assert.Equal(-1, ForecastValue.Of(outcome, context, GameTime.TicksPerDay, Outcome(0), [crafted, swap]).Goals, 9);
        Assert.Equal(0, ForecastValue.Of(outcome, context, GameTime.TicksPerDay, Outcome(0), [other]).Goals);
        var devilstrand = context with { GoalValues = [new("goals/worn-count/2", Importance.Normal, new GoalDeadline.Open()) { Wearers = [new("pawn", "Duster", "DevilstrandCloth", .5)] }] };
        Assert.Equal(0, ForecastValue.Of(outcome, devilstrand, GameTime.TicksPerDay, Outcome(0), [crafted]).Goals);
    }

    [Fact]
    public void AGoalWhoseEveryRouteBreaksAFloorIsInfeasibleAndNamesIt()
    {
        var goal = new GoalRecord { Id = 4, Kind = GoalKind.ResearchProject, Target = "Smithing" };
        var facts = new GoalsFacts
        {
            Goals = [goal],
            Research = new ResearchFacts { Projects = [new("Smithing")], Finished = [] },
            Tier = Known(TechTier.Camp),
        };
        Assert.Equal(GoalState.Pursuing, GoalRules.Evaluate(goal, facts).State);

        var infeasible = facts with { Infeasible = new Dictionary<string, string> { [GoalRules.NeedOf(goal).Value] = "urgent_zero" } };
        var status = GoalRules.Evaluate(goal, infeasible);
        Assert.Equal((GoalState.Infeasible, "urgent_zero"), (status.State, status.Reason));
        Assert.Equal(Satisfaction.Unmet, status.Satisfaction);

        // A met goal is not reported infeasible.
        var done = infeasible with { Research = new ResearchFacts { Projects = [new("Smithing")], Finished = [new("Smithing")] } };
        Assert.Equal(GoalState.Met, GoalRules.Evaluate(goal, done).State);
    }

    [Fact]
    public void AGoalsValueRowsFollowItsEffectiveImportanceDeadlineAndRemainingResearch()
    {
        var goal = new GoalRecord { Id = 4, Kind = GoalKind.ResearchProject, Target = "Smithing", Theme = ThemeMembership.Of(9) };
        var theme = new ThemeRecord { Id = 9, Name = "t", DeadlineDay = GoalDeadline.Of(30), Members = [4] };
        var facts = new GoalsFacts
        {
            Goals = [goal with { ImportanceOverride = ImportanceChoice.Of(Importance.High) }],
            Themes = [theme],
            Research = new ResearchFacts
            {
                Projects = [new("Smithing")], Finished = [],
                Details = new Dictionary<string, ResearchProjectFacts> { ["Smithing"] = new() { Cost = 800, Progress = .5 } },
            },
            Tier = Known(TechTier.Camp),
        };
        var row = Assert.Single(GoalRules.ValuesOf(facts));
        Assert.Equal(("goals/research-project/4", Importance.High, (GoalDeadline)new GoalDeadline.Due(30)), (row.Goal, row.Importance, row.Deadline));
        // Half of 800 points remain, so a point is 1/400 of the goal.
        Assert.Equal(1.0 / 400, Assert.Single(row.Research).SharePerPoint, 12);
        Assert.Empty(GoalRules.ValuesOf(facts with { Goals = [] }));
    }

    [Fact]
    public void ASelectionFindingNamesTheFloorOfAnInfeasibleGoal()
    {
        var selection = new ForecastCoupledSelection(new(["a"], new([], 0, 0),
            ["goal_infeasible:goals/research-project/4:break_risk_cap", "harm_capability_unassessed"], 0, false),
            null!, null!, null!, null!, [], 0, false);
        var infeasible = GoalRules.InfeasibleOf(selection);
        Assert.Equal("break_risk_cap", Assert.Single(infeasible).Value);
        Assert.Equal("goals/research-project/4", Assert.Single(infeasible).Key);
    }
}
