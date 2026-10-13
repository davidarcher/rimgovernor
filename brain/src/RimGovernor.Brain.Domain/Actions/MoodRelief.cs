namespace RimGovernor.Brain.Domain;

/// <summary>
/// Names EnsureMood's three ordinary native need-relief jobs (food/rest/joy). Mirrors policy.MoodNeed's wire
/// names; domain cannot import policy, so they are converted at the boundary.
/// </summary>
public enum MoodReliefNeed
{
    Food,
    Rest,
    Joy,
}

/// <summary>
/// Explicit intent to send one already-selected undrafted pawn to one ordinary native need-relief job
/// (EnsureMood). Native checks the pawn's eligibility, current job, timetable and need live when it applies
/// the intent.
/// </summary>
public sealed record MoodRelief : IActionPayload
{
    public MoodRelief(PawnId pawn, MoodReliefNeed need)
    {
        DomainException.Require(pawn.IsValid, "mood relief requires a valid pawn");
        DomainException.Require(Wire.IsDefined(need), "invalid mood relief need");
        Pawn = pawn;
        Need = need;
    }

    public PawnId Pawn { get; }

    public MoodReliefNeed Need { get; }
}

public sealed partial record DomainAction
{
    public static DomainAction NewMoodReliefAction(ActionId id, MoodRelief relief) => Create(id, ActionKind.MoodRelief, relief);

    public MoodRelief? MoodRelief => PayloadOf<MoodRelief>(ActionKind.MoodRelief);
}
