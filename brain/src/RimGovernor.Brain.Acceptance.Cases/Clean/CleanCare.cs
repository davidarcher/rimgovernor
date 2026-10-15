namespace RimGovernor.Brain.Acceptance.Cases;
internal static class CleanCare
{
    internal static AcceptanceCase RoutineCase() => SupportPortfolioCase.Create("clean/care-routine", "routine",
        "A nonbleeding tendable injury and needed food station enter normal coupled selection. Observe actual coupled treatment and useful cleaning without an automatic finalizer.");
    internal static AcceptanceCase Case() => SupportPortfolioCase.Create("clean/care", "care",
        "A bleeding patient retains CriticalMedical authority while useful room cleanup enters shared selection. Observe treatment within the prepared 2500-tick service budget (not a prognosis) and useful cleaning, without requiring cleaning before urgent care or a tend finalizer.");
}
