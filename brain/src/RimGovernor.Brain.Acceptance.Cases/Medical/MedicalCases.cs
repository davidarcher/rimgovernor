namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The <c>medical/*</c> acceptance cases (Go <c>cases/medical</c>): surgery planning against native doctors, the pawn
/// health read contracts and the stable-patient diagnostic. <see cref="Register"/> replaces the Go package's <c>init()</c>.
/// </summary>
public static class MedicalCases
{
    /// <summary>Registers every <c>medical/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(PegCycle.Case());
        registry.Register(PlagueReadback.Case());
        registry.Register(StablePatient.Case());
        foreach (var surgery in SurgeryCases.All())
        {
            registry.Register(surgery);
        }

        registry.Register(SurgeryFactsCase.Case());
        registry.Register(SurgeryIntentCase.Case());
        registry.Register(TendGates.Case());
    }
}
