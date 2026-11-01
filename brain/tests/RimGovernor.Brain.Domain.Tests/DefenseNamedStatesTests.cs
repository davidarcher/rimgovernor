using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

// #3901: the defense layout and turret facts read whole. A sight line is Clear, Blocked or Unseen;
// an unpriced definition is never afforded; a turret's service facts are plain.
public sealed class DefenseNamedStatesTests
{
    private static readonly Cell From = new(0, 0);
    private static readonly Cell To = new(4, 0);

    // A 5x1 strip of open ground, with the cells in skip left out of the census and a full wall at wall.
    private static DefenseSite Strip(Cell? wall = null, Cell? skip = null)
    {
        var site = new DefenseSite(new DefenseRequest { Bounds = new Bounds(5, 1), Region = new Rectangle { Width = 5, Height = 1 } });
        for (var x = 0; x < 5; x++)
        {
            var c = new Cell(x, 0);
            if (c == skip)
            {
                site.Fogged.Add(c);
                continue;
            }
            site.Cells[c] = c == wall
                ? new DefenseCell { Cell = c, Edifice = "Wall", CoverFill = 1.0 }
                : new DefenseCell { Cell = c, Walkable = true, Passable = true };
        }
        return site;
    }

    [Fact]
    public void ASightLineOverSeenOpenGroundIsClear() => Assert.Equal(Sightline.Clear, Strip().LineOfSight(From, To));

    [Fact]
    public void AFullWallBlocksTheSightLine()
    {
        var site = Strip(wall: new Cell(2, 0));
        Assert.Equal(SightCell.Blocks, site.BlocksSight(new Cell(2, 0)));
        Assert.Equal(Sightline.Blocked, site.LineOfSight(From, To));
    }

    [Fact]
    public void AFoggedCellOnTheWayLeavesTheSightLineUnseen()
    {
        var site = Strip(skip: new Cell(2, 0));
        Assert.Equal(SightCell.Unseen, site.BlocksSight(new Cell(2, 0)));
        Assert.Equal(Sightline.Unseen, site.LineOfSight(From, To));
    }

    [Fact]
    public void ABlockerOutranksAnUnseenCell() =>
        Assert.Equal(Sightline.Blocked, Strip(wall: new Cell(3, 0), skip: new Cell(1, 0)).LineOfSight(From, To));

    [Fact]
    public void ASightLineLeavingTheMapIsBlocked() => Assert.Equal(Sightline.Blocked, Strip().LineOfSight(From, new Cell(9, 0)));

    [Fact]
    public void AnUnpricedDefinitionIsNeverAfforded()
    {
        var steel = new Resource("Steel");
        var unit = new Dictionary<string, EquatableArray<Amount>> { ["Turret_MiniTurret"] = [new Amount(steel, 70)] };
        var stock = new Dictionary<Resource, long> { [steel] = 1000 };
        Building At(string def) => new(def, new Cell(1, 1), Rotation.North, "");
        Assert.True(DefenseLayout.StockCovers(unit, stock, [At("Turret_MiniTurret")]));
        Assert.False(DefenseLayout.StockCovers(unit, stock, [At("Turret_Autocannon")]));
        Assert.False(DefenseLayout.StockCovers(unit, new Dictionary<Resource, long>(), [At("Turret_MiniTurret")]));
    }

    [Fact]
    public void AnUnpoweredOrEmptyTurretIsAServiceDeficit()
    {
        var steel = new Resource("Steel");
        var turrets = new[]
        {
            PowerBuildingFixtures.Trader("t1", "Turret", new(1, 1), -100, 0, false) with { Turret = new TurretGun(5) },
            PowerBuildingFixtures.Trader("t2", "Turret", new(2, 2), -100, -100, true) with { Turret = new TurretGun(5), FuelDefinitions = [steel.Value], Service = BuildingServiceWire.NoService with { Power = new RimGovernor.Brain.GameFacts.Observations.BuildingPower.Trader { RatedW = -100, OutputW = -100, PowerOn = true, Network = "net" }, Fuel = new RimGovernor.Brain.GameFacts.Observations.BuildingFuel.Refuelable { AutoRefuel = new RimGovernor.Brain.GameFacts.Observations.AutoRefuelControl.NoSwitch(), Burn = BuildingServiceWire.IdleBurn, Fuel = 0, TargetFuel = 10, AutoRefuelPercent = 0.3, OutOfFuel = true } } },
        };
        var upkeep = DefenseRearm.DefenseRearmTurrets(turrets, false);
        Assert.Equal([new Cell(1, 1)], upkeep.Unpowered);
        Assert.Empty(DefenseRearm.DefenseRearmTurrets(turrets, true).Unpowered);
        Assert.Equal(["t2"], DefenseRearm.EmptyTurrets(turrets).Select(b => b.Id));
    }

    // Task 7.4: a spent barrel is rearmed through the maintenance choice's refuel (the 5.x servicing), not a fixed hold.
    [Fact]
    public void ASpentBarrelIsRefuelledThroughTheServicingChoice()
    {
        var steel = new Resource("Steel");
        var spent = PowerBuildingFixtures.Trader("t2", "Turret", new(2, 2), -100, -100, true) with { Turret = new TurretGun(5), FuelDefinitions = [steel.Value], HitPoints = new RimGovernor.Brain.GameFacts.Observations.BuildingHitPoints.Tracked { Current = 100, Maximum = 100 }, Service = BuildingServiceWire.NoService with { Power = new RimGovernor.Brain.GameFacts.Observations.BuildingPower.Trader { RatedW = -100, OutputW = -100, PowerOn = true, Network = "net" }, Fuel = new RimGovernor.Brain.GameFacts.Observations.BuildingFuel.Refuelable { AutoRefuel = new RimGovernor.Brain.GameFacts.Observations.AutoRefuelControl.NoSwitch(), Burn = BuildingServiceWire.IdleBurn, Fuel = 0, TargetFuel = 10, AutoRefuelPercent = 0.3, OutOfFuel = true } } };
        var census = new ServicingCensus([new ServicingOption.Assessed(new RefuelWork("t2", "a", true, true, steel, 10, 3, 100, 50, 40, 240, true) { ServiceLoss = new ServiceLoss.Unmodelled("turret service:Turret") })])
        {
            Components = new ServicingComponents.Counted(0), Stock = new Dictionary<string, int>(StringComparer.Ordinal) { [steel.Value] = 200 },
        };
        var empty = DefenseRearm.EmptyTurrets([spent]);
        var decision = Assert.Single(DefenseRearm.Refuels(empty, census, [], []));
        Assert.Equal((ServicingChoiceKind.Refuel, "a"), (decision.Chosen.Kind, decision.Chosen.Pawn));
        var method = Assert.Single(DefenseRearm.Methods(new NeedId("layout/defense"), [decision]));
        Assert.Equal("refuel-t2-a", method.Method.Value);
        var service = Assert.Single(method.Plan(new PlanId("p")).Actions).RecoveryService!;
        Assert.Equal(("a", "t2", RecoveryMethod.Refuel), (service.Pawn.Value, service.Thing, service.Method));
        // A refuel already underway is observed, not reordered.
        var running = Assert.Single(DefenseRearm.Refuels(empty, census, [new("t2", RimGovernor.Brain.GameFacts.Observations.ServicingKind.Refuel, "a", true)], []));
        Assert.Empty(DefenseRearm.Methods(new NeedId("layout/defense"), [running]));
    }
}
