using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Runtime;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// RG-HABITAT-001 animals (task 9.2 gap <c>animal access:&lt;unit&gt;</c>): a planned barn's ring door is the animal flap in its
/// own stuff (Fabric or Leathery, from the flap's catalog row), so the barn is offered and credited from the native
/// roamerCanOpen read; a barn standing with a plain door is owed the flap over it, and the ring read carries the wall's stuff,
/// the door's def and the door's stuff.
/// </summary>
public sealed class BarnAnimalFlapTests
{
    private static readonly Resource Wood = new("WoodLog");
    private static readonly Resource Granite = new("BlocksGranite");
    private static readonly Resource Cloth = new("Cloth");
    private static readonly Resource Leather = new("Leather_Plain");

    private static PlannedRoom Barn() => new()
    {
        Id = 1, Role = PlannedRole.Barn, Herd = "Cow", Region = RegionRules.RectRegion(new Rectangle { X = 5, Z = 5, Width = 3, Height = 3 }),
        Door = new Cell(6, 8), DoorSide = new DoorSide.Doored(Rotation.North),
    };

    private static StuffOption Option(Resource stuff, double value, double work, long count) => new()
    {
        Stuff = stuff.Value, Value = value, Common = true, Costs = [new Amount(stuff, count)],
        Stats = new Dictionary<string, double> { [StatDefNames.StatWorkToBuild] = work },
    };

    /// <summary>The catalog rows of the game's wall (Woody, Stony) and animal flap (Fabric, Leathery; 25 stuff, 850 work).</summary>
    private static RimGovernor.Brain.GameAccess.ColonyReading Colony(params Resource[] stocked) => ColonyFactsCoreTests.Reading() with
    {
        Facts = ColonyFactsCoreTests.Reading().Facts with
        {
            Census = ColonyFactsCoreTests.Reading().Snapshot with
            {
                Resources = EquatableArray.From(stocked.Select(r => new RimGovernor.Brain.GameFacts.Observations.Quantity { DefName = r.Value, Units = 300 })),
            },
            Definitions =
            [
                new PlanningDefinition { Name = ContainmentCellRules.ShellWallDefinition, Available = true, StuffOptions = [Option(Granite, 4.5, 270, 5), Option(Wood, 6, 94.5, 5)] },
                new PlanningDefinition { Name = HerdShelterRules.BarnDoorDefinition, Available = true, StuffOptions = [Option(Cloth, 37.5, 850, 25), Option(Leather, 50, 850, 25)] },
            ],
            RingSources = new RingStuffSources { Wood = EquatableArray.From([Wood.Value]) },
        },
    };

    [Fact]
    public void ABarnsRingIsAWallAndAnAnimalFlapEachReadInItsOwnStuff()
    {
        Assert.Equal((ContainmentCellRules.ShellWallDefinition, HerdShelterRules.BarnDoorDefinition), Barn().RingDefs());
        Assert.Equal((ContainmentCellRules.ShellWallDefinition, ContainmentCellRules.ShellDoorDefinition), (Barn() with { Role = LayoutSurveyRules.PlannedWorkshop }).RingDefs());

        // Wood the map offers for the wall, cloth stocked for the flap: no shared stuff, and neither refuses the other.
        var colony = Colony(Cloth);
        Assert.True(ColonyReview.OwnStuffDoor(colony, ContainmentCellRules.ShellWallDefinition, HerdShelterRules.BarnDoorDefinition));
        Assert.Equal([Wood.Value], ColonyReview.PlannedRingStuffsOf(colony, ContainmentCellRules.ShellWallDefinition, HerdShelterRules.BarnDoorDefinition));
        Assert.Equal([Cloth.Value], ColonyReview.PlannedDoorStuffsOf(colony, ContainmentCellRules.ShellWallDefinition, HerdShelterRules.BarnDoorDefinition));
        var (wall, door, _, ok) = BuildingPlanner.ShellMaterials(colony, ContainmentCellRules.ShellWallDefinition, HerdShelterRules.BarnDoorDefinition, null);
        Assert.True(ok);
        Assert.Equal((Wood.Value, Cloth.Value), (wall, door));
        // No fabric or leather stocked or offered: the flap has no stuff, so the ring is not placed.
        Assert.Empty(ColonyReview.PlannedDoorStuffsOf(Colony(), ContainmentCellRules.ShellWallDefinition, HerdShelterRules.BarnDoorDefinition));
        Assert.False(BuildingPlanner.ShellMaterials(Colony(), ContainmentCellRules.ShellWallDefinition, HerdShelterRules.BarnDoorDefinition, null).Ok);
    }

    [Fact]
    public void ThePlannedBarnsSitesCarryTheWallStuffTheFlapDefAndTheFlapStuff()
    {
        var room = Barn();
        var plan = new LayoutPlan { Rooms = [room] };
        var owed = ReconcileRules.Reconcile(new ReconcileInput { Rooms = RoomTestFacts.NoRooms, Plan = plan, Room = room, Ground = new GroundCensus(), Rows = [] }).Owed;
        Assert.Equal(plan.ShellDoors(room), Assert.Single(owed, o => o.Kind == OpKind.DoorIn).Cells);
        (string, IReadOnlyList<Amount>)? Ring(string def, string stuff) =>
            Colony().Facts.Definitions.Single(d => d.Name == def).StuffOptions.FirstOrDefault(o => o.Stuff == stuff) is { } o ? (stuff, o.Costs) : null;
        IReadOnlyList<string> Stuffs(PlannedRoom r, string def) => def == r.RingDefs().Door ? [Cloth.Value, Leather.Value] : [Wood.Value, Granite.Value];
        var sites = PlannedRules.PlannedSitesOf([(room, owed)], Stuffs, Ring, _ => null, _ => null, "Vent", new HashSet<(string, Cell)>(), new Dictionary<Cell, string>());
        var door = Assert.Single(sites, s => s.Op == OpKind.DoorIn && s.Stuff == Cloth.Value);
        Assert.Equal(("planned/room1/door/Cloth", HerdShelterRules.BarnDoorDefinition), (door.Id, door.Definition));
        Assert.Equal(plan.ShellDoors(room), door.Positions);
        Assert.Equal([new Amount(Cloth, 25)], door.Material);
        Assert.Contains(sites, s => s.Op == OpKind.DoorIn && s.Stuff == Leather.Value);
        Assert.All(sites.Where(s => s.Op == OpKind.WallIn), s => Assert.Contains(s.Stuff, new[] { Wood.Value, Granite.Value }));
        Assert.DoesNotContain(sites, s => s.Op == OpKind.WallIn && s.Stuff == Cloth.Value);
    }

    [Fact]
    public void ABarnStandingWithAPlainDoorIsOwedTheFlapOverItAndIsNotCreditedMeanwhile()
    {
        var room = Barn();
        var plan = new LayoutPlan { Rooms = [room] };
        var doorCell = Assert.Single(plan.ShellDoors(room));
        // The whole ring stands, built through the construction census: walls, and a plain door at the barn's door cell.
        var buildings = room.WallRing().Cells().Where(c => c != doorCell)
            .Select(c => ConstructionObservationFixtures.Built(new Building(ContainmentCellRules.ShellWallDefinition, c, Rotation.North, Wood.Value), "wall" + c.X + "_" + c.Z))
            .Append(ConstructionObservationFixtures.Built(new Building(ContainmentCellRules.ShellDoorDefinition, doorCell, Rotation.North, Wood.Value), "door"))
            .ToList();
        var ground = LayoutGroundRules.GroundOf(buildings);
        Assert.Equal(ContainmentCellRules.ShellDoorDefinition, ground.DoorDefs[doorCell]);
        Assert.True(plan.GroundMatches(room, ground));

        var input = new ReconcileInput { Rooms = RoomTestFacts.NoRooms, Plan = plan, Room = room, Ground = ground, Rows = [] };
        var reconciled = ReconcileRules.Reconcile(input);
        // The replacement is the ring's door placed over the standing one, ready at once; no wall or door comes out.
        Assert.Equal([doorCell], Assert.Single(reconciled.Owed, o => o.Kind == OpKind.DoorIn).Cells);
        Assert.Equal([doorCell], Assert.Single(reconciled.Ready, o => o.Kind == OpKind.DoorIn).Cells);
        Assert.DoesNotContain(reconciled.Owed, o => o.Kind is OpKind.WallIn or OpKind.WallOut or OpKind.DoorOut);
        // Its site is the flap in the flap's stuff.
        var site = Assert.Single(PlannedRules.PlannedSitesOf([(room, reconciled.Owed)], (r, def) => def == r.RingDefs().Door ? [Cloth.Value] : [Wood.Value],
            (def, stuff) => (stuff, [new Amount(new Resource(stuff), 25)]), _ => null, _ => null, "Vent", new HashSet<(string, Cell)>(), new Dictionary<Cell, string>()),
            s => s.Op == OpKind.DoorIn);
        Assert.Equal((HerdShelterRules.BarnDoorDefinition, Cloth.Value), (site.Definition, site.Stuff));

        // The flap standing there owes nothing more.
        var flapped = LayoutGroundRules.GroundOf([.. buildings.Take(buildings.Count - 1),
            ConstructionObservationFixtures.Built(new Building(HerdShelterRules.BarnDoorDefinition, doorCell, Rotation.North, Cloth.Value), "flap")]);
        Assert.DoesNotContain(ReconcileRules.Reconcile(input with { Ground = flapped }).Owed, o => o.Kind == OpKind.DoorIn);
        // A standing door with no def named in the census stands.
        var unnamed = LayoutGroundRules.GroundOf(buildings);
        unnamed.DoorDefs.Clear();
        Assert.DoesNotContain(ReconcileRules.Reconcile(input with { Ground = unnamed }).Owed, o => o.Kind == OpKind.DoorIn);
    }
}
