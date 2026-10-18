using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class PlannedRoomAlternativesTests
{
    private static PlannedBuildSite Site(string id, OpKind op, string stuff, int cells, int room = 1) =>
        new("planned/room" + room + "/" + id, room, op, op == OpKind.FloorIn ? "WoodPlankFloor" : "Def", stuff, Rotation.North,
            EquatableArray.From(Enumerable.Range(0, cells).Select(i => new Cell(i, 0))));

    /// <summary>A room whose ring reads in Steel then WoodLog, with a stuff-less floor, a bench and a roof.</summary>
    private static IReadOnlyList<PlannedBuildSite> Room() =>
    [
        Site("wall/Steel", OpKind.WallIn, "Steel", 12), Site("wall/WoodLog", OpKind.WallIn, "WoodLog", 12),
        Site("door/Steel", OpKind.DoorIn, "Steel", 1), Site("door/WoodLog", OpKind.DoorIn, "WoodLog", 1),
        Site("floor/WoodPlankFloor", OpKind.FloorIn, "", 9), Site("build/TableButcher/1/1", OpKind.Build, "WoodLog", 1),
        Site("roof", OpKind.RoofIn, "", 9),
    ];

    [Fact]
    public void EveryScopeOfTheFirstStuffThenEachFurtherStuffAtFullAndSitesAreShared()
    {
        var (alternatives, findings) = PlannedRules.PlannedRoomAlternatives(Room());
        Assert.Empty(findings);
        Assert.Equal(["planned/room1/Steel/Shell", "planned/room1/Steel/ShellFloor", "planned/room1/Steel/Full", "planned/room1/WoodLog/Full"],
            alternatives.Select(a => a.Id));
        Assert.Equal(["planned/room1/door/Steel", "planned/room1/roof", "planned/room1/wall/Steel"], alternatives[0].Sites);
        Assert.Contains("planned/room1/floor/WoodPlankFloor", alternatives[1].Sites);
        Assert.DoesNotContain("planned/room1/build/TableButcher/1/1", alternatives[1].Sites);
        // The stuff-less floor, the piece and the roof belong to both stuffs' Full alternatives; ring sites to their own stuff.
        var wood = alternatives[3];
        Assert.Equal(["planned/room1/build/TableButcher/1/1", "planned/room1/door/WoodLog", "planned/room1/floor/WoodPlankFloor", "planned/room1/roof",
            "planned/room1/wall/WoodLog"], wood.Sites);
        Assert.Equal("WoodLog", wood.Stuff);
    }

    [Fact]
    public void ScopesThatOweTheSameSitesCollapseAndShelfCandidatesAreNoRoom()
    {
        var (alternatives, _) = PlannedRules.PlannedRoomAlternatives(
            [Site("wall", OpKind.WallIn, "", 12), new PlannedBuildSite("shelf/0/0", 0, OpKind.Build, "Shelf", "WoodLog", Rotation.North, [new Cell(0, 0)])]);
        Assert.Equal(["planned/room1/Shell"], alternatives.Select(a => a.Id));
        Assert.Empty(PlannedRules.PlannedRoomAlternatives([]).Alternatives);
    }

    [Fact]
    public void AlternativesPastTheCapAreNamed()
    {
        var sites = Room().Concat([Site("wall/Granite", OpKind.WallIn, "BlocksGranite", 12)]).ToList();
        var (alternatives, findings) = PlannedRules.PlannedRoomAlternatives(sites);
        Assert.Equal(PlannedRules.PlannedAlternativesPerRoom, alternatives.Count);
        Assert.Equal(["planned_room_alternatives_truncated"], findings);
    }

    [Fact]
    public void RoomWorkSumsTheFirstStuffFullSitesPerPieceAndNamesTheRoofApart()
    {
        var (work, roof) = PlannedRules.PlannedRoomWork(Room(), 1, s => Known(s.Op == OpKind.FloorIn ? 85.0 : s.Op == OpKind.Build ? 600.0 : 135.0));
        // 12 steel walls and a steel door at 135, nine floors at 85, one bench at 600; the roof is left out.
        Assert.Equal(Known(13 * 135.0 + 9 * 85.0 + 600.0), work);
        Assert.True(roof);
        Assert.False(PlannedRules.PlannedRoomWork(Room(), 1, s => s.Op == OpKind.Build ? Unknown<double>() : Known(1.0)).Work.IsKnown);
        Assert.Equal((Known(0.0), false), PlannedRules.PlannedRoomWork(Room(), 2, _ => Known(1.0)));
    }
}
