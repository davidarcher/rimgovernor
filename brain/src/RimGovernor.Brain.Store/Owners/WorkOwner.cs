using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Store;

/// <summary>One Method an owner binds: its MethodId and the plan that executes it.</summary>
public sealed record OwnedMethod(MethodId Method, PlanId Plan);

/// <summary>
/// The journal owner a shared planner commits under (#3279): a <see cref="StandardState"/> as loaded, whose
/// revision scopes the commit and whose episode scopes its Methods, or a <see cref="NeedOwner"/> in its world,
/// admitted by claims. The building planner looks up and commits only through the Store's owner calls below, so
/// it builds the same under either.
/// </summary>
public interface IWorkOwner
{
    /// <summary>The owner's id in telemetry: the Standard id or the NeedId.</summary>
    string OwnerKey { get; }

    /// <summary>The owner's Methods whose plans are not retired, in admission order.</summary>
    IReadOnlyList<OwnedMethod> OpenMethods { get; }
}

/// <summary>How an owner commit ended: admitted, continued (the MethodId is open work already) or held on another Method's claim.</summary>
public sealed record WorkCommit(ClaimOutcome Outcome, ClaimConflict? Conflict = null);

public sealed partial class Store
{
    private const int MaxOwnerHistory = 256;

    /// <summary>
    /// The plan <paramref name="owner"/> last bound <paramref name="method"/> to, retired plans included; null when it
    /// never did. This is the planner's once-per-target key: a Standard's reads its current episode (a new episode
    /// issues again), a Need's reads its latest attempt in its world. A Need's MethodId is admitted again only once
    /// that attempt is released and what it built is gone per the family's facts (decision 19); the building
    /// planner's <c>MethodOnce</c> judges that, and nothing about it is stored.
    /// </summary>
    public PlanId? OwnerMethodPlan(IWorkOwner owner, MethodId method, CancellationToken cancellationToken)
    {
        switch (owner)
        {
            case StandardState standard:
                try
                {
                    return LoadOwnerMethod(standard, method, cancellationToken).Plan;
                }
                catch (NotFoundException)
                {
                    return null;
                }
            case NeedOwner need:
                return Run(tx =>
                {
                    var (plan, found) = tx.QueryFirst(
                        "SELECT plan_id FROM need_methods WHERE colony=$colony AND load_token=$load AND map_id=$map AND need_id=$need AND method_id=$method ORDER BY attempt DESC LIMIT 1",
                        [("$colony", need.Snapshot.Colony), ("$load", need.Snapshot.Load), ("$map", need.Snapshot.Map), ("$need", need.Need.Value), ("$method", method)],
                        r => new PlanId(r.GetString(0)));
                    return found ? plan : (PlanId?)null;
                }, cancellationToken);
            default:
                throw new DomainException("unsupported work owner");
        }
    }

    /// <summary>
    /// The Methods <paramref name="owner"/> has bound, retired plans included, in admission order: a Standard's current
    /// episode; a Need's latest attempt of each MethodId in its world, over its <see cref="MaxOwnerHistory"/> most
    /// recently bound MethodIds. A Need's rows are never trimmed and its MethodIds are re-issued (decision 19) or minted
    /// per subject, so its whole history is unbounded over a long game (#3310): this is the recent past work
    /// planners read (stages, departures, cooling windows). A budget or once-check over one MethodId reads
    /// <see cref="OwnerMethodPlan"/>, which is exact at any history length.
    /// </summary>
    public IReadOnlyList<OwnedMethod> OwnerMethodHistory(IWorkOwner owner, CancellationToken cancellationToken)
    {
        switch (owner)
        {
            case StandardState standard:
                return [.. LoadOwnerMethods(standard, cancellationToken).Select(m => new OwnedMethod(m.MethodId, m.Plan))];
            case NeedOwner need:
                return Run(tx => tx.Query(
                    """
                    SELECT method_id,plan_id FROM (
                        SELECT m.rowid AS bound,m.method_id,m.plan_id FROM need_methods m
                        WHERE m.colony=$colony AND m.load_token=$load AND m.map_id=$map AND m.need_id=$need
                          AND m.attempt=(SELECT MAX(l.attempt) FROM need_methods l WHERE l.colony=m.colony AND l.load_token=m.load_token AND l.map_id=m.map_id AND l.need_id=m.need_id AND l.method_id=m.method_id)
                        ORDER BY m.rowid DESC LIMIT $limit)
                    ORDER BY bound
                    """,
                    [("$colony", need.Snapshot.Colony), ("$load", need.Snapshot.Load), ("$map", need.Snapshot.Map), ("$need", need.Need.Value), ("$limit", MaxOwnerHistory)],
                    r => new OwnedMethod(new MethodId(r.GetString(0)), new PlanId(r.GetString(1)))), cancellationToken);
            default:
                throw new DomainException("unsupported work owner");
        }
    }

    /// <summary>
    /// Commits <paramref name="plan"/> as <paramref name="method"/> of <paramref name="owner"/>. A Standard commits as
    /// <see cref="CommitOwnerMethod"/> does (the open-work gate, its revision compare-and-swap, exceptions unchanged)
    /// and is always <see cref="ClaimOutcome.Admitted"/> when it returns. A Need commits through claim admission
    /// (<see cref="CommitClaimedMethod(NeedId, GenerationSnapshot, MethodId, string, PlanSpec, CancellationToken)"/>):
    /// an open MethodId continues and a held claim is <see cref="ClaimOutcome.Conflict"/>, with nothing written.
    /// </summary>
    public WorkCommit CommitOwnerWork(IWorkOwner owner, MethodId method, string reason, PlanSpec plan, CancellationToken cancellationToken)
    {
        switch (owner)
        {
            case StandardState standard:
                _ = CommitMethodReason(standard.Standard.Id, standard.Revision, method, reason, plan, cancellationToken);
                return new(ClaimOutcome.Admitted);
            case NeedOwner need:
                var commit = CommitClaimedMethod(need.Need, need.Snapshot, method, reason, plan, cancellationToken);
                return new(commit.Outcome, commit.Conflict);
            default:
                throw new DomainException("unsupported work owner");
        }
    }
}
