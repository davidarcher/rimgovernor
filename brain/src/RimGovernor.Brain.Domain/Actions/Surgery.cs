namespace RimGovernor.Brain.Domain;

public static partial class DomainConstants
{
    /// <summary>The part index of a whole-body recipe.</summary>
    public const int NoSurgeryPart = -1;
}

/// <summary>
/// Queues one medical operation bill on one patient: a medical ProductionBillIntent on Actions/Apply. Native
/// re-checks the patient, recipe and part live; native doctor jobs choose the surgeon. Applied means queued.
/// Carries the patient, the recipe defName, the body part index in the race body's AllParts
/// (<see cref="DomainConstants.NoSurgeryPart"/> for a whole-body recipe) and whether the planner accepted that
/// the recipe is a violation on the patient (organ harvest). Surgeon, when set, restricts the bill to that
/// colonist; null keeps vanilla's choice.
/// </summary>
public sealed record Surgery : IActionPayload
{
    public Surgery(PawnId pawn, string recipe, int part, bool acknowledgeViolation)
    {
        DomainException.Require(
            pawn.IsValid && Identity.IsValidText(recipe) && part >= DomainConstants.NoSurgeryPart,
            "surgery requires a patient, a recipe and a whole-body or exact part index"
        );
        Pawn = pawn;
        Recipe = recipe;
        Part = part;
        AcknowledgeViolation = acknowledgeViolation;
    }

    public PawnId Pawn { get; }

    public string Recipe { get; }

    public int Part { get; }

    public bool AcknowledgeViolation { get; }

    public PawnId? Surgeon { get; private init; }

    /// <summary>Restricts the bill to one colonist other than the patient.</summary>
    public Surgery WithSurgeon(PawnId surgeon)
    {
        DomainException.Require(surgeon.IsValid && surgeon != Pawn, "surgeon must be a colonist other than the patient");
        return this with { Surgeon = surgeon };
    }
}

public sealed partial record DomainAction
{
    public static DomainAction NewSurgeryAction(ActionId id, Surgery s) => Create(id, ActionKind.Surgery, s);

    public Surgery? Surgery => PayloadOf<Surgery>(ActionKind.Surgery);
}
