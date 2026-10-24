using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Contracts.Wire;
using Xunit;
using Common = RimGovernor.Contracts.Wire.Common;
using Obs = RimGovernor.Contracts.Wire.Observations;
using Ops = RimGovernor.Contracts.Wire.Operations;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The population read is whole (#3769): every person carries every fact, how the colony holds it is a named custody
/// state, and the colony's ideology mode is named. It round-trips through <c>WireJsonContext</c> exactly and decodes
/// into the prisoner census without an unknown.
/// </summary>
public sealed class PopulationWireTests
{
    private static readonly Obs.PawnBiography Bio = PawnFixtures.Row("p").Biography;

    private static Obs.PopulationPerson Person(string id) => new()
    {
        Pawn = new PawnId(id),
        Admitted = false,
        Guest = false,
        Recruitable = false,
        IdeoId = null,
        WildMan = false,
        Biography = Bio,
        HealthSummary = 1,
        LuciferiumAddicted = false,
        WearingApparel = true,
        Custody = new Obs.PersonCustody.None(),
        WillJoinIfRescued = false,
    };

    private static readonly Obs.PopulationSnapshot Population = new()
    {
        Context = new Common.ObservationContext { Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 100 },
        Persons =
        [
            Person("colonist") with { Admitted = true, IdeoId = "Ideo_1" },
            Person("prisoner") with
            {
                Guest = true,
                Recruitable = true,
                Custody = new Obs.PersonCustody.Prisoner(12.5, "ReduceResistance", 2500, Ops.MedicalCare.HerbalOrWorse, new Obs.PawnConditions(), new Obs.PawnSurgery(), new Common.Ref { Id = "Faction_3" }, -40),
            },
            Person("guest") with { Guest = true, Custody = new Obs.PersonCustody.Guest(Ops.MedicalCare.Best, new Obs.PawnConditions()) },
        ],
        Ideology = new Obs.ColonyIdeology.Installed(false, "Ideo_1"),
        OwnedNames = [new Obs.OwnedName { PawnId = new PawnId("colonist"), ShortName = "Tess", ThingId = 7 }],
    };

    [Fact]
    public void EveryCustodyStateAndTheIdeologyModeRoundTrip()
    {
        var json = WireJson.Serialize(Population);

        Assert.Contains("\"custody\":{\"type\":\"none\"}", json, StringComparison.Ordinal);
        Assert.Contains("\"custody\":{\"type\":\"prisoner\",\"resistance\":12.5,\"interaction\":\"ReduceResistance\",\"heldTicks\":2500", json, StringComparison.Ordinal);
        Assert.Contains("\"custody\":{\"type\":\"guest\"", json, StringComparison.Ordinal);
        Assert.Contains("\"ideology\":{\"type\":\"installed\",\"classic\":false,\"primaryIdeoId\":\"Ideo_1\"}", json, StringComparison.Ordinal);
        Assert.Contains("\"ownedNames\":[{\"pawnId\":\"colonist\",\"shortName\":\"Tess\",\"thingId\":7}]", json, StringComparison.Ordinal);
        Assert.Equal(Population, WireJson.Deserialize<Obs.PopulationSnapshot>(json));
        var bare = Population with { Ideology = new Obs.ColonyIdeology.NotInstalled() };
        Assert.Equal(bare, WireJson.Deserialize<Obs.PopulationSnapshot>(WireJson.Serialize(bare)));
    }

    [Fact]
    public void TheCensusReadsEachCustodyStateKnown()
    {
        var pawns = Pawns.Of(PawnFixtures.Row("colonist"), PawnFixtures.Row("prisoner") with { Prisoner = true, Colonist = false, FreeColonist = false }, PawnFixtures.Row("guest") with { Colonist = false, FreeColonist = false });

        var census = PopulationDecoder.Decode(Population, pawns, null);

        Assert.True(census.Prisoners.TryGetValue(out var prisoners));
        var prisoner = Assert.Single(prisoners);
        Assert.Equal(new PawnId("prisoner"), prisoner.Pawn);
        Assert.True(prisoner.Resistance.TryGetValue(out var resistance));
        Assert.Equal(12.5, resistance);
        Assert.True(prisoner.HarvestGoodwill.TryGetValue(out var goodwill));
        Assert.Equal(-40, goodwill);
        Assert.True(census.Guests.TryGetValue(out var guests));
        Assert.Equal(new PawnId("guest"), Assert.Single(guests).Id);
        Assert.True(census.Colony.TryGetValue(out var colony));
        Assert.True(colony.IdeologyActive);
        Assert.False(colony.ClassicIdeo);
        Assert.Equal("Ideo_1", colony.Ideo);
        Assert.Equal(1, colony.Colonists);
        Assert.True(census.Custody.TryGetValue(out var custody));
        Assert.Equal(3, custody.Count);
        Assert.All(custody, row => Assert.True(row.Prospect.IsKnown));
    }

    [Fact]
    public void APersonOutsideThePawnTableIsAContractFailure()
    {
        var pawns = Pawns.Of(PawnFixtures.Row("colonist"));

        Assert.Throws<BridgeContractException>(() => PopulationDecoder.Decode(Population, pawns, null));
    }
}
