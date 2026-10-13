namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The <c>pawn/*</c> acceptance cases (Go <c>cases/pawn</c>): the native pawn read facts and the per-pawn settings the
/// work routines write. <see cref="Register"/> replaces the Go package's <c>init()</c>.
/// </summary>
public static class PawnCases
{
    /// <summary>Registers every <c>pawn/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(PawnAnimalFoodPolicy.Case());
        registry.Register(PawnDrugAddiction.Case());
        registry.Register(PawnDrugPolicy.Case());
        registry.Register(PawnFoodPolicy.Case());
        registry.Register(PawnHostility.Case());
        registry.Register(PawnInspiration.Case());
        registry.Register(PawnMedicineCarry.Case());
        registry.Register(PawnMentalState.Case());
        registry.Register(PawnNickname.Case());
        registry.Register(PawnReadingPolicy.Case());
        registry.Register(PawnReads.Case());
        registry.Register(PawnSelfTend.Case());
    }
}
