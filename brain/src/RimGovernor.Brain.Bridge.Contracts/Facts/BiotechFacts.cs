using PolicyPawnBiotech = RimGovernor.Brain.Policy.PawnBiotech;

namespace RimGovernor.Brain.Bridge.Contracts;

// The lift of a pawn's Biotech block. The gene effects and work minimum ages the lift leaves unknown are resolved
// against the catalog by the caller.
public static class BiotechFactsRules
{
    /// <summary>Lifts a pawn row's Biotech block; unknown when Biotech is inactive.</summary>
    public static Fact<PolicyPawnBiotech> PawnBiotech(RimGovernor.Contracts.Wire.Observations.PawnBiotechState state) =>
        state is RimGovernor.Contracts.Wire.Observations.PawnBiotechState.Active { Facts: var biotech }
            ? Known(PolicyPawnBiotech.Of(biotech))
            : Unknown<PolicyPawnBiotech>();
}
