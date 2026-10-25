using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Runtime;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The population read is whole (#3769): every person carries every fact, how the colony holds it is a named custody
/// state, and the colony's ideology mode is named. It round-trips through <c>OrdersJsonContext</c> exactly and decodes
/// into the prisoner census without an unknown.
/// </summary>
public sealed class PopulationWireTests
{

    private static RimGovernor.Brain.GameFacts.Observations.PopulationPerson Person(string id) => new()
    {
        Pawn = new PawnId(id),
        Admitted = false,
        Guest = false,
        Recruitable = false,
        IdeoId = null,
        WildMan = false,
        Custody = new RimGovernor.Brain.GameFacts.Observations.PersonCustody.None(),
        WillJoinIfRescued = false,
    };

    private static readonly RimGovernor.Brain.GameFacts.Observations.PopulationSnapshot Population = new()
    {
        Persons =
        [
            Person("colonist") with { Admitted = true, IdeoId = "Ideo_1" },
            Person("prisoner") with
            {
                Guest = true,
                Recruitable = true,
                Custody = new RimGovernor.Brain.GameFacts.Observations.PersonCustody.Prisoner(12.5, "ReduceResistance", 2500, new RimGovernor.Brain.GameFacts.Common.Ref { Id = "Faction_3" }, -40),
            },
            Person("guest") with { Guest = true, Custody = new RimGovernor.Brain.GameFacts.Observations.PersonCustody.Guest() },
        ],
        Ideology = new RimGovernor.Brain.GameFacts.Observations.ColonyIdeology.Installed(false, "Ideo_1"),
        OwnedNames = [new RimGovernor.Brain.GameFacts.Observations.OwnedName { PawnId = new PawnId("colonist"), ShortName = "Tess", ThingId = 7 }],
    };

    [Fact]
    public void EveryCustodyStateAndTheIdeologyModeRoundTrip()
    {
        var json = OrdersJson.Serialize(Population);

        Assert.Contains("\"custody\":{\"type\":\"none\"}", json, StringComparison.Ordinal);
        Assert.Contains("\"custody\":{\"type\":\"prisoner\",\"resistance\":12.5,\"interaction\":\"ReduceResistance\",\"heldTicks\":2500", json, StringComparison.Ordinal);
        Assert.Contains("\"custody\":{\"type\":\"guest\"", json, StringComparison.Ordinal);
        Assert.Contains("\"ideology\":{\"type\":\"installed\",\"classic\":false,\"primaryIdeoId\":\"Ideo_1\"}", json, StringComparison.Ordinal);
        Assert.Contains("\"ownedNames\":[{\"pawnId\":\"colonist\",\"shortName\":\"Tess\",\"thingId\":7}]", json, StringComparison.Ordinal);
        Assert.Equal(Population, OrdersJson.Deserialize<RimGovernor.Brain.GameFacts.Observations.PopulationSnapshot>(json));
        var bare = Population with { Ideology = new RimGovernor.Brain.GameFacts.Observations.ColonyIdeology.NotInstalled() };
        Assert.Equal(bare, OrdersJson.Deserialize<RimGovernor.Brain.GameFacts.Observations.PopulationSnapshot>(OrdersJson.Serialize(bare)));
    }

    [Fact]
    public void TheCensusReadsEachCustodyStateKnown()
    {
        var native = PopulationFixtures.Held("prisoner").Observed;
        var guest = PawnFixtures.Row("guest");
        guest = guest with
        {
            Colonist = false,
            FreeColonist = false,
            Health = guest.Health with { LifeThreatening = true },
            Settings = guest.Settings with { Care = new RimGovernor.Brain.GameFacts.Observations.PawnCare.Configured(RimGovernor.Brain.GameFacts.Operations.MedicalCare.Best, false, RimGovernor.Brain.GameFacts.Operations.HostilityResponse.Flee) },
        };
        var pawns = Pawns.Of(PawnFixtures.Row("colonist"), native, guest);

        var census = PopulationDecoder.Decode(Population, pawns, null);

        var prisoner = Assert.Single(census.Prisoners);
        Assert.Equal(new PawnId("prisoner"), prisoner.Pawn);
        Assert.Equal(12.5, prisoner.Resistance);
        Assert.Equal(-40, prisoner.HarvestGoodwill);
        Assert.Equal(new PrisonerHeldInteraction.Settable(PrisonerInteractionMode.ReduceResistance), prisoner.CurrentInteraction);
        Assert.Equal(new PawnId("guest"), Assert.Single(census.Guests).Id);
        Assert.Equal(new RimGovernor.Brain.GameFacts.Observations.ColonyIdeology.Installed(false, "Ideo_1"), census.Colony.Ideology);
        Assert.True(census.Colony.PreceptsBind);
        Assert.Equal("Ideo_1", census.Colony.PrimaryIdeo);
        Assert.Equal(1, census.Colony.Colonists);
        Assert.Equal(3, census.Custody.Count);
        Assert.Same(Population.Persons[1], prisoner.Person);
        Assert.Same(Population.Persons[1].Custody, prisoner.Held);
        Assert.True(pawns.TryGetValue("prisoner", out var nativePrisoner));
        Assert.Same(nativePrisoner, prisoner.Observed);
        Assert.Same(prisoner.Observed, prisoner.Prospect.Observed);
        Assert.Same(Population.Persons[2], Assert.Single(census.Guests).Person);
        Assert.Same(Population.Persons[2].Custody, Assert.Single(census.Guests).Person.Custody);
        Assert.Equal(MedicalCare.Herbal, prisoner.MedicalCare);
        Assert.Same(guest, Assert.Single(census.Guests).Observed);
        Assert.Equal(MedicalCare.Best, Assert.Single(census.Guests).Care);
        Assert.True(Assert.Single(census.Guests).LifeThreatening);
        Assert.Equal([new RimGovernor.Brain.GameFacts.Observations.OwnedName { PawnId = new("colonist"), ShortName = "Tess", ThingId = 7 }], census.Names);
    }

    [Fact]
    public void RecruitmentUsesTheJoinedPawnSkillAndHealthWithoutCopies()
    {
        var pawn = PawnFixtures.Row("p");
        var skill = new RimGovernor.Brain.GameFacts.Observations.Skill { DefName = "Shooting", Level = 12, Passion = RimGovernor.Brain.GameFacts.Observations.Passion.Major };
        pawn = pawn with
        {
            Biography = pawn.Biography with { BiologicalAgeYears = 30, Skills = [skill] },
            Health = pawn.Health with { SummaryFraction = 0.3 },
        };
        var population = Population with { Persons = [Person("p") with { Admitted = true }] };
        var census = PopulationDecoder.Decode(population, Pawns.Of(pawn), null);
        var prospect = Assert.Single(census.Custody).Prospect;
        Assert.Same(skill, Assert.Single(prospect.Skills));
        Assert.Same(pawn, prospect.Observed);
        Assert.Equal(0.3, prospect.Health);
        Assert.Equal(12, census.Colony.BestSkill["Shooting"]);
        Assert.Equal(2, PopulationRules.PrisonerWorth(prospect, 0, census.Colony));
    }

    [Fact]
    public void TraitWorthRequiresTheCatalogInsteadOfInventingZero()
    {
        var pawn = PawnFixtures.Row("p");
        pawn = pawn with { Biography = pawn.Biography with { Traits = [new RimGovernor.Brain.GameFacts.Observations.Trait { DefName = "Nervous", Degree = 1 }] } };
        Assert.Throws<BridgeContractException>(() => PopulationDecoder.Decode(
            Population with { Persons = [Person("p")] }, Pawns.Of(pawn), null));
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
