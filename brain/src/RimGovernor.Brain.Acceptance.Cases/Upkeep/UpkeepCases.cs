namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The upkeep case family (Go <c>cases/upkeep</c>).</summary>
public static class UpkeepCases
{
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(UpkeepAutoHomeArea.Case());
        registry.Register(UpkeepCampaign.Case());
        registry.Register(UpkeepColonyExtent.Case());
        registry.Register(UpkeepCouple.Case());
        registry.Register(UpkeepGreedyBedroom.Case());
        registry.Register(UpkeepHomeCoverage.Case());
        registry.Register(UpkeepJealousAscetic.Case());
    }
}
