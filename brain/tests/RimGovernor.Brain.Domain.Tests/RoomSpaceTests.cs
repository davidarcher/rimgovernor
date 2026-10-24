using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The game's Space and Impressiveness of an empty rectangular room (#3748). The values are derived by hand from the
/// formulas transcribed from RimWorld 1.7 (RoomStatWorker_Space, RoomStatWorker_Impressiveness), not read in a game:
/// Space is 1.4 a cell; with Beauty, Cleanliness and Wealth zero the factors are 0, 0, Space / 125 and 1, so the score is
/// 100 x 0.65 x (Space / 125 + 1) / 4, below the space ceiling of 500 x Space / 125.
/// </summary>
public sealed class RoomSpaceTests
{
    [Theory]
    [InlineData(4, 5.6, 16.978)]
    [InlineData(9, 12.6, 17.888)]
    [InlineData(16, 22.4, 19.162)]
    [InlineData(25, 35.0, 20.8)]
    public void AnEmptyRoomsSpaceAndImpressivenessMatchTheGamesFormulas(int cells, double space, double impressiveness)
    {
        Assert.Equal(space, RoomSpaceRules.Space(cells), 9);
        Assert.Equal(impressiveness, RoomSpaceRules.BareImpressiveness(cells), 3);
    }

    [Fact]
    public void TheBareRoomTurnsDullAtTwentyOneCells()
    {
        var levels = new ImpressivenessLevels { Dull = 20, Mediocre = 30, Decent = 40, SlightlyImpressive = 50 };

        int[] cells = [4, 9, 16, 20, 21];
        Assert.Equal([0, 0, 0, 0, 1], cells.Select(c => levels.Stage(RoomSpaceRules.BareImpressiveness(c))));
    }

    [Fact]
    public void TheSpaceCeilingPullsAWealthyCrampedRoomDown()
    {
        // A tiny room of great wealth, beauty and cleanliness: the mean of its factors passes five times the space factor
        // and is pulled three quarters of the way to it.
        var s = 1.4 / 125;
        var w = 1 + Math.Log(15000 / 1500.0);
        var b = 1 + Math.Log(30 / 3.0);
        var n = 0.65 * ((w + b + s + 1) / 4) + (0.35 * s);
        var expected = (n + (0.75 * ((5 * s) - n))) * 100;

        Assert.Equal(expected, RoomSpaceRules.Impressiveness(1.4, 30, 0, 15000), 9);
    }
}
