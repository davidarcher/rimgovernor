namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The <c>burial/*</c> acceptance cases: the burial ranking cases.</summary>
public static class BurialCases
{
    /// <summary>Registers every <c>burial/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(GraveOverMorgue.Case());
        registry.Register(StrangerSarcophagus.Case());
    }
}
