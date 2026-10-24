using System.Text.Json;
using RimGovernor.Contracts.Wire;
using Xunit;
using Identity = RimGovernor.Contracts.Wire.Common.Identity;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// <c>WireJsonContext</c> writes every member, zeros included, and refuses JSON that lacks a <c>required</c> member
/// (#3762): map zero is a real identity, and a reply that leaves a required fact out fails to decode.
/// </summary>
public sealed class WireJsonMembersTests
{
    [Fact]
    public void ZeroIsWritten()
    {
        var json = WireJson.Serialize(new Identity { ColonyId = "colony", LoadToken = "load", MapId = 0 });

        Assert.Equal("""{"colonyId":"colony","loadToken":"load","mapId":0}""", json);
    }

    [Fact]
    public void MissingRequiredThrows() =>
        Assert.Throws<JsonException>(() => WireJson.Deserialize<Identity>("""{"colonyId":"colony","loadToken":"load"}"""));
}
