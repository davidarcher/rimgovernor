namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The <c>husbandry/*</c> cases (Go <c>cases/husbandry</c>). <see cref="Register"/> replaces the Go package's <c>init()</c>.</summary>
public static class HusbandryCases
{
    /// <summary>Registers every <c>husbandry/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(HusbandryDispatch.Case());
    }
}
