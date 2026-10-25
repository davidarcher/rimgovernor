using RimGovernor.Brain.GameFacts.Host;
using RimGovernor.Brain.GameOrders.Host;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// <c>set_time_speed</c>'s arguments carry the speed name alone (#3605): the name decides the dev tick boost
/// (Ultrafast off, Hyperfast on), so no request sends <c>ultraSpeedBoost</c>.
/// </summary>
public sealed class HostToolsJsonTests
{
    [Theory]
    [InlineData("Paused")]
    [InlineData("Ultrafast")]
    [InlineData("Hyperfast")]
    public void TimeSpeedRequestCarriesOnlyTheSpeed(string speed)
    {
        var json = HostJson.Arguments(new TimeSpeedRequest { Speed = speed });

        Assert.Equal(speed, (string?)json["speed"]);
        Assert.Single(json);
    }

    [Fact]
    public void PausedRequestHasNoBoost()
    {
        var json = HostJson.Arguments(TimeSpeedRequest.Paused);

        Assert.Equal("Paused", (string?)json["speed"]);
        Assert.False(json.ContainsKey("ultraSpeedBoost"));
    }

    [Fact]
    public void LoadArgumentsRetainTheRequestedReadinessAndModCompatibilityDecision()
    {
        var json = HostJson.Arguments(new LoadGameRequest
        {
            SaveName = "colony-a", Readiness = "playable", TimeoutMs = 12345, IgnoreModCompatibility = true,
        });

        Assert.Equal("colony-a", (string?)json["saveName"]);
        Assert.Equal("playable", (string?)json["readiness"]);
        Assert.Equal(12345L, (long?)json["timeoutMs"]);
        Assert.True((bool?)json["ignoreModCompatibility"]);
        Assert.Equal(4, json.Count);
        Assert.Equal("stage-a", (string?)HostJson.Arguments(new SaveGameRequest { SaveName = "stage-a" })["saveName"]);
    }
}
