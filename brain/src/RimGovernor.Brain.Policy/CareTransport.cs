using RimGovernor.Brain.GameFacts.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// The move of one care patient to the bed the care capacity comparison chose for it (task 4.3 of
/// <c>redefine-habitat-requirements</c>), as the typed native operation that does it: a downed patient
/// (<see cref="CareMovement.Carried"/>) is carried there by a rescue, a walking patient the player controls
/// (<see cref="CareMovement.Walk"/>) lies down there itself. A walking patient that cannot be ordered (a prisoner,
/// guest or animal) has no transport: the comparison excludes that bed for it (<see cref="CareTransportRules.NotOrderable"/>).
/// </summary>
public abstract record CareTransport(string Patient, string Bed)
{
    /// <summary>A rescue to the exact bed (vanilla Rescue, the bed its second target).</summary>
    public sealed record Carried(string Patient, string Bed) : CareTransport(Patient, Bed);

    /// <summary>The patient's own lay-down in the exact bed (vanilla LayDown).</summary>
    public sealed record Walk(string Patient, string Bed) : CareTransport(Patient, Bed);
}

/// <summary>
/// A bed one patient's transport holds: the transport is open, or its last attempt was accepted under the current native
/// authority, and the census still lists the patient as a care patient lying in no other bed.
/// </summary>
public sealed record CareBedHold(string Patient, string Bed);

/// <summary>
/// One transport the journal holds, as the hold derivation reads it: the patient, the bed, and whether it still holds (open, or
/// accepted under the current native authority). A refused or superseded attempt holds nothing.
/// </summary>
public sealed record IssuedCareTransport(string Patient, string Bed, bool Holding);

/// <summary>A transport the native refused in the current world under the refusal budget: a fact the comparison reads, never retried there.</summary>
public sealed record RefusedCareTransport(string Patient, string Bed, string Reason);

public static class CareTransportRules
{
    /// <summary>The exclusion prefix of a bed a walking patient that cannot be ordered would have to walk to; the occupant follows.</summary>
    public const string NotOrderable = "patient_not_orderable";

    /// <summary>The exclusion of a bed another patient's transport holds.</summary>
    public const string HeldByPatient = "bed_held_by_patient";

    /// <summary>Whether a walking patient of this occupant can be ordered to lie down: a colonist or slave the player controls.</summary>
    public static bool Orderable(BedOccupant occupant) => occupant is BedOccupant.Colonist or BedOccupant.Slave;

    /// <summary>
    /// The transports a comparison asks for, in decision order: each patient whose chosen alternative is a ready medical bed it
    /// does not lie in (<see cref="CareCapacityKind.Reuse"/>, or its placed field spot once it reads as medical), carried when
    /// downed and walking when it can be ordered. A conversion or a spot is not moved to before its bed reads as medical; an
    /// in-place or deferred choice moves nobody, and neither does a stabilizing tend (<see cref="CareCapacityKind.Stabilize"/>):
    /// its transfer is the choice of a later review that reads the patient recovering.
    /// </summary>
    public static IReadOnlyList<CareTransport> Of(CareCapacityPlan plan, SupportActivityRead support)
    {
        var transports = new List<CareTransport>();
        foreach (var decision in plan.Decisions)
        {
            if (decision.Chosen.Kind is not (CareCapacityKind.Reuse or CareCapacityKind.FieldSpot)) continue;
            var patient = support.Patients.Single(p => p.Demand == decision.Demand);
            if (patient.Beds.FirstOrDefault(b => b.Id == decision.Chosen.Bed) is not { Medical: true } bed) continue;
            if (bed.Movement is CareMovement.Carried)
                transports.Add(new CareTransport.Carried(patient.Patient, bed.Id));
            else if (bed.Movement is CareMovement.Walk && Orderable(patient.Occupant))
                transports.Add(new CareTransport.Walk(patient.Patient, bed.Id));
        }
        return transports;
    }

    /// <summary>
    /// The beds the issued transports hold now (Decision 4: re-derived each review, no owner store). A transport holds its bed
    /// while it is open or accepted under the current authority, and only while its patient is still one of the census's care
    /// patients and lies in no other bed: a patient healed, dead, gone from the map or no longer the colony's responsibility
    /// (a guest departed, a prisoner released, a slave freed) leaves the census and releases its own bed, and nobody else's.
    /// The latest issued transport of each patient is the one read; a doctor or rescuer changing or being drafted releases nothing.
    /// </summary>
    public static IReadOnlyList<CareBedHold> Holds(IEnumerable<IssuedCareTransport> issued, SupportActivityRead support)
    {
        var latest = new Dictionary<string, IssuedCareTransport>(StringComparer.Ordinal);
        foreach (var t in issued) latest[t.Patient] = t;
        var holds = new List<CareBedHold>();
        foreach (var t in latest.Values.Where(t => t.Holding).OrderBy(t => t.Patient, StringComparer.Ordinal))
        {
            if (support.Patients.FirstOrDefault(p => p.Patient == t.Patient) is not { } patient) continue;
            if (patient.Posture is CarePosture.InBed(var lying) && lying != t.Bed) continue;
            holds.Add(new(t.Patient, t.Bed));
        }
        return holds;
    }
}
