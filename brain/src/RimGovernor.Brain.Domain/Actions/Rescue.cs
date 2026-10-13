namespace RimGovernor.Brain.Domain;

/// <summary>
/// Explicit intent. Neither pawn is drafted; native eligibility, current position/bed facts and job
/// availability are established at inspection, not here.
/// </summary>
public sealed record Rescue : IActionPayload
{
    public Rescue(PawnId rescuer, PawnId patient)
    {
        DomainException.Require(rescuer.IsValid && patient.IsValid && rescuer != patient, "rescue requires distinct valid rescuer and patient identities");
        Rescuer = rescuer;
        Patient = patient;
    }

    public PawnId Rescuer { get; }

    public PawnId Patient { get; }
}

public sealed partial record DomainAction
{
    public static DomainAction NewRescueAction(ActionId id, Rescue rescue) => Create(id, ActionKind.Rescue, rescue);

    public Rescue? Rescue => PayloadOf<Rescue>(ActionKind.Rescue);
}
