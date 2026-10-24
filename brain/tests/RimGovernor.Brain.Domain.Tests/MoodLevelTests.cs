using RimGovernor.Brain.Observation;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store.Codec;
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
    private static Dictionary<string, O.PatientForecast> Patients(params (string Id, double Target)[] rows) =>
        rows.ToDictionary(r => r.Id, r => new O.PatientForecast { PawnId = r.Id, MoodTarget = r.Target }, StringComparer.Ordinal);

    [Fact]
    public void AMoodNeedCarriesItsLevelAndTheForecastTarget()
    {
        Assert.Equal(new MoodLevel.Has(.4, .6), RoundsMoodReads.MoodLevelOf("a", new O.NeedLevel.Has(.4), Patients(("a", .6))));
    }

    [Fact]
    public void AColonistWithoutAMoodNeedHasNeitherLevelNorTarget()
    {
        Assert.Equal(new MoodLevel.Lacks(), RoundsMoodReads.MoodLevelOf("a", new O.NeedLevel.Lacks(), Patients()));
    }

    [Fact]
    public void AMoodNeedWithoutItsPatientRowIsARequiredFact()
    {
        var thrown = Assert.Throws<RequiredFactException>(() => RoundsMoodReads.MoodLevelOf("a", new O.NeedLevel.Has(.4), Patients(("b", .6))));
        Assert.Equal("mood target of a", thrown.Fact);
    }

    [Fact]
    public void BothStatesRoundTripTheStore()
    {
        MoodLevel has = new MoodLevel.Has(.4, .6);
        MoodLevel lacks = new MoodLevel.Lacks();
        Assert.Equal(has, StoreJson.Deserialize<MoodLevel>(StoreJson.Serialize(has)));
        Assert.Equal(lacks, StoreJson.Deserialize<MoodLevel>(StoreJson.Serialize(lacks)));
        var pawn = new MoodPawn { Id = new("a"), Mood = has, Threshold = .35, Thoughts = [new("SleptOutside", -4)] };
        Assert.Equal(pawn, StoreJson.Deserialize<MoodPawn>(StoreJson.Serialize(pawn)));
    }

    [Fact]
    public void ANonFiniteTargetIsRefused()
    {
        var pawn = new MoodPawn { Id = new("a"), Mood = new MoodLevel.Has(.4, double.NaN), Threshold = .35 };
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
}
