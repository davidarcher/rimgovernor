using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Store;

public sealed partial class Store
{
    /// <summary>The previous rows an immediate review does not own, then the fresh rows (Go <c>mergeImmediateRows</c>).</summary>
    internal static EquatableArray<T> MergeImmediateRows<T>(EquatableArray<T> old, EquatableArray<T> fresh, Func<T, ConcernId> concern)
    {
        var merged = new List<T>();
        foreach (var row in old)
        {
            if (!PolicyFunctions.ImmediateConcern(concern(row)))
            {
                merged.Add(row);
            }
        }
        merged.AddRange(fresh);
        return EquatableArray.From(merged);
    }

    /// <summary>
    /// Overlays the scoped transaction on the prior record. Copying the untouched record retains each derived
    /// history's observation time; absence from this inspection is never a recovery or a new observation. Returns
    /// the retained review and the result carrying the rows it binds (Go <c>retainOrdinaryRounds</c>).
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
            Standards = MergeImmediateRows(previous.Standards, r.Standards, v => v.Concern),
            Incidents = MergeImmediateRows(previous.Incidents, r.Incidents, v => v.Kind),
            Emergency = MergeImmediateRows(previous.Emergency, r.Emergency, v => v),
            NoOps = MergeImmediateRows(previous.NoOps, r.NoOps, v => v.Concern),
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
        return (next, result with { Standards = standards, Incidents = incidents, Emergency = [.. next.Emergency] });
    }
}
