namespace RimGovernor.Brain.Policy;

public static class EmergencyPatientsRules
{
    /// <summary>Patient-only union; combat participants and medical workers remain colonists.</summary>
    public static IReadOnlyList<EmergencyPawn> EmergencyPatients(EmergencyFacts facts) =>
        [.. facts.Colonists, .. facts.DependentPatients
            .Where(p => p.Rescue || p.Tend).Select(p => p.Pawn)];

    public static IReadOnlyList<PawnId> EmergencyRescuePatients(EmergencyFacts facts) =>
        [.. facts.Colonists.Select(p => p.Id), .. facts.DependentPatients
            .Where(p => p.Rescue).Select(p => p.Pawn.Id)];

    public static IReadOnlyList<PawnId> EmergencyTendPatients(EmergencyFacts facts) =>
        [.. facts.Colonists.Select(p => p.Id), .. facts.DependentPatients
            .Where(p => p.Tend).Select(p => p.Pawn.Id)];
}
