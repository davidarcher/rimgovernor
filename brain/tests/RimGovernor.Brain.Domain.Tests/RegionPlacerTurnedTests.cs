using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The placer seats a room whose door stands on its far side as it seats the same room with the door on its near
/// side (#3746): the greedy pass, turned half round, fills it from the side away from the door.
/// </summary>
public sealed class RegionPlacerTurnedTests
{
    /// <summary>Six barracks beds, each keeping the cell past its end clear, in a 4x5 room with one door.</summary>
    private static List<InteriorPiece> Beds(Cell door) => RegionRules.PlaceInRegion(new RegionPlacement
    {
        Region = RegionRules.RectRegion(new Rectangle(0, 0, 4, 5)),
        Doors = EquatableArray.From<Cell>([door]),
        Wants = EquatableArray.From<RegionWant>([new RegionWant { Slot = "sleep", Def = "Bed", Size = new Cell(1, 2), Count = 6, Min = 6, Clear = 1 }]),
    });

    [Theory]
    [InlineData(-1, 2)] // west, on the middle row
    [InlineData(4, 2)] // east, on the middle row
    public void SixBedsFitBehindADoorOnEitherSide(int x, int z)
    {
        var beds = Beds(new Cell(x, z));

        Assert.Equal(6, beds.Count);
        Assert.Equal(12, beds.SelectMany(b => PlannedRules.RectCells(b.Rect)).Distinct().Count());
    }
}
