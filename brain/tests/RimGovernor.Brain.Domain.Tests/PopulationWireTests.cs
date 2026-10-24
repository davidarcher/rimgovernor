using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Policy;
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
        Persons =
        [
            Person("colonist") with { Admitted = true, IdeoId = "Ideo_1" },
            Person("prisoner") with
            {
                Guest = true,
                Recruitable = true,
                Custody = new Obs.PersonCustody.Prisoner(12.5, "ReduceResistance", 2500, Ops.MedicalCare.HerbalOrWorse, new Obs.PawnConditions { LifeThreatening = false, Hediffs = [] }, PawnFixtures.Healthy.Surgery!, new Common.Ref { Id = "Faction_3" }, -40),
            },
            Person("guest") with { Guest = true, Custody = new Obs.PersonCustody.Guest(Ops.MedicalCare.Best, new Obs.PawnConditions { LifeThreatening = false, Hediffs = [] }) },
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

        var prisoner = Assert.Single(census.Prisoners);
        Assert.Equal(new PawnId("prisoner"), prisoner.Pawn);
        Assert.Equal(12.5, prisoner.Resistance);
        Assert.Equal(-40, prisoner.HarvestGoodwill);
        Assert.Equal(new PrisonerHeldInteraction.Settable(PrisonerInteractionMode.ReduceResistance), prisoner.CurrentInteraction);
        Assert.Equal(new PawnId("guest"), Assert.Single(census.Guests).Id);
        Assert.Equal(new Obs.ColonyIdeology.Installed(false, "Ideo_1"), census.Colony.Ideology);
        Assert.True(census.Colony.PreceptsBind);
        Assert.Equal("Ideo_1", census.Colony.PrimaryIdeo);
        Assert.Equal(1, census.Colony.Colonists);
        Assert.Equal(3, census.Custody.Count);
        Assert.Equal([new Obs.OwnedName { PawnId = new("colonist"), ShortName = "Tess", ThingId = 7 }], census.Names);
    }

    [Theory]
    [InlineData("AttemptRecruit", PrisonerInteractionMode.Recruit)]
    [InlineData("MaintainOnly", PrisonerInteractionMode.Maintain)]
    [InlineData("ReduceResistance", PrisonerInteractionMode.ReduceResistance)]
    [InlineData("Release", PrisonerInteractionMode.Release)]
    [InlineData("Enslave", PrisonerInteractionMode.Enslave)]
    [InlineData("Convert", PrisonerInteractionMode.Convert)]
    public void AnInteractionTheIntentExposesIsSettable(string def, PrisonerInteractionMode mode)
    {
        Assert.Equal(new PrisonerHeldInteraction.Settable(mode), PrisonerCensusRules.PrisonerInteraction(def));
    }

    [Fact]
    public void AnyOtherInteractionIsNamedByItsDef()
    {
        Assert.Equal(new PrisonerHeldInteraction.Other(), PrisonerCensusRules.PrisonerInteraction("Execution"));
    }

    [Fact]
    public void APersonOutsideThePawnTableIsAContractFailure()
    {
        var pawns = Pawns.Of(PawnFixtures.Row("colonist"));

        Assert.Throws<BridgeContractException>(() => PopulationDecoder.Decode(Population, pawns, null));
    }
}
