using RimGovernor.Brain.Domain;
using PolicyConcernProgress = RimGovernor.Brain.Policy.ConcernProgress;
using PolicyMoodHistory = RimGovernor.Brain.Policy.MoodHistory;

namespace RimGovernor.Brain.Store;

/// <summary>The lookups a review answers over its persisted bindings.</summary>
public static class ReviewMembers
{
    /// <summary>
    /// Asks the Safeguards whether this review admits combat work, returning the veto's reason or "". The
    /// <c>military/combat</c> Need is routine work and reports no Concern of its own, as its Method admission does.
    /// </summary>
    public static string VetoCombat(this ReviewState r) => r.VetoNeed(default);

    /// <summary>The persisted mood evidence read back in policy terms; empty when the review recorded none.</summary>
    public static PolicyMoodHistory MoodHistory(this ReviewState r) =>
        r.Mood is null ? new PolicyMoodHistory() : new PolicyMoodHistory { States = [.. r.Mood.States.Select(s => s.ToPolicy())] };

    /// <summary>The progress record for <paramref name="need"/>, or null.</summary>
    public static PolicyConcernProgress? ConcernProgress(this ReviewState r, FilingKey need)
    {
        foreach (var p in r.Progress)
        {
            if (p.Concern == need)
            {
                return p;
            }
        }
        return null;
    }
}
