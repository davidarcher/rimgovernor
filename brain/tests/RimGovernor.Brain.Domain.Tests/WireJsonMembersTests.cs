using System.Text.Json;
using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;
using Xunit;
using Identity = RimGovernor.Brain.GameFacts.Common.Identity;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// <c>OrdersJsonContext</c> writes every member, zeros included, and refuses JSON that lacks a <c>required</c> member
/// (#3762): map zero is a real identity, and a reply that leaves a required fact out fails to decode.
/// </summary>
public sealed class OrdersJsonMembersTests
{
    [Fact]
    public void ZeroIsWritten()
    {
        var json = OrdersJson.Serialize(new Identity { ColonyId = "colony", LoadToken = "load", MapId = 0 });

        Assert.Equal("""{"colonyId":"colony","loadToken":"load","mapId":0}""", json);
    }

    [Fact]
    public void MissingRequiredThrows() =>
        Assert.Throws<JsonException>(() => OrdersJson.Deserialize<Identity>("""{"colonyId":"colony","loadToken":"load"}"""));
}
