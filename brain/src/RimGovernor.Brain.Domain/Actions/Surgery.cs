namespace RimGovernor.Brain.Domain;

public static class SurgeryConstants
{
    /// <summary>The part index of a whole-body recipe.</summary>
    public const int NoSurgeryPart = -1;
}

/// <summary>
/// Queues one medical operation bill on one patient: a medical ProductionBillIntent on Actions/Apply. Native
/// re-checks the patient, recipe and part live; native doctor jobs choose the surgeon. Applied means queued.
/// Carries the patient, the recipe defName, the body part index in the race body's AllParts
/// (<see cref="SurgeryConstants.NoSurgeryPart"/> for a whole-body recipe) and whether the planner accepted that
/// the recipe is a violation on the patient (organ harvest).
/// </summary>
public sealed record Surgery : IActionPayload
{
    public Surgery(PawnId pawn, string recipe, int part, bool acknowledgeViolation)
    {
        DomainException.Require(
            pawn.IsValid && IdText.IsValidText(recipe) && part >= SurgeryConstants.NoSurgeryPart,
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
}

public sealed partial record DomainAction
{
    public static DomainAction NewSurgeryAction(ActionId id, Surgery s) => Create(id, ActionKind.Surgery, s);

    public Surgery? Surgery => PayloadOf<Surgery>(ActionKind.Surgery);
}
