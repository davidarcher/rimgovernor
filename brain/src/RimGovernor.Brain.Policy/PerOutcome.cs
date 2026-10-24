using System.Runtime.CompilerServices;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// A value derived from one outcome the search evaluates (#3913, epic #3650 D1): its plan, the portfolio roots it chose, the
/// context and the tier-1 horizon. It is computed once per outcome and held as long as the outcome lives; a later read of the
/// same outcome under another context, horizon or chosen roots recomputes it. The work an outcome leaves short
/// (<see cref="JoinerMoodRules.ShortfallOf"/>) and the room credits priced against it (the prison's recruits, the classroom's
/// teacher) each keep one. A hit is one weak-table lookup, two reference/number compares and an in-order pass over the chosen
/// ids, with no allocation.
/// </summary>
internal sealed class PerOutcome<T>(Func<ForecastCoupledOutcome, IReadOnlyList<ForecastCommitment>, ForecastCoupledContext, double, T> compute)
    where T : class
{
    private sealed record Entry(ForecastCoupledContext Context, double Horizon, string[] Chosen, T Value);

    private readonly ConditionalWeakTable<ForecastCoupledOutcome, Entry> entries = new();

    /// <summary>The value of <paramref name="outcome"/> of the chosen <paramref name="commitments"/> over <paramref name="horizon"/>.</summary>
    public T Of(ForecastCoupledOutcome outcome, IReadOnlyList<ForecastCommitment> commitments, ForecastCoupledContext context, double horizon)
    {
        if (entries.TryGetValue(outcome, out var entry) && ReferenceEquals(entry.Context, context) && entry.Horizon == horizon && Same(entry.Chosen, commitments))
            return entry.Value;
        var value = compute(outcome, commitments, context, horizon);
        entries.AddOrUpdate(outcome, new(context, horizon, [.. commitments.Select(c => c.Id)], value));
        return value;
    }

    // The same chosen roots, in any order: in the stored order without allocating, otherwise as sorted sets.
    private static bool Same(string[] chosen, IReadOnlyList<ForecastCommitment> commitments)
    {
        if (chosen.Length != commitments.Count) return false;
        var inOrder = true;
        for (var i = 0; i < chosen.Length && inOrder; i++) inOrder = string.Equals(chosen[i], commitments[i].Id, StringComparison.Ordinal);
        return inOrder || chosen.Order(StringComparer.Ordinal).SequenceEqual(commitments.Select(c => c.Id).Order(StringComparer.Ordinal), StringComparer.Ordinal);
    }
}
