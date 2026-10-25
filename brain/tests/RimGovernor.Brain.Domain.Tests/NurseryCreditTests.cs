using RimGovernor.Brain.Policy;
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
        Pawn = pawn, Fits = ["Crying", "Giggling"], InNursery = inNursery,
        Hearers = now || open ? [new() { Pawn = "pawn", Now = now, NurseryOpen = open, Memories = [.. Memories(parent)] }] : [],
    };

    /// <summary>The baby's needs row: its mood held at <paramref name="mood"/>, its rest falling 1/3 as fast as it is regained.</summary>
    private static NeedsForecastPawn BabyRow(string pawn = "baby", double mood = 0.2) => NeedsFixtures.Pawn(pawn) with
    {
        Mood = NeedsFixtures.Mood with { Level = mood, Target = mood, BreakThreshold = .3, Memories = [] },
        Needs = [NeedsFixtures.Rate("rest", 1d, 1e-5, .28) with { UnservedGainPerTick = 3e-5 }],
    };

    private static ForecastCoupledContext Context(BabyFitsRead babies, IEnumerable<NeedsForecastPawn>? rows = null, params int[] nurseries)
    {
        var hearer = ForecastValueTests.Pawn();
        return ForecastValueTests.Context(hearer) with
        {
            Needs = NeedsFixtures.Empty with { Pawns = [hearer, .. rows ?? [BabyRow()]], Babies = babies },
            PlannedRoomRoles = (nurseries.Length == 0 ? [1] : nurseries).ToDictionary(r => r, _ => PlannedRole.Nursery),
            RoomCredits = PlannedRoomCredits.Shipped, InvestmentHorizonDays = Horizon,
        };
    }

    private static BabyFitsRead Read(params BabyRead[] babies) => NeedsFixtures.NoBabies with { Babies = [.. babies] };

    /// <summary>The colonist's mood breaks nothing unshifted or raised, and a day in the mildest tier at each lowering shift the context asks for.</summary>
    private static ForecastCoupledOutcome Outcome(ForecastCoupledContext context) =>
        new(new([], 0, 0), true, new Dictionary<string, ForecastNeedsResult>
        {
            ["pawn"] = new([], new ForecastTerm.Unassessed("not_projected"), new ForecastTerm.Unassessed("not_projected"), new(new("pawn", "", new ForecastCapacityWindows.Unread(), 0), []), [])
            {
                Mood = MoodResults.Flat(0d, [0, 0, 0], [.. PlannedRoomCredits.BreakShifts(context).GetValueOrDefault("pawn", []).Select(s => new ForecastShiftedBreaks(s, [s < 0 ? Day : 0, 0, 0]))]),
            },
        }, [])
        { ServedRooms = [Served] };

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
        Assert.Equal([without / 100], ForecastNursery.Shifts(context)["pawn"], new Tolerance());
        var gain = Assert.Single(Gains(context));
        Assert.Equal(("pawn", "pawn"), (gain.Subject, gain.Pawn));
        // Minus GainPerDay of the cries' shift over the one-day horizon: its wellbeing and the day of breaks it brings.
        var lost = context.MoodBreaks.DaysLost([Day, 0, 0])!.Value - context.MoodBreaks.DaysLost([0, 0, 0])!.Value;
        Assert.Equal(-(context.Value.WellbeingWeight * without / 100) + lost, gain.PerDay, 9);
        Assert.True(gain.PerDay > 0);
        Assert.Contains("nursery_disturbed_sleep", Findings(context));

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
        Assert.Empty(ForecastNursery.Shifts(through));
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
        var read = Read(Baby()) with { Fits = [.. NeedsFixtures.NoBabies.Fits, new() { Def = "Babbling", MtbDaysByMood = [new(0, 1)], DurationTicks = 600, Heard = false }] };
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
    public void TheCoupledContextCarriesTheBabyFitsTheRoundsRead()
    {
        // The production path (#3822): GenerateCoupledActivities builds the context from the review's facts; the babies, their
        // fits and who hears them ride on the needs read (NeedsForecastRead.Babies), which Read fills.
        var review = ForecastCoupledTests.ConstructionReview();
        var needs = review.ForecastObserved.Needs!;
        var hearer = needs.Pawns[0].Pawn;
        var babies = NeedsFixtures.NoBabies with { Babies = [Baby() with { Hearers = [.. Baby().Hearers.Select(h => h with { Pawn = hearer })] }] };
        var facts = review with
        {
            PlannedShelters = [new() { Id = 1, Role = PlannedRole.Nursery, Region = RegionRules.RectRegion(new(1, 1, 1, 1)) }],
            ForecastObserved = review.ForecastObserved with { Needs = needs with { Pawns = [.. needs.Pawns, BabyRow()], Babies = babies } },
        };
        var context = ForecastCoupledCandidates.GenerateCoupledActivities(facts)!.Context;
        Assert.Same(facts.ForecastObserved.Needs, context.Needs);
        Assert.Equal(PlannedRole.Nursery, context.PlannedRoomRoles[1]);
        Assert.True(context.Priced(1));
        var shift = Assert.Single(PlannedRoomCredits.BreakShifts(context)[hearer]);
        Assert.True(shift < 0);
        // A fresh colony's review (no baby) credits the nursery nothing and throws nothing.
        var fresh = ForecastCoupledCandidates.GenerateCoupledActivities(review with { PlannedShelters = facts.PlannedShelters })!.Context;
        Assert.Empty(ForecastNursery.NurseryCredit(new(1, "", Day), Outcome(fresh), [], fresh, Day));
        Assert.Contains(ForecastNursery.NoBaby, ForecastNursery.Findings(new(1, "", Day), Outcome(fresh), [], fresh, Day));
    }

    private sealed class Tolerance : IEqualityComparer<double>
    {
        public bool Equals(double x, double y) => Math.Abs(x - y) < 1e-12;

        public int GetHashCode(double obj) => 0;
    }
}
