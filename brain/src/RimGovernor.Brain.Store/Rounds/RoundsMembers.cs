using RimGovernor.Brain.Domain;
using DomainIncident = RimGovernor.Brain.Domain.Incident;
using PolicyConcernProgress = RimGovernor.Brain.Policy.ConcernProgress;
using PolicyMoodHistory = RimGovernor.Brain.Policy.MoodHistory;

namespace RimGovernor.Brain.Store;

/// <summary>The lookups a review answers over its persisted bindings (Go <c>Rounds</c> methods).</summary>
public static class RoundsMembers
{
    /// <summary>The need the review binds <paramref name="goal"/> to; false when it binds none (a player goal).</summary>
    public static bool Need(this Rounds r, StandardId goal, out ConcernId need)
    {
        foreach (var binding in r.Standards)
        {
            if (binding.Standard == goal)
            {
                need = binding.Concern;
                return true;
            }
        }
        need = default;
        return false;
    }

    /// <summary>The Standard the review binds to <paramref name="need"/>.</summary>
    public static bool StandardFor(this Rounds r, ConcernId need, out StandardId standard)
    {
        foreach (var binding in r.Standards)
        {
            if (binding.Concern == need)
            {
                standard = binding.Standard;
                return true;
            }
        }
        standard = default;
        return false;
    }

    /// <summary>
    /// Asks the policy Safeguards whether this review admits a proposal for the goal, returning the veto's reason
    /// or "". A goal the review does not bind (a player goal) is outside the routine Safeguards.
    /// </summary>
    public static string Veto(this Rounds r, Standard g) => r.Need(g.Id, out var need) ? r.VetoNeed(need, g.Priority) : "";

    /// <summary>Asks the Safeguards whether this review admits a proposal for the incident, returning the veto's reason or "".</summary>
    public static string VetoIncident(this Rounds r, DomainIncident i) => r.VetoNeed(i.Kind, i.Priority);

    /// <summary>The review's colony-wide binding for <paramref name="kind"/>, or null.</summary>
    public static RoundsIncident? Incident(this Rounds r, ConcernId kind)
    {
        foreach (var b in r.Incidents)
        {
            if (b.Kind == kind && b.Subject == default)
            {
                return b;
            }
        }
        return null;
    }

    /// <summary>The review's bindings for <paramref name="kind"/>'s per-subject occurrences (EnsureMood: one per pawn), in assessment order.</summary>
    public static List<RoundsIncident> SubjectIncidents(this Rounds r, ConcernId kind)
    {
        var output = new List<RoundsIncident>();
        foreach (var b in r.Incidents)
        {
            if (b.Kind == kind && b.Subject != default)
            {
                output.Add(b);
            }
        }
        return output;
    }

    /// <summary>The review's binding for an incident id, or null.</summary>
    public static RoundsIncident? IncidentBinding(this Rounds r, IncidentId id)
    {
        foreach (var b in r.Incidents)
        {
            if (b.Incident == id)
            {
                return b;
            }
        }
        return null;
    }

    /// <summary>The persisted mood evidence read back in policy terms; empty when the review recorded none.</summary>
    public static PolicyMoodHistory MoodHistory(this Rounds r) =>
        r.Mood is null ? new PolicyMoodHistory() : new PolicyMoodHistory { States = [.. r.Mood.States.Select(s => s.ToPolicy())] };

    /// <summary>The progress record for <paramref name="need"/>, or null.</summary>
    public static PolicyConcernProgress? ConcernProgress(this Rounds r, ConcernId need)
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
