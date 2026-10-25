using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store.Codec;
using RimGovernor.Brain.Store;
using RimGovernor.Contracts.Wire;
using Xunit;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// A colonist's mood is a named state (#3897): <see cref="MoodLevel.Has"/> its level and the target the forecast's patient
/// row carries, or <see cref="MoodLevel.Lacks"/> a mood need. The forecast skips a colonist without a mood need, so a
/// mood need without a patient row is a missing read; both states persist as written.
/// </summary>
public sealed class MoodLevelTests
{
    [Fact]
    public void AMoodNeedCarriesItsLevelAndTheForecastTarget()
    {
        Assert.Equal(new MoodLevel.Has(.4, .6), MoodFixtures.Has("a", .4, .6).Mood);
    }

    [Fact]
    public void AColonistWithoutAMoodNeedHasNeitherLevelNorTarget()
    {
        Assert.Equal(new MoodLevel.Lacks(), MoodFixtures.Pawn("a").Mood);
    }

    [Fact]
    public void AMoodNeedWithoutItsPatientRowIsARequiredFact()
    {
        var thrown = Assert.Throws<RequiredFactException>(() => (MoodFixtures.Has("a", .4, .6) with { Forecast = null }).Mood);
        Assert.Equal("mood target of a", thrown.Fact);
    }

    [Fact]
    public void NativeMoodInputsRoundTripTheStore()
    {
        var pawn = MoodFixtures.Has("a", .4, .6) with { Thoughts = [new("SleptOutside", -4)] };
        Assert.Equal(pawn, StoreJson.Deserialize<MoodPawn>(StoreJson.Serialize(pawn)));
    }

    [Fact]
    public void ANonFiniteTargetIsRefused()
    {
        var pawn = MoodFixtures.Has("a", .4, double.NaN);
        Assert.Throws<DomainException>(pawn.Validate);
    }

    [Fact]
    public void APatientRowRoundTripsTheWireWithBothFieldsRequired()
    {
        var patient = new O.PatientForecast { PawnId = "a", MoodTarget = .5 };
        var json = WireJson.Serialize(patient);
        Assert.Equal("""{"pawnId":"a","moodTarget":0.5}""", json);
        Assert.Equal(patient, WireJson.Deserialize<O.PatientForecast>(json));
    }
    [Fact]
    public void MoodViewRetainsItsActualNativeInputsAndAllNativeFlags()
    {
        var input = MoodFixtures.Has("a", .2, .1, .15);
        var native = input.Pawn with
        {
            Dead = true, Downed = true, Drafted = true,
            Job = input.Pawn.Job with { PlayerForced = true },
            MentalState = new O.PawnMentalState { DefName = "Berserk", IsAggro = true, Ticks = 90 },
            Social = input.Pawn.Social with { HighExpectations = true },
            Needs = input.Pawn.Needs with { Food = new O.FoodNeed.Has(.1, RimGovernor.Contracts.Wire.Defs.HungerCategory.Hungry), Rest = new O.NeedLevel.Has(.2), Joy = new O.NeedLevel.Has(.3) },
        };
        var pawn = input with { Pawn = native };
        Assert.Same(native, pawn.Pawn);
        Assert.Same(input.Forecast, pawn.Forecast);
        Assert.True(pawn.Dead && pawn.Downed && pawn.Drafted && pawn.PlayerForced && pawn.Mental && pawn.HighExpectations);
        Assert.Equal(new MoodLevel.Has(.2, .1), pawn.Mood);
        Assert.Equal(.15, pawn.Threshold);
        Assert.Equal(new O.NeedLevel.Has(.1), pawn.Food);
        Assert.Same(native.Needs.Rest, pawn.Rest);
        Assert.Same(native.Needs.Joy, pawn.Joy);
        Assert.Equal(pawn, StoreJson.Deserialize<MoodPawn>(StoreJson.Serialize(pawn)));
    }

    [Fact]
    public void MoodHistoryReplayRetainsTheWholeNativePawnForecastAndThoughts()
    {
        var input = MoodFixtures.Has("a", .2, .1, .15);
        var pawn = input with
        {
            Pawn = input.Pawn with
            {
                Inspiration = "Inspired_Creativity",
                Social = input.Pawn.Social with { HighExpectations = true },
                Needs = input.Pawn.Needs with { Rest = new O.NeedLevel.Has(.1) },
            },
            Thoughts = [new("SleptOutside", -4)],
        };
        var state = Assert.Single(MoodRules.ReviewMood([pawn], new MoodHistory()).States);
        var rounds = new Rounds { Mood = new RoundsMood { States = [new RoundsMoodState
        {
            Pawn = pawn, Active = state.Active, MentalRisk = state.MentalRisk,
            Causes = EquatableArray.From(state.Causes.Select(c => new RoundsMoodCause(c.Need, c.Level))),
            Provision = EquatableArray.From(state.Provision), Unowned = EquatableArray.From(state.Unowned),
        }] } };
        var restored = StoreJson.Deserialize<Rounds>(StoreJson.Serialize(rounds));
        var replay = Assert.Single(restored.MoodHistory().States);
        Assert.Equal(pawn, replay.Pawn);
        Assert.Equal(MoodRules.SelectMoodMethod(state, []), MoodRules.SelectMoodMethod(replay, []));
        Assert.Equal("Inspired_Creativity", replay.Pawn.Pawn.Inspiration);
        Assert.True(replay.Pawn.HighExpectations);
        Assert.Equal(pawn.Thoughts, replay.Pawn.Thoughts);
    }

    [Fact]
    public void ATargetForAnotherPawnOrAPawnWithoutMoodCannotSupplyEvidence()
    {
        var wrong = MoodFixtures.Has("a", .4, .6) with { Forecast = new O.PatientForecast { PawnId = "b", MoodTarget = .6 } };
        Assert.Throws<RequiredFactException>(wrong.Validate);
        var lacks = MoodFixtures.Pawn("a") with { Forecast = new O.PatientForecast { PawnId = "a", MoodTarget = .6 } };
        Assert.Throws<DomainException>(lacks.Validate);
        var restored = StoreJson.Deserialize<MoodPawn>(StoreJson.Serialize(MoodFixtures.Pawn("a")));
        Assert.Null(restored.Forecast);
        Assert.IsType<MoodLevel.Lacks>(restored.Mood);
    }

    [Fact]
    public void BridgeMoodViewUsesTheSameNativePawnAndForecastReferences()
    {
        var native = MoodFixtures.Has("a", .4, .6);
        var source = ColonyFactsCoreTests.Snapshot();
        source = source with { ColonistCount = 1, Forecast = source.Forecast with { Patients = [native.Forecast!] } };
        var emergency = new EmergencyFacts { Colonists = [EmergencyFixtures.Colonist("a")] };
        var census = new O.PawnSnapshot { Context = source.Context, Pawns = [native.Pawn] };
        var mood = Assert.Single(RoundsMoodReads.RoundsMood(source, emergency, census));
        Assert.Same(native.Pawn, mood.Pawn);
        Assert.Same(native.Forecast, mood.Forecast);
        source = source with { Forecast = source.Forecast with { Patients = [] } };
        Assert.Throws<RequiredFactException>(() => RoundsMoodReads.RoundsMood(source, emergency, census));
    }
}
