namespace RimGovernor.Brain.Acceptance.Cases;
internal static class CleanBench
{
    internal static AcceptanceCase Case() => SupportPortfolioCase.Create("clean/bench", "bench",
        "A real dirty cooking station and hungry consumer enter normal shared selection. Observe selected cleaning and completed production, permitting self service or helper work without a fixed cleaning quota.");
}
