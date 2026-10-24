using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// #3809: a planned classroom is priced by the lessons it seats. A child with no school desk takes no lesson; seated, its lessons
/// are the lesson share of its learning refill, taught as XP its skill stock values, and the teacher's lesson ticks are charged as
/// the work they would have done.
/// </summary>
public sealed class ClassroomCreditTests
{
    private const double Day = GameTime.TicksPerDay;
    private const double Horizon = 30;
    private const double Price = 0.01;

    private static readonly MoodBreakModel Breaks = new() { Threshold = 0.35, Tiers = [new(1, 2), new(1, 3), new(1, 5)] };

    private static readonly FoodHaulLegs Legs = new() { Field = true, Bench = true, FieldToStore = 20, SourceToBench = 10, BenchToDish = 5 };

    private static readonly ForecastRoomService Served = new(1, "", Day);

    private static SchoolChildRead Child(string pawn = "child", params string[] skills) => new()
    {
        Pawn = pawn, LearningRateFactor = 0.8,
        Taught = EquatableArray.From((skills.Length == 0 ? ["Shooting"] : skills).Select(s => new TaughtSkillRead(s, SparringCreditTests.Melee()))),
    };

    private static SchoolRead School(params SchoolChildRead[] children) => NeedsFixtures.NoSchool with { Teacher = "pawn", Children = [.. children] };

    private sealed record Review(ForecastCoupledContext Context, ForecastCoupledOutcome Outcome, ForecastCommitment[] Commitments, double Horizon)
    {
        public IReadOnlyList<RoomGain> Gains() => ForecastClassroom.ClassroomCredit(Served, Outcome, Commitments, Context, Horizon);

        public string[] Findings() => [.. ForecastClassroom.Findings(Served, Outcome, Commitments, Context, Horizon)];
    }

    /// <summary>
    /// A one-colonist construction review short of work (PrisonCreditTests' production path), its colonist "pawn" the teacher,
    /// room 1 a planned classroom of <paramref name="desks"/> desks served from day 1, the colony's lessons read as <paramref name="school"/>
    /// and the children's Shooting valued at <see cref="Price"/> a XP.
    /// </summary>
    private static Review Of(SchoolRead school, int desks = 1, bool planned = true)
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
                    School = school,
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
        var selected = ForecastCoupledSelection.CompareActivityOutcomes(ForecastCoupled.CoupledForecastRequestOf(new("observed-review", 100),
            ForecastCoupled.TierOneHorizonDays, facts, new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, [], new()), coupled);
        var chosen = selected.Selection.Selected.ToHashSet(StringComparer.Ordinal);
        var context = coupled.Context with
        {
            Joiners = new() { Roots = [.. coupled.Alternatives, .. coupled.Incumbents], Work = facts.WorkSpeeds, Food = facts.FoodWork },
            Bed = new(true, 0), FoodHaul = planned ? Legs : null, InvestmentHorizonDays = Horizon,
            Investment = new Dictionary<string, double>(StringComparer.Ordinal) { [ProjectGraph.SkillKey("child", "Shooting")] = Price, [ProjectGraph.SkillKey("other", "Shooting")] = Price / 2 },
            PlannedRoomRoles = new Dictionary<int, PlannedRole> { [1] = PlannedRole.Classroom },
            RoomCredits = PlannedRoomCredits.Shipped,
            Closures = [new(1, "", [], [], [], PlannedRoomPredictionTests.Closed("1", "", "Classroom", 40d, 3, 9, 0, false) with { Desks = [.. Enumerable.Repeat(new PlannedRoomDesk(1.4), desks)] })],
        };
        return new(context, selected.Selected with { ServedRooms = [Served] },
            [.. coupled.Alternatives.Concat(coupled.Incumbents).Where(c => chosen.Contains(c.Id))], selected.Horizon);
    }

    // The teacher's lost work a day for lesson ticks at the child's learning rate on a 1.4 desk.
    private static double LostWork(Review review, SchoolChildRead child)
    {
        var read = review.Context.Needs.School;
        var teacher = review.Context.Needs.Pawns.Single(p => p.Pawn == read.Teacher).Upkeep;
        var ticks = ForecastClassroom.LessonTicksPerDay(read, ForecastClassroom.LessonShare(read), child.LearningRateFactor * 1.4);
        var shortfall = JoinerMoodRules.ShortfallOf(review.Outcome, review.Commitments, review.Context, review.Horizon);
        return PawnValueRules.WorkAdded(new PawnValueFacts(read.Teacher)
        {
            Speeds = teacher.Speeds.ToDictionary(s => s.Stat, s => s.Value, StringComparer.Ordinal), Incapable = teacher.IncapableWork, FreeTicksPerDay = ticks,
        }, shortfall).Sum(s => s.TicksPerDay) / Day;
    }

    [Fact]
    public void ASeatedChildIsCreditedItsLessonXpAtTheStocksValueLessTheTeachersLostWork()
    {
        var child = Child();
        var review = Of(School(child));
        var read = review.Context.Needs.School;
        var share = ForecastClassroom.LessonShare(read);
        var raw = ForecastClassroom.LessonXpPerDay(read, share);
        // share × 3e-6 × 60000 × 0.25 / 1.2e-5 = share × 3750 raw XP a day.
        Assert.Equal(share * 3750, raw, 9);
        var applied = ForecastClassroom.AppliedXpPerDay("child", child.Taught[0], raw);
        Assert.True(applied > 0);
        var lost = LostWork(review, child);
        Assert.True(lost > 0);
        var gain = Assert.Single(review.Gains());
        Assert.Equal("child", gain.Subject);
        Assert.Equal(applied * Price - lost, gain.PerDay, 9);
        Assert.Equal(0, gain.Once);
        Assert.Contains("classroom_desk_sharing", review.Findings());

        // Through the #3796 seam: one investment row under the child, over the served days.
        var rows = new List<ForecastInvestmentContribution>();
        PlannedRoomCredits.Credit(review.Outcome, review.Commitments, review.Context, review.Horizon, rows, new SortedSet<string>(StringComparer.Ordinal));
        var row = Assert.Single(rows);
        Assert.Equal(("classroom/1/child", Horizon - 1), (row.Key, row.Units));
        Assert.Equal(-gain.PerDay * (Horizon - 1), row.Value, 9);
    }

    [Fact]
    public void TheRoomsOneDeskSeatsTheChildTheLessonsAreWorthMostTo()
    {
        var review = Of(School(Child("other"), Child()));
        Assert.Equal("child", Assert.Single(review.Gains()).Subject);
        // A standing desk seats that child; the room seats the other.
        var standing = Of(School(Child("other"), Child()) with { StandingDesks = 1 });
        Assert.Equal("other", Assert.Single(standing.Gains()).Subject);
    }

    [Fact]
    public void ATaughtSkillTierTwoDoesNotValueAddsNothing()
    {
        var child = Child("child", "Shooting", "Cooking");
        var review = Of(School(child));
        var read = review.Context.Needs.School;
        var raw = ForecastClassroom.LessonXpPerDay(read, ForecastClassroom.LessonShare(read));
        var gain = Assert.Single(review.Gains());
        Assert.Equal(ForecastClassroom.AppliedXpPerDay("child", child.Taught[0], raw / 2) * Price - LostWork(review, child), gain.PerDay, 9);
        Assert.Contains(ForecastClassroom.SkillUnvalued, review.Findings());
    }

    [Fact]
    public void BeforeTheFirstLayoutPlanTheRoomIsPricedTheSame()
    {
        var gain = Assert.Single(Of(School(Child()), planned: false).Gains());
        Assert.Equal(Assert.Single(Of(School(Child())).Gains()).PerDay, gain.PerDay, 12);
    }

    [Fact]
    public void AColonyWithNoChildEarnsNothing()
    {
        var review = Of(School());
        Assert.Empty(review.Gains());
        Assert.Contains(ForecastClassroom.NoChild, review.Findings());
    }

    [Fact]
    public void AColonyWithNoTeacherEarnsNothing()
    {
        var review = Of(School(Child()) with { Teacher = "" });
        Assert.Empty(review.Gains());
        Assert.Contains(ForecastClassroom.NoTeacher, review.Findings());
    }

    [Fact]
    public void AColonyWithoutBiotechsLessonEarnsNothing()
    {
        var review = Of(School(Child()) with { Lessontaking = "", LessonXpPerTick = 0 });
        Assert.Empty(review.Gains());
        Assert.Contains(ForecastClassroom.NoLesson, review.Findings());
    }

    [Fact]
    public void StandingDesksSeatingEveryChildLeaveTheRoomNothing()
    {
        var review = Of(School(Child()) with { StandingDesks = 1 });
        Assert.Empty(review.Gains());
        Assert.Contains(ForecastClassroom.Seated, review.Findings());
    }

    [Fact]
    public void AChildTaughtNothingAndARoomWithNoDeskEarnNothing()
    {
        var untaught = Of(School(Child() with { Taught = [] }));
        Assert.Empty(untaught.Gains());
        Assert.Contains(ForecastClassroom.NothingTaught, untaught.Findings());
        var deskless = Of(School(Child()), desks: 0);
        Assert.Empty(deskless.Gains());
        Assert.Contains(ForecastClassroom.NoDesk, deskless.Findings());
    }

    [Fact]
    public void AnUnreadTeacherOrLearningIsAnUnreadFact()
    {
        Assert.StartsWith("teacher upkeep:ghost", Assert.Throws<RequiredFactException>(() => Of(School(Child()) with { Teacher = "ghost" }).Gains()).Fact, StringComparison.Ordinal);
        var stale = Child() with { Taught = [new("Shooting", SparringCreditTests.Melee() with { ValidForTicks = 1 })] };
        Assert.StartsWith("lesson xp skill/child/Shooting", Assert.Throws<RequiredFactException>(() => Of(School(stale)).Gains()).Fact, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLessonShareIsTheChanceLessontakingIsActiveTimesHalfItsJobs()
    {
        SchoolRead With(params LearningDesireRead[] desires) => NeedsFixtures.NoSchool with { Desires = [.. desires] };
        // Equal weights: each of n desires is active 2/n of the time and wins half its jobs.
        Assert.Equal(1d / 6, ForecastClassroom.LessonShare(With([.. Enumerable.Range(0, 6).Select(i => new LearningDesireRead(i == 0 ? "Lessontaking" : "D" + i, 1, true))])), 9);
        // Three desires cycle whatever their weights: the newest is the one not active.
        Assert.Equal(1d / 3, ForecastClassroom.LessonShare(With(new("Lessontaking", 5, true), new("A", 1, true), new("B", 0.1, true))), 9);
        Assert.Equal(0.5, ForecastClassroom.LessonShare(With(new("Lessontaking", 1, true), new("A", 1, true), new("B", 1, false))), 12);
        Assert.Equal(0, ForecastClassroom.LessonShare(With(new("Lessontaking", 1, false), new("A", 1, true))));
        // The game's desires (Radiotalking ungivable without a radio): Lessontaking's 2.5 weight raises it above the equal 1/6.
        // A simulation of the tracker's draws agrees: the time Lessontaking is active, halved.
        var vanilla = ForecastClassroom.LessonShare(NeedsFixtures.NoSchool);
        Assert.InRange(vanilla, 1d / 6, 0.5);
        var givable = NeedsFixtures.NoSchool.Desires.Where(d => d.CanGive).ToArray();
        var random = new Random(3809);
        var active = new List<int> { 0, 1 };
        var held = 0;
        const int Steps = 400000;
        for (var step = 0; step < Steps; step++)
        {
            var open = Enumerable.Range(0, givable.Length).Where(c => !active.Contains(c)).ToArray();
            var pick = random.NextDouble() * open.Sum(c => givable[c].SelectionWeight);
            var drawn = open.First(c => (pick -= givable[c].SelectionWeight) < 0 || c == open[^1]);
            active.RemoveAt(0);
            active.Add(drawn);
            if (active.Any(c => givable[c].Def == "Lessontaking")) held++;
        }

        Assert.Equal((double)held / Steps / 2, vanilla, 2);
    }
}
