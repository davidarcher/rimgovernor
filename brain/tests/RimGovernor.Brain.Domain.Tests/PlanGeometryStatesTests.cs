using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store.Codec;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// Plan geometry is read whole (#3914): a planned room's door is Doored on a side or Doorless (an unfenced room), a
/// training range's dummies and a site's facing are Toward a side or Unturned, and a site node's origin is Fixed on a
/// cell or Free. The store persists each state and reads it back.
/// </summary>
public sealed class PlanGeometryStatesTests
{
    private static PlannedRoom Storage(DoorSide side) => new()
    {
        Id = 1,
        Role = PlannedRole.Storage,
        Region = RegionRules.RectRegion(new Rectangle(10, 10, 3, 3)),
        Door = new Cell(11, 9),
        DoorSide = side,
    };

    [Fact]
    public void AFencedRoomNeedsADoorSideAndADoorlessRoomHasNoShell()
    {
        var doored = Storage(new DoorSide.Doored(Rotation.South));
        var doorless = Storage(new DoorSide.Doorless());

        Assert.True(new LayoutPlan { Rooms = [doored], NextRoomId = 2 }.Valid());
        Assert.False(new LayoutPlan { Rooms = [doorless], NextRoomId = 2 }.Valid());
        Assert.Equal(new Cell(11, 9), doored.Footprint().Door);
        Assert.Throws<DomainException>(() => doorless.Footprint());
        Assert.IsType<DoorSide.Doorless>(new PlannedRoom().DoorSide);
    }

    [Fact]
    public void ARangeLaysItsStandsTowardItsFacingAndAnUnturnedRoomHasNone()
    {
        var range = PlannedRoom.RangeFromBlock(new Rectangle(0, 0, 4, TrainingRules.RangeDistance + 3), Rotation.North, 2);

        Assert.Equal(new Heading.Toward(Rotation.North), range.Facing);
        Assert.Equal(2, PlannedRoom.RangeStandCount(range));
        Assert.Equal(4, TrainingRules.RangeLayout(range).Count);
        Assert.Equal(0, PlannedRoom.RangeStandCount(range with { Facing = new Heading.Unturned() }));
        Assert.Empty(TrainingRules.RangeLayout(range with { Facing = new Heading.Unturned() }));
    }

    [Fact]
    public void AGeothermalNodeIsFixedOnItsVentAndATurbineIsFreeInEveryTurn()
    {
        var plan = new LayoutPlan
        {
            Reservations = [new LayoutReservation { Kind = LayoutPlanRules.ReserveGeothermal, Area = RegionRules.RectRegion(new Rectangle(20, 30, 6, 6)) }],
        };
        var geothermal = Assert.Single(LayoutPlan.PowerNodesOf(plan));
        Assert.Equal(new SiteAnchor.Fixed(new Cell(20, 30)), geothermal.Anchor);
        Assert.IsType<Heading.Unturned>(geothermal.Rotation);

        var turbine = LayoutPowerNodesRules.TurbineNode("wind-1");
        Assert.IsType<SiteAnchor.Free>(turbine.Anchor);
        Assert.Equal(
            [Rotation.North, Rotation.South, Rotation.East, Rotation.West],
            new[] { turbine }.Concat(turbine.Alts).Select(s => Assert.IsType<Heading.Toward>(s.Rotation).Side));
    }

    [Fact]
    public void AnUnturnedTurbineSiteIsNoPowerSite()
    {
        var site = new PlannedSite
        {
            Node = "wind-1",
            Kind = LayoutPowerNodesRules.NodeTurbine,
            Footprint = RegionRules.RectRegion(new Rectangle(0, 0, LayoutPowerNodesRules.WindPathWidth, 2)),
        };

        Assert.Single(LayoutPlan.PlannedPowerSites(new LayoutPlan { Sites = [site with { Rotation = new Heading.Toward(Rotation.North) }] }, PowerRules.WindTurbineDefinition));
        Assert.Empty(LayoutPlan.PlannedPowerSites(new LayoutPlan { Sites = [site] }, PowerRules.WindTurbineDefinition));
    }

    [Fact]
    public void TheStoreRoundTripsEveryGeometryState()
    {
        foreach (var room in new[]
        {
            Storage(new DoorSide.Doored(Rotation.East)),
            Storage(new DoorSide.Doorless()) with { Role = PlannedRole.TrainingRange, Facing = new Heading.Toward(Rotation.West) },
            Storage(new DoorSide.Doorless()),
        })
        {
            var back = StoreJson.Deserialize<PlannedRoom>(StoreJson.Serialize(room));
            Assert.True(room.Same(back));
            Assert.Equal(room.Facing, back.Facing);
        }
        foreach (var heading in new Heading[] { new Heading.Toward(Rotation.South), new Heading.Unturned() })
        {
            var site = new PlannedSite { Node = "vent_1_2", Origin = new Cell(1, 2), Kind = NodeKind.Vent, Rotation = heading };
            Assert.Equal(heading, StoreJson.Deserialize<PlannedSite>(StoreJson.Serialize(site)).Rotation);
        }
    }
}
