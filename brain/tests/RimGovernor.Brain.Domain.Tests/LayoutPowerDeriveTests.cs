using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public class LayoutPowerDeriveTests
{
    private static StationFacts Facts(params LampDefinition[] lamps) => new()
    {
        FinishedResearch = new HashSet<string>(StringComparer.Ordinal) { "Fire" },
        Catalog = new StationCatalog { PlayBuildings = [], Light = new LightModel { Lamps = lamps } },
    };

    private static readonly RoomGroup Room = new() { Id = 0, Area = 20 };

    [Fact]
    public void UnreadLampsAreARequiredFactAndAnUnresearchedLampDrawsNothing()
    {
        Assert.Equal("lamp", Assert.Throws<RequiredFactException>(() => Facts().PlannedDemandW([Room])).Fact);

        // A tribal start: the standing lamp waits on Electricity and the torch burns wood. The rooms draw no watts (#3538).
        var electric = new LampDefinition { Name = "StandingLamp", Powered = true, Model = new LampModel(75, 30, 12, 217), Research = ["Electricity"] };
        var torch = new LampDefinition { Name = "TorchLamp", Refuelable = true, Model = new LampModel(0, 10, 10, 160), Research = ["Fire"] };
        Assert.Equal(0, Facts(electric, torch).PlannedDemandW([Room]));

        // Electricity finished: the standing lamp lights the room.
        var lit = Facts(electric, torch) with { FinishedResearch = new HashSet<string>(StringComparer.Ordinal) { "Fire", "Electricity" } };
        Assert.True(lit.PlannedDemandW([Room]) >= 75);
    }
}
