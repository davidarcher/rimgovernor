using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameFacts;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class NurserySleepTests
{
    private static readonly HeardMemoryRead Disturbed = new("Crying", "SleepDisturbed", -1, GameTime.TicksPerDay, 3, 1);
    private static readonly Dictionary<string, double> Rates = new() { ["Crying"] = 1d / 10000 };
    private static NeedsForecastPawn Sleeper(double gain = 3e-5, double fall = 1e-5) => NeedsFixtures.Pawn() with
    {
        Location = new(100, 100), // Away from the bed and the crying baby.
        Sleep = new("bed", false, false, gain, .75, 21),
        Needs = [NeedsFixtures.Rate("rest", .8, fall, .28)],
        Targets = [NeedsFixtures.Service("bed", "rest", 0, gain), NeedsFixtures.Service("quiet-bed", "rest", 0, 7e-5)],
    };
    private static BabyRead Baby(bool standing = false, bool now = true, bool open = true) => new()
    {
        CribMood = new(0, 0, 0), Pawn = "baby", InNursery = standing, Fits = ["Crying"], Play = null, Hearers = [],
        SleepWithoutNursery = new("", false, true, 3e-5, 0, 21),
        SleepHearers = [new("pawn", "bed", now, open, Disturbed), new("pawn", "quiet-bed", false, false, Disturbed)],
    };
    private static ForecastCoupledContext Context(NeedsForecastPawn? pawn = null) => ForecastValueTests.Context(pawn ?? Sleeper()) with
    {
        Needs = NeedsFixtures.Empty with { Pawns = [pawn ?? Sleeper()] },
    };
    private static ForecastCoupledOutcome Outcome => new(new([], 0, 0), true, new Dictionary<string, ForecastNeedsResult>(), []);
    private static (double With, double Without) Mood(ForecastCoupledContext context, BabyRead baby, ForecastCoupledOutcome? outcome = null)
    {
        var pool = new ForecastNurserySleep(context, outcome ?? Outcome);
        pool.Add(baby, Rates, Rates);
        return pool.Moods().GetValueOrDefault("pawn");
    }

    [Fact]
    public void BedReachPricesTheAsleepShareEvenWhenTheColonistStandsElsewhereAndHasNoRestJob()
    {
        var mood = Mood(Context(), Baby());
        var rate = Rates["Crying"] * .25;
        Assert.Equal(0, mood.With);
        Assert.Equal(ForecastNursery.HeardMood([(Disturbed, rate / (1 + rate * 300))]), mood.Without, 12);
        Assert.True(mood.Without < 0);
        Assert.True(Mood(Context(Sleeper(fall: 2e-5)), Baby()).Without < mood.Without);
        Assert.Equal(0, Mood(Context(Sleeper(fall: 0)), Baby()).Without);
    }

    [Fact]
    public void ClosedNurseryDoorsAvoidTheSleepCostAndOpenDoorsRetainIt()
    {
        Assert.True(Mood(Context(), Baby(standing: true, now: false)).Without < 0);
        Assert.Equal(0, Mood(Context(), Baby(standing: true, now: false)).With);
        var open = Mood(Context(), Baby(standing: true));
        Assert.Equal(open.With, open.Without);
        Assert.Equal((0d, 0d), Mood(Context(), Baby(now: false, open: false)));
    }

    [Fact]
    public void TheOutcomeUsesItsSelectedSleepingSurfaceAndRestGain()
    {
        var context = Context() with { NeedTasks = [new("rest", "pawn", "rest", "quiet-bed", new(""), 0, 1, [])] };
        var outcome = Outcome with { Plan = new([new("rest", "pawn", 0, 0, 0, 1000, 0, 0, 1000, 0, 0)], 1000, 0) };
        Assert.Equal((0d, 0d), Mood(context, Baby(), outcome));
        var audible = Baby() with { SleepHearers = [new("pawn", "quiet-bed", true, true, Disturbed)] };
        var rate = Rates["Crying"] * .125;
        Assert.Equal(ForecastNursery.HeardMood([(Disturbed, rate / (1 + rate * 300))]), Mood(context, audible, outcome).Without, 12);
    }

    [Fact]
    public void DifferentBabiesShareOneDisturbedSleepStackAndCooldown()
    {
        var pool = new ForecastNurserySleep(Context(), Outcome);
        for (var i = 0; i < 100; i++) pool.Add(Baby() with { Pawn = "baby" + i }, Rates, Rates);
        var rate = 100 * Rates["Crying"] * .25;
        var mood = pool.Moods()["pawn"].Without;
        Assert.Equal(ForecastNursery.HeardMood([(Disturbed, rate / (1 + rate * 300))]), mood, 12);
        Assert.InRange(mood, -3, 0);
    }

    [Fact]
    public void GigglesAndIneligibleHearersNeverCreateDisturbedSleep()
    {
        var pool = new ForecastNurserySleep(Context(), Outcome);
        pool.Add(Baby(), new Dictionary<string, double> { ["Giggling"] = .001 }, Rates);
        Assert.Empty(pool.Moods());
        pool.Add(Baby() with { SleepHearers = [] }, Rates, Rates);
        Assert.Empty(pool.Moods());
    }

    [Fact]
    public void RequiredSleepingFactsCannotDisappearIntoZeroCredit()
    {
        Assert.Throws<RequiredFactException>(() => Mood(Context(Sleeper() with { Sleep = null }), Baby()));
        Assert.Throws<RequiredFactException>(() => Mood(Context(Sleeper() with { Needs = [] }), Baby()));
        Assert.Throws<RequiredFactException>(() => Mood(Context(Sleeper(gain: double.NaN)), Baby()));
        Assert.Throws<RequiredFactException>(() => Mood(Context(), Baby() with { SleepHearers = [new("pawn", "wrong-bed", true, true, Disturbed)] }));
        Assert.Throws<RequiredFactException>(() => Mood(Context() with { Needs = NeedsFixtures.Empty with { Pawns = [Sleeper()], Babies = NeedsFixtures.NoBabies with { SleepDisturbanceIntervalTicks = double.NaN } } }, Baby()));
    }

    [Fact]
    public void SleepingReachRoundTripsThroughTheCanonicalReadAndAnAbsentReadFails()
    {
        var read = NeedsFixtures.Empty with { Babies = NeedsFixtures.NoBabies with { Babies = [Baby()] } };
        var json = JsonSerializer.Serialize(read, FactsJsonContext.Default.NeedsForecastRead);
        Assert.Equal(read, JsonSerializer.Deserialize(json, FactsJsonContext.Default.NeedsForecastRead));
        var absent = JsonNode.Parse(json)!.AsObject();
        absent["babies"]!["babies"]![0]!.AsObject().Remove("sleepHearers");
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(absent.ToJsonString(), FactsJsonContext.Default.NeedsForecastRead));
    }
}
