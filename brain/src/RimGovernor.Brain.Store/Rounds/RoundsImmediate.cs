using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Store;

public sealed partial class Store
{
    /// <summary>
    /// Overlays the scoped transaction on the prior record. The immediate review judges no Concern, so the prior
    /// record's goal, occurrence and no-op rows stand as they were; copying the untouched record retains each derived
    /// history's observation time, and absence from this inspection is never a recovery or a new observation.
    /// Returns the retained review and the result carrying the rows it binds (Go <c>retainOrdinaryRounds</c>).
    /// </summary>
    internal static (Rounds Review, RoundsResult Result) RetainOrdinaryRounds(StoreTransaction tx, Rounds r, Rounds previous, RoundsResult result)
    {
        var next = previous with
        {
            Revision = r.Revision,
            Snapshot = r.Snapshot,
            Tick = r.Tick,
            Enabled = r.Enabled,
            Immediate = true,
            Latches = r.Latches,
            // Combat is judged in every pass: the immediate review's standing replaces the last one.
            Combat = r.Combat,
        };
        // The previous record may be a disabled round, which files no mood proposals; an enabled record carries
        // one per mood state.
        next = next with { MoodMethods = EquatableArray.From(MoodProposals(next.MoodHistory())) };
        var standards = new List<StandardState>();
        foreach (var b in next.Standards)
        {
            standards.Add(LoadStandard(tx, b.Standard));
        }
        var incidents = new List<IncidentState>();
        foreach (var b in next.Incidents)
        {
            incidents.Add(LoadIncident(tx, b.Incident));
        }
        return (next, result with { Standards = standards, Incidents = incidents });
    }
}
