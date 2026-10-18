using System.Text.Json.Nodes;
using RimGovernor.Brain.Acceptance;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// #3555: a result.json world block read back with absent or null members (a served fixture case records only what it
/// knows) writes the repeat summary with those members omitted instead of throwing.
/// </summary>
public sealed class WorldRecordTests
{
    [Theory]
    [InlineData("""{"pinned":false}""")]
    [InlineData("""{"seed":null,"save":null,"save_sha256":null,"fixture":null,"fixture_hash":null}""")]
    public void AbsentMembersOfAReadBackBlockAreOmitted(string block)
    {
        var record = LaunchJson.World(JsonNode.Parse(block)!.AsObject());

        Assert.Empty(record.ToJson());
    }

    [Fact]
    public void MembersAReadBackBlockCarriesAreKept()
    {
        var record = LaunchJson.World(JsonNode.Parse("""{"fixture":"shelter","fixture_hash":"abc","seed":null}""")!.AsObject());

        var json = record.ToJson();

        Assert.Equal("shelter", (string?)json["fixture"]);
        Assert.Equal("abc", (string?)json["fixture_hash"]);
        Assert.False(json.ContainsKey("seed"));
    }
}
