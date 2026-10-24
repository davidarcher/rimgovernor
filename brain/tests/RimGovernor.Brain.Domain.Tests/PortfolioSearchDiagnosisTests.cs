using System.Text.Json;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// Diagnosis for the four-worker fixture (#3196): why the bounded portfolio search serves one of four demanded meals.
/// It observes the production search and scheduler through their public API only and changes no selection, budget or
/// traversal. <c>TraversalReplica</c> mirrors the pre-#3197 plain subset order so the opt-in measurement can count pair
/// positions. Findings: docs/developers/testing/portfolio-allocations.md.
/// </summary>
public sealed class PortfolioSearchDiagnosisTests
{
    private static readonly int[] Budgets = PortfolioAllocationTests.Budgets;
    private const int Default = ForecastSearchBudget.DefaultEvaluations;
    private const string IncumbentRoot = "food/0/delivery/0/composition/2/after/food/14/delivery/0/eat/diner/0/composition/16";
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static readonly Lazy<Subject> Shared = new(() => new Subject());

    private sealed class Subject
    {
        public readonly ForecastCoupledCandidates Generated;
        public readonly ColonyForecastRequest Request;
        public readonly ForecastCommitment[] Roots; // in the order the search visits them
        public readonly ForecastCommitment[] ReferenceRoots; // one ingestion each: diner/0, pawn/0, diner/1, pawn/1

        public Subject()
        {
            var facts = PortfolioAllocationTests.LargerFacts();
            Generated = ForecastCoupledCandidates.GenerateCoupledActivities(facts, 700)!.Priced(700);
            Request = PortfolioAllocationTests.Request(facts, Generated);
            Roots = [.. Generated.Alternatives.Concat(Generated.Incumbents).OrderBy(c => c.Id, StringComparer.Ordinal)];
            var eats = PortfolioReferenceScheduleTests.Reference().Select(s => s.Activity).Where(a => a.Contains("/eat/", StringComparison.Ordinal)).ToHashSet();
            ReferenceRoots = [.. Roots.Where(r => r.ActivityCompletions.Any(eats.Contains))];
        }

        public ForecastCoupledSelection Select(ForecastCommitment[] universe, int evaluations) =>
            ForecastCoupledSelection.CompareActivityOutcomes(Request, Generated with { Alternatives = EquatableArray.From(universe) }, new(evaluations));
    }

    /// <summary>Subset order and pruning of the production search, rebuilt from public data for counting only.</summary>
    private sealed class TraversalReplica(ForecastCommitment[] roots, ForecastCoupledContext context)
    {
        private readonly HashSet<(string Contained, string Container)> redundant = Redundant(roots, context);

        private static HashSet<(string, string)> Redundant(ForecastCommitment[] roots, ForecastCoupledContext context)
        {
            var activities = context.Activities.Activities.ToDictionary(a => a.Id, StringComparer.Ordinal);
            var closures = roots.ToDictionary(r => r.Id, r =>
            {
                var closure = new HashSet<string>(StringComparer.Ordinal);
                void Include(string id)
                {
                    if (!closure.Add(id)) return;
                    foreach (var prerequisite in activities[id].Prerequisites) Include(prerequisite);
                }
                foreach (var completion in r.ActivityCompletions) Include(completion);
                return closure;
            }, StringComparer.Ordinal);
            static bool GraphOnly(ForecastCommitment c) => c.ActivityCompletions.Count > 0 && c.Outcome is null
                && c.StockChanges.Count == 0 && c.MaterialClaims.Count == 0 && c.EquipmentClaims.Count == 0
                && c.UnsupportedEffects.Count == 0 && c.Construction is null;
            return roots.Where(GraphOnly).SelectMany(a => roots.Where(b => a.Id != b.Id && closures[a.Id].IsSubsetOf(closures[b.Id]))
                .Select(b => (a.Id, b.Id))).ToHashSet();
        }

        private static bool Compatible(IEnumerable<ForecastCommitment> input)
        {
            var rows = input.ToArray();
            return !rows.Where(c => c.ExclusiveOutcome.Length != 0).GroupBy(c => c.ExclusiveOutcome).Any(g => g.Count() > 1)
                && !rows.Any(c => c.Outcome is { } effect && rows.Any(other => c.Id != other.Id
                    && (effect.Excludes.Contains(other.Outcome?.Id ?? other.Id) || effect.ExclusiveOutput.Length != 0 && effect.ExclusiveOutput == other.Outcome?.ExclusiveOutput)));
        }

        /// <summary>The subsets of one cardinality, in the order the search evaluates them.</summary>
        public IEnumerable<int[]> Subsets(int size)
        {
            var partial = new List<int>();
            IEnumerable<int[]> Walk(int start, int left)
            {
                if (left == 0) { yield return [.. partial]; yield break; }
                for (var i = start; i <= roots.Length - left; i++)
                {
                    if (!Compatible(partial.Select(p => roots[p]).Append(roots[i]))) continue;
                    if (partial.Any(p => redundant.Contains((roots[p].Id, roots[i].Id)) || redundant.Contains((roots[i].Id, roots[p].Id)))) continue;
                    partial.Add(i);
                    foreach (var subset in Walk(i + 1, left - 1)) yield return subset;
                    partial.RemoveAt(partial.Count - 1);
                }
            }
            return Walk(0, size);
        }
    }

    private static double[] Flat(IEnumerable<Fact<double>> facts) => [.. facts.Select(x => x.IsKnown ? x.Value : -1)];

    private static string[] LastSubset(ForecastCoupledSelection selection) => [.. selection.Rejected[^1].Commitments];

    [Fact]
    public void ReferenceRootSetIsGeneratedWithItsClosureAndOptions()
    {
        var s = Shared.Value;
        Assert.Equal(62, s.Roots.Length);
        Assert.False(s.Generated.Truncated);
        Assert.Equal(88, s.Generated.Context.Activities.Activities.Length);
        Assert.Equal(158, s.Generated.Context.Activities.Options.Length);
        // The four ingestions are four roots with one completion each, so four is the smallest portfolio that serves four.
        Assert.Equal(["food/27/delivery/0/eat/diner/0/composition/19", "food/27/delivery/1/eat/pawn/0/composition/25",
            "food/27/delivery/2/eat/diner/1/composition/28", "food/27/delivery/3/eat/pawn/1/composition/34"], s.ReferenceRoots.Select(r => r.Id));
        Assert.All(s.ReferenceRoots, r => Assert.Single(r.ActivityCompletions));
        Assert.Contains(s.Roots, r => r.Id == "food/27/composition/18" && r.ActivityCompletions.Count == 4);
        // The closure of the four roots is exactly the reference schedule's sixteen activities, each with the reference worker as an option.
        var activities = s.Generated.Context.Activities.Activities.ToDictionary(a => a.Id, StringComparer.Ordinal);
        var closure = new HashSet<string>(StringComparer.Ordinal);
        void Include(string id) { if (closure.Add(id)) foreach (var p in activities[id].Prerequisites) Include(p); }
        foreach (var completion in s.ReferenceRoots.SelectMany(r => r.ActivityCompletions)) Include(completion);
        var reference = PortfolioReferenceScheduleTests.Reference();
        Assert.Equal(reference.Select(r => r.Activity).Order(StringComparer.Ordinal), closure.Order(StringComparer.Ordinal));
        var options = s.Generated.Context.Activities.Options.Select(o => (o.Activity, o.Pawn)).ToHashSet();
        Assert.All(reference, step => Assert.Contains((step.Activity, step.Worker), options));
    }

    [Fact]
    public void ProductionAcceptsTheFourIngestionReferenceOnASharedStack()
    {
        var s = Shared.Value;
        // Both consumers' ingestion tasks name the same delivery-cell target, a stack that admits both (capacity 10).
        var context = s.Generated.Context;
        var targets = context.NeedTasks.Where(n => n.Kind == "food" && s.ReferenceRoots.SelectMany(r => r.ActivityCompletions).Contains(n.Task))
            .GroupBy(n => n.Pawn).ToDictionary(g => g.Key, g => g.Select(n => n.Target).Distinct().ToArray());
        Assert.Equal(["diner", "pawn"], targets.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["cell/3/1"], targets["diner"]);
        Assert.Equal(["cell/3/1"], targets["pawn"]);

        // The per-portfolio bound is 4096 and cannot be raised: a larger bound is rejected, not searched.
        Assert.Throws<DomainException>(() => ColonyForecastRules.ForecastColony(Replay(s, s.ReferenceRoots, 4097)));
        var production = ColonyForecastRules.ForecastColony(Replay(s, s.ReferenceRoots, 4096)).Baseline.Coupled!;
        Assert.True(production.Truncated);
        // Pinning every activity to its reference worker leaves nothing to assign differently.
        var pairs = PortfolioReferenceScheduleTests.Reference().Select(r => (r.Activity, r.Worker)).ToHashSet();
        var pinned = ColonyForecastRules.ForecastColony(Replay(s, s.ReferenceRoots, 4096, o => pairs.Contains((o.Activity, o.Pawn)))).Baseline.Coupled!;
        var mixed = ColonyForecastRules.ForecastColony(Replay(s, [s.ReferenceRoots[0], s.ReferenceRoots[1]], 4096)).Baseline.Coupled!;
        var diner = ColonyForecastRules.ForecastColony(Replay(s, [s.ReferenceRoots[0], s.ReferenceRoots[2]], 4096)).Baseline.Coupled!;
        MaybeWrite("replay", new
        {
            Reference = Describe(production, s.ReferenceRoots), Pinned = Describe(pinned, s.ReferenceRoots),
            Mixed = Describe(mixed, [s.ReferenceRoots[0], s.ReferenceRoots[1]]), Diner = Describe(diner, [s.ReferenceRoots[0], s.ReferenceRoots[2]])
        });
        // Both consumers' ingestions share one delivered stack (capacity 10, the batch quantity): #3210 accepts them.
        Assert.True(production.Truncated);
        Assert.Equal(1023, production.Alternatives.Count);
        Assert.All(production.Alternatives, a => Assert.Equal(Known(true), a.Feasible));
        Assert.DoesNotContain(production.Alternatives.SelectMany(a => a.Findings),
            f => f.Reason is "conflicting_selected_target_owners" or "target_stack_quantity_exceeded");
        // The pinned reference assignment serves all four ingestions with no exposure: the reference set is accepted.
        Assert.Equal(420, pinned.Alternatives.Count);
        Assert.All(pinned.Alternatives, a => Assert.Equal(Known(true), a.Feasible));
        Assert.Equal(1018, mixed.Alternatives.Count);
        Assert.All(mixed.Alternatives, a => Assert.Equal(Known(true), a.Feasible));
        Assert.Contains(diner.Alternatives, a => a.Feasible == Known(true));
        Assert.Contains(pinned.Alternatives.Select(a => ForecastCoupledSelection.CoupledConsequences(a, s.ReferenceRoots)),
            v => v.Labor == 841 && v.Urgent.All(x => x == Known(0d)));
    }

    [Fact]
    public void ProductionRanksATwoIngestionPortfolioAboveTheIncumbentItNeverReaches()
    {
        var s = Shared.Value;
        // The pre-#3197 incumbent (one ingestion, recorded at every budget up to 65,536) is the single root IncumbentRoot.
        var incumbentRoot = s.Roots.Single(r => r.Id == IncumbentRoot);

        // The same search, scheduler and ranking restricted to two roots: the incumbent and one more ingestion root.
        var universe = new[] { incumbentRoot, s.ReferenceRoots[0] };
        var selection = s.Select(universe, Default);
        MaybeWrite("pair", new
        {
            selection.Selection.Selected, selection.Selection.Expansions, selection.Selection.SearchTruncated,
            Baseline = Flat(selection.BaselineVector.Urgent), Vector = Flat(selection.SelectedVector.Urgent), Needs = Flat(selection.SelectedVector.Needs),
            selection.Selected.Plan.LaborCost
        });
        Assert.Equal(universe.Select(u => u.Id).Order(StringComparer.Ordinal), selection.Selection.Selected.Order(StringComparer.Ordinal));
        var food = s.Generated.Context.NeedTasks.Where(n => n.Kind == "food").Select(n => n.Task).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(2, selection.Selected.Plan.Allocations.Count(a => food.Contains(a.Activity)));
        // Strictly fewer hunger capability-days and one fewer need left at zero than the incumbent; the zero-time floor is equal.
        var single = s.Select([incumbentRoot], Default);
        Assert.Equal(single.SelectedVector.Value.UrgentZero, selection.SelectedVector.Value.UrgentZero);
        Assert.True(selection.SelectedVector.Value.Ledger.LossAgainst(single.SelectedVector.Value.Ledger) < -selection.SelectedVector.Value.Policy.TieResolution);
        Assert.Equal(2d, single.SelectedVector.Needs[4].Value);
        Assert.Equal(197, single.Selected.Plan.LaborCost);
        Assert.Equal(1d, selection.SelectedVector.Needs[4].Value);
        // It costs more labor (329 vs 197) and the per-portfolio scheduler search is truncated at its bound, so it is not proven best.
        Assert.Equal(329, selection.Selected.Plan.LaborCost);
        Assert.True(selection.Selection.SearchTruncated);
    }

    [Fact]
    public void ScreenedFixpointExtensionServesAllFourDemandedIngestionsAtTheDefaultBudget()
    {
        var s = Shared.Value;
        var selection = s.Select(s.Roots, Default);
        var food = s.Generated.Context.NeedTasks.Where(n => n.Kind == "food").Select(n => n.Task).ToHashSet(StringComparer.Ordinal);
        var eats = selection.Selected.Plan.Allocations.Where(a => food.Contains(a.Activity) && a.Activity.Contains("/eat/", StringComparison.Ordinal)).ToArray();

        // Four roots, one ingestion each, two per consumer. Every food ledger term and the zero floor are met, so which of the
        // equally priced four-ingestion sets wins is decided by the tiebreakers and labor, not by the urgent-time vector.
        Assert.Equal(4, selection.Selection.Selected.Length);
        Assert.True(selection.Selected.Feasible == Known(true));
        Assert.Equal(4, eats.Length);
        Assert.Equal(2, eats.Count(a => a.Pawn == "pawn"));
        Assert.Equal(2, eats.Count(a => a.Pawn == "diner"));
        Assert.Equal(0d, selection.SelectedVector.Value.UrgentZero.Excess);
        Assert.Equal(new LedgerTerm.Priced(0), selection.SelectedVector.Value.Ledger.Hunger);
        // #3624: the scheduler tries the pawn that made a meal for carrying it first, so the cook delivers without a hand waiting: 721, was 947.
        Assert.Equal(721, selection.Selected.Plan.LaborCost);
        Assert.DoesNotContain("selected_activity_budget_truncated", selection.Selection.Findings);

        Assert.Equal(0d, selection.SelectedVector.Needs[4].Value);

        // Cost stays under the default budget: cardinalities 3 and 4 were entered through one-root extension steps from the
        // leader, not a subset sweep (which would need 28,580 / 339,189 subsets), and the sweep the budget left could not
        // finish is skipped (#3563), so the search ends converged with budget unspent and still says it is bounded.
        Assert.True(selection.OutcomeEvaluations - selection.BaselineEvaluations < Default);
        Assert.True(selection.Selection.SearchTruncated);
        Assert.Contains("portfolio_search_truncated", selection.Selection.Findings);
        Assert.Contains(selection.Rejected, r => r.Commitments.Count == 4);
        Assert.Contains(selection.Rejected, r => r.Commitments.Count == 3);

        // Deterministic: a second run is identical.
        var again = s.Select(s.Roots, Default);
        Assert.Equal(selection.Selection.Selected, again.Selection.Selected);
        Assert.Equal(selection.Selection.Expansions, again.Selection.Expansions);
        Assert.Equal(selection.Selected.Plan.Allocations.Select(a => (a.Activity, a.Pawn, a.Start, a.End)),
            again.Selected.Plan.Allocations.Select(a => (a.Activity, a.Pawn, a.Start, a.End)));
        Assert.Equal(selection.SelectedVector, again.SelectedVector);
    }

    [Fact]
    public void ServedIngestionsGrowWithTheBudgetUpToTheDefault()
    {
        var s = Shared.Value;
        var food = s.Generated.Context.NeedTasks.Where(n => n.Kind == "food").Select(n => n.Task).ToHashSet(StringComparer.Ordinal);
        // Before #3215 these were 1, 1, 1, 2 (one at 8,192 expansions and below). Screening lets even 256 evaluations reach a second ingestion.
        Assert.Equal([2, 3, 4, 4, 4], Budgets.Where(b => b <= Default).Select(budget => s.Select(s.Roots, budget).Selected.Plan.Allocations
            .Count(a => food.Contains(a.Activity) && a.Activity.Contains("/eat/", StringComparison.Ordinal))));
    }

    [Fact]
    public void ExtensionIsNeverWorseThanTheOneIngestionIncumbentAtAnyMatrixBudget()
    {
        var s = Shared.Value;
        foreach (var budget in Budgets.Where(b => b <= Default))
        {
            var selection = s.Select(s.Roots, budget);
            // The zero-time floor, then capability-days; labor is not part of the comparison here.
            var incumbent = s.Select([s.Roots.Single(r => r.Id == IncumbentRoot)], Default).SelectedVector.Value;
            var value = selection.SelectedVector.Value;
            Assert.True(value.UrgentZero.Excess <= incumbent.UrgentZero.Excess, "budget " + budget + " is worse than the one-ingestion incumbent");
            if (value.UrgentZero.Excess == incumbent.UrgentZero.Excess)
                Assert.True(value.Ledger.LossAgainst(incumbent.Ledger) <= value.Policy.TieResolution, "budget " + budget + " loses more capability-days than the incumbent");
            Assert.True(selection.Selection.SearchTruncated, "budget " + budget + " is still bounded");
        }
    }

    [Fact]
    public void WriteSearchDiagnosisMeasurements()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RIMGOVERNOR_PORTFOLIO_OUTPUT"))) return;
        var s = Shared.Value;
        var replica = new TraversalReplica(s.Roots, s.Generated.Context);
        var pairs = replica.Subsets(2).Select(x => string.Join("+", x)).ToList();
        var ids = s.Roots.Select(r => r.Id).ToList();
        // The three budgets beyond the matrix are diagnostic only: they show where the search starts to improve, not a proposal.
        var food = s.Generated.Context.NeedTasks.Where(n => n.Kind == "food").Select(n => n.Task).ToHashSet(StringComparer.Ordinal);
        var rows = Budgets.Concat([131072, 262144, 524288]).Select(budget =>
        {
            var selection = s.Select(s.Roots, budget);
            var last = LastSubset(selection);
            var lastIndexes = last.Select(id => ids.IndexOf(id)).ToArray();
            return new
            {
                Budget = budget, selection.Selection.Expansions, selection.Selection.SearchTruncated,
                Cardinalities = selection.Rejected.GroupBy(r => r.Commitments.Count).OrderBy(g => g.Key).Select(g => new { Size = g.Key, Evidence = g.Count() }).ToArray(),
                LastRoot = last, LastRootIndexes = lastIndexes,
                LastPairPosition = last.Length == 2 ? pairs.IndexOf(string.Join("+", lastIndexes)) + 1 : 0,
                Selected = selection.Selection.Selected, Vector = Flat(selection.SelectedVector.Urgent), Needs = Flat(selection.SelectedVector.Needs),
                selection.Selected.Plan.LaborCost, EndTick = selection.Selected.Plan.CompletedAt,
                Ingestions = selection.Selected.Plan.Allocations.Count(a => food.Contains(a.Activity) && a.Activity.Contains("/eat/", StringComparison.Ordinal))
            };
        }).ToArray();
        MaybeWrite("production", rows);

        // Diagnostic only (#3212): the same search over a universe that holds just the reference ingestion roots, with and
        // without the incumbent, shows whether cardinality 4 is reachable when the other roots do not spend the budget.
        var incumbentRoot = s.Roots.Single(r => r.Id == IncumbentRoot);
        var narrow = new (string Name, ForecastCommitment[] Universe)[] { ("reference-4", s.ReferenceRoots), ("incumbent+reference-4", [incumbentRoot, .. s.ReferenceRoots]) }
            .SelectMany(u => Budgets.Where(b => b >= Default).Select(budget =>
            {
                var selection = s.Select(u.Universe, budget);
                return new
                {
                    u.Name, Budget = budget, selection.Selection.Expansions, selection.Selection.SearchTruncated, Selected = selection.Selection.Selected,
                    Ingestions = selection.Selected.Plan.Allocations.Count(a => food.Contains(a.Activity) && a.Activity.Contains("/eat/", StringComparison.Ordinal)),
                    Vector = Flat(selection.SelectedVector.Urgent), Needs = Flat(selection.SelectedVector.Needs), selection.Selected.Plan.LaborCost,
                    Cardinalities = selection.Rejected.GroupBy(r => r.Commitments.Count).OrderBy(g => g.Key).Select(g => new { Size = g.Key, Evidence = g.Count() }).ToArray()
                };
            })).ToArray();
        MaybeWrite("narrow", narrow);
    }

    private static ColonyForecastRequest Replay(Subject s, ForecastCommitment[] roots, int bound, Func<ForecastActivityOption, bool>? keep = null)
    {
        var problem = s.Generated.Context.Activities with { ExpansionBudget = bound };
        if (keep is not null) problem = problem with { Options = [.. problem.Options.Where(keep)] };
        return s.Request with { Coupled = s.Generated.Context with { Activities = problem }, Commitments = EquatableArray.From(roots), Proposed = new() };
    }

    private static object Describe(ForecastCoupledScenario scenario, ForecastCommitment[] roots) => new
    {
        scenario.Truncated, Alternatives = scenario.Alternatives.Count,
        Feasible = scenario.Alternatives.Count(a => a.Feasible == Known(true)),
        Reasons = scenario.Alternatives.SelectMany(a => a.Findings.Select(f => f.Reason)).GroupBy(x => x).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.Key + "=" + g.Count()).ToArray(),
        Best = scenario.Alternatives.Where(a => a.Feasible == Known(true)).Select(a => ForecastCoupledSelection.CoupledConsequences(a, roots))
            .Select(v => new { Urgent = v.Urgent.Select(x => x.IsKnown ? x.Value : -1).ToArray(), Needs = v.Needs.Select(x => x.IsKnown ? x.Value : -1).ToArray(), v.Labor })
            .OrderBy(v => v.Urgent[0]).ThenBy(v => v.Urgent[1]).ThenBy(v => v.Urgent[2]).ThenBy(v => v.Urgent[3]).ThenBy(v => v.Needs[4]).Take(1).ToArray()
    };

    // Explicit opt-in output, like PortfolioAllocationTests: never read back as an expectation.
    private static void MaybeWrite(string name, object value)
    {
        var output = Environment.GetEnvironmentVariable("RIMGOVERNOR_PORTFOLIO_OUTPUT");
        if (string.IsNullOrEmpty(output)) return;
        File.WriteAllText(Path.Combine(output, "search-diagnosis." + name + ".json"), JsonSerializer.Serialize(value, Indented));
    }
}
