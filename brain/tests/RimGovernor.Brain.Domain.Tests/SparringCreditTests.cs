using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// #3813: a planned sparring ring is credited with the raid harm its trainees' Melee saves. Room 1 (PlannedRoomCreditTests'
/// room, serving from day three of a ten-day horizon) is the ring. Two capable pawns at Melee 0 spar to the daily cap (4000 XP,
/// two levels of 1000 a day here) and reach the ceiling of 2 the first day; their MeleeDPS curve gives 4 more DPS each at level
/// 2, 40 raid points of defense between them, which lifts 990 to 1030 against 1000 raid points: the ratio leaves the 1-2
/// exposure bin (5 hits) for 0.5-1 (1 hit). From the second served day each day saves the raids' loss between the two bins.
/// </summary>
public sealed class SparringCreditTests
{
    private const double Day = GameTime.TicksPerDay, Horizon = 10, Served = 3;

    internal static SkillLearning Melee(int stored = 0) => new()
    {
        Disabled = Known(false), TrackerAllowsLearning = Known(true), StoredLevel = Known(stored), Aptitude = Known(0), Xp = Known(0d),
        XpSinceMidnight = Known(0d), DirectFactor = Known(1d), UnsaturatedFactor = Known(1d), SaturationThreshold = Known(4000d),
        SaturatedFactor = Known(.2), LossThreshold = Known(-1000d), DecayInterval = Known(200), NextDecay = Known(200), ValidForTicks = Known(1000),
        NextMidnightReset = Known(1000),
        Levels = EquatableArray.From(Enumerable.Range(0, 3).Select(l => new SkillLevelMechanics(l, 1000, 0))),
        PracticeEligible = Known(true), PracticeCapable = true, PracticeCeiling = Known(2),
    };

    internal static readonly SkillStatCurve MeleeDps = new("MeleeDPS", "Melee", EquatableArray.From(Enumerable.Range(0, 3).Select(l => new SkillStatLevel(l, 2 + 2 * l))), "test");

    private static PracticeTrainee Trainee(string pawn, int stored = 0) =>
        new(pawn, Melee(stored), 2, EquatableArray.From(MeleeDps.Levels.Select(l => (double)l.Value)));

    private static ExposureTable Exposure() => new(RaidExposure.Schema,
    [
        new ExposureCell("1-2", ExposureRoles.Noncombatant, 100, 5, 3, 20, 0.5, 0.4, 0.6),
        new ExposureCell("0.5-1", ExposureRoles.Noncombatant, 100, 1, 1, 3, 0.1, 0.05, 0.15),
    ]);

    private static HarmInputs Raids(double raidsPerDay = 1) => ForecastValueTests.RaidDay(Exposure(), raidsPerDay) with { DefenseCapacity = Known(990d) };

    private static ForecastCoupledContext Context(PracticeTrainees? trainees, HarmInputs? harm = null) =>
        PlannedRoomCreditTests.Context(PlannedRoomCredits.Shipped) with
        {
            PlannedRoomRoles = new Dictionary<int, PlannedRole> { [1] = PlannedRole.SparringRing, [2] = PlannedRole.Workshop },
            Needs = NeedsFixtures.Empty with { Pawns = [ForecastValueTests.HarmPawn()] },
            CapacityPenalties = ForecastValueTests.HarmTables(),
            Harm = harm ?? Raids(),
            Outfits = new Dictionary<string, EquatableArray<GearOption>> { ["pawn"] = ForecastValueTests.Cloth },
            Sparring = trainees,
        };

    private static PracticeTrainees Two(params PracticeTrainee[] extra) => new([Trainee("a"), Trainee("b"), .. extra], [], 0);

    private static ForecastCoupledOutcome Stub() => new(new([], 0, 0), default, new Dictionary<string, ForecastNeedsResult>(), []);

    /// <summary>The raids' loss a day at the observed defense plus <paramref name="shift"/> points, priced by the ledger.</summary>
    private static double RaidLoss(ForecastCoupledContext context, double shift)
    {
        var rows = context.Needs.Pawns.ToDictionary(p => p.Pawn, p => p, StringComparer.Ordinal);
        var outcome = new ForecastCoupledOutcome(new([], 0, 0), default, rows.Keys.ToDictionary(p => p, _ => new ForecastNeedsResult([], default, default, new ForecastCapacityResult(default!, []), [])), []);
        return HarmLedger.Of(outcome, context, Day, new(), rows, null, null, shift).Detail!.RaidLoss;
    }

    [Fact]
    public void TwoCapablePawnsWhoseMeleeCrossesAnExposureBinEarnTheHarmSaved()
    {
        var context = Context(Two());
        var saved = RaidLoss(context, 0) - RaidLoss(context, 40);
        Assert.True(saved > 0, "the 0.5-1 bin's raid costs less than the 1-2 bin's");
        var ledger = ForecastValue.Of(PlannedRoomCreditTests.Outcome(context), context, Day);
        var rows = ledger.InvestmentContributions.Where(r => r.Key.StartsWith(Wire.Name(PlannedRole.SparringRing) + "/", StringComparison.Ordinal)).ToArray();
        Assert.Equal([PlannedRoomCredits.Key(PlannedRole.SparringRing, 1, "a"), PlannedRoomCredits.Key(PlannedRole.SparringRing, 1, "b")], rows.Select(r => r.Key));
        Assert.All(rows, r => Assert.Equal(Horizon - Served, r.Units));
        // The first served day trains them; the six after it hold Melee 2 against the raids, the saving shared by the added power.
        Assert.Equal(-(Horizon - Served - 1) * saved, rows.Sum(r => r.Value), 9);
        Assert.Equal(rows[0].Value, rows[1].Value, 12);
        Assert.DoesNotContain(PlannedRoomCredits.Unassessed(PlannedRole.SparringRing, 1), ledger.Findings);
    }

    [Fact]
    public void NobodyToTrainNoPartnerAStandingRingOrNoRaidEarnsNothing()
    {
        ForecastRoomService ring = new(1, "", Served * Day);
        // No capable pawn: nobody to train is a named zero.
        Assert.Empty(ForecastSparring.RingCredit(ring, Stub(), Context(new([], [], 0)), Day));
        // One capable pawn has nobody to spar with.
        Assert.Empty(ForecastSparring.RingCredit(ring, Stub(), Context(new([Trainee("a")], [], 0)), Day));
        // Two standing Melee targets: the colony already spars without the ring.
        Assert.Empty(ForecastSparring.RingCredit(ring, Stub(), Context(Two() with { Targets = 2 }), Day));
        // Trainees already at the ceiling gain nothing.
        Assert.Empty(ForecastSparring.RingCredit(ring, Stub(), Context(new([Trainee("a", 2), Trainee("b", 2)], [], 0)), Day));
        // No raid to expect: the Melee saves no harm.
        Assert.Empty(ForecastSparring.RingCredit(ring, Stub(), Context(Two(), Raids(0)), Day));
        // A second ring takes no trainees: the lowest id does.
        var two = Context(Two()) with { PlannedRoomRoles = new Dictionary<int, PlannedRole> { [1] = PlannedRole.SparringRing, [3] = PlannedRole.SparringRing } };
        Assert.Empty(ForecastSparring.RingCredit(new(3, "", Served * Day), Stub(), two, Day));
        Assert.NotEmpty(ForecastSparring.RingCredit(ring, Stub(), two, Day));
    }

    [Fact]
    public void UnreadTraineesMechanicsOrHarmAreRequiredFacts()
    {
        ForecastRoomService ring = new(1, "", Served * Day);
        Assert.Equal("work pawns", Assert.Throws<RequiredFactException>(() => ForecastSparring.RingCredit(ring, Stub(), Context(null), Day)).Fact);
        Assert.StartsWith("sparring trainees", Assert.Throws<RequiredFactException>(() => ForecastSparring.RingCredit(ring, Stub(), Context(new([Trainee("a"), Trainee("b")], ["c"], 0)), Day)).Fact, StringComparison.Ordinal);
        var unread = Trainee("b") with { Learning = Melee() with { Xp = default } };
        Assert.StartsWith("melee learning b", Assert.Throws<RequiredFactException>(() => ForecastSparring.RingCredit(ring, Stub(), Context(new([Trainee("a"), unread], [], 0)), Day)).Fact, StringComparison.Ordinal);
        Assert.StartsWith("raid harm", Assert.Throws<RequiredFactException>(() => ForecastSparring.RingCredit(ring, Stub(), Context(Two(), Raids() with { RaidPoints = default }), Day)).Fact, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCoupledContextCarriesTheCapablePawnsTheRoundsRead()
    {
        // The production path (#3822): RoundsWork fills each work pawn's Skills (the native SkillLearning, PracticeCapable the
        // one practice eligibility rule) and SkillStatCurves (the biography's MeleeDPS curve) and Health; GenerateCoupledActivities
        // builds the context's trainees from the review's facts.
        var facts = ForecastCoupledTests.ConstructionReview();
        var pawns = facts.ForecastObserved.WorkPawns.Value;
        WorkPawn Capable(WorkPawn pawn, string id) => pawn with
        {
            Id = new(id), Health = Known(0.5),
            Skills = Known(EquatableArray.From([new WorkSkill { Name = "Melee", Level = 0, Learning = Melee() }])),
            SkillStatCurves = [MeleeDps],
        };
        var capable = facts with { ForecastObserved = facts.ForecastObserved with { WorkPawns = Known(EquatableArray.From([Capable(pawns[0], "a"), Capable(pawns[0], "b")])) } };
        var context = ForecastCoupledCandidates.GenerateCoupledActivities(capable)!.Context;
        Assert.Equal(["a", "b"], context.Sparring!.Trainees.Select(t => t.Pawn));
        // Melee power is MeleeDPS x health: 6 x 0.5 at Melee 2.
        Assert.All(context.Sparring.Trainees, t => Assert.Equal((2, 3d), (t.Ceiling, t.Power[2])));
        // A capable pawn without a MeleeDPS curve is a named not-modelled finding, not a trainee.
        var uncurved = capable with { ForecastObserved = capable.ForecastObserved with { WorkPawns = Known(EquatableArray.From([Capable(pawns[0], "a") with { SkillStatCurves = [] }])) } };
        var generated = ForecastCoupledCandidates.GenerateCoupledActivities(uncurved)!;
        Assert.Empty(generated.Context.Sparring!.Trainees);
        Assert.Contains("sparring_ring_melee_not_modelled:a", generated.Findings);
        // A fresh colony (its colonist's skills read, none capable of Melee practice) credits its planned ring nothing and throws nothing.
        var shooter = pawns[0] with { Skills = Known(EquatableArray.From([new WorkSkill { Name = "Shooting", Level = 3, Learning = Melee() }, new WorkSkill { Name = "Melee", Disabled = true, Learning = Melee() with { PracticeCapable = false } }])) };
        var fresh = ForecastCoupledCandidates.GenerateCoupledActivities(facts with { ForecastObserved = facts.ForecastObserved with { WorkPawns = Known(EquatableArray.From([shooter])) } })!.Context;
        Assert.Empty(fresh.Sparring!.Trainees);
        Assert.Empty(ForecastSparring.RingCredit(new(1, "", 0), Stub(), fresh with { PlannedRoomRoles = new Dictionary<int, PlannedRole> { [1] = PlannedRole.SparringRing } }, Day));
    }
}
