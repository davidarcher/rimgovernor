namespace RimGovernor.Brain.Policy.Methods.Generation;

internal static class ForecastLocations
{
    internal static string Of(Cell cell) => "cell/" + cell.X + "/" + cell.Z;
    /// <summary>One route per (pawn, from, to); rows that disagree on the walk are an inconsistent read.</summary>
    internal static ForecastActivityRoute[] Merge(IEnumerable<ForecastActivityRoute> routes)
        => routes.GroupBy(r => (r.Pawn, r.From, r.To)).Select(g => g.Select(r => r.Ticks).Distinct().Count() == 1 ? g.First()
            : throw new DomainException("routes disagree: " + g.Key)).ToArray();
}
