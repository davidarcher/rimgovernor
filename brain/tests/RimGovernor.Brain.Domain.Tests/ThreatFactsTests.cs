using System.Text.Json;
using System.Text.Json.Nodes;
using RimGovernor.Contracts.Wire;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

// #3764: the census's threat section is required and every ThreatFacts member is read (#3539 added the storyteller
// facts); a threat read the native cannot complete fails the census, so a section missing a member does not decode.
public sealed class ThreatFactsTests
{
    private static readonly ThreatFacts Raided = ColonyFactsCoreTests.QuietThreat with
    {
        WealthItems = 1200, WealthBuildings = 3400, WealthPawns = 900, WealthTotal = 5500, RaidPoints = 310.5,
        Storyteller = "Randy", DaysPassed = 12.5, HostileFactions = ["Pirate"],
        Raids =
        [
            new RaidTrack
            {
                LordId = "lord-1", SpawnTick = new Tick(60000), Spawn = new Cell(0, 40), Ground = true,
                Trail = [new Cell(0, 40), new Cell(3, 41)],
            },
        ],
    };

    [Fact]
    public void TheThreatSectionRoundTripsExactly()
    {
        var facts = ColonyFactsCoreTests.Snapshot() with { Threat = new ThreatSection { Observed = Raided } };
        var json = WireJson.Serialize(facts);
        Assert.Contains("\"trail\":[{\"x\":0,\"z\":40},{\"x\":3,\"z\":41}]", json);
        var back = WireJson.Deserialize<ColonyFactsSnapshot>(json);

        Assert.Equal(json, WireJson.Serialize(back));
        Assert.Equal(Raided, back.Threat.Observed);
    }

    [Theory]
    [InlineData("storyteller")]
    [InlineData("daysPassed")]
    [InlineData("raidPoints")]
    [InlineData("raids")]
    public void AThreatMissingAMemberDoesNotDecode(string member)
    {
        var facts = ColonyFactsCoreTests.Snapshot() with { Threat = new ThreatSection { Observed = Raided } };
        var node = JsonNode.Parse(WireJson.Serialize(facts))!.AsObject();
        Assert.True(node["threat"]!["observed"]!.AsObject().Remove(member));

        Assert.ThrowsAny<JsonException>(() => WireJson.Deserialize<ColonyFactsSnapshot>(node.ToJsonString()));
    }
}
