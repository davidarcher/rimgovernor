using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class CoverScratchTests
{
    [Fact]
    public void ReusedScratchCountsTheSameUncoveredCellsAsTheGrid()
    {
        var rng = new Random(3435);
        var scratch = new CoverScratch();
        var onMap = new Rectangle(0, 0, 48, 48);
        // The grid spans only the rectangles' bounding box; a query reaching past it, or over no rectangles at
        // all, still reads what the sweep counts, weighted or not.
        var weight = new Prefix(48, 48, c => (c.X + (2 * c.Z)) % 3);
        for (var round = 0; round < 500; round++)
        {
            var rects = new XRects(Enumerable.Range(0, rng.Next(0, 12)).Select(_ => Random(rng)));
            var grid = new CoverGrid(onMap, rects, null);
            var k = Random(rng);
            Assert.Equal(grid.Uncovered(k, null), scratch.Uncovered(k, rects, null));
            var weighted = new CoverGrid(onMap, rects, weight);
            Assert.Equal(weighted.Uncovered(k, weight), scratch.Uncovered(k, rects, weight));
        }
    }

    private static Rectangle Random(Random rng) => new(rng.Next(0, 40), rng.Next(0, 40), rng.Next(1, 9), rng.Next(1, 9));
}
