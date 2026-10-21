using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>lab/route-floods</c>: the coupled forecast's route matrix by one Dijkstra flood per origin (#3685) agrees with the
/// game's A* search. test/route_floods walls the lab in three lines, floods about two hundred reachable cells each to
/// every other, and searches a sample of the pairs by FindPathNow with the same pawn and TraverseParms. A flood is the
/// shortest walk under the game's cost model, and A*'s heuristic overestimates a diagonal step, so a flood is never
/// dearer, at most 5% (or 20 ticks) cheaper, and equal on a path with no diagonal step. The matrix's game-thread time is
/// recorded, never gated. A unit test cannot run the pathfinder.
/// </summary>
internal static class LabRouteFloods
{
    private const string Tool = "test/route_floods";

    private static readonly FixtureOp<NoArgs, Fx.RouteFloodsRead> Floods = ServedOps.Read<Fx.RouteFloodsRead>(Tool);

    internal static TypedCase Case() => new()
    {
        Name = "lab/route-floods",
        Scope = "About two hundred walled-lab cells flooded each to every other; on at least 200 sampled pairs the flood and A* agree "
            + "on reachability, the flood is never dearer and at most 5% (or 20 ticks) cheaper, and equal without a diagonal step.",
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = 3 },
        Ops = [Tool],
        QuietWorld = true,
        Run = async (s, ct) =>
        {
            var read = await s.FixtureAsync("route-floods", Floods, NoArgs.Value, ct).ConfigureAwait(false);
            s.Record("route_floods", ServedOps.Json(read));
            var wrong = new List<string>();
            if (read.Floods == 0)
            {
                wrong.Add($"no floods ran for {read.Origins} origins");
            }

            if (read.Pairs.Count < 200)
            {
                wrong.Add($"{read.Pairs.Count} pairs, want at least 200");
            }

            var deviations = new List<double>();
            foreach (var pair in read.Pairs)
            {
                if (pair.Flood is null != pair.Search is null)
                {
                    wrong.Add($"{pair.Leg}: flood {pair.Flood?.ToString() ?? "none"}, search {pair.Search?.ToString() ?? "none"}");
                    continue;
                }

                if (pair.Flood is not { } flood || pair.Search is not { } search)
                {
                    continue;
                }

                var under = search - flood;
                deviations.Add(under);
                if (under < 0)
                {
                    wrong.Add($"{pair.Leg}: flood {flood} > search {search}");
                }
                else if (under > Math.Max(0.05 * search, 20))
                {
                    wrong.Add($"{pair.Leg}: flood {flood} is {under} under search {search}");
                }
                else if (pair.Diagonal == 0 && under != 0)
                {
                    wrong.Add($"{pair.Leg}: flood {flood} != search {search} on a path with no diagonal step");
                }
            }

            s.Record("floods", read.Floods);
            s.Record("matrix_ms", read.MatrixMs);
            s.Record("max_deviation", deviations.Count == 0 ? 0 : deviations.Max());
            s.Record("mean_deviation", deviations.Count == 0 ? 0 : deviations.Average());
            if (wrong.Count != 0)
            {
                throw new AcceptanceException("route floods: " + string.Join("; ", wrong));
            }
        },
    };
}
