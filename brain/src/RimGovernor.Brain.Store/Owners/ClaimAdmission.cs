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

/// <summary>The open Method holding a claim the candidate asked for: its owner (<c>standard &lt;id&gt;</c>), MethodId and the claim.</summary>
public sealed record ClaimConflict(string Holder, MethodId Method, string Claim)
{
    /// <summary>The planner reason a refusal files.</summary>
    public string Reason => "claim_conflict: " + Claim + " held by " + Holder + " method " + Method.Value;
}

/// <summary>A claimed commit's result: the owner after the commit and, on <see cref="ClaimOutcome.Conflict"/>, the holder.</summary>
public sealed record ClaimCommit(ClaimOutcome Outcome, StandardState State, ClaimConflict? Conflict = null);

public sealed partial class Store
{
    private const int MaxClaimPlans = 1024;

    /// <summary>
    /// Claim-overlap admission (#3249), the opt-in beside <see cref="CommitMethodReason"/>: a migrated owner commits
    /// through here and skips the open-work gate and its exemptions. The revision compare-and-swap comes first; a
    /// MethodId the owner already binds to open work then continues that Method (<see cref="ClaimOutcome.Continued"/>,
    /// no new row). Otherwise the open-deficit check, Safeguards and current review run as for the gate, and the
    /// candidate's claims (<see cref="PolicyFunctions.MethodClaimsOf"/>) are compared with every open Method in the
    /// same world, any owner: an overlap is <see cref="ClaimOutcome.Conflict"/> naming the holder and the claim, with
    /// nothing written. Family admission, work resources and binding are unchanged, so a MethodId whose plan was
    /// released still raises <see cref="MethodBoundException"/>.
    /// </summary>
    public ClaimCommit CommitClaimedMethod(StandardId id, ulong revision, MethodId method, string reason, PlanSpec plan, CancellationToken cancellationToken)
    {
        if (plan.Actions.Count == 0) throw new DomainException("empty standard method");
        return Run(tx => CommitClaimedMethodIn(tx, id, revision, method, reason, plan), cancellationToken);
    }

    private static ClaimCommit CommitClaimedMethodIn(StoreTransaction tx, StandardId id, ulong revision, MethodId method, string reason, PlanSpec plan)
    {
        var state = LoadStandard(tx, id);
        IMethodOwner owner = state;
        if (state.Revision != revision)
            throw new StaleOwnerException($"{owner.OwnerLabel} is at revision {state.Revision}, not {revision}");
        if (owner.BoundPlan(tx, method) is { } bound)
        {
            var held = LoadIn(tx, bound);
            if (!held.Retired && Standard.WorkOpen(held.Progress)) return new(ClaimOutcome.Continued, state);
        }
        var summary = SummarizeOwner(state);
        if (summary.Status != StandardStatus.Open || summary.Finding != Finding.Unmet)
            throw new DomainException("standard does not admit a method");
        AdmitRoundsSafeguards(tx, state);
        AdmitRoundsReview(tx, summary);
        if (ClaimOverlapIn(tx, owner.OwnerSnapshot, plan) is { } conflict) return new(ClaimOutcome.Conflict, state, conflict);
        new Method(state.Standard.Id, summary.Episode, method, plan.Id).Validate();
        if (state.Revision == ulong.MaxValue) throw new CapacityException();
        BindOwnerMethod(tx, state, method, reason, plan);
        BumpRevision(tx, state);
        return new(ClaimOutcome.Admitted, LoadStandard(tx, id));
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
            SELECT m.plan_id,m.kind||' '||m.owner_id,COALESCE(p.method_id,'') FROM plan_methods m
            JOIN (SELECT id,payload,'standard' AS kind FROM standards UNION ALL SELECT id,payload,'incident' AS kind FROM incidents) g
            ON g.id=m.owner_id AND g.kind=m.kind
            JOIN plans p ON p.id=m.plan_id
            WHERE p.retired=0 AND json_extract(g.payload,'$.Snapshot.Colony')=$colony
              AND json_extract(g.payload,'$.Snapshot.Load')=$load AND json_extract(g.payload,'$.Snapshot.Map')=$map
            ORDER BY m.plan_id LIMIT 1025
            """, [("$colony", current.Colony), ("$load", current.Load), ("$map", current.Map)],
            r => (Plan: new PlanId(r.GetString(0)), Holder: r.GetString(1), Method: new MethodId(r.GetString(2))));
        if (links.Count > MaxClaimPlans) throw new NotAdmittedException("claim_census_truncated");
        foreach (var link in links)
        {
            var held = LoadIn(tx, link.Plan);
            if (held.Retired || !Standard.WorkOpen(held.Progress)) continue;
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
