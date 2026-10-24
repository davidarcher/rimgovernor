using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Contracts.Wire.Common;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using WireRoomDoor = RimGovernor.Contracts.Wire.Observations.RoomDoor;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class CombatFrameTemperatureTests
{
    private static readonly ObservationContext Context = new() { Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 100 };

    // Every native frame carries its rooms census (the capture fails the frame whole without it).
    private static FrameView View(ColonyFactsSnapshot? colony, RoomsSnapshot? rooms) =>
        new(1, new NativeFrame(new BundleSnapshot { Context = Context, ColonyFacts = colony, Rooms = rooms }, 0, 0));

    private static FrameView View(ColonyFactsSnapshot? colony) => View(colony, new RoomsSnapshot { Context = Context, Rooms = [] });

    [Fact]
    public void TheCombatStateTakesTheFramesOutdoorTemperatureAndTheCutFrameCarriesNoColonyFacts()
    {
        var combat = FacadeNativeClient.DecodeCombat(View(ColonyFactsCoreTests.Snapshot() with { OutdoorTemperatureC = -31.5, ColonistCount = 4 }));
        Assert.Equal(new RimGovernor.Brain.Policy.OutdoorTemperature.Measured(-31.5), combat.OutdoorTemperature);
        // No partial colony facts whose other fields would read as zero.
        Assert.Null(combat.Frame!.ColonyFacts);
    }

    [Fact]
    public void AFrameWithoutColonyFactsNamesTheMissingColonyFactsNotZero()
    {
        var combat = FacadeNativeClient.DecodeCombat(View(null));
        Assert.IsType<RimGovernor.Brain.Policy.OutdoorTemperature.NoColonyFacts>(combat.OutdoorTemperature);
    }

    [Fact]
    public void TheDoorStatesAreTheRoomsCensusDoorsAndAFrameWithoutRoomsBreaksTheContract()
    {
        var door = new WireRoomDoor { Cell = new Cell { X = 4, Z = 5 }, Outside = new Cell { X = 4, Z = 6 }, Outdoors = true, PlayerOwned = true, Open = false, HoldOpen = true, BlockedOpen = false, Forbidden = false };
        var room = RoomFixtures.State("a", gridRoom: 1) with { Doors = [door] };
        var combat = FacadeNativeClient.DecodeCombat(View(null, new RoomsSnapshot { Context = Context, Rooms = [room] }));
        Assert.Equal(new[] { door }, combat.DoorStates.ToArray());

        Assert.Throws<BridgeContractException>(() => FacadeNativeClient.DecodeCombat(View(null, null)));
    }
}
