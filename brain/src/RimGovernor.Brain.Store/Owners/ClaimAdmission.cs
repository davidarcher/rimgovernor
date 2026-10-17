using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Store;

/// <summary>How a claimed Method commit ended (#3249).</summary>
public enum ClaimOutcome
{
    /// <summary>The Method was bound and the owner's revision bumped.</summary>
    Admitted,

    /// <summary>The MethodId is already bound to open work: the request continues that Method and opens nothing new.</summary>
    Continued,

    /// <summary>The Method's claims overlap an open Method's; nothing was written.</summary>
    Conflict,
}

/// <summary>The open Method holding a claim the candidate asked for: its owner (<c>need &lt;NeedId&gt;</c>), MethodId and the claim.</summary>
public sealed record ClaimConflict(string Holder, MethodId Method, string Claim)
{
    /// <summary>The planner reason a refusal files.</summary>
    public string Reason => "claim_conflict: " + Claim + " held by " + Holder + " method " + Method.Value;
}

/// <summary>A Need's claimed commit result (#3250): the Need's owner after the commit and, on <see cref="ClaimOutcome.Conflict"/>, the holder.</summary>
public sealed record NeedCommit(ClaimOutcome Outcome, NeedOwner Owner, ClaimConflict? Conflict = null);

public sealed partial class Store
{
    private const int MaxClaimPlans = 1024;

    /// <summary>
    /// A Need's claimed commit (#3249, #3250): every Method is admitted through claim overlap. The Need was derived from <paramref name="current"/>, which must be the current enabled review's world.
    /// A MethodId whose latest attempt is still open work continues it (<see cref="ClaimOutcome.Continued"/>); one
    /// that was released is admitted again as the next attempt. Then the Safeguards, the claim overlap with every
    /// open Method in the world (<see cref="ClaimOutcome.Conflict"/>, nothing written), the family admission checks
    /// and binding run.
    /// </summary>
    public NeedCommit CommitClaimedMethod(NeedId need, GenerationSnapshot current, MethodId method, string reason, PlanSpec plan, CancellationToken cancellationToken)
    {
        need.Validate();
        if (!method.IsValid) throw new DomainException("invalid need method id");
        if (plan.Actions.Count == 0) throw new DomainException("empty need method");
        return Run(tx => CommitClaimedNeedMethodIn(tx, need, current, method, reason, plan), cancellationToken);
    }

    private static NeedCommit CommitClaimedNeedMethodIn(StoreTransaction tx, NeedId need, GenerationSnapshot current, MethodId method, string reason, PlanSpec plan)
    {
        var review = LoadRounds(tx);
        if (!review.Enabled || review.Snapshot != current)
            throw new NotAdmittedException($"need {need.Value} has no current enabled review");
        var owner = LoadNeedIn(tx, need, current);
        IMethodOwner held = owner;
        if (held.BoundPlan(tx, method) is not null) return new(ClaimOutcome.Continued, owner);
        AdmitRoundsSafeguards(tx, owner);
        if (ClaimOverlapIn(tx, current, plan) is { } conflict) return new(ClaimOutcome.Conflict, owner, conflict);
        BindOwnerMethod(tx, owner, method, reason, plan);
        return new(ClaimOutcome.Admitted, LoadNeedIn(tx, need, current));
    }

    /// <summary>
    /// The first open Method in <paramref name="current"/>'s world whose claims overlap <paramref name="plan"/>'s,
    /// in plan order. Footprints come from the current review's building costs; without them a building claims its
    /// anchor. More open Methods than the census bound refuse the candidate rather than admit it blind.
    /// </summary>
    private static ClaimConflict? ClaimOverlapIn(StoreTransaction tx, GenerationSnapshot current, PlanSpec plan)
    {
        var review = LoadRounds(tx);
        var evidence = review.Snapshot == current && review.WorkEvidence is { } known ? known : new WorkAdmissionEvidence();
        var candidate = PolicyFunctions.MethodClaimsOf(evidence,
            [.. plan.Actions.Select(a => new WorkAction(plan.Id, a, new ProgressView { Action = a.Id, Stage = Stage.Pending }))]);
        if (candidate.Pawns.Count == 0 && candidate.Targets.Count == 0) return null;
        var links = tx.Query("""
            SELECT n.plan_id,'need '||n.need_id,COALESCE(p.method_id,'') FROM need_methods n
            JOIN plans p ON p.id=n.plan_id
            WHERE p.retired=0 AND n.colony=$colony AND n.load_token=$load AND n.map_id=$map
            ORDER BY 1 LIMIT 1025
            """, [("$colony", current.Colony), ("$load", current.Load), ("$map", current.Map)],
            r => (Plan: new PlanId(r.GetString(0)), Holder: r.GetString(1), Method: new MethodId(r.GetString(2))));
        if (links.Count > MaxClaimPlans) throw new NotAdmittedException("claim_census_truncated");
        foreach (var link in links)
        {
            var held = LoadIn(tx, link.Plan);
            if (held.Retired || !WorkProgress.Open(held.Progress)) continue;
            var rows = held.Progress
                // Native authority rollover ends activity assignments, as in the work resource census.
                .Where(p => p.Action.PawnActivities is not { } activity || activity.AuthorityGeneration == current.Native.Value)
                .Select(p => new WorkAction(link.Plan, p.Action, p.View)).ToArray();
            if (PolicyFunctions.MethodClaimOverlap(candidate, PolicyFunctions.MethodClaimsOf(evidence, rows)) is { } claim)
                return new(link.Holder, link.Method, claim);
        }
        return null;
    }
}
