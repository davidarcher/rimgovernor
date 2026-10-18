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

    [Fact]
    public void GridsBuiltInAThreadsReusedArraysReadAsFreshOnesAndTheLastBuilderHoldsThem()
    {
        var rng = new Random(3436);
        var onMap = new Rectangle(0, 0, 48, 48);
        var weight = new Prefix(48, 48, c => (c.X + c.Z) % 4);
        XRects Rects() => new(Enumerable.Range(0, rng.Next(1, 12)).Select(_ => Random(rng)));
        for (var round = 0; round < 100; round++)
        {
            var a = new EnclosureSet(Rects(), Rects(), Rects());
            var b = new EnclosureSet(Rects(), Rects(), Rects());
            a.Grids = CoverGridBuffers.Build(a, onMap, weight);
            b.Grids = CoverGridBuffers.Build(b, onMap, weight);
            // b's grids overwrote the arrays a's were in, so a no longer reads them.
            Assert.Null(a.Grids);
            var fresh = new[] { new CoverGrid(onMap, b.Grown, weight), new CoverGrid(onMap, b.In, weight), new CoverGrid(onMap, b.Walls, null) };
            for (var q = 0; q < 20; q++)
            {
                var k = Random(rng);
                Assert.Equal(fresh[0].Uncovered(k, weight), b.Grids[0].Uncovered(k, weight));
                Assert.Equal(fresh[1].Uncovered(k, weight), b.Grids[1].Uncovered(k, weight));
                Assert.Equal(fresh[2].Uncovered(k, null), b.Grids[2].Uncovered(k, null));
            }
        }
    }

    private static Rectangle Random(Random rng) => new(rng.Next(0, 40), rng.Next(0, 40), rng.Next(1, 9), rng.Next(1, 9));
}
