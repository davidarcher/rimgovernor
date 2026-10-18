using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The open ground the tiler's exterior doors open onto. A live replan sited the bedrooms on the far bank of a
/// river (no_go water, walkable but no core) and failed every hour with TileClosedException: only the largest free
/// core area was open ground, so no packed room on the smaller bank got a door. Ground a pawn walks across joins
/// the banks; a pocket the rooms close in, and a bank past deep water, stay closed.
/// </summary>
public sealed class TileOpenGroundTests
{
    private const int W = 80;
    private const int H = 40;
    private const int River = 50;

    /// <summary>A firm open map split by a one-cell river column: soft walkable water, or deep water no pawn crosses.</summary>
    private static MapSurvey Survey(bool deep) => new()
    {
        Bounds = new Bounds(W, H),
        Cells = [.. Enumerable.Range(0, W).SelectMany(x => Enumerable.Range(0, H).Select(z => x == River
            ? new SurveyCell { Cell = new Cell(x, z), Walkable = !deep, Footing = Footing.None }
            : new SurveyCell { Cell = new Cell(x, z), Walkable = true, Footing = Footing.Firm }))],
    };

    /// <summary>A tiler holding two bedrooms packed wall to wall on the small (east) bank, as JoinSealed sees them.</summary>
    private static (Tiler T, PlanScorer Scorer) PackedOnFarBank(bool deep)
    {
        var s = Survey(deep);
        var zones = LayoutZoningRules.Zone(s);
        Rectangle[] rects = [new(56, 15, 2, 3), new(59, 15, 2, 3)];
        var t = new Tiler(new TileInput
        {
            Groups = [new RoomGroup { Id = 0, Role = RoomRole.Bedroom }, new RoomGroup { Id = 1, Role = RoomRole.Bedroom }],
            Zones = zones,
            Survey = s,
        });
        var rooms = new List<PlannedRoom>();
        for (var i = 0; i < rects.Length; i++)
        {
            t.Rooms.Add(new TileRoom(i, rects[i]));
            rooms.Add(new PlannedRoom { Id = i + 1, Role = PlannedRole.Bedroom, Region = RegionRules.RectRegion(rects[i]) });
        }
        t.Plan = new LayoutPlan { Zones = zones, Rooms = rooms };
        t.Parent = [0, 1];
        t.Adj[(0, 1)] = LayoutHerdRules.SharedWallRun(LayoutPerimeterRules.Pad(rects[0], 1), LayoutPerimeterRules.Pad(rects[1], 1));
        return (t, new PlanScorer(zones, [], s));
    }

    [Fact]
    public void PackedRoomsAcrossAWalkableRiverGetAnExteriorDoorOntoTheirOwnBank()
    {
        var (t, scorer) = PackedOnFarBank(deep: false);

        t.ChooseExteriors(scorer);
        t.JoinSealed();

        Assert.All(t.Reach(), Assert.True);
        Assert.Contains(t.ExtDoor, d => d is not null);
        Assert.All(t.ExtDoor.OfType<Cell>(), d => Assert.True(d.X > River));
    }

    [Fact]
    public void PackedRoomsPastDeepWaterStayClosed()
    {
        var (t, scorer) = PackedOnFarBank(deep: true);

        t.ChooseExteriors(scorer);

        Assert.Throws<TileClosedException>(t.JoinSealed);
    }

    [Fact]
    public void APocketTheRoomsCloseInIsNoOpenGround()
    {
        var s = Survey(deep: false);
        var scorer = new PlanScorer(LayoutZoningRules.Zone(s), [], s);
        var ring = PlannedRules.RectCells(new Rectangle(20, 15, 5, 5)).Where(c => c.X is 20 or 24 || c.Z is 15 or 19).ToHashSet();

        var open = LayoutTileRules.TileOpenGround(scorer, ring);

        Assert.DoesNotContain(new Cell(22, 17), open);
        Assert.Contains(new Cell(30, 17), open);
        Assert.Contains(new Cell(60, 17), open);
    }
}
