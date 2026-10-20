using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// A lone role-less group (#3659): a group whose stations state no room role, such as a crafting spot alone, is planned
/// as a Workshop though the game scores it a generic room. The tiler places it, its standing room is matched back to
/// it, and the role guard finds nothing to flag, so no replan or repair follows.
/// </summary>
public sealed class RolelessWorkshopTests
{
    private const string Spot = "CraftingSpot";

    private static readonly StationCatalog Catalog = new()
    {
        Stations = new Dictionary<string, Station>
        {
            [Spot] = new Station { Shape = new InteriorPieceDef(Spot, new Cell(1, 1), RoomRole.None, null) },
        },
    };

    [Fact]
    public void ALoneRolelessGroupIsMatchedToTheStandingWorkshopItWasPlannedAs()
    {
        var group = new RoomGroup { Id = 0, Role = RoomRole.Room, Planned = PlannedRole.Workshop, Area = 9 };
        // A planned workshop stands as a built region of its native role (ModuleRoomRoles).
        var built = new BuiltRegion { Room = 4, Role = RoomRole.Workshop, Region = RegionRules.RectRegion(new Rectangle(0, 0, 3, 3)) };

        var matched = Assert.Single(LayoutStationPlanRules.MatchGroupRooms([group], [built]));
        Assert.Equal([4], matched.Rooms);
    }

    [Fact]
    public void TheGuardFindsNoMismatchInAStandingSpotWorkshopTheGameScoresAGenericRoom()
    {
        var region = RegionRules.RectRegion(new Rectangle(0, 0, 3, 3));
        var plan = new LayoutPlan { Rooms = [new PlannedRoom { Id = 1, Role = PlannedRole.Workshop, Region = region }] };
        var rooms = new RoomObservation
        {
            Rooms = [new Room { Id = "r1", Role = Known(RoomRole.Room), Enclosed = Known(true), Cells = EquatableArray.From(region.Cells()) }],
        };
        var contents = new[] { new RoomContents { Room = 1, Pieces = [new RoomPiece { Def = Spot }] } };

        Assert.Empty(RoomRoleRules.GuardNativeRoles(Catalog, plan, rooms, contents));
    }
}
