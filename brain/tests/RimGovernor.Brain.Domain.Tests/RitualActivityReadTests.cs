using System.Text.Json;
using System.Text.Json.Nodes;
using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameFacts.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

// #4057: the ritual activity read is a plain required record, like the other coupled sub-reads; the native throws
// instead of answering an unknown, so a site missing a member does not decode.
public sealed class RitualActivityReadTests
{
    internal static RitualActivityRead Read => new()
    {
        Sites =
        [
            new("Precept_3", "Gathering", "RitualSpot1", new(4, 5))
            {
                Usable = true, Refusal = "",
                Candidates =
                [
                    new("pawn-a") { Roles = [new("leader", true, "")], Spectate = new("", true, "") },
                    new("pawn-b") { Roles = [new("leader", false, "Busy")], Spectate = new("", false, "Downed") },
                ],
            },
            new("Precept_3", "Gathering", "RitualSpot2", new(9, 1)) { Usable = false, Refusal = "Another ritual is active here.", Candidates = [] },
        ],
    };

    [Fact]
    public void TheReadRoundTripsAndRefusesAMissingMember()
    {
        var read = Read;
        var json = JsonSerializer.Serialize(read, FactsJsonContext.Default.RitualActivityRead);
        Assert.Equal(read, JsonSerializer.Deserialize(json, FactsJsonContext.Default.RitualActivityRead));
        var absent = JsonNode.Parse(json)!.AsObject();
        absent["sites"]![0]!.AsObject().Remove("usable");
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(absent.ToJsonString(), FactsJsonContext.Default.RitualActivityRead));
    }

    [Fact]
    public void TheCoupledFixtureCarriesNoRitualSites()
    {
        Assert.Empty(CoupledFixtures.Empty.Rituals.Sites);
    }
}
