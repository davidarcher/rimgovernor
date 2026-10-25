using RimGovernor.Brain.Policy;
using Verse;
using Xunit;
using Cell = RimGovernor.Brain.Domain.Cell;

namespace RimGovernor.Brain.Domain.Tests.Game;

public sealed class NurseryGeometryNativeTests
{
    [Fact]
    public void SocialSightCrossesExactlyTheNativeCellsInBothDirectionsAndOnTies()
    {
        // GenSight.PointsOnLineOfSight and LineOfSight share the installed native integer stepping law.
        // SocialInteractionUtility requests skipFirstCell; LineOfSight stops before inspecting the end cell.
        var cells = Enumerable.Range(0, 7).SelectMany(x => Enumerable.Range(0, 7).Select(z => new IntVec3(x, 0, z))).ToArray();
        foreach (var from in cells) foreach (var to in cells)
        {
            var native = GenSight.PointsOnLineOfSight(from, to).Where(c => c != from && c != to).Select(c => new Cell(c.x, c.z));
            Assert.Equal(native, ForecastNurseryGeometry.SightCells(new(from.x, from.z), new(to.x, to.z)));
        }
    }
}
