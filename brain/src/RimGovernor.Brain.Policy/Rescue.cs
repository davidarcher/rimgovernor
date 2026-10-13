namespace RimGovernor.Brain.Policy;

/// <summary>Describes one undrafted candidate rescuer.</summary>
public sealed record RescuerFacts
{
    public PawnId Pawn { get; init; }

    public string SnapshotToken { get; init; } = "";

    public Fact<bool> Dead { get; init; }

    public Fact<bool> Downed { get; init; }

    public Fact<bool> Drafted { get; init; }

    public Fact<bool> MentalState { get; init; }

    public Fact<bool> PlayerForced { get; init; }

    public Fact<uint> QueuedJobs { get; init; }

    public Fact<string> ExistingJobDef { get; init; }
}

/// <summary>
/// Describes one candidate patient. Completion is InBed with a known non-empty BedID; a downed pawn who has
/// simply stopped moving is not evidence of a completed rescue.
/// </summary>
public sealed record RescuePatientFacts
{
    public PawnId Pawn { get; init; }

    public string SnapshotToken { get; init; } = "";

    public Fact<bool> Dead { get; init; }

    public Fact<bool> Downed { get; init; }

    public Fact<bool> InBed { get; init; }

    public Fact<string> BedID { get; init; }

    public Fact<string> ExistingJobDef { get; init; }
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// Pairs the first available undrafted rescuer with the first downed, not-yet-bedded patient (by ID for a
    /// stable, deterministic choice). This is a proposal only; EvaluateRescue re-validates the chosen pair.
    /// </summary>
    public static (PawnId Rescuer, PawnId Patient, bool Ok) SelectRescue(
        IReadOnlyList<RescuerFacts> rescuers, IReadOnlyList<RescuePatientFacts> patients)
    {
        static bool EligibleRescuer(RescuerFacts r)
        {
            if (!r.Dead.TryGetValue(out var dead) || !r.Downed.TryGetValue(out var downed)
                || !r.Drafted.TryGetValue(out var drafted) || !r.MentalState.TryGetValue(out var mental)
                || !r.ExistingJobDef.TryGetValue(out var existing))
            {
                return false;
            }
            return !dead && !downed && !drafted && !mental && existing != "Rescue";
        }

        static bool EligiblePatient(RescuePatientFacts p)
        {
            if (!p.Dead.TryGetValue(out var dead) || !p.Downed.TryGetValue(out var downed)
                || !p.InBed.TryGetValue(out var inBed) || !p.ExistingJobDef.TryGetValue(out var existing))
            {
                return false;
            }
            return !dead && downed && !inBed && existing != "Rescue";
        }

        var rescuerPool = rescuers.Where(EligibleRescuer).ToList();
        var patientPool = patients.Where(EligiblePatient).ToList();
        if (rescuerPool.Count == 0 || patientPool.Count == 0)
        {
            return (default, default, false);
        }
        var rescuer = rescuerPool.OrderBy(r => OrderedWorkCost(r.PlayerForced, r.QueuedJobs)).ThenBy(r => r.Pawn).First();
        var patient = patientPool.OrderBy(p => p.Pawn).First();
        return (rescuer.Pawn, patient.Pawn, true);
    }
}
