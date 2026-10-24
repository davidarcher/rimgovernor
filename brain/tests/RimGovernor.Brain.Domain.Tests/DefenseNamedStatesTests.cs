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
            new DefenseTurretFacts { Id = "t1", Cell = new Cell(1, 1), Dps = 5, Powered = false, OutOfFuel = false },
            new DefenseTurretFacts { Id = "t2", Cell = new Cell(2, 2), Dps = 5, Powered = true, OutOfFuel = true, FuelDefinitions = [steel] },
        };
        var upkeep = DefenseRearm.DefenseRearmTurrets(turrets, [], new Dictionary<Resource, long>(), false);
        Assert.Equal([new Cell(1, 1)], upkeep.Unpowered);
        Assert.Equal([new Cell(2, 2)], upkeep.Empty);
        Assert.False(upkeep.Rearm);
        Assert.Empty(DefenseRearm.DefenseRearmTurrets(turrets, [], new Dictionary<Resource, long>(), true).Unpowered);
    }
}
