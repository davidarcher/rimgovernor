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
    /// issues again), a Need's reads its whole <c>need_methods</c> history in its world, so a wave already committed
    /// for a target is not re-issued because its Method was released.
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
    /// Every Method <paramref name="owner"/> has bound, retired plans included: a Standard's current episode, a Need's
    /// every attempt in its world. Planners that number attempts or read past work count these, not the open ones.
    /// </summary>
    public IReadOnlyList<OwnedMethod> OwnerMethodHistory(IWorkOwner owner, CancellationToken cancellationToken)
    {
        switch (owner)
        {
            case StandardState standard:
                return [.. LoadOwnerMethods(standard, cancellationToken).Select(m => new OwnedMethod(m.MethodId, m.Plan))];
            case NeedOwner need:
                var rows = Run(tx => tx.Query(
                    "SELECT method_id,plan_id FROM need_methods WHERE colony=$colony AND load_token=$load AND map_id=$map AND need_id=$need ORDER BY rowid LIMIT 257",
                    [("$colony", need.Snapshot.Colony), ("$load", need.Snapshot.Load), ("$map", need.Snapshot.Map), ("$need", need.Need.Value)],
                    r => new OwnedMethod(new MethodId(r.GetString(0)), new PlanId(r.GetString(1)))), cancellationToken);
                return rows.Count > MaxOwnerHistory ? throw new DomainException("need method history exceeds bound") : rows;
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
