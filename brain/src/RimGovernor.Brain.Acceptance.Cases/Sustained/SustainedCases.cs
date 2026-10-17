namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The sustained case family (Go <c>cases/sustained</c>).</summary>
public static class SustainedCases
{
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(SustainedColonyCase.Colony(
            "sustained/colony",
            QuietMode.Required,
            "Diagnostic: every routine family on the " + SustainedFoodCase.BaselineSave + " save under a quiet storyteller for " +
            "ColonyWindowTicks game ticks, sampling the foothold and first maintenance standards together. " +
            "Evidence for #99's sustained coverage: which standard stalls, thrashes or starves the others once " +
            "all families share one step budget. Two gates only: keep-alive resumes cost one native generation each " +
            "(#259) and no colonist ends the window past Malnutrition 0.3 (#260); otherwise it fails only when the " +
            "harness itself cannot complete."));
    }
}
