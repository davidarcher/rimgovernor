namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The campaign case family (Go <c>cases/campaign</c>).</summary>
public static class CampaignCases
{
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(CampaignFootholdCase.Case());
        registry.Register(CampaignRecoveryCase.Case());
    }
}
