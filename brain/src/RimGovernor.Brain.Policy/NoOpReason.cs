using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// Says why an inspection raised nothing in a review: a closed vocabulary, so a silent no-op is never read as
/// "satisfied". <see cref="None"/> is Go's empty string and is not a valid reason.
/// </summary>
public enum NoOpReason
{
    [WireName("")]
    None = 0,

    /// <summary>The facts the detector needs were not measured, so the need is unknown, not recovered.</summary>
    InputsUnknown,

    /// <summary>The detector filed no assessment (its precondition does not hold: no such need, no such pawn, no disaster history).</summary>
    NotApplicable,

    /// <summary>The facts are measured and the need is met.</summary>
    Satisfied,
}

public static class NoOpReasonExtensions
{
    /// <summary>Refuses a reason outside the vocabulary.</summary>
    public static void Validate(this NoOpReason r)
    {
        if (r is not (NoOpReason.InputsUnknown or NoOpReason.NotApplicable or NoOpReason.Satisfied))
        {
            throw new DomainException($"unknown no-op reason \"{Wire.Name(r)}\"");
        }
    }
}

/// <summary>One detector's recorded no-op in the review.</summary>
public sealed record NoOpRecord(ConcernId Concern, NoOpReason Reason);

public static partial class PolicyFunctions
{
    /// <summary>
    /// Classifies what a detector left on the run: it is a no-op when it raised no goal and filed no deficit
    /// assessment for its goal. The reason follows the assessments it filed: none is not_applicable, any
    /// unknown is inputs_unknown, otherwise satisfied.
    /// </summary>
    internal static NoOpRecord? NoOpOf(ConcernId id, IReadOnlyList<RoundsConcern> goals, IReadOnlyList<RoundsAssessment> assessments)
    {
        var reason = NoOpReason.NotApplicable;
        if (goals.Any(g => g.Id == id))
        {
            return null;
        }
        foreach (var a in assessments)
        {
            if (a.Id != id)
            {
                continue;
            }
            switch (a.Finding)
            {
                case Finding.Unmet:
                    return null;
                case Finding.Unclear:
                    reason = NoOpReason.InputsUnknown;
                    break;
                default:
                    if (reason != NoOpReason.InputsUnknown)
                    {
                        reason = NoOpReason.Satisfied;
                    }
                    break;
            }
        }
        return new NoOpRecord(id, reason);
    }
}
