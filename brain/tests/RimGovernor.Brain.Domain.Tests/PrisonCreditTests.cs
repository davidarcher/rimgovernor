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
        Mood = new(0.5 - 0.04, 0) { CellStages = [DecentCell], Memories = [] },
        Opinions = [Opinion()],
        Joiner = new()
        {
            Upkeep = context.Needs.Pawns[0].Upkeep,
            Mood = new(0.6, 0, 0.35) { Traits = [], RoomTraits = [], SituationalTraits = [], Memories = [] },
            Needs = context.Needs.Pawns[0].Needs,
        },
    };

    /// <summary>The prisoner's opinion of the warden "pawn": no relation, situational thought or memory, no rapport a chat gives.</summary>
    private static WardenOpinionRead Opinion(double rapport = 0, double tame = 0) =>
        new("pawn", 0, 0, 1, false) { Memories = [], Rapport = rapport, OtherRapport = [], TameChance = tame };

    /// <summary>The warden "pawn" at NegotiationAbility 1, uninspired, with no inspiration it can start.</summary>
    private static WardenRead Warden(InspirationNowRead? inspired = null) =>
        new("pawn", 1) { Inspired = inspired, InspirationBlocked = false, Inspirations = [] };

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
    private static Review Of(Func<ForecastCoupledContext, PrisonerRecruitRead[]> prisoners, bool planned = true, bool warden = true, WardenRead? wardenRead = null)
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
        var selected = ForecastCoupledSelection.CompareActivityOutcomes(ForecastCoupled.CoupledForecastRequestOf(new(100),
            ForecastCoupled.TierOneHorizonDays, facts, new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, []), coupled);
        var chosen = selected.Selection.Selected.ToHashSet(StringComparer.Ordinal);
        var input = ColonyModelFixtures.Devilstrand();
        input = input with { Pawns = EquatableArray.From(input.Pawns.Select(p => p with { Pawn = "pawn" })) };
        var context = coupled.Context with
        {
            ModelBasis = new(input, new() { MedicineTendsPerDay = 0 }),
            Joiners = new() { Roots = [.. coupled.Alternatives, .. coupled.Incumbents], Work = facts.WorkSpeeds, Food = facts.FoodWork },
            ColonistThoughts = new Dictionary<string, EquatableArray<MoodThought>>(StringComparer.Ordinal) { ["pawn"] = [] },
            Bed = new(true, 0), FoodHaul = planned ? Legs : null, InvestmentHorizonDays = Horizon,
            PlannedRoomRoles = new Dictionary<int, PlannedRole> { [1] = PlannedRole.Prison },
            RoomCredits = PlannedRoomCredits.Shipped,
            Closures = [new(1, "", [], [], [], PlannedRoomPredictionTests.Closed("1", "", "PrisonCell", 40d, 3, 9, 0, false) with { Beds = ["Bed"] })],
        };
        // The colonist's needs read as tier 2 models them (RoundsColonyModel.InputOf reads the model's needs from it), at bed effectiveness 1.
        EquatableArray<NeedRate> rates = [.. input.Pawns[0].Needs.Select(n => NeedsFixtures.Rate(n.Kind, 0.5, n.Fall, n.Threshold) with
        {
            OutdoorShareByTwelfth = n.OutdoorShares, GainPerTickPerEffectiveness = 1,
        })];
        context = context with { Needs = context.Needs with { Pawns = [context.Needs.Pawns[0] with { Needs = rates }] } };
        context = context with
        {
            Needs = context.Needs with { Prison = NeedsFixtures.NoPrison with { Wardens = warden ? [wardenRead ?? Warden()] : [], Prisoners = [.. prisoners(context)] } },
        };
        return new(context, selected.Selected with { ServedRooms = [Served] },
            [.. coupled.Alternatives.Concat(coupled.Incumbents).Where(c => chosen.Contains(c.Id))], selected.Horizon);
    }

    // The warden's work one chat job takes (#3899): the shortfall rows its toil ticks a day fill, a job's share.
    private static double LostPerJob(Review review)
    {
        var shortfall = JoinerMoodRules.ShortfallOf(review.Outcome, review.Commitments, review.Context, review.Horizon);
        var pawn = review.Context.Needs.Pawns[0];
        return PawnValueRules.WorkAdded(new PawnValueFacts("pawn")
        {
            Speeds = pawn.Upkeep.Speeds.ToDictionary(s => s.Stat, s => s.Value, StringComparer.Ordinal), Incapable = pawn.Upkeep.IncapableWork,
            FreeTicksPerDay = ForecastPrison.ChatJobTicks * 2,
        }, shortfall).Sum(s => s.TicksPerDay) / Day / 2;
    }

    [Fact]
    public void ARecruitableResistanceTenPrisonerInADecentCellIsCreditedItsValueOverTheDaysTheRoomSavesLessTheWardensChats()
    {
        var review = Of(c => [Prisoner(c)]);
        var context = review.Context;
        var shortfall = JoinerMoodRules.ShortfallOf(review.Outcome, review.Commitments, context, review.Horizon);
        Assert.NotEmpty(shortfall.Rows);
        var value = JoinerMoodRules.ValueOf("prisoner", context.Needs.Prison.Prisoners[0].Joiner, shortfall, context).PerDay;
        Assert.True(value > 0);
        // An attempt lowers 1 × curve(0.5) 1 × curve(0) 1: ten jobs to 0, the eleventh recruits, its attempt 1,750 ticks in,
        // jobs half a day apart from the room's service.
        var days = ((10 * Day / 2) + 1750) / Day;
        var lost = LostPerJob(review);
        Assert.True(lost > 0);
        var gain = Assert.Single(review.Gains());
        Assert.Equal(("prisoner", value), (gain.Subject, gain.PerDay));
        Assert.Equal(-(value * days) - (lost * 11), gain.Once, 9);
        Assert.Contains("prison_warden_walk", review.Findings());

        // Through the #3796 seam: one investment row, the value over the served days less the recruitment's and the chats' work.
        var rows = new List<ForecastInvestmentContribution>();
        PlannedRoomCredits.Credit(review.Outcome, review.Commitments, context, review.Horizon, rows, new SortedSet<string>(StringComparer.Ordinal));
        var row = Assert.Single(rows);
        Assert.Equal(("prison/1/prisoner", Horizon - 1), (row.Key, row.Units));
        Assert.Equal(-((value * (Horizon - 1 - days)) - (lost * 11)), row.Value, 9);
    }

    [Fact]
    public void AWardenInspiredToRecruitRecruitsAtTheFirstAttempt()
    {
        var review = Of(c => [Prisoner(c)], wardenRead: Warden(new("Inspired_Recruitment", 1, 8)));
        var value = Assert.Single(review.Gains());
        Assert.Equal(-(value.PerDay * 1750 / Day) - LostPerJob(review), value.Once, 9);
    }

    [Fact]
    public void AnotherInspirationRecruitsNothingAndTheResistancePathHolds()
    {
        var read = NeedsFixtures.NoPrison;
        var prisoner = Bare(10);
        var plain = ForecastPrison.Recruitment(read, Warden(), null, prisoner, 4, Day, 29);
        var other = ForecastPrison.Recruitment(read, Warden(new("Frenzy_Work", 0, 8)), null, prisoner, 4, Day, 29);
        Assert.Equal(plain, other);
        // No mood need: the curve at 1 (InteractionWorker_RecruitAttempt), 1.5 an attempt; seven to 0, the eighth recruits.
        Assert.Equal(8, plain.Jobs, 12);
    }

    // A prisoner with no mood need (the mood curve at 1: 1.5) of <paramref name="resistance"/>, no rapport, opinion 0.
    private static PrisonerRecruitRead Bare(double resistance, bool wild = false, double rapport = 0, double tame = 0) => new()
    {
        Pawn = "prisoner", Recruitable = true, Resistance = resistance, PrisonBed = false, WildMan = wild, Mood = null,
        Opinions = [Opinion(rapport, tame)],
        Joiner = new() { Upkeep = NeedsFixtures.Upkeep, Mood = null, Needs = [] },
    };

    [Fact]
    public void RapportRaisesTheOpinionTwoAChatAndRecruitsSooner()
    {
        var read = NeedsFixtures.NoPrison;
        var opinion = new ForecastPrison.Opinion(read.Rapport, Opinion(2));
        for (var i = 0; i < 5; i++) opinion.Chat(i * 350);
        Assert.Equal(10, opinion.At(1750));
        // Opinion 10 makes the curve 1.05 from the first attempt and more after: fewer than the eight jobs of no rapport.
        Assert.True(ForecastPrison.Recruitment(read, Warden(), null, Bare(10, rapport: 2), null, 0, 29).Jobs < 8);
    }

    [Fact]
    public void RapportStacksToFiftyAPawnAndFadesFromSeventyPercentOfItsDuration()
    {
        var read = NeedsFixtures.NoPrison;
        var opinion = new ForecastPrison.Opinion(read.Rapport, Opinion(1));
        for (var i = 0; i < 60; i++) opinion.Chat(i);
        Assert.Equal(50, opinion.At(60));
        // At 85% of the 20 days each memory counts half (InverseLerp(1, 0.7, 0.85)); past the duration none.
        Assert.Equal(25, opinion.At(0.85 * read.Rapport.DurationTicks + 30));
        Assert.Equal(0, opinion.At(read.Rapport.DurationTicks + 61));
    }

    [Fact]
    public void ARapportMemoryOfAnotherPawnOlderThanTheWardensIsTheOneTheDefLimitDrops()
    {
        var read = NeedsFixtures.NoPrison with { Rapport = NeedsFixtures.Rapport with { StackLimit = 2 } };
        var opinion = new ForecastPrison.Opinion(read.Rapport, Opinion(1) with { OtherRapport = [100] });
        opinion.Chat(0);
        opinion.Chat(1);
        Assert.Equal(2, opinion.At(2));
        opinion.Chat(2);
        Assert.Equal(2, opinion.At(3));
    }

    [Fact]
    public void ACumulativeSocialMemoryMovesOneTowardZeroADay()
    {
        var group = new SocialMemoryGroupRead("Insulted", 1, 0.7) { Members = [new SocialMemoryRead.Cumulative(-5, 0, 100)] };
        var opinion = new ForecastPrison.Opinion(NeedsFixtures.Rapport, Opinion() with { Memories = [group] });
        Assert.Equal(-5, opinion.At(0));
        Assert.Equal(-3, opinion.At(2.5 * Day));
        Assert.Equal(0, opinion.At(6 * Day));
    }

    [Fact]
    public void TheOpinionIsTheRoundedPartsTimesTheHediffFactorAndNoPositiveOneOfAHostilePawn()
    {
        var read = NeedsFixtures.NoPrison;
        Assert.Equal(8, new ForecastPrison.Opinion(read.Rapport, Opinion() with { Relations = 10, Situational = 5, Factor = 0.5 }).At(0));
        Assert.Equal(0, new ForecastPrison.Opinion(read.Rapport, Opinion() with { Relations = 10, Hostile = true }).At(0));
        Assert.Equal(-100, new ForecastPrison.Opinion(read.Rapport, Opinion() with { Relations = -150 }).At(0));
    }

    [Fact]
    public void AMoodMemoryFadesAndExpiresOverTheRecruitment()
    {
        var group = new MemoryGroupRead(0.5) { Members = [NeedsFixtures.Memory("AteWithoutTable", -10, 30000, fades: true), NeedsFixtures.Memory("AteWithoutTable", -4, null, fades: false)] };
        Assert.Equal(-7 * 1.5, ForecastPrison.MoodGroupAt(group, 0), 12);
        Assert.Equal(-4.5 * 1.5, ForecastPrison.MoodGroupAt(group, 15000), 12);
        Assert.Equal(-4, ForecastPrison.MoodGroupAt(group, 40000), 12);
        var mood = new PrisonerMoodRead(0.4, 0) { CellStages = [], Memories = [group] };
        Assert.Equal(0.4 + ((10.5 - 4) / 100), ForecastPrison.Mood(mood, 0, 40000)!.Value, 12);
    }

    [Fact]
    public void AWildManIsTamedAtTheWardensChanceEachJob()
    {
        var read = NeedsFixtures.NoPrison;
        var expected = ForecastPrison.Recruitment(read, Warden(), null, Bare(0, wild: true, tame: 0.5), null, 0, 10);
        // Job k is reached with 0.5^k and tames with half of that, 1,750 ticks after it starts half a day apart.
        double days = 0, jobs = 0, alive = 1;
        for (var k = 0; k * Day / 2 + 1750 <= 10 * Day; k++)
        {
            jobs += alive;
            days += alive * 0.5 * ((k * Day / 2) + 1750) / Day;
            alive *= 0.5;
        }

        Assert.Equal(jobs, expected.Jobs, 9);
        Assert.Equal(days + (alive * 10), expected.Days, 9);
    }

    [Fact]
    public void AHeldWildManIsCreditedLikeARecruit()
    {
        var review = Of(c => [Prisoner(c) with { WildMan = true, Opinions = [Opinion(tame: 1)] }]);
        var gain = Assert.Single(review.Gains());
        Assert.Equal(-(gain.PerDay * 1750 / Day) - LostPerJob(review), gain.Once, 9);
    }

    [Fact]
    public void AFutureInspirationStartsAtTheMoodsMtbAndTheRecruitingDefsShare()
    {
        // At mood 1 the MTB is 10 days: a check's chance 100 / 600,000.
        Assert.Equal(1 - Math.Pow(1 - (100 / (10 * Day)), 600), ForecastPrison.Inspirations.StartChance(1, Day), 12);
        Assert.Equal(0, ForecastPrison.Inspirations.StartChance(0.49, Day));
        Assert.Equal(0, ForecastPrison.Inspirations.StartChance(null, Day));
        var read = NeedsFixtures.NoPrison;
        var never = ForecastPrison.Recruitment(read, Warden(), 1, Bare(1000), null, 0, 29);
        var inspired = Warden() with { Inspirations = [new("Inspired_Recruitment", 1, 8), new("Frenzy_Work", 3, 8)] };
        var sooner = ForecastPrison.Recruitment(read, inspired, 1, Bare(1000), null, 0, 29);
        Assert.True(sooner.Days < never.Days);
        var blocked = ForecastPrison.Recruitment(read, inspired with { InspirationBlocked = true }, 1, Bare(1000), null, 0, 29);
        Assert.Equal(never, blocked);
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
        var review = Of(c => [Prisoner(c) with { Mood = new(0.5, 0) { CellStages = [], Memories = [] } }]);
        Assert.Throws<RequiredFactException>(() => review.Gains());
    }

    [Fact]
    public void AJoinersTraitThoughtTheColonistsAlsoHaveCountsOnceAtItsOwnOffset()
    {
        var mood = new JoinerMoodRead(0.5, 0, 0.35) { Traits = [new("Greedy_Room", -4)], RoomTraits = [], SituationalTraits = [], Memories = [new("AteFine", 2)] };
        var colony = new Dictionary<string, double>(StringComparer.Ordinal) { ["Greedy_Room"] = 6, ["AteWithoutTable"] = -3 };
        Assert.Equal(0.5 + ((-3 - 4 + 2) / 100d), JoinerMoodRules.MoodOf(mood, colony, [NeedsFixtures.Pawn()])!.Target, 12);
        Assert.Null(JoinerMoodRules.MoodOf(null, colony, [NeedsFixtures.Pawn()]));
    }

    [Fact]
    public void AJoinersBedroomTraitThoughtsAreTheMedianOfThemInEachColonistsRoom()
    {
        EquatableArray<NeedThoughtStage> rows =
        [
            new("Greedy", "greedy", 0, -10, NeedsFixtures.Situational), new("Greedy", "greedy", 1, -5, NeedsFixtures.Situational),
            new("Greedy", "greedy", 2, 0, NeedsFixtures.Situational), new("Greedy", "greedy", 3, 0, NeedsFixtures.Situational),
            new("Jealous", "jealous", 0, -8, NeedsFixtures.Situational),
        ];
        // No room: Greedy stage 0, jealous of the 40 room. A stage-0 room (10): Greedy stage 1, jealous of 40. A stage-2 room at 60: neither.
        var colonists = new[]
        {
            NeedsFixtures.Pawn("a") with { RivalRooms = [40] },
            NeedsFixtures.Pawn("b") with { OwnedRoom = new(10, 0), RivalRooms = [40] },
            NeedsFixtures.Pawn("c") with { OwnedRoom = new(60, 2), RivalRooms = [40] },
        };
        Assert.Equal(-13, JoinerMoodRules.RoomTraits(rows, colonists), 12);
        var mood = new JoinerMoodRead(0.5, 0, 0.35) { Traits = [], RoomTraits = rows, SituationalTraits = [], Memories = [] };
        var colony = new Dictionary<string, double>(StringComparer.Ordinal) { ["Greedy"] = -20 };
        Assert.Equal(0.5 - 0.13, JoinerMoodRules.MoodOf(mood, colony, colonists)!.Target, 12);
        Assert.Throws<RequiredFactException>(() => JoinerMoodRules.RoomTraits(rows, [NeedsFixtures.Pawn() with { OwnedRoom = new(500, 5) }]));
    }
}
