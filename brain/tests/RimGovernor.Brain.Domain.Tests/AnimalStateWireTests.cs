using RimGovernor.Contracts.Wire;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// An animal's read is total (#3786): its pregnancy is an <see cref="AnimalPregnancy"/> and its next life stage an
/// <see cref="AnimalNextLifeStage"/>, and every setting is a plain value. It round-trips through <c>WireJsonContext</c>
/// exactly.
/// </summary>
public sealed class AnimalStateWireTests
{
    private static readonly AnimalState Cow = new()
    {
        Gender = RimGovernor.Contracts.Wire.Defs.Gender.Female,
        AgeYears = 3.5,
        Pregnancy = new AnimalPregnancy.Gestating(120000),
        PenId = "CompAnimalPenMarker_1",
        Training = [],
        Release = false,
        Slaughter = false,
        Tameable = false,
        Tame = false,
        SafeToRelease = true,
        MasterId = "",
        FollowDrafted = false,
        FollowFieldwork = false,
        Obedient = false,
        SupportsAllowedAreas = false,
        Sick = false,
        Venerated = false,
        Adult = true,
        Bonded = false,
        Sterilized = false,
        SterilizeQueued = false,
        Downed = false,
        InMentalState = false,
        ColonistBonded = false,
        SlaughterDesignatable = true,
        LifeStageIndex = 2,
        NextLifeStage = new AnimalNextLifeStage.LastStage(),
        BondedPawnIds = [],
    };

    [Fact]
    public void AnAnimalReadRoundTripsExactly()
    {
        foreach (var (state, pregnancy, stage) in new (AnimalState, string, string)[]
        {
            (Cow, """{"type":"gestating","ticksToBirth":120000}""", """{"type":"last_stage"}"""),
            (Cow with { Pregnancy = new AnimalPregnancy.NotPregnant(), NextLifeStage = new AnimalNextLifeStage.InTicks(60000) }, """{"type":"not_pregnant"}""", """{"type":"in_ticks","ticks":60000}"""),
            (Cow with { Pregnancy = new AnimalPregnancy.Stalled(), NextLifeStage = new AnimalNextLifeStage.NotAging() }, """{"type":"stalled"}""", """{"type":"not_aging"}"""),
        })
        {
            var json = WireJson.Serialize(state);

            Assert.Contains("\"pregnancy\":" + pregnancy, json);
            Assert.Contains("\"nextLifeStage\":" + stage, json);
            Assert.Contains("\"masterId\":\"\"", json);
            Assert.Equal(json, WireJson.Serialize(WireJson.Deserialize<AnimalState>(json)));
        }
    }

    [Fact]
    public void AnAnimalWithoutItsPregnancyDoesNotDecode()
    {
        var json = WireJson.Serialize(Cow).Replace("\"pregnancy\":{\"type\":\"gestating\",\"ticksToBirth\":120000},", "", StringComparison.Ordinal);

        Assert.ThrowsAny<System.Text.Json.JsonException>(() => WireJson.Deserialize<AnimalState>(json));
    }
}
