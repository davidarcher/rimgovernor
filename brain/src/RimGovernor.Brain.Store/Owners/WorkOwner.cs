using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Store;

/// <summary>One Method an owner binds: its MethodId and the plan that executes it.</summary>
public sealed record OwnedMethod(MethodId Method, PlanId Plan);

/// <summary>How an owner commit ended: admitted, continued (the MethodId is open work already), held on another Method's claim or refused by admission.</summary>
public sealed record WorkCommit(ClaimOutcome Outcome, ClaimConflict? Conflict = null, AdmissionRefusal? Refusal = null);

public sealed partial class Store
{
    private const int MaxOwnerHistory = 256;

    /// <summary>
    /// The plan <paramref name="owner"/> last bound <paramref name="method"/> to, retired plans included; null when it
    /// never did. This is the planner's once-per-target key: the Need's latest attempt in its world. A MethodId is admitted again only once
    /// that attempt is released and what it built is gone per the family's facts (decision 19); the building
    /// planner's <c>MethodOnce</c> judges that, and nothing about it is stored.
    /// </summary>
    public PlanId? OwnerMethodPlan(NeedOwner owner, MethodId method, CancellationToken cancellationToken)
    {
        return Run(tx =>
        {
            var (plan, found) = tx.QueryFirst(
                "SELECT plan_id FROM need_methods WHERE colony=$colony AND load_token=$load AND map_id=$map AND need_id=$need AND method_id=$method ORDER BY attempt DESC LIMIT 1",
                [("$colony", owner.Snapshot.Colony), ("$load", owner.Snapshot.Load), ("$map", owner.Snapshot.Map), ("$need", owner.Need.Value), ("$method", method)],
                r => new PlanId(r.GetString(0)));
            return found ? plan : (PlanId?)null;
        }, cancellationToken);
    }

    /// <summary>
    /// The Methods <paramref name="owner"/> has bound, retired plans included, in admission order: the Need's latest
    /// attempt of each MethodId in its world, over its <see cref="MaxOwnerHistory"/> most
    /// recently bound MethodIds. A Need's rows are never trimmed and its MethodIds are re-issued (decision 19) or minted
    /// per subject, so its whole history is unbounded over a long game (#3310): this is the recent past work
    /// planners read (stages, departures, cooling windows). A budget or once-check over one MethodId reads
    /// <see cref="OwnerMethodPlan"/>, which is exact at any history length.
    /// </summary>
    public IReadOnlyList<OwnedMethod> OwnerMethodHistory(NeedOwner owner, CancellationToken cancellationToken)
    {
        return Run(tx => tx.Query(
            """
            SELECT method_id,plan_id FROM (
                SELECT m.rowid AS bound,m.method_id,m.plan_id FROM need_methods m
                WHERE m.colony=$colony AND m.load_token=$load AND m.map_id=$map AND m.need_id=$need
                  AND m.attempt=(SELECT MAX(l.attempt) FROM need_methods l WHERE l.colony=m.colony AND l.load_token=m.load_token AND l.map_id=m.map_id AND l.need_id=m.need_id AND l.method_id=m.method_id)
                ORDER BY m.rowid DESC LIMIT $limit)
            ORDER BY bound
            """,
            [("$colony", owner.Snapshot.Colony), ("$load", owner.Snapshot.Load), ("$map", owner.Snapshot.Map), ("$need", owner.Need.Value), ("$limit", MaxOwnerHistory)],
            r => new OwnedMethod(new MethodId(r.GetString(0)), new PlanId(r.GetString(1)))), cancellationToken);
    }

    /// <summary>
    /// Commits <paramref name="plan"/> as <paramref name="method"/> of <paramref name="owner"/> through claim admission
    /// (<see cref="CommitClaimedMethod(NeedId, GenerationSnapshot, MethodId, string, PlanSpec, CancellationToken)"/>):
    /// an open MethodId continues and a held claim is <see cref="ClaimOutcome.Conflict"/>, with nothing written.
    /// </summary>
    public WorkCommit CommitOwnerWork(NeedOwner owner, MethodId method, string reason, PlanSpec plan, CancellationToken cancellationToken)
    {
        var commit = CommitClaimedMethod(owner.Need, owner.Snapshot, method, reason, plan, cancellationToken);
        return new(commit.Outcome, commit.Conflict, commit.Refusal);
    }
}
