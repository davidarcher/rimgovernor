using RimGovernor.Contracts.Host;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// <c>rimgovernor/set_time_speed</c>'s arguments carry the speed name alone (#3605): the name decides the dev tick boost
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
}
