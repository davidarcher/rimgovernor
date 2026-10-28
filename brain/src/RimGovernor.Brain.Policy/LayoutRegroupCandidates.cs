namespace RimGovernor.Brain.Policy;

/// <summary>A geometrically complete regroup trial. Id travels with its tiled layout and native prediction batch.</summary>
public sealed record RegroupCandidate(string Id, Regrouping Layout);

/// <summary>The canonical forecast of a trial, retaining unassessed terms. Effects names its losses.</summary>
public abstract record RegroupValuation(string Candidate)
{
    public sealed record Evaluated(string Candidate, ForecastConsequenceVector Consequences, IReadOnlyList<string> Effects) : RegroupValuation(Candidate);
    public sealed record Unplaceable(string Candidate, string Reason) : RegroupValuation(Candidate);
}

public static partial class LayoutRegroupRules
{
    /// <summary>
    /// Searches the existing eight proposals over four rounds using the colony forecast as its sole economic owner.
    /// The evaluator tiles the candidates and reads their native room predictions before running the canonical credits.
    /// Only walking is added here; shell costs already belong to the forecast ledger. No model service is called by policy.
    /// Identical station groupings share their evaluation throughout this search.
    /// </summary>
    public static async Task<Regrouping> RegroupAsync(RegroupInput input,
        Func<IReadOnlyList<RegroupCandidate>, CancellationToken, Task<IReadOnlyList<RegroupValuation>>> evaluate,
        CancellationToken cancellationToken = default)
    {
        if (!input.Price.Work.Valid()) throw new RequiredFactException("regroup_work_price");
        var search = new Regrouper(input);
        var current = search.Evaluate(WithRules([], input.Cluster.Pairs));
        var values = new Dictionary<string, RegroupValuation>(StringComparer.Ordinal);
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        var layouts = new Dictionary<string, Regrouping>(StringComparer.Ordinal);
        var decisions = new List<RegroupDecision>();
        var nextId = 0;
        string Id(RegroupTrial trial)
        {
            var signature = trial.Signature();
            if (!ids.TryGetValue(signature, out var id)) ids[signature] = id = "regroup:" + (nextId++).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return id;
        }
        async Task Read(IReadOnlyList<RegroupTrial> trials)
        {
            var pending = trials.DistinctBy(t => t.Signature()).Where(t => !values.ContainsKey(Id(t))).ToArray();
            if (pending.Length == 0) return;
            var candidates = pending.Select(t => new RegroupCandidate(Id(t), t.Result([]))).ToArray();
            var returned = await evaluate(candidates, cancellationToken).ConfigureAwait(false);
            if (returned.Count != candidates.Length || returned.Select(v => v.Candidate).Distinct(StringComparer.Ordinal).Count() != returned.Count)
                throw new RequiredFactException("regroup_candidate_values");
            foreach (var candidate in candidates)
            {
                var value = returned.SingleOrDefault(v => v.Candidate == candidate.Id)
                    ?? throw new RequiredFactException("regroup_candidate_value:" + candidate.Id);
                values.Add(candidate.Id, value);
                layouts.Add(candidate.Id, candidate.Layout);
            }
        }
        ForecastConsequenceVector Consequences(RegroupTrial trial)
        {
            var vector = ((RegroupValuation.Evaluated)values[Id(trial)]).Consequences;
            var walk = -(double)trial.Embedding.Score.Walk / PlanWeights.Walk * input.Price.Work.CellWalkDays;
            var ledger = vector.Value.Ledger;
            return vector with { Value = vector.Value with { Ledger = ledger with
            {
                Investment = ledger.Investment + walk,
                InvestmentContributions = EquatableArray.From(ledger.InvestmentContributions.Append(new("", "", "room_sharing_walk", 1, walk))),
                Total = ledger.Total is LedgerTerm.Priced total ? new LedgerTerm.Priced(total.Loss + walk) : ledger.Total,
            } } };
        }
        bool Better(RegroupTrial a, RegroupTrial b)
        {
            if (values[Id(a)] is not RegroupValuation.Evaluated) return false;
            if (values[Id(b)] is not RegroupValuation.Evaluated) return true;
            return ForecastCoupledSelection.CompareConsequences(Consequences(a), Consequences(b)) < 0;
        }
        RegroupDecision Decision(RegroupProposal proposal, RegroupTrial before, RegroupTrial after, bool applied) => new()
        {
            Kind = proposal.Kind, A = proposal.A, B = proposal.B,
            Verdict = applied ? RegroupApplied : RegroupKept,
            Reason = values[Id(after)] is RegroupValuation.Unplaceable unavailable ? unavailable.Reason : applied ? "regroup_canonical_credit_gain" : "regroup_canonical_credit_loss",
            // Diagnostics normalize the incumbent to zero, and show the shared canonical ledger delta.
            Before = 0,
            After = values[Id(before)] is RegroupValuation.Evaluated && values[Id(after)] is RegroupValuation.Evaluated
                ? Consequences(after).Value.Ledger.LossAgainst(Consequences(before).Value.Ledger) : 0,
            Walk = -(double)(after.Embedding.Score.Walk - before.Embedding.Score.Walk) / PlanWeights.Walk * input.Price.Work.CellWalkDays,
            Effects = values[Id(after)] is RegroupValuation.Evaluated evaluated ? evaluated.Effects : [],
        };
        await Read([current]).ConfigureAwait(false);
        var random = new SearchRand(current.Embedding.Seed);
        for (var round = 0; round < RegroupReview; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var proposals = Sample(Regrouper.Candidates(current), RegroupProposals, random);
            var trials = proposals.Select(p => search.Evaluate(WithRules(current.Pairs, p.Rules))).ToArray();
            await Read(trials).ConfigureAwait(false);
            RegroupTrial? best = null;
            RegroupProposal? move = null;
            for (var i = 0; i < trials.Length; i++)
            {
                if (trials[i].Signature() == current.Signature()) continue;
                if (Better(trials[i], current) && (best is null || Better(trials[i], best))) (best, move) = (trials[i], proposals[i]);
                else decisions.Add(Decision(proposals[i], current, trials[i], false));
            }
            if (best is null || move is null) break;
            decisions.Add(Decision(move, current, best, true));
            current = best;
        }
        return layouts[Id(current)] with { Decisions = decisions };
    }
}
