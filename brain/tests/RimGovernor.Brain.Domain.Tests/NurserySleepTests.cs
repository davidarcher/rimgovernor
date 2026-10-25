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
    private const double Rate = 1d / 10000;
    private static readonly Dictionary<(string Baby, string Pawn, string Fit, bool Sleep), ForecastNurseryExposure.Range> EmptyRates = new();
    private static NeedsForecastPawn Sleeper(double gain = 3e-5, double fall = 1e-5) => NeedsFixtures.Pawn() with
    {
        Location = new(100, 100), // Snapshot is deliberately outside the map: only the exact sleeping slot is read.
        Sleep = new("bed", false, false, gain, .75, 21) { Position = new ServicePositionRead.Located(new(0, 0)) },
        Needs = [NeedsFixtures.Rate("rest", .8, fall, .28)],
        Targets = [NeedsFixtures.Service("quiet-bed", "rest", 0, 7e-5) with { ServicePosition = new ServicePositionRead.Located(new(15, 0)) }],
    };
    private static BabyRead Baby() => new()
    {
        Care = new() { Active = null, Food = null, Play = null, Opportunities = [] },
        CribMood = new(0, 0, 0), Pawn = "baby", InNursery = true, NurseryRoom = 10, Fits = ["Crying"], Play = null, Hearers = [],
        SleepWithoutNursery = new("", false, true, 3e-5, 0, 21) { Position = new ServicePositionRead.Located(new(0, 0)) },
        SleepHearers = [new("pawn", Disturbed)],
    };
    private static ForecastCoupledContext Context(NeedsForecastPawn? pawn = null) => ForecastValueTests.Context(pawn ?? Sleeper()) with
    {
        Needs = NeedsFixtures.Empty with
        {
            Pawns = [pawn ?? Sleeper(), NeedsFixtures.Pawn("baby") with
            {
                Sleep = new("crib", false, false, 3e-5, .75, 21) { Position = new ServicePositionRead.Located(new(2, 0)) },
                Needs = [NeedsFixtures.Rate("rest", 1, 1e-5, .28)],
            }],
            Babies = NeedsFixtures.NoBabies with
            {
                Babies = [Baby()], Geometry = new(20, 1,
                    [.. Enumerable.Range(0, 20).Select(x => x == 1 ? 1 : x is >= 2 and <= 9 ? 2 : 0)],
                    [.. Enumerable.Range(0, 20).Select(x => x != 1)],
                    [new(0, false, true, 0, [1]), new(1, true, false, -1, [0, 2]), new(2, false, true, 10, [1])], 9.9, 6, 15),
            },
        },
    };
    private static ForecastCoupledOutcome Outcome => new(new([], 0, 0), true, new Dictionary<string, ForecastNeedsResult>(), []);
    private static Dictionary<(string Baby, string Pawn, string Fit, bool Sleep), ForecastNurseryExposure.Range> Rates(BabyRead baby, ForecastNurseryExposure.Range share) =>
        new() { [(baby.Pawn, "pawn", "Crying", true)] = new(Rate * share.Lower, Rate * share.Upper) };
    private static (double With, double Without) Mood(ForecastCoupledContext context, ForecastCoupledOutcome? outcome = null)
    {
        var baby = context.Needs.Babies.Babies[0];
        var exposure = new ForecastNurseryExposure(context, outcome ?? Outcome, 60000, new HashSet<string>());
        var pool = new ForecastNurserySleep(context);
        pool.Add(baby, Rates(baby, exposure.HeardAt(baby, "pawn", 1, 2000, true, true)),
            Rates(baby, exposure.HeardAt(baby, "pawn", 1, 2000, false, true)));
        return pool.Moods().GetValueOrDefault("pawn");
    }

    [Fact]
    public void CanonicalSleepingSlotAndRecurrentShareApplyWhenThePawnStandsElsewhere()
    {
        var mood = Mood(Context());
        var rate = Rate * .25;
        Assert.Equal(0, mood.With);
        Assert.Equal(ForecastNursery.HeardMood([(Disturbed, rate / (1 + rate * 300))]), mood.Without, 12);
        Assert.True(Mood(Context(Sleeper(fall: 2e-5))).Without < mood.Without);
        Assert.Equal(0, Mood(Context(Sleeper(fall: 0))).Without);
    }

    [Fact]
    public void ClosedDoorsAndTheSelectedFutureSleepingSurfaceUseTheSameExposureOwner()
    {
        Assert.True(Mood(Context()).Without < 0);
        var context = Context() with
        {
            NeedTasks = [new("rest", "pawn", "rest", "quiet-bed", new(""), 0, 1, [])],
            Activities = new() { Deadline = 60000, Activities = [new("rest", "bed", "bed", [])],
                Options = [new("rest", "pawn", 1000, 0) { ServicePosition = new ServicePositionRead.Located(new(15, 0)) }] },
        };
        var outcome = Outcome with { Plan = new([new("rest", "pawn", 0, 0, 0, 1000, 0, 0, 1000, 0, 0)], 1000, 0) };
        Assert.Equal((0d, 0d), Mood(context, outcome));
        var audible = context with { Needs = context.Needs with { Pawns = [.. context.Needs.Pawns.Select(p => p.Pawn != "pawn" ? p : p with
        { Targets = [.. p.Targets.Select(t => t with { ServicePosition = new ServicePositionRead.Located(new(0, 0)) })] })] } };
        var rate = Rate * .125;
        Assert.Equal(ForecastNursery.HeardMood([(Disturbed, rate / (1 + rate * 300))]), Mood(audible, outcome).Without, 12);
    }

    [Fact]
    public void DifferentBabiesShareOneDisturbedSleepStackAndCooldown()
    {
        var pool = new ForecastNurserySleep(Context());
        for (var i = 0; i < 100; i++)
        {
            var baby = Baby() with { Pawn = "baby" + i };
            pool.Add(baby, EmptyRates, Rates(baby, new(.25, .25)));
        }
        var rate = 100 * Rate * .25;
        var mood = pool.Moods()["pawn"].Without;
        Assert.Equal(ForecastNursery.HeardMood([(Disturbed, rate / (1 + rate * 300))]), mood, 12);
        Assert.InRange(mood, -3, 0);
    }

    [Fact]
    public void GigglesAndIneligibleHearersNeverCreateDisturbedSleep()
    {
        var pool = new ForecastNurserySleep(Context());
        pool.Add(Baby() with { SleepHearers = [] }, EmptyRates, EmptyRates);
        Assert.Empty(pool.Moods());
        pool.Add(Baby(), new Dictionary<(string, string, string, bool), ForecastNurseryExposure.Range>
            { [("baby", "pawn", "Giggling", true)] = new(.001, .001) }, EmptyRates);
        Assert.All(pool.Moods().Values, mood => Assert.Equal((0d, 0d), mood));
    }

    [Fact]
    public void MissingSleepingFactsCannotBecomeZeroCredit()
    {
        Assert.Throws<RequiredFactException>(() => Mood(Context(Sleeper() with { Sleep = null })));
        Assert.Throws<RequiredFactException>(() => Mood(Context(Sleeper() with { Needs = [] })));
        Assert.Throws<RequiredFactException>(() => Mood(Context(Sleeper(gain: double.NaN))));
        Assert.Throws<RequiredFactException>(() => Mood(Context(Sleeper() with { Sleep = Sleeper().Sleep! with { Position = new ServicePositionRead.Unprojected("synthetic_sleep_surface") } })));
        var context = Context();
        Assert.Throws<RequiredFactException>(() => Mood(context with { Needs = context.Needs with
        { Babies = context.Needs.Babies with { SleepDisturbanceIntervalTicks = double.NaN } } }));
    }

    [Fact]
    public void CanonicalExposureFactsRoundTripAndAnAbsentSleepAdmissionReadFails()
    {
        var read = Context().Needs;
        var json = JsonSerializer.Serialize(read, FactsJsonContext.Default.NeedsForecastRead);
        Assert.Equal(read, JsonSerializer.Deserialize(json, FactsJsonContext.Default.NeedsForecastRead));
        var absent = JsonNode.Parse(json)!.AsObject();
        absent["babies"]!["babies"]![0]!.AsObject().Remove("sleepHearers");
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(absent.ToJsonString(), FactsJsonContext.Default.NeedsForecastRead));
    }
}
