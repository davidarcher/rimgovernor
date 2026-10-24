using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// #3805: a planned prison is priced by the recruitment it enables. A held prisoner without a prison bed is bedded, chatted
/// to resistance 0 and recruited <c>d</c> days after the room serves, then adds its #3825 value as a colonist a day: the credit
/// is that value times the served days less <c>d</c>.
/// </summary>
public sealed class PrisonCreditTests
{
    private const double Day = GameTime.TicksPerDay;
    private const double Horizon = 30;

    private static readonly MoodBreakModel Breaks = new() { Threshold = 0.35, Tiers = [new(1, 2), new(1, 3), new(1, 5)] };

    private static readonly FoodHaulLegs Legs = new() { Field = true, Bench = true, FieldToStore = 20, SourceToBench = 10, BenchToDish = 5 };

    private static readonly NeedThoughtStage DecentCell = new("PrisonCell", "prison_cell", 3, 4, NeedsFixtures.Situational);

    private static readonly ForecastRoomService Served = new(1, "", Day);

    private static PrisonerRecruitRead Prisoner(ForecastCoupledContext context, bool recruitable = true) => new()
    {
        Pawn = "prisoner", Recruitable = recruitable, Resistance = 10, PrisonBed = false, WildMan = false,
        // Its prison thought now none, the decent cell's +4 brings its instant mood to 0.5: the mood curve is 1 there, and the
        // opinion curve 1 at opinion 0.
        Mood = new(0.5 - 0.04, 0) { CellStages = [DecentCell] },
        Opinions = [new("pawn", 0)],
        Joiner = new()
        {
            Upkeep = context.Needs.Pawns[0].Upkeep,
            Mood = new(0.6, 0, 0.35) { Traits = [], SituationalTraits = [], Memories = [] },
            Needs = context.Needs.Pawns[0].Needs,
        },
    };

    private sealed record Review(ForecastCoupledContext Context, ForecastCoupledOutcome Outcome, ForecastCommitment[] Commitments, double Horizon)
    {
        public IReadOnlyList<RoomGain> Gains() => ForecastPrison.PrisonCredit(Served, Outcome, Commitments, Context, Horizon);

        public string[] Findings() => [.. ForecastPrison.Findings(Served, Outcome, Commitments, Context, Horizon)];
    }

    /// <summary>
    /// A one-colonist construction review short of work (PawnValueTests' production path, its selection the production
    /// review's), its tier-2 bases set, and room 1 a planned prison closing a decent (Impressiveness stage 3) cell of one bed,
    /// served from day 1, the colony holding <paramref name="prisoners"/>.
    /// </summary>
    private static Review Of(Func<ForecastCoupledContext, PrisonerRecruitRead[]> prisoners, bool planned = true, bool warden = true)
    {
        var catalog = Game.WorkSpeedFactsTests.Catalog();
        var facts = ForecastCoupledTests.ConstructionReview(1, 100);
        var observed = facts.ForecastObserved.Coupled!;
        var needs = facts.ForecastObserved.Needs!;
        facts = facts with
        {
            MoodBreak = Breaks, WorkSpeeds = catalog.WorkSpeedFacts(), FoodWork = catalog.FoodWork(new HashSet<string>(StringComparer.Ordinal)),
            ForecastObserved = facts.ForecastObserved with
            {
                Needs = needs with
                {
                    Pawns = [needs.Pawns[0] with { Upkeep = NeedsFixtures.Upkeep with { Speeds = [new("ConstructionSpeed", 1.0), new("CookSpeed", 1.0), new("MoveSpeed", 4.6), new("CarryingCapacity", 75)], FoodPerDay = 1.6, Mood = new(0.6, 0.35) } }],
                },
                Coupled = observed with
                {
                    Construction = observed.Construction with
                    {
                        Options = [.. observed.Construction.Options.Select(o => o with { WorkGiver = "ConstructFinishFrames", Finish = new ConstructionFinish.Finishes(3 * Day, (o.Finish as ConstructionFinish.Finishes)?.SkillWork) })],
                    },
                },
            },
        };
        facts = ForecastCoupledCandidates.ReviewCoupledActivities(facts);
        var coupled = facts.CoupledAlternatives!;
        // The production review's selection (RoundsRules.InspectRounds): its outcome and chosen roots leave the construction short.
        var selected = ForecastCoupledSelection.CompareActivityOutcomes(ForecastCoupled.CoupledForecastRequestOf(new("observed-review", 100),
            ForecastCoupled.TierOneHorizonDays, facts, new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, [], new()), coupled);
        var chosen = selected.Selection.Selected.ToHashSet(StringComparer.Ordinal);
        var input = ColonyModelFixtures.Devilstrand();
        input = input with { Pawns = Known(EquatableArray.From(input.Pawns.Value.Select(p => p with { Pawn = "pawn" }))) };
        var context = coupled.Context with
        {
            ModelBasis = new(input, new()),
            Joiners = new() { Roots = [.. coupled.Alternatives, .. coupled.Incumbents], Work = facts.WorkSpeeds, Food = facts.FoodWork },
            ColonistThoughts = new Dictionary<string, EquatableArray<MoodThought>>(StringComparer.Ordinal) { ["pawn"] = [] },
            Bed = new(true, 0), FoodHaul = planned ? Legs : null, InvestmentHorizonDays = Horizon,
            PlannedRoomRoles = new Dictionary<int, PlannedRole> { [1] = PlannedRole.Prison },
            RoomCredits = PlannedRoomCredits.Shipped,
            Closures = [new(1, "", [], [], [], PlannedRoomPredictionTests.Closed("1", "", "PrisonCell", 40d, 3, 9, 0, false) with { Beds = ["Bed"] })],
        };
        // The colonist's needs read as tier 2 models them (RoundsColonyModel.InputOf reads the model's needs from it), at bed effectiveness 1.
        EquatableArray<NeedRate> rates = [.. input.Pawns.Value[0].Needs.Select(n => NeedsFixtures.Rate(n.Kind, 0.5, n.Fall, n.Threshold) with
        {
            OutdoorShareByTwelfth = n.OutdoorShares, GainPerTickPerEffectiveness = 1,
        })];
        context = context with { Needs = context.Needs with { Pawns = [context.Needs.Pawns[0] with { Needs = rates }] } };
        context = context with
        {
            Needs = context.Needs with { Prison = NeedsFixtures.NoPrison with { Wardens = warden ? [new("pawn", 1)] : [], Prisoners = [.. prisoners(context)] } },
        };
        return new(context, selected.Selected with { ServedRooms = [Served] },
            [.. coupled.Alternatives.Concat(coupled.Incumbents).Where(c => chosen.Contains(c.Id))], selected.Horizon);
    }

    [Fact]
    public void ARecruitableResistanceTenPrisonerInADecentCellIsCreditedItsValueOverTheDaysTheRoomSaves()
    {
        var review = Of(c => [Prisoner(c)]);
        var context = review.Context;
        var shortfall = JoinerMoodRules.ShortfallOf(review.Outcome, review.Commitments, context, review.Horizon);
        Assert.NotEmpty(shortfall.Rows);
        var value = JoinerMoodRules.ValueOf("prisoner", context.Needs.Prison.Prisoners[0].Joiner, shortfall, context).PerDay;
        Assert.True(value > 0);
        // One chat lowers 1 × curve(0.5) 1 × curve(0) 1: ten chats to 0 and one to recruit, two a day.
        var days = ForecastPrison.RecruitDays(10, 1, 2);
        Assert.Equal(5.5, days, 12);
        var gain = Assert.Single(review.Gains());
        Assert.Equal(("prisoner", value), (gain.Subject, gain.PerDay));
        Assert.Equal(-value * days, gain.Once, 9);
        Assert.Contains("prison_mood_held_static", review.Findings());

        // Through the #3796 seam: one investment row, the value over the served days less the recruitment's.
        var rows = new List<ForecastInvestmentContribution>();
        PlannedRoomCredits.Credit(review.Outcome, review.Commitments, context, review.Horizon, rows, new SortedSet<string>(StringComparer.Ordinal));
        var row = Assert.Single(rows);
        Assert.Equal(("prison/1/prisoner", Horizon - 1), (row.Key, row.Units));
        Assert.Equal(-value * (Horizon - 1 - days), row.Value, 9);
    }

    [Fact]
    public void ALoyalPrisonerEarnsNothing()
    {
        var review = Of(c => [Prisoner(c, recruitable: false)]);
        Assert.Empty(review.Gains());
        Assert.Contains(ForecastPrison.Unrecruitable, review.Findings());
        Assert.Contains(ForecastPrison.NoRecruit, review.Findings());
    }

    [Fact]
    public void AColonyWithNoPrisonerEarnsNothing()
    {
        var review = Of(_ => []);
        Assert.Empty(review.Gains());
        Assert.Contains(ForecastPrison.NoPrisoner, review.Findings());
    }

    [Fact]
    public void APrisonerAlreadyInAPrisonBedIsNoneTheRoomRecruits()
    {
        var review = Of(c => [Prisoner(c) with { PrisonBed = true }]);
        Assert.Empty(review.Gains());
        Assert.Contains(ForecastPrison.NoRecruit, review.Findings());
    }

    [Fact]
    public void BeforeTheFirstLayoutPlanTheRoomNamesThePlanItAwaitsInsteadOfThrowing()
    {
        var review = Of(c => [Prisoner(c)], planned: false);
        Assert.Empty(review.Gains());
        Assert.Contains(ForecastPrison.AwaitingPlan, review.Findings());
    }

    [Fact]
    public void AColonyWithoutAWardenRecruitsNobody()
    {
        var review = Of(c => [Prisoner(c)], warden: false);
        Assert.Empty(review.Gains());
        Assert.Contains(ForecastPrison.NoWarden, review.Findings());
    }

    [Fact]
    public void ThePrisonersPrisonThoughtStageMissingIsAnUnreadFact()
    {
        var review = Of(c => [Prisoner(c) with { Mood = new(0.5, 0) { CellStages = [] } }]);
        Assert.Throws<RequiredFactException>(() => review.Gains());
    }

    [Fact]
    public void AJoinersTraitThoughtTheColonistsAlsoHaveCountsOnceAtItsOwnOffset()
    {
        var mood = new JoinerMoodRead(0.5, 0, 0.35) { Traits = [new("Greedy_Room", -4)], SituationalTraits = [], Memories = [new("AteFine", 2)] };
        var colony = new Dictionary<string, double>(StringComparer.Ordinal) { ["Greedy_Room"] = 6, ["AteWithoutTable"] = -3 };
        Assert.Equal(0.5 + ((-3 - 4 + 2) / 100d), JoinerMoodRules.MoodOf(mood, colony)!.Target, 12);
        Assert.Null(JoinerMoodRules.MoodOf(null, colony));
    }

    [Theory]
    [InlineData(10, 1, 5.5)]
    [InlineData(10, 1.5, 4)] // ceil(6.67) = 7 chats to 0, one to recruit
    [InlineData(0, 1, 0.5)] // at resistance 0 the next chat recruits
    [InlineData(3, 1, 2)]
    public void RecruitDaysAreTheChatsToResistanceZeroAndTheRecruitingChat(double resistance, double perChat, double days) =>
        Assert.Equal(days, ForecastPrison.RecruitDays(resistance, perChat, 2), 12);

    [Fact]
    public void AChatThatLowersNothingNeverRecruits() => Assert.Equal(double.PositiveInfinity, ForecastPrison.RecruitDays(10, 0, 2));
}
