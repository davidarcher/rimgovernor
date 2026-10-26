using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Policy;

/// <summary>Which observed drafted colonists are free of current plan and custody work.</summary>
public static class DraftReleaseRules
{
    /// <summary>The drafted, live, sane colonists of <paramref name="rows"/> that no plan needs and that run no arrest or capture job, sorted.</summary>
    public static List<PawnId> UndraftCandidates(IEnumerable<RimGovernor.Brain.GameFacts.Observations.PawnState?> rows, HashSet<PawnId> needed,
        string arrestJob, string captureJob)
    {
        var output = new List<PawnId>();
        foreach (var row in rows)
        {
            if (row is null)
            {
                continue;
            }
            var id = new PawnId((row.Pawn.Id ?? ""));
            if (needed.Contains(id) || id.Value == "" || output.Contains(id))
            {
                continue;
            }
            if (!row.Colonist || !row.Drafted || row.Dead || row.Downed || row.MentalState is not null)
            {
                continue;
            }
            if (row.Job.DefName == arrestJob || row.Job.DefName == captureJob)
            {
                continue;
            }
            output.Add(id);
        }
        output.Sort((a, b) => string.CompareOrdinal(a.Value, b.Value));
        return output;
    }

    public static bool ReleaseHeld(EmergencyFacts facts) =>
        !facts.AllSeen || facts.Colonists.Count == 0 || facts.Threats.Any(threat => threat.Holds());
}
