using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The embedding prices wall sharing for every room and never leaves two rooms one cell apart (#3746, epic #3738 D4).
/// </summary>
public sealed class LayoutEmbedWallTests
{
    /// <summary>An open, firm map; cells in blocked are ground nothing walks on.</summary>
    private static MapSurvey Survey(int side, Func<int, int, bool>? blocked = null) => new()
    {
        Bounds = new Bounds(side, side),
        Cells = [.. Enumerable.Range(0, side).SelectMany(x => Enumerable.Range(0, side).Select(z => new SurveyCell
        {
            Cell = new Cell(x, z), Walkable = blocked is null || !blocked(x, z), Footing = Footing.Firm, Fertility = 1,
        }))],
    };

    /// <summary>Rooms of the given areas; links are (from, to, weight, required), added at both ends.</summary>
    private static List<RoomGroup> Groups(int[] areas, (int A, int B, double W, bool Req)[] links)
    {
        var groups = areas.Select((a, i) => new RoomGroup { Id = i, Area = a, Footprint = a / 2, Clearance = a - (a / 2) }).ToList();
        foreach (var (a, b, w, req) in links)
        {
            groups[a].Links.Add(new GroupLink { To = b, Weight = w, Required = req });
            groups[b].Links.Add(new GroupLink { To = a, Weight = w, Required = req });
        }
        return groups;
    }

    /// <summary>The groups on the survey, every walkable cell core ground.</summary>
    private static EmbedInput Input(MapSurvey survey, List<RoomGroup> groups) => new()
    {
        Groups = groups,
        Survey = survey,
        RoofDistance = 6,
        Zones = [new LayoutZone { Kind = LayoutZoningRules.ZoneCore, Runs = [.. survey.Cells.Where(c => c.Walkable).Select(c => new RowRun(c.Cell.Z, c.Cell.X, 1))] }],
    };

    public static TheoryData<string> Fixtures => ["pair", "chain", "outskirts", "obstacles"];

    private static EmbedInput Fixture(string name) => name switch
    {
        "pair" => Input(Survey(30), Groups([12, 12], [(0, 1, 2, false)])),
        "chain" => Input(Survey(40), Groups([12, 9, 16, 6, 9], [(0, 1, 3, false), (1, 2, 1, true), (2, 3, 0.5, false), (3, 4, 2, false)])),
        "outskirts" => Input(Survey(40), Groups([12, 9, 9, 6, 6, 4], [(0, 1, 2, false), (1, 2, 1, false)])),
        _ => Input(Survey(40, (x, z) => (x % 9 == 4 && z % 7 != 0) || (z == 20 && x < 30)), Groups([16, 12, 9, 9, 6, 6], [(0, 1, 2, false), (0, 2, 1, true), (2, 3, 1, false), (4, 5, 0.5, false)])),
    };

    [Fact]
    public void TwoLinkedRoomsShareAWallAndTheScoreRecordsTheSavedWalls()
    {
        var input = Fixture("pair");
        var emb = Embedding.Embed(input);

        var (a, okA) = emb.Block(0);
        var (b, okB) = emb.Block(1);
        Assert.True(okA && okB);
        Assert.Equal(1, LayoutEmbedRules.GapDist(a, b));

        // The linked rooms are priced by enclosure too, not only rooms no flow reaches.
        var scorer = new PlanScorer(input.Zones, input.Reservations, input.Survey);
        var e = new Embedder(input, LayoutClusterRules.RoomLimits(input.RoofDistance).Item1, scorer, ObstacleLevels.All[0], new EmbedFixed(input, scorer));
        Assert.NotNull(e.EnclosureFor(0, [a, b]));
        Assert.NotNull(e.EnclosureFor(1, [a, b]));

        // The score records the union's enclosure, in Total: less than the two rooms' own rings by the walls they share.
        Rectangle[] none = [default, default];
        var apart = e.EnclosureCost(a, e.EnclosureRects(0, none)) + e.EnclosureCost(b, e.EnclosureRects(1, none));
        Assert.Equal(-(int)e.EnclosureTotal([a, b]), emb.Score.Enclosure);
        Assert.True(apart + emb.Score.Enclosure >= PlanWeights.Wall * Math.Min(a.Width, a.Height), $"apart {apart}, together {-emb.Score.Enclosure}");
        Assert.Equal(emb.Score.Soil + emb.Score.Edge + emb.Score.Centre + emb.Score.Walk + emb.Score.Switch + emb.Score.Enclosure, emb.Score.Total());
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void NoTwoBlocksStandAtGapTwo(string fixture)
    {
        var emb = Embedding.Embed(Fixture(fixture));

        Assert.NotEmpty(emb.Blocks);
        Assert.Empty(emb.Unplaceable());
        foreach (var x in emb.Blocks)
        {
            foreach (var y in emb.Blocks.Where(y => y.Group > x.Group))
            {
                Assert.False(LayoutEmbedRules.AtDeadGap(x.Interior, y.Interior), $"{x.Interior} and {y.Interior} stand two cells apart");
            }
        }
    }

    [Fact]
    public void TheRingAtGapTwoOffersOnlyCornerPositions()
    {
        var anchor = new Rectangle(10, 10, 3, 4);
        var starts = new List<(int X, int Z)>();
        LayoutEmbedRules.RingPositions(anchor, (3, 3), LayoutEmbedRules.DeadGap, false, starts);
        Assert.NotEmpty(starts);
        foreach (var (x, z) in starts)
        {
            var r = new Rectangle(x, z, 3, 3);
            Assert.Equal(LayoutEmbedRules.DeadGap, LayoutEmbedRules.GapDist(anchor, r));
            Assert.False(LayoutEmbedRules.AtDeadGap(anchor, r), $"{r} stands two cells beside {anchor}");
        }

        // A field keeps its own rule: it may stand two cells beside a room.
        starts.Clear();
        LayoutEmbedRules.RingPositions(anchor, (3, 3), LayoutEmbedRules.DeadGap, true, starts);
        Assert.Contains(starts, s => LayoutEmbedRules.AtDeadGap(anchor, new Rectangle(s.X, s.Z, 3, 3)));
    }
}
