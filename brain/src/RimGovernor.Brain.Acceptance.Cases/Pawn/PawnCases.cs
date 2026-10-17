namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The <c>pawn/*</c> acceptance cases: the native pawn read facts and the per-pawn settings and policies the intents write.</summary>
public static class PawnCases
{
    /// <summary>Registers every <c>pawn/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(PawnReads.Case());
        foreach (var c in PawnStateReads.All().Concat(PawnSettings.All()).Concat(PawnPolicies.All()))
        {
            registry.Register(c);
        }
    }
}
