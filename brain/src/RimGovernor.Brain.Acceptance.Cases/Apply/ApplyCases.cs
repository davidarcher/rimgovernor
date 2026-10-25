
namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The <c>apply/*</c> lab contract cases: Actions/Apply behaviours a unit test cannot see because
/// they live in the native handlers.
/// </summary>
internal static class ApplyCases
{
    /// <summary>Registers every <c>apply/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(ApplyActions.Case());
        registry.Register(ApplyAreaIntent.Case());
        registry.Register(ApplyDeferredSnapshot.Case());
        registry.Register(ApplyPolicyPrune.Case());
        registry.Register(ApplyRefusal.Case());
    }

    /// <summary>The effect an applied action's receipt observed; null when the outcome is not an applied one.</summary>
    internal static RimGovernor.Brain.GameFacts.Receipts.EffectEvidenceEffect? Effect(RimGovernor.Brain.GameFacts.Operations.ActionResultOutcome outcome) =>
        outcome.Applied?.Outcome?.Applied?.Observed?.Effect;
}
