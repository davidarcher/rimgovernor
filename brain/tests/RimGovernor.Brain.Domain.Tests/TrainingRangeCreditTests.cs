using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// #3846: a planned training range is credited with the raid harm its trainees' Shooting saves. Room 1 (PlannedRoomCreditTests'
/// room, serving from day three of a ten-day horizon) is the range. A capable pawn at Shooting 0 drills to the daily budget (3000
/// applied XP, three levels of 1000 here) and reaches the ceiling of 2 the first day; its RangedDefenseDps curve (ranged DPS at
/// the hit factor each level's accuracy gives) adds 4 DPS at level 2, 20 raid points of defense, which lifts 990 to 1010
/// against 1000 raid points: the ratio leaves the 1-2 exposure bin (5 hits) for 0.5-1 (1 hit). From the second served day
/// each day saves the raids' loss between the two bins.
/// </summary>
public sealed class TrainingRangeCreditTests
{
    private const double Day = GameTime.TicksPerDay, Horizon = 10, Served = 3;

    private static SkillLearning Shooting(int stored = 0) => SparringCreditTests.Melee(stored) with { Practice = new SkillPractice.Range(true, true, 2, 3000, 3000) };

    private static readonly SkillStatCurve RangedDps = new(SkillStatCurve.RangedDefenseDps, "Shooting",
        EquatableArray.From(Enumerable.Range(0, 3).Select(l => new SkillStatLevel(l, 2 + 2 * l))), "test");

    private static PracticeTrainee Trainee(string pawn, int stored = 0, SkillStatCurve? curve = null) =>
        new(pawn, Shooting(stored), 2, EquatableArray.From((curve ?? RangedDps).Levels.Select(l => (double)l.Value)));

    private static ExposureTable Exposure() => new(RaidExposure.Schema,
    [
        new ExposureCell("1-2", ExposureRoles.Noncombatant, 100, 5, 3, 20, 0.5, 0.4, 0.6),
        new ExposureCell("0.5-1", ExposureRoles.Noncombatant, 100, 1, 1, 3, 0.1, 0.05, 0.15),
    ]);

    private static HarmInputs Raids(double raidsPerDay = 1) => ForecastValueTests.RaidDay(Exposure(), raidsPerDay) with { DefenseCapacity = Known(990d) };

    private static ForecastCoupledContext Context(PracticeTrainees? trainees, HarmInputs? harm = null) =>
        PlannedRoomCreditTests.Context(PlannedRoomCredits.Shipped) with
        {
            PlannedRoomRoles = new Dictionary<int, PlannedRole> { [1] = PlannedRole.TrainingRange, [2] = PlannedRole.Workshop },
            Needs = NeedsFixtures.Empty with { Pawns = [ForecastValueTests.HarmPawn()] },
            CapacityPenalties = ForecastValueTests.HarmTables(),
            Harm = harm ?? Raids(),
            Outfits = new Dictionary<string, EquatableArray<GearOption>> { ["pawn"] = ForecastValueTests.Cloth },
            Range = trainees,
        };

    private static PracticeTrainees One(params PracticeTrainee[] extra) => new([Trainee("a"), .. extra], [], 0);

    private static ForecastCoupledOutcome Stub() => new(new([], 0, 0), default, new Dictionary<string, ForecastNeedsResult>(), []);

    /// <summary>The raids' loss a day at the observed defense plus <paramref name="shift"/> points, priced by the ledger.</summary>
    private static double RaidLoss(ForecastCoupledContext context, double shift)
    {
        var rows = context.Needs.Pawns.ToDictionary(p => p.Pawn, p => p, StringComparer.Ordinal);
        var outcome = new ForecastCoupledOutcome(new([], 0, 0), default, rows.Keys.ToDictionary(p => p, _ => new ForecastNeedsResult([], default, default, new ForecastCapacityResult(default!, []), [])), []);
        return HarmLedger.Of(outcome, context, Day, new(), rows, null, null, shift).Detail!.RaidLoss;
    }

    [Fact]
    public void ARangeRaisesALowShooterCapacityAndLowersTheRaidHarm()
    {
        var context = Context(One());
        // Shooting 2's 4 more DPS at the hit factor: 20 points of capacity, so the raid's ratio to it falls.
        Assert.True(HarmLedger.PointsRatio(context.Harm, 20) < HarmLedger.PointsRatio(context.Harm, 0));
        var saved = RaidLoss(context, 0) - RaidLoss(context, 20);
        Assert.True(saved > 0, "the 0.5-1 bin's raid costs less than the 1-2 bin's");
        var ledger = ForecastValue.Of(PlannedRoomCreditTests.Outcome(context), context, Day);
        var row = Assert.Single(ledger.InvestmentContributions, r => r.Key.StartsWith(Wire.Name(PlannedRole.TrainingRange) + "/", StringComparison.Ordinal));
        Assert.Equal(PlannedRoomCredits.Key(PlannedRole.TrainingRange, 1, "a"), row.Key);
        Assert.Equal(Horizon - Served, row.Units);
        // The first served day trains it; the six after it hold Shooting 2 against the raids.
        Assert.Equal(-(Horizon - Served - 1) * saved, row.Value, 9);
        Assert.Contains("range_stand_capacity_unmodelled", ledger.Findings);
    }

    [Fact]
    public void NobodyToTrainAnUnarmedTraineeAStandingRangeOrNoRaidEarnsNothing()
    {
        ForecastRoomService range = new(1, "", Served * Day);
        // No capable pawn: nobody to train is a named zero.
        Assert.Empty(ForecastTrainingRange.RangeCredit(range, Stub(), [], Context(new([], [], 0)), Day));
        // A trainee without a ranged primary lands no shots at any level.
        var unarmed = new SkillStatCurve(SkillStatCurve.RangedDefenseDps, "Shooting", EquatableArray.From(Enumerable.Range(0, 3).Select(l => new SkillStatLevel(l, 0))), "test");
        Assert.Empty(ForecastTrainingRange.RangeCredit(range, Stub(), [], Context(new([Trainee("a", curve: unarmed)], [], 0)), Day));
        // A standing Shooting target: the colony already drills without the range.
        Assert.Empty(ForecastTrainingRange.RangeCredit(range, Stub(), [], Context(One() with { Targets = 1 }), Day));
        // A trainee already at the ceiling gains nothing.
        Assert.Empty(ForecastTrainingRange.RangeCredit(range, Stub(), [], Context(new([Trainee("a", 2)], [], 0)), Day));
        // No raid to expect: the Shooting saves no harm.
        Assert.Empty(ForecastTrainingRange.RangeCredit(range, Stub(), [], Context(One(), Raids(0)), Day));
        // A second range takes no trainees: the lowest id does.
        var two = Context(One()) with { PlannedRoomRoles = new Dictionary<int, PlannedRole> { [1] = PlannedRole.TrainingRange, [3] = PlannedRole.TrainingRange } };
        Assert.Empty(ForecastTrainingRange.RangeCredit(new(3, "", Served * Day), Stub(), [], two, Day));
        Assert.NotEmpty(ForecastTrainingRange.RangeCredit(range, Stub(), [], two, Day));
    }

    [Fact]
    public void UnreadTraineesMechanicsOrHarmAreRequiredFacts()
    {
        ForecastRoomService range = new(1, "", Served * Day);
        Assert.Equal("work pawns", Assert.Throws<RequiredFactException>(() => ForecastTrainingRange.RangeCredit(range, Stub(), [], Context(null), Day)).Fact);
        Assert.StartsWith("range trainees", Assert.Throws<RequiredFactException>(() => ForecastTrainingRange.RangeCredit(range, Stub(), [], Context(new([Trainee("a")], ["c"], 0)), Day)).Fact, StringComparison.Ordinal);
        var unread = Trainee("a") with { Learning = Shooting() with { Xp = double.NaN } };
        Assert.StartsWith("shooting learning a", Assert.Throws<RequiredFactException>(() => ForecastTrainingRange.RangeCredit(range, Stub(), [], Context(new([unread], [], 0)), Day)).Fact, StringComparison.Ordinal);
        Assert.StartsWith("raid harm", Assert.Throws<RequiredFactException>(() => ForecastTrainingRange.RangeCredit(range, Stub(), [], Context(One(), Raids() with { RaidPoints = default }), Day)).Fact, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCoupledContextCarriesEveryCapableShooterTheRoundsRead()
    {
        // The production path (#3822): RoundsWork fills each work pawn's Skills (the native SkillLearning, its SkillPractice.Range
        // the one practice eligibility rule and RangeTraining.DailyXpBudget) and SkillStatCurves (the biography's
        // RangedDefenseDps curve); GenerateCoupledActivities builds the context's range trainees from the review's facts.
        var facts = ForecastCoupledTests.ConstructionReview();
        var pawns = facts.ForecastObserved.WorkPawns.Value;
        WorkPawn Capable(WorkPawn pawn, string id, bool capable = true) => pawn with
        {
            Id = new(id), Health = Known(0.5),
            Skills = Known(EquatableArray.From([new WorkSkill { Name = "Shooting", Level = 0, Learning = Shooting() with { Practice = new SkillPractice.Range(capable, true, 2, 3000, 3000) } }])),
            SkillStatCurves = [RangedDps],
        };
        WorkPawn[] colony = [Capable(pawns[0], "a"), Capable(pawns[0], "b", capable: false)];
        var review = facts with { ForecastObserved = facts.ForecastObserved with { WorkPawns = Known(EquatableArray.From(colony)) } };
        var context = ForecastCoupledCandidates.GenerateCoupledActivities(review)!.Context;
        // A pawn incapable of Shooting (a Brawler, or Shooting disabled) is no trainee and gains nothing.
        Assert.Equal(["a"], context.Range!.Trainees.Select(t => t.Pawn));
        // Ranged damage counts at full health in the defense capacity: the curve's 6 DPS at Shooting 2.
        Assert.Equal((2, 6d), (context.Range.Trainees[0].Ceiling, context.Range.Trainees[0].Power[2]));
        // The range is planned for every capable shooter below the ceiling.
        Assert.Equal(1, TrainingRules.ReviewTraining(review).Value.ShootingTrainees);
        // A capable pawn without the curve is a named not-modelled finding, not a trainee.
        var uncurved = review with { ForecastObserved = review.ForecastObserved with { WorkPawns = Known(EquatableArray.From([Capable(pawns[0], "a") with { SkillStatCurves = [] }])) } };
        var generated = ForecastCoupledCandidates.GenerateCoupledActivities(uncurved)!;
        Assert.Empty(generated.Context.Range!.Trainees);
        Assert.Contains("training_range_shooting_not_modelled:a", generated.Findings);
        // A fresh colony (its colonist's skills read, none capable of Shooting practice) credits its planned range nothing and throws nothing.
        var brawler = pawns[0] with { Skills = Known(EquatableArray.From([new WorkSkill { Name = "Shooting", Level = 3, Learning = Shooting() with { Practice = new SkillPractice.Range(false, true, 2, 3000, 3000) } }])) };
        var fresh = ForecastCoupledCandidates.GenerateCoupledActivities(facts with { ForecastObserved = facts.ForecastObserved with { WorkPawns = Known(EquatableArray.From([brawler])) } })!.Context;
        Assert.Empty(fresh.Range!.Trainees);
        Assert.Empty(ForecastTrainingRange.RangeCredit(new(1, "", 0), Stub(), [], fresh with { PlannedRoomRoles = new Dictionary<int, PlannedRole> { [1] = PlannedRole.TrainingRange } }, Day));
    }
}
