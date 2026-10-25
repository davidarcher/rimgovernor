using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// #3854: a planned nursery is credited the cries and giggles of its babies it keeps from the colonists who hear them now. A baby
/// fits at the rates its mood gives; each colonist in reach gains the fit's memory, stacked as the game stacks them; the room
/// gains each colonist its mood with the room less its mood without it.
/// </summary>
public sealed class NurseryCreditTests
{
    private const double Day = GameTime.TicksPerDay;
    private const double Horizon = 10;

    private static readonly ForecastRoomService Served = new(1, "", 2 * Day);

    /// <summary>Biotech's CryingBaby (−8) or the parent's MyCryingBaby (−4), GigglingBaby (+4) or MyGigglingBaby (+8): a quarter day, stackLimit 3, ×0.8.</summary>
    private static HeardMemoryRead[] Memories(bool parent = false) =>
    [
        new("Crying", parent ? "MyCryingBaby" : "CryingBaby", parent ? -4 : -8, Day / 4, 3, 0.8),
        new("Giggling", parent ? "MyGigglingBaby" : "GigglingBaby", parent ? 8 : 4, Day / 4, 3, 0.8),
    ];

    private static BabyRead Baby(string pawn = "baby", bool inNursery = false, bool now = true, bool open = true, bool parent = false) => new()
    {
        Care = new() { Active = null, Food = null, Play = null, Opportunities = [] },
        CribMood = new(0, 0, 0), Pawn = pawn, Fits = ["Crying", "Giggling"], InNursery = inNursery,
        NurseryRoom = inNursery ? now ? 0 : 10 : -1, SleepHearers = [],
        Play = null,
        SleepWithoutNursery = new("", false, true, 3e-5, 0, 21) { Position = new ServicePositionRead.Located(new(inNursery ? open ? 0 : 15 : now ? 0 : 15, 0)) },
        Hearers = now || open ? [new() { Pawn = "pawn", Hearing = 1, Memories = [.. Memories(parent)] }] : [],
    };

    /// <summary>The baby's needs row: its mood held at <paramref name="mood"/>, its rest falling 1/3 as fast as it is regained.</summary>
    private static NeedsForecastPawn BabyRow(string pawn = "baby", double mood = 0.2) => NeedsFixtures.Pawn(pawn) with
    {
        Mood = NeedsFixtures.Mood with { Level = mood, Target = mood, UnclampedTarget = mood, BreakThreshold = .3, Memories = [] },
        Needs = [NeedsFixtures.Rate("rest", 1d, 1e-5, .28) with { UnservedGainPerTick = 3e-5, GainPerTickPerEffectiveness = 3e-5 }],
        Sleep = new("crib", false, false, 3e-5, .75, 21),
    };

    private static ForecastCoupledContext Context(BabyFitsRead babies, IEnumerable<NeedsForecastPawn>? rows = null, params int[] nurseries)
    {
        var hearer = ForecastValueTests.Pawn();
        var babyRows = (rows ?? [BabyRow()]).Select(p => p.Sleep is null || p.Sleep.Position is ServicePositionRead.Located ? p : p with
        { Sleep = p.Sleep with { Position = new ServicePositionRead.Located(new(babies.Babies.FirstOrDefault(b => b.Pawn == p.Pawn)?.NurseryRoom == 10 ? 2 : 0, 0)) } });
        return ForecastValueTests.Context(hearer) with
        {
            Needs = NeedsFixtures.Empty with { Pawns = [hearer, .. babyRows], Babies = babies },
            PlannedRoomRoles = (nurseries.Length == 0 ? [1] : nurseries).ToDictionary(r => r, _ => PlannedRole.Nursery),
            RoomCredits = PlannedRoomCredits.Shipped, InvestmentHorizonDays = Horizon,
            PlannedBeds = new Dictionary<int, IReadOnlyList<SleepingBedFacts?>> { [1] = [.. babies.Babies.Select(_ => new SleepingBedFacts("Crib", "WoodLog", false, 1, .75))] },
            NurserySites = [new("crib", 1, OpKind.Build, "Crib", "WoodLog", Rotation.North, [.. babies.Babies.Select(_ => new Cell(2, 0))])],
            Closures = [new(1, "WoodLog", [.. Enumerable.Range(2, 8).Select(x => new Cell(x, 0))], [], [],
                PlannedRoomPredictionTests.Closed("1", "WoodLog", "Nursery", 10, 1, 8, 0, false))],
            Activities = new() { Deadline = Day, Activities = [new("observed-work", "cell/0/0", "cell/0/0", [])],
                Options = [new("observed-work", "pawn", Day, 0) { ServicePosition = new ServicePositionRead.Located(new(0, 0)) }] },
        };
    }

    private static BabyFitsRead Read(params BabyRead[] babies) => NeedsFixtures.NoBabies with
    {
        Babies = [.. babies], Geometry = new(20, 1, [.. Enumerable.Range(0, 20).Select(x => x == 1 ? 1 : x is >= 2 and <= 9 ? 2 : 0)],
            [.. Enumerable.Range(0, 20).Select(x => x != 1)], [new(0, false, true, 0, [1]), new(1, true, false, -1, [0, 2]), new(2, false, true, 10, [1])], 9.9, 6, 15)
            { BedSlots = [new("Crib", [new(0, 0), new(0, 0), new(0, 0), new(0, 0)])] },
    };

    /// <summary>The colonist's mood breaks nothing unshifted or raised, and a day in the mildest tier at each lowering shift the context asks for.</summary>
    private static ForecastCoupledOutcome Outcome(ForecastCoupledContext context) =>
        new(new(context.Activities.Activities.Any(a => a.Id == "observed-work") ? [new("observed-work", "pawn", 0, 0, 0, Day, 0, 0, Day, 0, 0)] : [], Day, 0), true, context.Needs.Pawns.ToDictionary(p => p.Pawn, p => new ForecastNeedsResult([], new ForecastTerm.Unassessed("not_projected"), new ForecastTerm.Unassessed("not_projected"), new(new(p.Pawn, "", new ForecastCapacityWindows.Unread(), 0), []), [])
            {
                Mood = FlatMood(p),
            }), [])
        { ServedRooms = [Served] };

    private static ForecastMoodResult.Projected FlatMood(NeedsForecastPawn pawn) => MoodResults.Flat(0, [0, 0, 0]) with
    {
        Response = new(pawn.Mood?.Level ?? .5, 1, 1, pawn.Mood?.BreakThreshold ?? .3,
            [new(0, Day, pawn.Mood?.Target ?? .5, pawn.Mood?.Target ?? .5, false)]),
    };

    private static IReadOnlyList<RoomGain> Gains(ForecastCoupledContext context, ForecastRoomService? room = null) =>
        ForecastNursery.NurseryCredit(room ?? Served, Outcome(context), [], context, Day);

    private static string[] Findings(ForecastCoupledContext context, ForecastRoomService? room = null) =>
        [.. ForecastNursery.Findings(room ?? Served, Outcome(context), [], context, Day)];

    // The mean mood the colonist's heard memories give it, at one baby's rates (HeardMood's own formula, one baby).
    private static double OneBaby(double rate, HeardMemoryRead memory)
    {
        var mean = rate * memory.DurationTicks;
        double P(int n) => Math.Exp(-mean) * Math.Pow(mean, n) / Enumerable.Range(1, n).Aggregate(1d, (f, i) => f * i);
        var (p1, p2) = (P(1), P(2));
        return memory.Offset * ((p1 * 1) + (p2 * 1.8) + ((1 - P(0) - p1 - p2) * (1 + 0.8 + 0.64)));
    }

    [Fact]
    public void ANurseryIsPriced() => Assert.True(Context(Read(Baby())).Priced(1));

    internal static ForecastCoupledContext Nonworker(NurseryRecipientKind kind, string fit, double hearing = 1, int cell = 0)
    {
        var baby = Baby() with { Fits = [fit], Hearers = [new() { Pawn = "recipient", Hearing = hearing, Memories = [.. Memories()] }] };
        var surface = new NeedSleep("recipient-bed", false, false, 1e-9, .5, 21)
        { Position = new ServicePositionRead.Located(new(cell, 0)) };
        var mood = new PrisonerMoodRead(.36, 0)
        {
            Comfort = null, Beauty = null, Meals = [], Memories = [], CellStages = [],
            Rest = new(.1, .28, .8, 1e-5, 1e-9, false, 1e-9, true),
        };
        var recipient = new NurseryRecipientRead("recipient", kind, mood, .36, 1e-5, 1e-5, .35, false, false, surface,
            new ServicePositionRead.PassableMap()) { ObservedSleep = surface };
        var context = Context(Read(baby));
        return context with { Needs = context.Needs with { Babies = context.Needs.Babies with { OtherRecipients = [recipient] } } };
    }

    [Theory]
    [InlineData(NurseryRecipientKind.Prisoner)]
    [InlineData(NurseryRecipientKind.Guest)]
    [InlineData(NurseryRecipientKind.Slave)]
    [InlineData(NurseryRecipientKind.Other)]
    public void FreshNonworkerRecipientsReachProductionWellbeingAndBreakCostsWithoutJoiningTheWorkerCensus(NurseryRecipientKind kind)
    {
        var context = Nonworker(kind, "Crying");
        var workerIds = context.Needs.Pawns.Select(p => p.Pawn).ToArray();
        var gain = Assert.Single(Gains(context));
        Assert.Equal("recipient", gain.Pawn);
        Assert.True(gain.PerDay > 0);
        var noBreakCosts = context with { MoodBreaks = context.MoodBreaks with { Tiers = [new(1, 1e-9), new(1, 1e-9), new(1, 1e-9)] } };
        Assert.True(gain.PerDay > Assert.Single(Gains(noBreakCosts)).PerDay);
        Assert.Equal(workerIds, context.Needs.Pawns.Select(p => p.Pawn));
        Assert.DoesNotContain(context.Needs.Pawns, p => p.Pawn == "recipient");
        Assert.Contains("nursery_recipient_" + kind.ToString().ToLowerInvariant(), Findings(context));
        Assert.DoesNotContain("nursery_other_hearers", Findings(context));

        // A new capture with no hearing loses the gain; neither presence nor admission is cached across reads.
        Assert.Empty(Gains(Nonworker(kind, "Crying", hearing: 0)));
        Assert.Empty(Gains(Nonworker(kind, "Crying", cell: 19)));
        Assert.True(Assert.Single(Gains(Nonworker(kind, "Giggling"))).PerDay < 0);
    }

    [Fact]
    public void NonworkerFutureWakeDoesNotFreezeTheObservedSleepingCell()
    {
        var context = Nonworker(NurseryRecipientKind.Guest, "Crying");
        var recipient = context.Needs.Babies.OtherRecipients[0];
        recipient = recipient with { Mood = recipient.Mood with { Rest = recipient.Mood.Rest! with { Level = .799, GroundGainPerTick = 1e-5 } },
            Sleep = recipient.Sleep! with { GainPerTick = 1e-5 }, ObservedSleep = recipient.ObservedSleep! with { GainPerTick = 1e-5 } };
        context = context with { Needs = context.Needs with { Babies = context.Needs.Babies with { OtherRecipients = [recipient] } } };
        var exposure = new ForecastNurseryExposure(context, Outcome(context), Day, new HashSet<string>());
        Assert.IsType<ServicePositionRead.Located>(Assert.Single(exposure.At("recipient", 0)).Position);
        var awake = Assert.Single(exposure.At("recipient", 200));
        Assert.False(awake.Asleep);
        Assert.IsType<ServicePositionRead.PassableMap>(awake.Position);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SleepDisturbanceReachesTheProductionCreditWhenTheHearerIsAwayFromItsBed(bool standing)
    {
        var memory = new HeardMemoryRead("Crying", "SleepDisturbed", -1, Day, 3, 1);
        var baby = Baby(inNursery: standing, now: false, open: false) with
        {
            Hearers = [new() { Pawn = "pawn", Hearing = 1, Memories = [] }], SleepHearers = [new("pawn", memory)],
            SleepWithoutNursery = Baby().SleepWithoutNursery,
        };
        var context = Context(Read(baby));
        context = context with { Needs = context.Needs with { Pawns = [.. context.Needs.Pawns.Select(p => p.Pawn != "pawn" ? p : p with
        {
            Location = new(100, 100), Sleep = new("bed", false, false, 3e-5, .75, 21) { Position = new ServicePositionRead.Located(new(0, 0)) },
            Needs = [NeedsFixtures.Rate("rest", .8, 1e-5, .28)],
        })] } };
        context = context with { Activities = new() { Deadline = Day } };
        var rate = ForecastNursery.FitRates(context.Needs.Babies, [.. baby.Fits], .2, .75)["Crying"] * .25;
        var avoided = -ForecastNursery.HeardMood([(memory, rate / (1 + rate * 300))]);
        var gain = Assert.Single(Gains(context));
        Assert.Equal("pawn", gain.Pawn);
        Assert.Equal(context.Value.WellbeingWeight * avoided / 100, gain.PerDay, 12);
        Assert.True(gain.PerDay > 0);
        Assert.DoesNotContain("nursery_disturbed_sleep", Findings(context));
        Assert.Contains("nursery_sleep_disturbance_projected", Findings(context));

        var noSleep = context with { Needs = context.Needs with { Babies = Read(baby with { SleepHearers = [] }) } };
        Assert.Empty(Gains(noSleep));
        Assert.Contains("nursery_no_sleep_hearers", Findings(noSleep));
        if (standing)
        {
            var open = context with { Needs = context.Needs with { Pawns = [.. context.Needs.Pawns.Select(p => p.Pawn != "baby" ? p : p with { Sleep = p.Sleep! with { Position = new ServicePositionRead.Located(new(0, 0)) } })] } };
            Assert.Empty(Gains(open));
        }
    }

    [Fact]
    public void AFitFiresAtItsShareOfTheChecksOverTheMeanCycle()
    {
        var read = NeedsFixtures.NoBabies;
        // At mood 0.5 both curves give a day: each check fires a cry 150/60000, a giggle when the cry did not.
        var rates = ForecastNursery.FitRates(read, ["Crying", "Giggling"], 0.5, 0.75);
        const double Q = 150d / 60000;
        var (cry, giggle) = (Q, (1 - Q) * Q);
        var any = cry + giggle;
        var cycle = ((150 / any) + 600) / 0.75 + 1200;
        Assert.Equal(cry / any / cycle, rates["Crying"], 15);
        Assert.Equal(giggle / any / cycle, rates["Giggling"], 15);
        // A happy baby giggles far more than it cries; a fit it cannot have fires never.
        var happy = ForecastNursery.FitRates(read, ["Crying", "Giggling"], 1, 0.75);
        Assert.True(happy["Giggling"] > 20 * happy["Crying"]);
        Assert.Equal(["Giggling"], ForecastNursery.FitRates(read, ["Giggling"], 0.5, 0.75).Keys);
        Assert.Equal(0, ForecastNursery.FitRates(read, ["Crying"], 0.5, 0)["Crying"]);
    }

    [Fact]
    public void HeardMemoriesStackAsTheGameStacksThem()
    {
        var cry = Memories()[0];
        const double Rate = 1 / Day;
        Assert.Equal(OneBaby(Rate, cry), ForecastNursery.HeardMood([(cry, Rate)]), 12);
        // Two babies share the def's stack limit: without the multiplier their count is the one baby's at the summed rate.
        var flat = cry with { StackedMultiplier = 1 };
        Assert.Equal(ForecastNursery.HeardMood([(flat, 2 * Rate)]), ForecastNursery.HeardMood([(flat, Rate), (flat, Rate)]), 12);
        // With it, two babies' cries weigh more than one baby's at the summed rate: two groups, each counting its first in full.
        Assert.True(ForecastNursery.HeardMood([(cry, Rate), (cry, Rate)]) < ForecastNursery.HeardMood([(cry, 2 * Rate)]));
        // A parent's memory is its own def, with its own stack.
        var mine = Memories(parent: true)[0];
        Assert.Equal(ForecastNursery.HeardMood([(cry, Rate)]) + ForecastNursery.HeardMood([(mine, Rate)]), ForecastNursery.HeardMood([(cry, Rate), (mine, Rate)]), 12);
        // No stack limit: each baby's own Poisson count, Σ 0.8^i in closed form.
        var unlimited = cry with { StackLimit = -1 };
        var lambda = Rate * cry.DurationTicks;
        Assert.Equal(-8 * (1 - Math.Exp(-lambda * 0.2)) / 0.2, ForecastNursery.HeardMood([(unlimited, Rate)]), 12);
    }

    [Fact]
    public void ACryingBabyHeardNowIsCreditedTheCriesItsNurseryKeepsFromTheColonist()
    {
        var context = Context(Read(Baby()));
        var read = context.Needs.Babies;
        var rates = ForecastNursery.FitRates(read, ["Crying", "Giggling"], 0.2, 0.75);
        var without = OneBaby(rates["Crying"], Memories()[0]) + OneBaby(rates["Giggling"], Memories()[1]);
        Assert.True(without < 0);
        Assert.DoesNotContain("pawn", PlannedRoomCredits.BreakShifts(context).Keys); // Outcome-dependent heard shifts never enter the context cache key.
        var gain = Assert.Single(Gains(context));
        Assert.Equal(("pawn", "pawn"), (gain.Subject, gain.Pawn));
        // Minus GainPerDay of the cries' shift over the one-day horizon: its wellbeing and the day of breaks it brings.
        var response = FlatMood(context.Needs.Pawns[0]).Response!;
        var lost = context.MoodBreaks.DaysLost(ForecastMood.ShiftedTierTicks(response, without / 100))!.Value
            - context.MoodBreaks.DaysLost(ForecastMood.ShiftedTierTicks(response, 0))!.Value;
        Assert.Equal(-(context.Value.WellbeingWeight * without / 100) + lost, gain.PerDay, 9);
        Assert.True(gain.PerDay > 0);
        Assert.DoesNotContain("nursery_disturbed_sleep", Findings(context));
        Assert.Contains("nursery_no_sleep_hearers", Findings(context));

        // Through the #3796 seam: one investment row under the colonist, over the served days.
        var rows = new List<ForecastInvestmentContribution>();
        PlannedRoomCredits.Credit(Outcome(context), [], context, Day, rows, new SortedSet<string>(StringComparer.Ordinal));
        var row = Assert.Single(rows);
        Assert.Equal(("nursery/1/pawn", Horizon - 2), (row.Key, row.Units));
        Assert.Equal(-gain.PerDay * (Horizon - 2), row.Value, 9);
    }

    [Fact]
    public void AHappyBabysNurseryLosesTheGigglesAndAParentHearsItsOwnBaby()
    {
        var happy = Context(Read(Baby()), [BabyRow(mood: 0.95)]);
        Assert.True(Assert.Single(Gains(happy)).PerDay < 0);
        // The parent's cry costs half and its giggle gives double: the same crying baby is worth less to keep from it.
        var parent = Assert.Single(Gains(Context(Read(Baby(parent: true))))).PerDay;
        Assert.InRange(parent, 0, Assert.Single(Gains(Context(Read(Baby())))).PerDay);
    }

    [Fact]
    public void ABabyInAStandingNurseryIsHeardWithItsDoorsOpenWithoutTheRoom()
    {
        // Behind its closed door nobody hears it; with the door open the colonist would: the same credit as a baby heard now.
        var closed = Assert.Single(Gains(Context(Read(Baby(inNursery: true, now: false, open: true))))).PerDay;
        Assert.Equal(Assert.Single(Gains(Context(Read(Baby())))).PerDay, closed, 12);
        // A colonist who hears it through the room's walls either way gains nothing from the room.
        var through = Context(Read(Baby(inNursery: true, now: true, open: true)));
        Assert.Empty(Gains(through));
        Assert.Contains(ForecastNursery.Unheard, Findings(through));
        Assert.DoesNotContain("pawn", PlannedRoomCredits.BreakShifts(through).Keys);
    }

    [Fact]
    public void ASelectedWorkLocationChangesHearingEvenWhenTheSnapshotPositionIsFarAway()
    {
        var outside = Context(Read(Baby(inNursery: true, now: false, open: true)));
        var inside = outside with
        {
            Needs = outside.Needs with { Pawns = [.. outside.Needs.Pawns.Select(p => p.Pawn != "pawn" ? p : p with { Location = new(15, 0) })] },
            Activities = outside.Activities with
            {
                Activities = [new("observed-work", "cell/2/0", "cell/2/0", [])],
                Options = [new("observed-work", "pawn", Day, 0) { ServicePosition = new ServicePositionRead.Located(new(2, 0)) }],
            },
        };
        Assert.True(Assert.Single(Gains(outside)).PerDay > 0);
        Assert.Empty(Gains(inside));
        Assert.DoesNotContain("nursery_hearers_move", Findings(inside));
    }

    [Fact]
    public void WorkInAndOutOfTheNurseryContributesItsSelectedTimeShareAndExistingFitsCanBeHeardOnEntry()
    {
        var outside = Context(Read(Baby(inNursery: true, now: false, open: true)));
        var mixed = outside with { Activities = outside.Activities with
        {
            Activities = [new("outside", "cell/0/0", "cell/0/0", []), new("inside", "cell/2/0", "cell/2/0", [])],
            Options = [new("outside", "pawn", Day / 2, 0) { ServicePosition = new ServicePositionRead.Located(new(0, 0)) },
                new("inside", "pawn", Day / 2, 0) { ServicePosition = new ServicePositionRead.Located(new(2, 0)) }],
        } };
        var outcome = Outcome(mixed) with { Plan = new([
            new("outside", "pawn", 0, 0, 0, Day / 2, 0, 0, Day / 2, 0, 0),
            new("inside", "pawn", Day / 2, Day / 2, Day / 2, Day, 0, 0, Day / 2, 0, 0)], Day, 0) };
        var exposure = new ForecastNurseryExposure(mixed, outcome, Day, new HashSet<string>());
        var baby = mixed.Needs.Babies.Babies[0];
        Assert.Equal(0, exposure.HeardAt(baby, "pawn", 1, Day / 4, true).Upper);
        Assert.Equal(1, exposure.HeardAt(baby, "pawn", 1, 3 * Day / 4, true).Lower);
        Assert.Equal(1, exposure.HeardDuringFit(baby, "pawn", 1, Day / 2 - 600, 1200, true).Lower);
        var gain = Assert.Single(ForecastNursery.NurseryCredit(Served, outcome, [], mixed, Day));
        Assert.InRange(gain.PerDay, 0, Assert.Single(Gains(outside)).PerDay);
    }

    [Fact]
    public void ColoniesWithNoBabyOrNoFitOrNobodyInReachEarnNothing()
    {
        var none = Context(Read());
        Assert.Empty(Gains(none));
        Assert.Contains(ForecastNursery.NoBaby, Findings(none));
        var mute = Context(Read(Baby() with { Fits = [] }));
        Assert.Empty(Gains(mute));
        Assert.Contains(ForecastNursery.NoFit, Findings(mute));
        var alone = Context(Read(Baby(now: false, open: false)));
        Assert.Empty(Gains(alone));
        Assert.Contains(ForecastNursery.Unheard, Findings(alone));
    }

    [Fact]
    public void TheLowestIdNurseryTakesTheBabies()
    {
        var context = Context(Read(Baby()), null, 1, 2);
        Assert.Single(Gains(context));
        var second = new ForecastRoomService(2, "", 2 * Day);
        Assert.Empty(Gains(context, second));
        Assert.Contains(ForecastNursery.Housed, Findings(context, second));
    }

    [Fact]
    public void AFitTheReadDoesNotKnowIsNamed()
    {
        var read = Read(Baby()) with { Fits = [.. NeedsFixtures.NoBabies.Fits, new() { Def = "Babbling", OpinionThought = "", MtbDaysByMood = [new(0, 1)], DurationTicks = 600, Heard = false }] };
        Assert.Contains(ForecastNursery.UnknownFit + "Babbling", Findings(Context(read)));
    }

    [Fact]
    public void AnUnreadBabyIsAnUnreadFact()
    {
        Assert.StartsWith("baby needs:baby", Assert.Throws<RequiredFactException>(() => Gains(Context(Read(Baby()), []))).Fact, StringComparison.Ordinal);
        Assert.StartsWith("baby mood:baby", Assert.Throws<RequiredFactException>(() => Gains(Context(Read(Baby()), [BabyRow() with { Mood = null }]))).Fact, StringComparison.Ordinal);
        Assert.StartsWith("baby rest:baby", Assert.Throws<RequiredFactException>(() => Gains(Context(Read(Baby()), [BabyRow() with { Needs = [] }]))).Fact, StringComparison.Ordinal);
    }

    [Fact]
    public void FasterCribRestFreesTheBabysCapabilityEvenWhenItCannotHaveAFit()
    {
        var baby = Baby(now: false, open: false) with { Fits = [], SleepWithoutNursery = new("old-bed", false, false, 1e-5, .5, 21) };
        var context = Context(Read(baby));
        var gain = Assert.Single(Gains(context));
        Assert.Equal("baby", gain.Pawn);
        Assert.Equal(.5 - .25, gain.PerDay, 12);
        Assert.DoesNotContain("nursery_crib_rest", Findings(context));
        Assert.Contains("nursery_baby_no_comfort_need", Findings(context));
        // A better otherwise-available bed reverses the crib's rest credit; ground's gain is irrelevant.
        var better = baby with { SleepWithoutNursery = baby.SleepWithoutNursery with { GainPerTick = 7e-5 } };
        Assert.Equal(.125 - .25, Assert.Single(Gains(Context(Read(better)))).PerDay, 12);
    }

    [Fact]
    public void TheSameHearerGetsDifferentFitRatesWhenTheCribChangesAwakeShare()
    {
        var baby = Baby(inNursery: true) with { SleepWithoutNursery = new("other-bed", false, false, 1e-5, .75, 21) { Position = new ServicePositionRead.Located(new(0, 0)) } };
        var context = Context(Read(baby));
        var gains = Gains(context).ToDictionary(g => g.Pawn);
        Assert.Equal(.25, gains["baby"].PerDay, 12);
        // This colonist hears both surfaces: faster rest leaves more awake checks, hence more crying.
        Assert.True(gains["pawn"].PerDay < 0);
        Assert.DoesNotContain("pawn", PlannedRoomCredits.BreakShifts(context).Keys);
    }

    [Fact]
    public void AStandingNurseryUsesItsActualCribStatsAndMissingPlannedStatsThrow()
    {
        var read = Read(Baby(inNursery: true, now: false, open: false));
        var context = Context(read, [BabyRow() with { Sleep = new("quality-crib", false, false, 7e-5, .9, 21) }]) with { PlannedBeds = new Dictionary<int, IReadOnlyList<SleepingBedFacts?>>() };
        Assert.Equal(.25 - .125, Assert.Single(Gains(context)).PerDay, 12);
        var missing = Context(Read(Baby())) with { PlannedBeds = new Dictionary<int, IReadOnlyList<SleepingBedFacts?>>() };
        Assert.StartsWith("nursery planned crib:", Assert.Throws<RequiredFactException>(() => Gains(missing)).Fact, StringComparison.Ordinal);
        var unread = Context(read) with { PlannedBeds = new Dictionary<int, IReadOnlyList<SleepingBedFacts?>> { [1] = [null] } };
        Assert.StartsWith("nursery crib stats:", Assert.Throws<RequiredFactException>(() => Gains(unread)).Fact, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAdmittedComfortNeedChangesTheBabysMoodAndFitMoodInput()
    {
        var baby = Baby(inNursery: true) with { SleepWithoutNursery = new("other-bed", false, false, 3e-5, 0, 21) { Position = new ServicePositionRead.Located(new(0, 0)) } };
        var stages = Enumerable.Range(0, 5).Select(i => new NeedThoughtStage("NeedComfort", ForecastHousing.NeedComfortRole,
            i, new double[] { -3, 4, 6, 8, 10 }[i], NeedsFixtures.Situational));
        var row = BabyRow() with { ThoughtStages = EquatableArray.From(stages) };
        var context = Context(Read(baby), [row]) with { ComfortCurve = new(.5, .6, .04, false) };
        var gains = Gains(context).ToDictionary(g => g.Pawn);
        Assert.True(gains["baby"].PerDay > 0);
        Assert.True(gains["pawn"].PerDay > 0); // Better comfort: fewer cries and more giggles, with unchanged rest gain/reach.
        Assert.DoesNotContain("nursery_baby_mood", Findings(context));
        Assert.DoesNotContain("nursery_baby_no_comfort_need", Findings(context));
    }

    [Fact]
    public void PlaySavingsReachTheCaretakersNurseryInvestmentRow()
    {
        var baby = Baby(now: false, open: false) with { Fits = [], Play = new()
        {
            OngoingCarer = "pawn", RoomFactor = 1, Carers = [new("pawn", null, null, 1, 1, 1, 1)],
        } };
        var context = Context(Read(baby)) with
        {
            NurserySites = [new("toy", 1, OpKind.Build, "ToyBox", "WoodLog", Rotation.North, [new(1, 1)])],
        };
        var gain = Assert.Single(Gains(context));
        Assert.Equal("pawn", gain.Pawn);
        Assert.True(gain.PerDay > 0);
        Assert.DoesNotContain("nursery_play", Findings(context));
        var rows = new List<ForecastInvestmentContribution>();
        PlannedRoomCredits.Credit(Outcome(context), [], context, Day, rows, new SortedSet<string>(StringComparer.Ordinal));
        var row = Assert.Single(rows);
        Assert.Equal("nursery/1/play/baby", row.Key);
        Assert.Equal(-gain.PerDay * (Horizon - 2), row.Value, 12);
    }

    [Fact]
    public void FoodAndRestMoodChangesInTheOutcomeChangeNurseryFitCreditWithoutChangingContextShifts()
    {
        var context = Context(Read(Baby()));
        var shifts = PlannedRoomCredits.BreakShifts(context);
        var baseline = Outcome(context);
        var baby = context.Needs.Pawns.Single(p => p.Pawn == "baby");
        var changedMood = ForecastMood.ForecastMoodTimeline(baby, Day, [],
            banded: [("food-relief", [(0, Day / 2, 0d), (Day / 2, Day, 75d)])], response: true);
        var needs = baseline.Needs.ToDictionary(p => p.Key, p => p.Value);
        needs["baby"] = needs["baby"] with { Mood = changedMood };
        var changed = baseline with { Needs = needs };
        var before = Assert.Single(ForecastNursery.NurseryCredit(Served, baseline, [], context, Day));
        var after = Assert.Single(ForecastNursery.NurseryCredit(Served, changed, [], context, Day));
        Assert.True(after.PerDay < before.PerDay);
        var earlyNeeds = needs.ToDictionary(p => p.Key, p => p.Value);
        earlyNeeds["baby"] = earlyNeeds["baby"] with { Mood = ForecastMood.ForecastMoodTimeline(baby, Day, [],
            banded: [("food-relief", [(0d, Day, 75d)])], response: true) };
        var early = baseline with { Needs = earlyNeeds };
        Assert.Equal(changedMood.Projected().TerminalMood, earlyNeeds["baby"].Mood.Projected().TerminalMood, 12);
        Assert.True(after.PerDay > Assert.Single(ForecastNursery.NurseryCredit(Served, early, [], context, Day)).PerDay);
        Assert.Same(shifts, PlannedRoomCredits.BreakShifts(context));
        Assert.DoesNotContain("nursery_baby_mood", ForecastNursery.Findings(Served, changed, [], context, Day));
    }

    [Fact]
    public void AStandingQualityCribChangesBothBabyMoodAndTheFitsItsHearerHears()
    {
        var baby = Baby(inNursery: true) with { CribMood = new(8, 0, 0) };
        var context = Context(Read(baby), [BabyRow(mood: .5)]) with
        { PlannedBeds = new Dictionary<int, IReadOnlyList<SleepingBedFacts?>>() };
        var gains = Gains(context).ToDictionary(g => g.Pawn);
        Assert.True(gains["baby"].PerDay > 0);
        Assert.True(gains["pawn"].PerDay > 0);
        var replaced = context with { PlannedBeds = new Dictionary<int, IReadOnlyList<SleepingBedFacts?>> { [1] = [new("Crib", "WoodLog", false, 1, .75)] } };
        Assert.Empty(Gains(replaced)); // Both counterfactuals lose the observed quality thought once, never twice.
    }

    [Fact]
    public void SleepTemperatureChangesTheBabysOwnMoodAndFitRates()
    {
        var baby = Baby(inNursery: true) with { SleepWithoutNursery = Baby().SleepWithoutNursery with { Temperature = -10 } };
        var row = BabyRow(mood: .5) with
        {
            SleepComfortableMin = 16, SleepComfortableMax = 26,
            ThoughtStages = [new("SleptInCold", "slept_in_cold", 0, -4, NeedsFixtures.Situational),
                new("SleptInHeat", "slept_in_heat", 0, -4, NeedsFixtures.Situational)],
        };
        var context = Context(Read(baby), [row]) with { Thermal = SleepFixtures.Outdoors() with { Outdoors = 21 } };
        var gains = Gains(context).ToDictionary(g => g.Pawn);
        Assert.True(gains["baby"].PerDay > 0);
        Assert.True(gains["pawn"].PerDay > 0);
        Assert.Throws<RequiredFactException>(() => Gains(context with { Thermal = null }));
    }

    [Fact]
    public void MissingOutcomeMoodResponseAndCribMoodAreRequiredFacts()
    {
        var context = Context(Read(Baby()));
        var outcome = Outcome(context);
        var needs = outcome.Needs.ToDictionary(p => p.Key, p => p.Value);
        needs["baby"] = needs["baby"] with { Mood = MoodResults.Flat(0, [0, 0, 0]) };
        Assert.StartsWith("baby outcome mood response:", Assert.Throws<RequiredFactException>(() =>
            ForecastNursery.NurseryCredit(Served, outcome with { Needs = needs }, [], context, Day)).Fact, StringComparison.Ordinal);
        Assert.StartsWith("baby crib mood:", Assert.Throws<RequiredFactException>(() =>
            Gains(Context(Read(Baby() with { CribMood = null })))).Fact, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCoupledContextCarriesTheBabyFitsTheRoundsRead()
    {
        // The production path (#3822): GenerateCoupledActivities builds the context from the review's facts; the babies, their
        // fits and who hears them ride on the needs read (NeedsForecastRead.Babies), which Read fills.
        var review = ForecastCoupledTests.ConstructionReview();
        var needs = review.ForecastObserved.Needs!;
        var hearer = needs.Pawns[0].Pawn;
        var babies = Read(Baby() with { SleepWithoutNursery = new("other-bed", false, false, 1e-5, .5, 21) { Position = new ServicePositionRead.Located(new(0, 0)) }, Hearers = [.. Baby().Hearers.Select(h => h with { Pawn = hearer })] });
        babies = babies with { Geometry = new(20, 20, [.. Enumerable.Repeat(0, 400)], [.. Enumerable.Repeat(true, 400)], [new(0, false, true, 0, [])], 9.9, 6, 15) { BedSlots = [new("Crib", [new(0, 0), new(0, 0), new(0, 0), new(0, 0)])] } };
        var facts = review with
        {
            MoodBreak = ForecastValueTests.Context(needs.Pawns[0]).MoodBreaks,
            PlannedShelters = [new() { Id = 1, Role = PlannedRole.Nursery, Region = RegionRules.RectRegion(new(1, 1, 1, 1)) }],
            PlannedSites = new() { Roles = [(1, PlannedRole.Nursery)], Sites = [new("crib", 1, OpKind.Build, "Crib", "WoodLog", Rotation.North, [new(1, 1)]), new("toy", 1, OpKind.Build, "ToyBox", "WoodLog", Rotation.North, [new(2, 1)])] },
            SleepingBeds = [new("Crib", "WoodLog", false, 1, .75)],
            PlannedRoomPredictions = [PlannedRoomPredictionTests.Closed("1", "WoodLog", "Nursery", 10, 1, 1, 0, false)],
            ForecastObserved = review.ForecastObserved with { Needs = needs with { Pawns = [.. needs.Pawns, BabyRow() with { Sleep = BabyRow().Sleep! with { Position = new ServicePositionRead.Located(new(0, 0)) } }], Babies = babies } },
        };
        var context = ForecastCoupledCandidates.GenerateCoupledActivities(facts)!.Context;
        Assert.Same(facts.ForecastObserved.Needs, context.Needs);
        Assert.Equal(PlannedRole.Nursery, context.PlannedRoomRoles[1]);
        Assert.True(context.Priced(1));
        Assert.Equal("Crib", Assert.Single(context.PlannedBeds[1])!.Def);
        Assert.Contains(context.NurserySites, s => s.Id == "toy");
        Assert.DoesNotContain("pawn", PlannedRoomCredits.BreakShifts(context).Keys);
        Assert.Equal(.25, ForecastNursery.NurseryCredit(Served, Outcome(context), [], context, Day).Single(g => g.Pawn == "baby").PerDay, 12);
        // A fresh colony's review (no baby) credits the nursery nothing and throws nothing.
        var fresh = ForecastCoupledCandidates.GenerateCoupledActivities(review with { PlannedShelters = facts.PlannedShelters })!.Context;
        Assert.Empty(ForecastNursery.NurseryCredit(new(1, "", Day), Outcome(fresh), [], fresh, Day));
        Assert.Contains(ForecastNursery.NoBaby, ForecastNursery.Findings(new(1, "", Day), Outcome(fresh), [], fresh, Day));
    }

    private static (ForecastCoupledContext Context, ForecastCoupledOutcome Outcome) SocialScenario(ActivitySocialMode mode,
        bool eligible = true, bool admitted = true, bool moving = false)
    {
        var cry = new SocialMemoryDefRead("BabyCriedSocial", -12, 10 * Day, 5, -1, .9, .7);
        var giggle = cry with { Def = "BabyGiggledSocial", Offset = 12 };
        var insult = new SocialMemoryDefRead("InsultedMood", -5, 2 * Day, 10, -1, .9, 1);
        var social = NeedsFixtures.NoBabies.Social with
        {
            MoodDefs = ["InsultedMood"],
            Pawns = [new()
            {
                Pawn = "pawn", MaximumMode = ActivitySocialMode.SuperActive, CooldownTicks = 0,
                CheckAfterTicks = 60, RetryAfterTicks = 91, AbsoluteCooldownTicks = 0, WantsInteraction = false,
                Unprojected = "", Recipients = ["baby"], Pairs = [new()
                {
                    Baby = "baby", Eligible = eligible, Cry = admitted ? cry : null, Giggle = admitted ? giggle : null,
                    Opinion = new(0, 0, 1, false),
                    Interactions = [new("Chitchat", 1, false, []), new("Slight", .02, true, []), new("Insult", .007, true, [insult])],
                }],
            }],
        };
        var context = Context(Read(Baby(inNursery: true, now: false, open: true)) with { Social = social });
        context = context with { Activities = context.Activities with
        {
            Activities = [new("work", "cell/0/0", moving ? "cell/1/0" : "cell/0/0", [])],
            Options = [new("work", "pawn", 5000, 0) { ServicePosition = new ServicePositionRead.Located(new(0, 0)), SocialMode = new ForecastSocialService.Verified(mode) }],
        } };
        var outcome = Outcome(context) with { Plan = new([new("work", "pawn", 0, 0, 0, 5000, 0, 0, 5000, 0, 0)], 5000, 0) };
        return (context, outcome);
    }

    private static double SocialBabyGain((ForecastCoupledContext Context, ForecastCoupledOutcome Outcome) scenario) =>
        ForecastNursery.NurseryCredit(Served, scenario.Outcome, [], scenario.Context, 5000).SingleOrDefault(g => g.Pawn == "baby").PerDay;

    [Theory]
    [InlineData(-5)]
    [InlineData(5)]
    public void BoundedRecipientMembershipCannotEarnBenefitByDroppingHarmOrHelp(double offset)
    {
        var scenario = SocialScenario(ActivitySocialMode.Normal);
        var read = scenario.Context.Needs.Babies.Social;
        var actor = read.Pawns[0];
        var memory = new SocialMemoryDefRead("BoundedInteraction", offset, 2 * Day, 10, -1, .9, 1);
        var social = read with { MoodDefs = [memory.Def], Pawns = [actor with
        {
            Recipients = ["baby", "unlocated-competitor"],
            Pairs = [actor.Pairs[0] with { Interactions = [new("Interaction", 1, false, [memory])] }],
        }] };
        var context = scenario.Context with { Needs = scenario.Context.Needs with { Babies = scenario.Context.Needs.Babies with { Social = social } } };
        Assert.True(SocialBabyGain((context, scenario.Outcome)) <= 0);
        Assert.Contains("nursery_opinion_adverse_exposure_envelope:pawn",
            ForecastNursery.Findings(Served, scenario.Outcome, [], context, 5000));
    }

    [Fact]
    public void ActiveCarryAndFutureFeedUseTheSameBabyAndCarerExposureWithOneNeedBudget()
    {
        var baby = Baby(inNursery: true, now: false, open: true) with { Care = new()
        {
            Active = new("pawn", BabyCareKind.Carry, new(0, 0), new(2, 0), new ServicePositionRead.PassableMap(), ActivitySocialMode.Quiet),
            Play = null, Food = new(.5, .00001, .0002),
            Opportunities = [new("pawn", BabyCareKind.Breastfeed, 2, new ServicePositionRead.PassableMap(), new ServicePositionRead.PassableMap(), ActivitySocialMode.Normal),
                new("other", BabyCareKind.BottleFeed, 2, new ServicePositionRead.PassableMap(), new ServicePositionRead.PassableMap(), ActivitySocialMode.Normal)],
        } };
        var context = Context(Read(baby));
        var exposure = new ForecastNurseryExposure(context, Outcome(context), Day, new HashSet<string>());
        Assert.Equal(0, exposure.HeardAt(baby, "pawn", 1, 0, true).Upper);
        Assert.Equal(new ServicePositionRead.Located(new(0, 0)), exposure.BabyAt(baby, 0, true).Single(p => p.LowerShare == 1).Position);
        // Carry has its own observed uncertainty budget; the two feeding alternatives share one food demand.
        Assert.Equal(Day, exposure.CareBudgetTicks("baby", "pawn", true));
        var feedOnly = baby with { Care = baby.Care with { Active = null } };
        var fed = Context(Read(feedOnly));
        var feedExposure = new ForecastNurseryExposure(fed, Outcome(fed), Day, new HashSet<string>());
        Assert.Equal((.5 + .00001 * Day) / .0002, feedExposure.CareBudgetTicks("baby", "pawn", true), 8);
        Assert.Equal(new ForecastNurseryExposure.Range(0, 1), feedExposure.HeardAt(feedOnly, "pawn", 1, 100, true));
    }

    [Fact]
    public void AVerifiedSocialToilPricesOpinionThroughTheBabysInsultMoodAndLaterFits()
    {
        var normal = SocialScenario(ActivitySocialMode.Normal);
        var off = SocialScenario(ActivitySocialMode.Off);
        Assert.True(SocialBabyGain(normal) > SocialBabyGain(off));
        var normalHearer = ForecastNursery.NurseryCredit(Served, normal.Outcome, [], normal.Context, 5000).Single(g => g.Pawn == "pawn");
        var offHearer = ForecastNursery.NurseryCredit(Served, off.Outcome, [], off.Context, 5000).Single(g => g.Pawn == "pawn");
        Assert.NotEqual(offHearer.PerDay, normalHearer.PerDay);
        var findings = ForecastNursery.Findings(Served, normal.Outcome, [], normal.Context, 5000).ToArray();
        Assert.Contains("nursery_opinion_mean_field_feedback", findings);
        Assert.DoesNotContain("nursery_baby_opinion", findings);
    }

    [Fact]
    public void NativeBabyRecipientEligibilityAndSocialThoughtStageAdmissionAreRequired()
    {
        Assert.Equal(SocialBabyGain(SocialScenario(ActivitySocialMode.Off)), SocialBabyGain(SocialScenario(ActivitySocialMode.Normal, eligible: false)), 12);
        var absent = SocialScenario(ActivitySocialMode.Normal, admitted: false);
        // Relocation still changes ordinary interactions; missing social-thought admission removes only opinion feedback.
        Assert.NotEqual(SocialBabyGain(SocialScenario(ActivitySocialMode.Normal)), SocialBabyGain(absent));
        Assert.Contains("nursery_no_opinion_hearer", ForecastNursery.Findings(Served, absent.Outcome, [], absent.Context, 5000));
    }

    [Fact]
    public void ATravellingServiceDoesNotBorrowItsEndpointsSocialExposure()
    {
        var moving = SocialScenario(ActivitySocialMode.Normal, moving: true);
        Assert.True(SocialBabyGain(moving) <= SocialBabyGain(SocialScenario(ActivitySocialMode.Off)));
        Assert.Contains("nursery_transit_exposure:pawn", ForecastNursery.Findings(Served, moving.Outcome, [], moving.Context, 5000));
    }

    [Fact]
    public void PositiveMoodInteractionsRemainInTheNativeSelectionDenominator()
    {
        var scenario = SocialScenario(ActivitySocialMode.Normal);
        var read = scenario.Context.Needs.Babies.Social;
        var actor = read.Pawns[0]; var pair = actor.Pairs[0];
        var positive = new SocialMemoryDefRead("KindWordsMood", 5, 2 * Day, 10, -1, .9, 1);
        var changed = read with { MoodDefs = [.. read.MoodDefs, positive.Def], Pawns = [actor with { Pairs = [pair with
        { Interactions = [new("Chitchat", 1, false, [positive]), .. pair.Interactions.Skip(1)] }] }] };
        var context = scenario.Context with { Needs = scenario.Context.Needs with { Babies = scenario.Context.Needs.Babies with { Social = changed } } };
        // The nursery separates these two: removing helpful interaction exposure lowers its benefit.
        Assert.True(SocialBabyGain((context, scenario.Outcome)) < SocialBabyGain(scenario));
    }

    [Fact]
    public void NativeMemoryGroupsComeFromCanonicalNeedsAndDifferingModdedGroupsAreNamed()
    {
        var scenario = SocialScenario(ActivitySocialMode.Normal);
        var pawns = scenario.Context.Needs.Pawns.Select(p => p.Pawn != "baby" ? p : p with { Mood = p.Mood! with
        { Memories = [new("InsultedMood", -10, new MemoryLife.Expires(Day), false)
            { StackLimit = 10, Group = new("pawn", 10, 2 * Day, -10, .9, -1, 1, MemoryGroupKind.Mood, 0) }] } }).ToArray();
        var context = scenario.Context with { Needs = scenario.Context.Needs with { Pawns = [.. pawns] } };
        Assert.Contains("nursery_opinion_mood_group_unprojected:baby/InsultedMood",
            ForecastNursery.Findings(Served, scenario.Outcome, [], context, 5000));
        var missing = pawns.Select(p => p.Pawn != "baby" ? p : p with { Mood = p.Mood! with
        { Memories = [.. p.Mood!.Memories.Select(m => m with { Group = null })] } });
        Assert.StartsWith("live memory group:", Assert.Throws<RequiredFactException>(() => ForecastNursery.Findings(Served,
            scenario.Outcome, [], context with { Needs = context.Needs with { Pawns = [.. missing] } }, 5000)).Fact, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionBindingsAttachOnlyTheVerifiedNativeServiceMode()
    {
        var review = ForecastCoupledTests.ConstructionReview();
        var first = ForecastCoupledCandidates.GenerateCoupledActivities(review)!.Context;
        var binding = first.Bindings.Values.First(b => b.Kind == PawnActivityKind.Work);
        var needs = review.ForecastObserved.Needs!;
        var updated = review with { ForecastObserved = review.ForecastObserved with { Needs = needs with
        { Babies = needs.Babies with { Social = needs.Babies.Social with
        { Jobs = [new(binding.Kind, binding.Job, ActivitySocialMode.Quiet)] } } } } };
        var context = ForecastCoupledCandidates.GenerateCoupledActivities(updated)!.Context;
        Assert.All(context.Activities.Options.Where(o => context.Bindings.TryGetValue((o.Activity, o.Pawn), out var b)
            && b.Kind == binding.Kind && b.Job == binding.Job), o => Assert.Equal(ActivitySocialMode.Quiet, Assert.IsType<ForecastSocialService.Verified>(o.SocialMode).Mode));
        Assert.Contains(context.Activities.Options, o => o.SocialMode is ForecastSocialService.Unprojected);
    }

    [Fact]
    public void SocialCohortsRetainNativeSharedStackEvictionExpiryAndSignedGroupRounding()
    {
        var memory = new SocialMemoryDefRead("cry", -12, 100, 2, -1, .9, .7);
        var pool = new ForecastSocialMemories([new("one", memory, 20), new("two", memory, 10)]);
        Assert.Equal(-12, pool.Offset("one", 0, true));
        pool.Gain("two", memory, 0, 10, 10);
        Assert.InRange(pool.Offset("one", 10, false), -1e-20, 0);
        Assert.Equal(0, pool.Offset("two", 120, true));
        Assert.Equal(1, ForecastSocialMemories.RoundGroup(.1));
        Assert.Equal(-1, ForecastSocialMemories.RoundGroup(-.1));
        // Same-pawn cap can evict even when the global cap has spare room.
        var same = new ForecastSocialMemories([new("one", memory with { StackLimit = 10, SamePawnLimit = 1 }, 20)]);
        same.Gain("one", memory with { StackLimit = 10, SamePawnLimit = 1 }, 0, 10, 10);
        Assert.Equal(-12, same.Offset("one", 10, false), 6);
        // Another pawn's same-pawn cap limits its competition for the shared global cap.
        var shared = new ForecastSocialMemories([new("one", memory with { SamePawnLimit = 1 }, 20)]);
        shared.Gain("two", memory with { SamePawnLimit = 1 }, 0, 10, 10);
        Assert.Equal(-12, shared.Offset("one", 10, true));
    }
}
