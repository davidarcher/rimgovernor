namespace RimGovernor.Brain.Domain;

/// <summary>
/// Explicit intent: the rescuer carries the downed patient to the exact bed the care comparison chose (vanilla Rescue,
/// JobDriver_TakeToBed with the bed as its second target). Neither pawn is drafted; native eligibility and whether the
/// bed is a valid bed for that patient and rescuer are established at apply, not here.
/// </summary>
public sealed record Rescue : IActionPayload
{
    public Rescue(PawnId rescuer, PawnId patient, string bed)
    {
        DomainException.Require(rescuer.IsValid && patient.IsValid && rescuer != patient, "rescue requires distinct valid rescuer and patient identities");
        DomainException.Require(IdText.IsValidText(bed) && bed != patient.Value && bed != rescuer.Value, "rescue requires the exact bed");
        Rescuer = rescuer;
        Patient = patient;
        Bed = bed;
    }

    public PawnId Rescuer { get; }

    public PawnId Patient { get; }

    /// <summary>The bed the patient is carried to.</summary>
    public string Bed { get; }
}

/// <summary>
/// Explicit intent: a walking patient the player controls lies down in the exact bed the care comparison chose (vanilla
/// LayDown on that bed, ordered to the patient itself). Native refuses a drafted, downed or uncontrolled patient and a bed
/// that is no valid bed for it at apply.
/// </summary>
public sealed record BedRest : IActionPayload
{
    public BedRest(PawnId patient, string bed)
    {
        DomainException.Require(patient.IsValid && IdText.IsValidText(bed) && bed != patient.Value, "bed rest requires a valid patient and its exact bed");
        Patient = patient;
        Bed = bed;
    }

    public PawnId Patient { get; }

    /// <summary>The bed the patient walks to and lies down in.</summary>
    public string Bed { get; }
}

public sealed partial record DomainAction
{
    public static DomainAction NewRescueAction(ActionId id, Rescue rescue) => Create(id, ActionKind.Rescue, rescue);

    public Rescue? Rescue => PayloadOf<Rescue>(ActionKind.Rescue);

    public static DomainAction NewBedRestAction(ActionId id, BedRest rest) => Create(id, ActionKind.BedRest, rest);

    public BedRest? BedRest => PayloadOf<BedRest>(ActionKind.BedRest);
}
