namespace RimGovernor.Brain.Acceptance.Cases;
internal static class StorageRefillHysteresis
{
    internal static AcceptanceCase Case() => SupportPortfolioCase.Create("storage/refill-hysteresis", "delivery",
        "A small exact ingredient deficit drives a selected delivery and actual production. Count delivered material against demand; a full native stack is not a prerequisite.");
    internal static AcceptanceCase CancellationCase() => SupportPortfolioCase.Create("storage/changed-demand", "cancel",
        "Withdraw a real bill after selected pickup and observe release of the obsolete delivery obligation. Existing completed work is not rolled back and no replacement plan is installed.");
}
