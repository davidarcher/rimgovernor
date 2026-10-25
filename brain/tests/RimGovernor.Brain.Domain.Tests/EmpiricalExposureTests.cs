using RimGovernor.Brain.Acceptance;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>Empirical exposure preserves raid correlation and a fixed fixture mixture (#3969).</summary>
public sealed class EmpiricalExposureTests
{
    [Fact]
    public void FourCorrelatedPawnsDoNotBecomeFourIndependentRaids()
    {
        var clustered = new List<ExposureRow>();
        var independent = new List<ExposureRow>();
        for (var seed = 0; seed < 40; seed++)
        for (var pawn = 0; pawn < 4; pawn++)
        {
            var hit = seed % 2 == 0;
            clustered.Add(Row("fight", seed, hit ? 4 : 0, hit));
            independent.Add(Row("fight", seed * 4 + pawn, hit ? 4 : 0, hit));
        }

        var raids = Cell(clustered);
        var pawns = Cell(independent);

        Assert.Equal(160, raids.Trials);
        Assert.Equal(40, raids.Raids);
        Assert.Equal(20, raids.HitRaids);
        Assert.Equal(160, pawns.Raids);
        Assert.Equal(80, pawns.HitRaids);
        Assert.Equal(2.0, raids.MeanHits);
        Assert.Equal(pawns.MeanHits, raids.MeanHits);
        Assert.Equal(0.5, raids.DownRate);
        Assert.Equal(pawns.DownRate, raids.DownRate);
        Assert.True(raids.HitsHi - raids.HitsLo > 1.5 * (pawns.HitsHi - pawns.HitsLo));
        Assert.True(raids.DownHi - raids.DownLo > 1.5 * (pawns.DownHi - pawns.DownLo));
    }

    [Fact]
    public void MoreSamplingRoundsForOneFixtureDoNotChangeItsMixtureWeight()
    {
        var rows = new List<ExposureRow>();
        for (var seed = 0; seed < 20; seed++)
        {
            for (var pawn = 0; pawn < 4; pawn++)
            {
                rows.Add(Row("four-safe-pawns", seed, 0));
            }

            rows.Add(Row("one-hit-pawn", seed, 5, down: true));
        }

        var first = Cell(rows);
        for (var seed = 20; seed < 60; seed++)
        {
            rows.Add(Row("one-hit-pawn", seed, 5, down: true));
        }

        var more = Cell(rows);
        Assert.Equal(1.0, first.MeanHits);
        Assert.Equal(0.2, first.DownRate);
        Assert.Equal((first.MeanHits, first.HitsLo, first.HitsHi, first.DownRate, first.DownLo, first.DownHi),
            (more.MeanHits, more.HitsLo, more.HitsHi, more.DownRate, more.DownLo, more.DownHi));
        Assert.Equal((100, 40, 20), (first.Trials, first.Raids, first.HitRaids));
        Assert.Equal((140, 80, 60), (more.Trials, more.Raids, more.HitRaids));
    }

    [Fact]
    public void ShufflingFixturesRaidsAndPawnsLeavesTheEntireTableUnchanged()
    {
        var rows = new List<ExposureRow>();
        foreach (var fixture in new[] { "z", "a" })
        for (var seed = 0; seed < 30; seed++)
        for (var pawn = 0; pawn < 4; pawn++)
        {
            rows.Add(Row(fixture, seed, (seed + pawn) % 7, down: (seed + pawn) % 5 == 0));
        }

        var expected = RaidExposure.Serialize(SweepExposure.Build(rows));
        var random = new Random(3969);
        for (var i = rows.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (rows[i], rows[j]) = (rows[j], rows[i]);
        }

        Assert.Equal(expected, RaidExposure.Serialize(SweepExposure.Build(rows)));
    }

    [Fact]
    public void ZeroHitRaidsKeepTheirPawnRowsAndAnEmpiricalZeroInterval()
    {
        var rows = new List<ExposureRow>();
        foreach (var fixture in new[] { "first", "second" })
        for (var seed = 0; seed < 20; seed++)
        for (var pawn = 0; pawn < 4; pawn++)
        {
            rows.Add(Row(fixture, seed, 0));
        }

        var cell = Cell(rows);

        Assert.Equal((160, 40, 0), (cell.Trials, cell.Raids, cell.HitRaids));
        Assert.Equal((0.0, 0.0, 0.0), (cell.MeanHits, cell.HitsLo, cell.HitsHi));
        Assert.Equal((0.0, 0.0, 0.0), (cell.DownRate, cell.DownLo, cell.DownHi));
    }

    [Fact]
    public void UnequalRaidCohortsStillUsePerColonistMeansAndObservedFixtureWeights()
    {
        ExposureRow[] rows =
        [
            Row("variable", 1, 2, down: true),
            Row("variable", 2, 0), Row("variable", 2, 0), Row("variable", 2, 0),
            Row("single", 1, 5, down: true), Row("single", 2, 5, down: true),
        ];

        var cell = Cell(rows);

        // The variable fixture has two pawns per raid on average, versus one for single.
        Assert.Equal(2.0, cell.MeanHits);
        Assert.Equal(0.5, cell.DownRate);
        Assert.Equal((6, 4, 3), (cell.Trials, cell.Raids, cell.HitRaids));
        Assert.True(cell.HitsLo <= cell.MeanHits && cell.MeanHits <= cell.HitsHi);
        Assert.True(cell.DownLo <= cell.DownRate && cell.DownRate <= cell.DownHi);
    }

    private static ExposureRow Row(string fixture, int seed, int hits, bool down = false) =>
        new(fixture, seed, 1.5, ExposureRoles.Gunner, hits, Downed: down, Dead: false);

    private static ExposureCell Cell(IReadOnlyList<ExposureRow> rows) => Assert.Single(SweepExposure.Build(rows).Cells);
}
