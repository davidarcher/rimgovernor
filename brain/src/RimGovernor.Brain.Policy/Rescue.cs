namespace RimGovernor.Brain.Policy;

/// <summary>Describes one undrafted candidate rescuer.</summary>
public sealed record RescuerFacts
{
    public required PawnId Pawn { get; init; }

    public required bool Dead { get; init; }

    public required bool Downed { get; init; }

    public required bool Drafted { get; init; }

    public required bool MentalState { get; init; }

    public required bool PlayerForced { get; init; }

    public required uint QueuedJobs { get; init; }

    /// <summary>The def of the job the pawn runs; null when it runs none.</summary>
    public required string? ExistingJobDef { get; init; }
}

/// <summary>
/// Describes one candidate patient. Completion is InBed; a downed pawn who has
/// simply stopped moving is not evidence of a completed rescue.
/// </summary>
public sealed record RescuePatientFacts
{
    public required PawnId Pawn { get; init; }

    public required bool Dead { get; init; }

    public required bool Downed { get; init; }

    public required bool InBed { get; init; }

    /// <summary>The def of the job the pawn runs; null when it runs none.</summary>
    public required string? ExistingJobDef { get; init; }
}

public static partial class RescueRules
{
    /// <summary>
    /// Pairs the first available undrafted rescuer with the first downed, not-yet-bedded patient (by ID for a
    /// stable, deterministic choice). This is a proposal only; EvaluateRescue re-validates the chosen pair.
    /// </summary>
    public static (PawnId Rescuer, PawnId Patient, bool Ok) SelectRescue(
        IReadOnlyList<RescuerFacts> rescuers, IReadOnlyList<RescuePatientFacts> patients)
    {
        static bool EligibleRescuer(RescuerFacts r) =>
            !r.Dead && !r.Downed && !r.Drafted && !r.MentalState && r.ExistingJobDef != "Rescue";

        static bool EligiblePatient(RescuePatientFacts p) =>
            !p.Dead && p.Downed && !p.InBed && p.ExistingJobDef != "Rescue";

        var rescuerPool = rescuers.Where(EligibleRescuer).ToList();
        var patientPool = patients.Where(EligiblePatient).ToList();
        if (rescuerPool.Count == 0 || patientPool.Count == 0)
        {
            return (default, default, false);
        }
        var rescuer = rescuerPool.OrderBy(r => OrderedWorkRules.OrderedWorkCost(r.PlayerForced, r.QueuedJobs)).ThenBy(r => r.Pawn).First();
        var patient = patientPool.OrderBy(p => p.Pawn).First();
        return (rescuer.Pawn, patient.Pawn, true);
    }
}
