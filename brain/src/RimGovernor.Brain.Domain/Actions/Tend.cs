namespace RimGovernor.Brain.Domain;

/// <summary>
/// Explicit intent. The patient is never drafted; the doctor may be drafted (the controller's own fallback
/// when no undrafted doctor exists). Native eligibility, current health facts and job availability are
/// established at inspection, not here.
/// </summary>
public sealed record Tend : IActionPayload
{
    public Tend(PawnId doctor, PawnId patient)
    {
        DomainException.Require(doctor.IsValid && patient.IsValid && doctor != patient, "tend requires distinct valid doctor and patient identities");
        Doctor = doctor;
        Patient = patient;
    }

    public PawnId Doctor { get; }

    public PawnId Patient { get; }
}

public sealed partial record DomainAction
{
    public static DomainAction NewTendAction(ActionId id, Tend tend) => Create(id, ActionKind.Tend, tend);

    public Tend? Tend => PayloadOf<Tend>(ActionKind.Tend);
}
