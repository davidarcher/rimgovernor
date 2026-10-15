namespace RimGovernor.Brain.Acceptance.Cases;
internal static class ClearanceChunkDump
{
    internal static AcceptanceCase Case() => SupportPortfolioCase.Create("clearance/chunk-dump", "chunks",
        "A real stonecutting requirement selects useful chunk pickup and exact delivery while unrelated chunks remain unmoved. No global haulability mutation or blanket chunk clearance is expected.");
}
