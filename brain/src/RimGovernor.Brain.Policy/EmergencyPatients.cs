namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    /// <summary>Patient-only union; combat participants and medical workers remain colonists.</summary>
    public static IReadOnlyList<EmergencyPawn> EmergencyPatients(EmergencyFacts facts) =>
        [.. facts.Colonists, .. facts.DependentPatients.ValueOrDefault
            .Where(p => p.Rescue.ValueOrDefault || p.Tend.ValueOrDefault).Select(p => p.Pawn)];

    public static IReadOnlyList<PawnId> EmergencyRescuePatients(EmergencyFacts facts) =>
        [.. facts.Colonists.Select(p => p.Id), .. facts.DependentPatients.ValueOrDefault
            .Where(p => p.Rescue.TryGetValue(out var allowed) && allowed).Select(p => p.Pawn.Id)];

    public static IReadOnlyList<PawnId> EmergencyTendPatients(EmergencyFacts facts) =>
        [.. facts.Colonists.Select(p => p.Id), .. facts.DependentPatients.ValueOrDefault
            .Where(p => p.Tend.TryGetValue(out var allowed) && allowed).Select(p => p.Pawn.Id)];
}
