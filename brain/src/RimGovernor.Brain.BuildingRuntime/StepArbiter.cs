namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// Prevents two planners running concurrently within one scheduler step from both claiming the same pawn or the
/// same non-pawn resource (a bench, an item slot, ...). It is scoped to exactly one step: fresh at the top,
/// discarded at the end, never shared across ticks.
/// </summary>
public sealed class StepArbiter
{
    private readonly Lock _mu = new();
    private readonly List<ProposalArrival> _arrivals = [];
    private bool _closed;

    internal StepArbiter(LateProposals? late = null) => Late = late;

    internal HashSet<PawnId> Pawns { get; } = [];

    /// <summary>Namespaced entities, e.g. "haul-item:id" or "bench:id".</summary>
    internal HashSet<string> Resources { get; } = new(StringComparer.Ordinal);

    /// <summary>The carry to the next step's coordinator for proposals that arrive after the cutoff.</summary>
    internal LateProposals? Late { get; }

    /// <summary>Ends the arbiter's own step: later proposals are carried, not arbitrated here.</summary>
    public void Close()
    {
        lock (_mu)
        {
            _closed = true;
        }
    }

    /// <summary>Reserves every given pawn and resource atomically, or none and false if any is already claimed this step.</summary>
    public bool TryClaim(IReadOnlyList<PawnId> pawns, params string[] resources)
    {
        lock (_mu)
        {
            if (pawns.Any(Pawns.Contains) || resources.Any(Resources.Contains))
            {
                return false;
            }
            foreach (var p in pawns)
            {
                Pawns.Add(p);
            }
            foreach (var r in resources)
            {
                Resources.Add(r);
            }
            return true;
        }
    }

    /// <summary>Records a planner's result for the coordinator. The planner has already finished every read; nothing is claimed here.</summary>
    internal void Propose(string planner, PlanResult result, Action<ProposalOutcome>? settle)
    {
        lock (_mu)
        {
            var arrival = new ProposalArrival(planner, result, settle);
            if (_closed)
            {
                // Past the cutoff the step has moved on and its result is not read again: a proposal is carried to
                // the next coordinator, and a non-proposal result has nothing to carry.
                if (Late is not null && result.Kind == PlanResultKind.Proposed && result.Proposal is not null)
                {
                    Late.Add(arrival with { Settle = null });
                }
                return;
            }
            _arrivals.Add(arrival);
        }
    }

    /// <summary>Whether any proposal the wave delivered claims a quantity, so the step reads a budget only when one is needed.</summary>
    internal bool ClaimsQuantities()
    {
        lock (_mu)
        {
            return _arrivals.Any(a => a.Result.Kind == PlanResultKind.Proposed && a.Result.Proposal is { Claims.Quantities.Count: > 0 });
        }
    }

    /// <summary>
    /// Arbitrates the wave's proposals once the admission cycle has joined it: proposals sorted by (priority,
    /// urgency, ID) are revalidated against <paramref name="scope"/> and take their claims in that order, and a
    /// proposal whose claims are free commits through its planner's own admission path. A loser is reported
    /// waiting, never failed; a stale one is reported expired. Commit errors are returned beside the outcomes.
    /// </summary>
    internal async Task<(List<ProposalOutcome> Outcomes, List<Exception> Failures)> CoordinateAsync(ProposalBudget budget, ReadValidity scope, CancellationToken ct)
    {
        List<ProposalArrival> arrivals;
        lock (_mu)
        {
            arrivals = [.. Late?.Drain() ?? [], .. _arrivals];
            _arrivals.Clear();
        }
        var index = new ClaimIndex(this, budget);
        var proposals = new List<ProposalArrival>();
        foreach (var arrival in arrivals)
        {
            if (arrival.Result.Kind == PlanResultKind.Proposed && arrival.Result.Proposal is not null)
            {
                proposals.Add(arrival);
                continue;
            }
            arrival.Settle?.Invoke(new ProposalOutcome { Planner = arrival.Planner, Verdict = arrival.Result.Verdict });
        }
        // List.Sort is unstable; OrderBy with the rank comparer is stable like Go's SliceStable.
        proposals = [.. proposals.OrderBy(a => a.Result.Proposal!, Proposals.RankComparer)];
        var outcomes = new List<ProposalOutcome>();
        var failures = new List<Exception>();
        foreach (var arrival in proposals)
        {
            var p = arrival.Result.Proposal!;
            var outcome = new ProposalOutcome { ProposalId = p.Id, Planner = arrival.Planner, Concern = p.Concern };
            var stale = Proposals.Stale(scope, p);
            if (stale != "")
            {
                outcome = outcome with { Stale = stale, Verdict = BuildingReasonExpired };
            }
            else
            {
                var refused = index.Refusal(p);
                IReadOnlyList<Amount>? demand = null;
                if (refused == "")
                {
                    (refused, demand) = index.Shortfall(p);
                }
                if (refused != "")
                {
                    outcome = outcome with { Waiting = refused, Verdict = BuildingReasonWaiting };
                    if (demand is not null)
                    {
                        outcome = outcome with { Verdict = BuildingReasonDemand, Demand = demand };
                    }
                }
                else
                {
                    index.Take(p);
                    try
                    {
                        var (plan, reason) = await p.Commit(ct).ConfigureAwait(false);
                        outcome = outcome with { Admitted = reason == BuildingReasonAdmitted, Plan = plan, Verdict = reason };
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        failures.Add(new InvalidOperationException($"{arrival.Planner}: {e.Message}", e));
                        outcome = outcome with { Verdict = Verdict.Refuse(Cause.SharedAdmission, "commit_failed", arrival.Planner) };
                    }
                }
            }
            arrival.Settle?.Invoke(outcome);
            outcomes.Add(outcome);
        }
        return (outcomes, failures);
    }
}
