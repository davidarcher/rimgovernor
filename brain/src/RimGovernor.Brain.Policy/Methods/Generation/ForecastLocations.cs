namespace RimGovernor.Brain.Policy.Methods.Generation;

internal static class ForecastLocations
{
    internal static string Of(Cell cell) => "cell/" + cell.X + "/" + cell.Z;
    internal static ForecastActivityRoute[] Merge(IEnumerable<ForecastActivityRoute> routes)
        => routes.GroupBy(r => (r.Pawn, r.From, r.To)).Select(g => g.First() with
        { Ticks = g.Select(r => r.Ticks).Distinct().Count() == 1 ? g.First().Ticks : default }).ToArray();
}
