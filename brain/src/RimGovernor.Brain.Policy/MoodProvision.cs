using RimGovernor.Brain.Domain;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// One grouped native thought row (the Mood tab's stacking) with its total mood offset; the census keeps
/// negative and positive rows (a memory like KnowBuriedInSarcophagus is positive).
/// </summary>
public sealed record MoodThought(string Def, double Offset);

/// <summary>
/// One upkeep goal whose facility would remove the pawn's observed environment thought pressure, with the
/// summed offset of the thoughts it owns (most negative first in MoodState.Provision).
/// </summary>
public sealed record MoodProvision(ConcernId Concern, double Offset);

public static partial class PolicyFunctions
{
    private static List<MoodProvision> SortProvision(IEnumerable<MoodProvision> rows) =>
        [.. rows.OrderBy(r => r.Offset).ThenBy(r => r.Concern)];

    /// <summary>
    /// Better meals can offset high-expectation mood pressure; they do not remove the expectation itself.
    /// Only an observed deficit requests this lever.
    /// </summary>
    internal static IReadOnlyList<MoodProvision> MealMoodProvision(MoodPawn p, IReadOnlyList<MoodProvision> rows)
    {
        if (!p.HighExpectations.TryGetValue(out var high) || !high
            || !p.Mood.TryGetValue(out var mood) || !p.Target.TryGetValue(out var target) || mood >= target)
        {
            return rows;
        }
        if (rows.Any(row => row.Concern == EnsureCooking))
        {
            return rows;
        }
        var result = rows.ToList();
        result.Add(new MoodProvision(EnsureCooking, -Math.Max(1, (target - mood) * 100)));
        return SortProvision(result);
    }

    internal static void ValidateMoodThoughts(Fact<EquatableArray<MoodThought>> f)
    {
        if (!f.TryGetValue(out var rows))
        {
            return;
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in rows)
        {
            if (!FoodId(t.Def) || !seen.Add(t.Def) || !double.IsFinite(t.Offset))
            {
                throw new DomainException("invalid mood thought");
            }
        }
    }

    internal static void ValidateMoodProvision(IReadOnlyList<MoodProvision> rows)
    {
        if (rows.Count > 8)
        {
            throw new DomainException("mood provision exceeds bound");
        }
        var seen = new HashSet<ConcernId>();
        for (var i = 0; i < rows.Count; i++)
        {
            var p = rows[i];
            if (p.Concern.Value.Length == 0 || seen.Contains(p.Concern) || !double.IsFinite(p.Offset) || p.Offset >= 0)
            {
                throw new DomainException("invalid mood provision");
            }
            if (i > 0 && rows[i - 1].Offset > p.Offset)
            {
                throw new DomainException("mood provision out of order");
            }
            seen.Add(p.Concern);
        }
    }

    /// <summary>
    /// Reduces a pawn's observed negative thoughts to the owner goals of its removable environment thoughts.
    /// The result is empty unless those thoughts dominate: they carry at least half of the pawn's total
    /// negative thought offset. Unknown thoughts provision nothing.
    /// </summary>
    internal static IReadOnlyList<MoodProvision> MoodProvisioning(Fact<EquatableArray<MoodThought>> f)
    {
        if (!f.TryGetValue(out var rows))
        {
            return [];
        }
        double total = 0, owned = 0;
        var byConcern = new Dictionary<ConcernId, double>();
        foreach (var t in rows)
        {
            if (t.Offset >= 0)
            {
                continue;
            }
            total += t.Offset;
            var owners = ThoughtOwners(t.Def);
            if (owners.Count > 0)
            {
                owned += t.Offset;
            }
            foreach (var goal in owners)
            {
                byConcern[goal] = byConcern.GetValueOrDefault(goal) + t.Offset;
            }
        }
        if (owned >= 0 || owned > total / 2)
        {
            return [];
        }
        return SortProvision(byConcern.Select(kv => new MoodProvision(kv.Key, kv.Value)));
    }

    /// <summary>
    /// Reports, per owner goal, the ledger-weighted share of reviewed pawns under the entry margin: a pawn at
    /// or below threshold plus MoodEntryMargin weighs the fraction of its negative thought loss the owner can
    /// remove; a pawn above the margin weighs 0. A pawn whose loss, mood or threshold is unreadable is left
    /// out of the denominator, never counted as zero. DetectRounds raises the owner's development deficit to
    /// at least this.
    /// </summary>
    public static IReadOnlyDictionary<ConcernId, double> MoodProvisionDeficits(IReadOnlyList<MoodPawn> census, MoodLedger ledger)
    {
        var byId = new Dictionary<PawnId, MoodPawn>();
        foreach (var p in census)
        {
            byId[p.Id] = p;
        }
        var read = 0;
        var weights = new Dictionary<ConcernId, double>();
        foreach (var lp in ledger.Pawns)
        {
            if (!lp.Lost.TryGetValue(out var lost) || !byId.TryGetValue(lp.Id, out var p)
                || !p.Mood.TryGetValue(out var mood) || !p.Threshold.TryGetValue(out var threshold))
            {
                continue;
            }
            read++;
            if (mood > threshold + MoodEntryMargin)
            {
                continue;
            }
            double total = 0;
            var byConcern = new Dictionary<ConcernId, double>();
            foreach (var t in lost)
            {
                total += t.Offset;
                foreach (var goal in ThoughtOwners(t.Def))
                {
                    byConcern[goal] = byConcern.GetValueOrDefault(goal) + t.Offset;
                }
            }
            if (total >= 0)
            {
                continue;
            }
            foreach (var (goal, offset) in byConcern)
            {
                weights[goal] = weights.GetValueOrDefault(goal) + offset / total;
            }
        }
        if (read == 0 || weights.Count == 0)
        {
            return new Dictionary<ConcernId, double>();
        }
        foreach (var goal in weights.Keys.ToList())
        {
            weights[goal] /= read;
        }
        return weights;
    }
}

public static class MoodStateProvisionExtensions
{
    /// <summary>
    /// The same state with its provisioning dropped: the relief planner falls back to it when no owner goal
    /// is active to provision.
    /// </summary>
    public static MoodState WithoutProvision(this MoodState s) => s with { Provision = [] };
}
