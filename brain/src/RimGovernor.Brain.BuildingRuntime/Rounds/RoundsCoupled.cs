namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class Rounder
{
    /// <summary>Normal review sequencing only. Policy selected the graph; the admin/work-assignments Need's
    /// claimed commit, the Method journal and Hands retain admission, claims, execution and observed continuation.</summary>
    private void AdmitCoupledSelection(RoundsResult review, RoundsFacts facts, EquatableArray<ForecastObservedIncumbent> incumbents, GenerationSnapshot snapshot,
        CancellationToken call, CancellationToken epoch)
    {
        if (review.Needs.CoupledSelection is not { } selection || facts.CoupledAlternatives is not { } candidates) return;
        var forecast = selection;
        void Capture(ForecastAdmission admission, PawnActivities? batch = null) => Player.ForecastEvidence.Capture(snapshot, facts.Tick.Value, review.Review.Revision, forecast,
            candidates.Context, admission, batch is null ? incumbents.Select(i => i.Batch).ToArray() : [batch, .. incumbents.Select(i => i.Batch)], batch?.Orders.FirstOrDefault()?.Method ?? "");
        var execution = ForecastCoupledExecution.ReviewCoupledExecution(selection, incumbents, facts.Tick.Value, candidates.Context.Activities.Activities, candidates);
        if (execution.Releases.Count > 0)
        {
            Player.Current(call, epoch);
            if (Player.Session.State().Snapshot != snapshot) throw new ControlException("coupled activity scope changed");
            foreach (var release in execution.Releases)
            {
                var next = PlanIds.MintPlanId();
                Player.Journal.ContinueActivityMethod(release.Incumbent.Plan, snapshot, facts.Tick,
                    release.Incumbent.Owned, new PlanSpec(next, new PlanRevision(1),
                        [DomainAction.NewPawnActivitiesAction(new ActionId($"{next}-0"), release.Batch)]), call);
            }
            // The released pawn waits for fresh native ownership evidence; the rest of the selection is admitted now (#3598).
            if (execution.Dispatch is null) { Capture(ForecastAdmission.Selected); RecordCoupledSelection(selection, facts.Tick.Value, "release_for_reconsideration"); return; }
        }
        if (execution.Dispatch is not { } dispatch)
        { Capture(ForecastAdmission.Continued); RecordCoupledSelection(selection, facts.Tick.Value, "retained_method_future_work", true); return; }
        selection = dispatch;
        if (selection.Selection.Selected.Length == 0)
        { Capture(ForecastAdmission.Continued); RecordCoupledSelection(selection, facts.Tick.Value, "continue", facts.CoupledWorkActive); return; }
        // The activity is the admin/work-assignments Need's Method (#3304); a runtime without the Need starts none.
        if (!Needs.Contains(AdminFamily.WorkAssignments)) { Capture(ForecastAdmission.Selected); return; }
        var id = PlanIds.MintPlanId();
        var method = new MethodId($"coupled-{id}");
        var activity = ForecastCoupledSelection.StartCoupledActivity(selection, candidates, snapshot.Native.Value, method.Value, facts.Tick.Value);
        if (activity is null)
        {
            Capture(ForecastAdmission.Selected);
            // Selected planned sites start no order: the room reconcile places them from the review's RoomSelection (#3278).
            if (selection.Selected.PlannedRooms.Count > 0)
            {
                RecordCoupledSelection(selection, facts.Tick.Value, PlannedUnplacedTarget);
                // The comparison's room verdicts, in a row the payload cap cannot drop (#3828).
                Tel.Decide(new Decision
                {
                    Kind = TelemetryKinds.PlannedRoomSelectionKind, Component = "rounds", Target = PlannedUnplacedTarget, Verdict = "selected",
                    Reason = selection.Selection.SearchTruncated ? "best_found_bounded" : "shared_consequences",
                    Attrs = PlannedRoomSelectionAttrs(selection, candidates, facts.Tick.Value)
                });
            }
            return;
        }
        Player.Current(call, epoch);
        if (Player.Session.State().Snapshot != snapshot) throw new ControlException("coupled activity scope changed");
        // A proposal owns nothing until admitted. Refusal must not revoke authority
        // or interrupt the useful Methods whose current claims prevented it.
        var commit = Player.Journal.CommitClaimedMethod(AdminFamily.WorkAssignments, snapshot, method, "shared colony consequence portfolio",
            new PlanSpec(id, new PlanRevision(1), [DomainAction.NewPawnActivitiesAction(new ActionId($"{id}-0"), activity)]), call);
        Capture(commit.Conflict is not null || commit.Refusal is not null ? ForecastAdmission.Refused : ForecastAdmission.Admitted, activity);
        RecordCoupledSelection(selection, facts.Tick.Value, method.Value, admissionFailure: commit.Conflict?.Reason ?? commit.Refusal?.Message);
    }

    /// <summary>
    /// The planned-room selection record after this review (#3278): the comparison's selected planned sites and assessed
    /// rooms replace the previous record (kept when the review compared nothing), and a room observed started clears.
    /// </summary>
    internal static PlannedSelection PlannedSelectionReviewed(PlannedSelection previous, ColonyProjection colony, ForecastCoupledSelection? selection)
    {
        var facts = colony.Facts;
        var latest = PlannedRules.PlannedSelectionOf(facts.CoupledAlternatives, selection, facts.PlannedSites);
        var plan = colony.LayoutPlan;
        var rooms = plan is not null ? plan.Rooms.DistinctBy(r => r.Id).ToDictionary(r => r.Id) : [];
        return PlannedRules.PlannedSelectionMemory(previous, latest,
            room => rooms.TryGetValue(room, out var planned) && RoundsBuildingPlanner.PlannedRoomStartedOf(colony, plan!, planned) == true,
            rooms.ContainsKey);
    }

    private static void RecordCoupledSelection(ForecastCoupledSelection selection, long tick, string method, bool protectedContinuation = false, string? admissionFailure = null)
        => Tel.Decide(new Decision
        {
            Kind = TelemetryKinds.CoupledSelectionKind, Component = "rounds", Target = method,
            Verdict = admissionFailure is not null ? "deferred" : protectedContinuation || selection.Selection.Selected.Length == 0 ? "continue" : "selected",
            Reason = admissionFailure is not null ? "method_not_admitted" : protectedContinuation ? "protected_method_ownership" : selection.Selection.SearchTruncated ? "best_found_bounded" : "shared_consequences",
            Attrs = CoupledSelectionAttrs(selection, tick, protectedContinuation, admissionFailure)
        });

    /// <summary>The search's accounting on the <c>rounds_review</c> row (#3722; keys in <see cref="RoundsReviewTelemetry"/>): scheduler
    /// expansions against the traversal bound, outcome evaluations against their budget, the mandatory baseline's share of each, both
    /// limits, and per phase the expansions (<c>search_phases</c>) and the profile (<c>search_profile</c>: wall, schedule searches,
    /// outcome evaluations, schedule wall, infeasible, unsupported, swaps, improved, and the pruning of #3724: pruned, unbounded).
    /// -1 and null when nothing was compared.</summary>
    internal static IEnumerable<(string Key, object? Value)> ReviewSearchAttrs(ForecastCoupledSelection? selection)
    {
        yield return (RoundsReviewTelemetry.CoupledSearchExpansionsKey, selection?.Selection.Expansions ?? -1);
        yield return (RoundsReviewTelemetry.CoupledSearchEvaluationsKey, selection?.OutcomeEvaluations ?? -1);
        yield return (RoundsReviewTelemetry.CoupledBaselineEvaluationsKey, selection?.BaselineEvaluations ?? -1);
        yield return (RoundsReviewTelemetry.CoupledBaselineExpansionsKey, selection?.BaselineExpansions ?? -1);
        yield return (RoundsReviewTelemetry.CoupledEvaluationBudgetKey, selection?.Budget.Evaluations ?? -1);
        yield return (RoundsReviewTelemetry.CoupledTraversalBoundKey, selection?.Budget.Traversal ?? -1);
        yield return (RoundsReviewTelemetry.SearchPhasesKey, selection?.SearchPhases.ToDictionary(p => p.Phase, p => (object?)p.Expansions));
        yield return (RoundsReviewTelemetry.SearchProfileKey, selection?.SearchPhases.ToDictionary(p => p.Phase, p => (object?)new Dictionary<string, object?>
        {
            ["ms"] = p.Ms, ["schedule_searches"] = p.ScheduleSearches, ["outcome_evaluations"] = p.OutcomeEvaluations, ["schedule_ms"] = p.ScheduleMs,
            ["infeasible"] = p.Infeasible, ["unsupported"] = p.Unsupported, ["swaps"] = p.Swaps, ["improved"] = p.Improved,
            ["pruned"] = p.Pruned, ["unbounded"] = p.Unbounded,
        }));
    }

    /// <summary>The rounds_review row's work shortfall and pawn values (#3825): per "work type/stat" its ticks a day, per colonist its
    /// value and parts in capability-days a day; both null when tier 2 did not run. Values awaiting a plan (#3857) are null, the plan
    /// named beside them.</summary>
    internal static IEnumerable<(string Key, object? Value)> PawnValueAttrs(RoundsFindings findings)
    {
        var awaiting = findings.PawnValuesAwaitingPlan.Length > 0;
        yield return (RoundsReviewTelemetry.WorkShortfallKey, findings.WorkShortfall?.Rows.ToDictionary(r => r.WorkType + "/" + r.Stat, r => (object?)r.TicksPerDay));
        yield return (RoundsReviewTelemetry.PawnValuesAwaitingPlanKey, awaiting ? findings.PawnValuesAwaitingPlan : null);
        yield return (RoundsReviewTelemetry.PawnValuesKey, findings.WorkShortfall is null || awaiting ? null : findings.PawnValues.ToDictionary(v => v.Pawn, v => (object?)new Dictionary<string, object?>
        {
            ["per_day"] = v.PerDay, ["work"] = v.WorkAdded, ["food"] = v.FoodUpkeep, ["food_haul"] = v.FoodHaul, ["breaks"] = v.BreakUpkeep, ["bed"] = v.BedUpkeep,
            ["shares"] = v.Work.ToDictionary(s => s.WorkType + "/" + s.Stat, s => (object?)s.TicksPerDay), ["not_modelled"] = v.NotModelled.ToArray(),
        }));
    }

    /// <summary>The <c>coupled_selection</c> row's attrs (docs/developers/contracts/otel-signals.md); the acceptance reader
    /// <c>CoupledSelections.Row</c> reads them back, the search accounting (#3722) included.</summary>
    internal static Dictionary<string, object?> CoupledSelectionAttrs(ForecastCoupledSelection selection, long tick, bool protectedContinuation, string? admissionFailure) => new()
            {
                [RimGovernor.Brain.Telemetry.Otlp.ForecastHandoffReader.Key] = RimGovernor.Brain.Telemetry.Otlp.ForecastHandoffReader.Fields(ForecastHandoffPolicy.Project(selection.Selected)),
                ["observed_tick"] = tick, ["horizon_ticks"] = selection.Horizon,
                ["protected_continuation"] = protectedContinuation, ["admission_failure"] = admissionFailure,
                ["selected_ids"] = selection.Selection.Selected,
                ["deferred_pawns"] = selection.Deferred.ToArray(),
                ["baseline"] = ConsequenceAttrs(selection.BaselineVector), ["selected"] = ConsequenceAttrs(selection.SelectedVector),
                ["baseline_thermal_exposure"] = ThermalAttrs(selection.Baseline), ["selected_thermal_exposure"] = ThermalAttrs(selection.Selected),
                ["rejected"] = selection.Rejected.Take(16).Select(r => new Dictionary<string, object?>
                { ["ids"] = r.Commitments.ToArray(), ["reason"] = r.Reason, ["vector"] = r.Consequences is { } v ? ConsequenceAttrs(v) : null }).ToArray(),
                ["explanation_truncated"] = selection.Rejected.Count > 16,
                ["generation_truncated"] = selection.GenerationTruncated, ["search_truncated"] = selection.Selection.SearchTruncated,
                ["composition_expansions"] = selection.CompositionExpansions, ["search_expansions"] = selection.Selection.Expansions,
                // The outcome-evaluation accounting (#3722): the evaluations against their budget, the mandatory baseline's share, the traversal bound.
                ["search_evaluations"] = selection.OutcomeEvaluations, ["baseline_evaluations"] = selection.BaselineEvaluations, ["baseline_expansions"] = selection.BaselineExpansions,
                ["evaluation_budget"] = selection.Budget.Evaluations, ["traversal_bound"] = selection.Budget.Traversal,
                // The pruning (#3724): the subsets and sweep prefixes a proven bound skipped, and why a bound was unavailable where it was.
                ["search_pruned"] = selection.SearchPhases.Sum(p => p.Pruned), ["bounds_unavailable"] = selection.BoundsUnavailable.ToArray(),
                ["search_phases"] = selection.SearchPhases.ToDictionary(p => p.Phase, p => (object?)p.Expansions),
                ["selected_phase"] = selection.SelectedPhase, ["warm_start"] = selection.WarmStart,
                ["uncertainty"] = selection.Selection.Findings,
                ["planned_rooms"] = selection.Selected.PlannedRooms.Select(r => new Dictionary<string, object?>
                { ["room"] = r.Room, ["completed_sites"] = r.CompletedSites.ToArray(), ["complete"] = r.Complete.ToArray() }).ToArray(),
                ["beneficiaries"] = selection.Selected.Needs.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new Dictionary<string, object?>
                {
                    ["pawn"] = p.Key,
                    ["needs"] = p.Value.Needs.Select(n => new Dictionary<string, object?>
                    {
                        ["kind"] = n.Kind, ["terminal"] = Number(n.FinalLevel), ["threshold_ticks"] = Number(n.ThresholdExposureTicks),
                        ["depth_ticks"] = Number(n.ThresholdDepthTicks), ["zero_ticks"] = Number(n.ZeroTicks)
                    }).ToArray(),
                    ["mood"] = p.Value.Mood is ForecastMoodResult.Projected mood ? new Dictionary<string, object?>
                        {
                            ["target_pressure_ticks"] = mood.PressureTicks, ["target_exposure_ticks"] = mood.TargetExposureTicks,
                            ["actual_exposure_ticks"] = mood.ActualExposureTicks, ["actual_depth_ticks"] = mood.ActualDepthTicks,
                            ["terminal"] = mood.TerminalMood, ["unknown"] = mood.Findings.ToArray()
                        } : p.Value.Mood is { } unprojected ? new Dictionary<string, object?>
                        {
                            ["target_pressure_ticks"] = null, ["target_exposure_ticks"] = null, ["actual_exposure_ticks"] = null,
                            ["actual_depth_ticks"] = null, ["terminal"] = null, ["unknown"] = unprojected.Findings.ToArray()
                        } : null
                }).ToArray(),
                ["service_investments"] = selection.ServiceInvestments.Select(i => new Dictionary<string, object?>
                { ["owner"] = i.Target.Owner.Value, ["predicate"] = i.Target.Predicate, ["horizon_ticks"] = i.HorizonTicks,
                    ["beneficiaries"] = i.Beneficiaries.ToArray(), ["tasks"] = i.Tasks.ToArray(),
                    ["metric"] = i.Metric, ["avoided_exposure"] = i.AvoidedExposure, ["retirement"] = i.Retirement }).ToArray(),
                ["predicted_tasks"] = selection.Selected.Plan.Allocations.Select(a => new Dictionary<string, object?>
                { ["task"] = a.Activity, ["pawn"] = a.Pawn, ["start"] = a.Start, ["end"] = a.End,
                    ["productive_learning"] = a.Learning is { } learning ? new Dictionary<string, object?>
                    {
                        ["skill"] = learning.Skill, ["observed_rate_work_ticks"] = learning.ObservedWorkTicks,
                        ["conditional_work_ticks"] = learning.WorkTicks, ["raw_xp"] = learning.Callbacks.Sum(c => c.RawXp * c.Count),
                        ["before_level"] = learning.Before.Level, ["after_level"] = learning.After.Level,
                        ["applied_gain"] = learning.After.AppliedGain, ["decay_loss"] = learning.After.DecayLoss,
                        ["provenance"] = learning.Provenance
                    } : null }).ToArray()
            };

    /// <summary>The target of a selection of planned sites that starts no order (#3278).</summary>
    internal const string PlannedUnplacedTarget = "planned_unplaced";

    /// <summary>The characters the <c>planned_room_selection</c> row's ids and findings may spend: a quarter of the 64 KiB
    /// payload cap is left for keys, numbers and JSON punctuation, so the row is never truncated (#3828).</summary>
    internal const int PlannedRoomSelectionBudget = 48 << 10;

    /// <summary>Per candidate root, the planned rooms whose site activities its completions reach through their prerequisites.</summary>
    private static Dictionary<string, int[]> RootRooms(ForecastCoupledCandidates candidates)
    {
        var context = candidates.Context;
        var siteRoom = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var site in context.PlannedSites)
            foreach (var activity in site.Activities.Concat(site.Finishes)) siteRoom.TryAdd(activity, site.Room);
        var activities = context.Activities.Activities.ToDictionary(a => a.Id, StringComparer.Ordinal);
        var result = new Dictionary<string, int[]>(StringComparer.Ordinal);
        foreach (var root in candidates.Alternatives.Concat(candidates.Incumbents))
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var rooms = new SortedSet<int>();
            var stack = new Stack<string>(root.ActivityCompletions);
            while (stack.Count > 0)
            {
                var id = stack.Pop();
                if (!seen.Add(id)) continue;
                if (siteRoom.TryGetValue(id, out var room)) rooms.Add(room);
                if (activities.TryGetValue(id, out var activity)) foreach (var prerequisite in activity.Prerequisites) stack.Push(prerequisite);
            }
            if (rooms.Count != 0) result.TryAdd(root.Id, [.. rooms]);
        }
        return result;
    }

    /// <summary>
    /// The <c>planned_room_selection</c> row's attrs (#3828; docs/developers/contracts/otel-signals.md): the compact companion of a
    /// <c>planned_unplaced</c> <c>coupled_selection</c> row, whose vectors can pass the payload cap and drop every field. It keeps,
    /// within <see cref="PlannedRoomSelectionBudget"/>: the selected ids and planned rooms; per planned room with a valued
    /// alternative or a root among the candidates its alternatives, root count and whether it is selected; the ledger total and
    /// development of the baseline and the selection; the selection findings that name a planned room or a truncation; and the
    /// rejected subsets once each (ids and reason, with the ledger total and development when priced), first the earliest touching
    /// each room, then the rest in search order, counting what the budget left out.
    /// </summary>
    internal static Dictionary<string, object?> PlannedRoomSelectionAttrs(ForecastCoupledSelection selection, ForecastCoupledCandidates candidates, long tick)
    {
        var left = PlannedRoomSelectionBudget;
        bool Spend(IEnumerable<string> strings, int overhead)
        {
            var cost = overhead + strings.Sum(s => s.Length + 3);
            if (cost > left) return false;
            left -= cost;
            return true;
        }
        static Dictionary<string, object?> ValueOf(ForecastConsequenceVector vector) => new()
        {
            ["total"] = Number(vector.Value.Ledger.Total), ["investment"] = vector.Value.Ledger.Investment, ["development"] = vector.Development,
        };
        var rootRooms = RootRooms(candidates);
        int[] RoomsOf(string id) => rootRooms.GetValueOrDefault(id) ?? [];
        var selected = selection.Selection.Selected.ToArray();
        Spend(selected, 64);
        var selectedRooms = selection.Selected.PlannedRooms.Select(r => r.Room).Concat(selected.SelectMany(RoomsOf)).ToHashSet();
        var plannedRooms = selection.Selected.PlannedRooms.Select(r => new Dictionary<string, object?>
        { ["room"] = r.Room, ["completed_sites"] = r.CompletedSites.ToArray(), ["complete"] = r.Complete.ToArray() }).ToArray();
        Spend(selection.Selected.PlannedRooms.SelectMany(r => r.CompletedSites.Concat(r.Complete)), 64 * plannedRooms.Length);
        var alternatives = candidates.Context.PlannedRooms.ToLookup(a => a.Room, a => a.Id);
        var roots = rootRooms.Values.SelectMany(r => r).ToLookup(r => r);
        var rooms = alternatives.Select(g => g.Key).Concat(roots.Select(g => g.Key)).Distinct().Order().ToArray();
        var roomRows = rooms.Select(room => new Dictionary<string, object?>
        {
            ["room"] = room, ["assessed"] = alternatives[room].Any(), ["alternatives"] = alternatives[room].ToArray(),
            ["roots"] = roots[room].Count(), ["selected"] = selectedRooms.Contains(room),
        }).ToArray();
        Spend(rooms.SelectMany(r => alternatives[r]), 96 * rooms.Length);
        // Findings that name a planned room, then the truncations that say what the comparison never saw.
        var findings = selection.Selection.Findings.Where(f => f.Contains("room", StringComparison.Ordinal) || f.Contains("planned", StringComparison.Ordinal))
            .Concat(selection.Selection.Findings.Where(f => f.Contains("truncated", StringComparison.Ordinal)))
            .Distinct(StringComparer.Ordinal).ToList();
        var uncertainty = findings.TakeWhile(f => Spend([f], 0)).ToArray();
        // Each rejected subset once (a subset rescheduled under one reason repeats), the earliest touching each room first.
        var distinct = selection.Rejected.DistinctBy(r => (string.Join("\n", r.Commitments.Order(StringComparer.Ordinal)), r.Reason)).ToList();
        var first = rooms.Select(room => distinct.FindIndex(r => r.Commitments.Any(id => RoomsOf(id).Contains(room)))).Where(i => i >= 0).Distinct().Order().ToList();
        var order = first.Concat(Enumerable.Range(0, distinct.Count).Where(i => !first.Contains(i)));
        var rejected = new List<Dictionary<string, object?>>();
        foreach (var index in order)
        {
            var row = distinct[index];
            if (!Spend([.. row.Commitments, row.Reason], 96)) break;
            rejected.Add(new()
            {
                ["ids"] = row.Commitments.ToArray(), ["reason"] = row.Reason,
                ["total"] = row.Consequences is { } v ? Number(v.Value.Ledger.Total) : null, ["development"] = row.Consequences?.Development,
            });
        }
        return new()
        {
            ["observed_tick"] = tick, ["selected_ids"] = selected, ["planned_rooms"] = plannedRooms, ["rooms"] = roomRows,
            ["baseline"] = ValueOf(selection.BaselineVector), ["selected"] = ValueOf(selection.SelectedVector),
            ["uncertainty"] = uncertainty, ["uncertainty_omitted"] = findings.Count - uncertainty.Length,
            ["rejected"] = rejected.ToArray(), ["rejected_distinct"] = distinct.Count, ["rejected_omitted"] = distinct.Count - rejected.Count,
            ["generation_truncated"] = selection.GenerationTruncated, ["search_truncated"] = selection.Selection.SearchTruncated,
        };
    }

    private static object[] ThermalAttrs(ForecastCoupledOutcome outcome) => outcome.ThermalExposure.Select(e => (object)new Dictionary<string, object?>
    {
        ["pawn"] = e.Pawn, ["assessed_ticks"] = e.AssessedTicks, ["unknown_ticks"] = e.UnknownTicks,
        ["cold_degree_ticks"] = e.AssessedColdDegreeTicks,
        ["initial_temperature"] = e.Spans.MinBy(s => s.Start)?.Initial,
        ["initial_comfortable_min"] = e.Spans.MinBy(s => s.Start)?.ComfortableMin,
        ["final_temperature"] = e.Spans.MaxBy(s => s.End)?.Final
    }).ToArray();

    private static double? Number(Fact<double> value) => value.IsKnown ? value.Value : null;
    private static double? Number(ForecastTerm term) => term is ForecastTerm.Assessed(var value) ? value : null;
    private static double? Number(LedgerTerm term) => term is LedgerTerm.Priced(var loss) ? loss : null;

    /// <summary>What an outcome was ranked by (#3522): the ledger term by term, the floors, and the policy constants.</summary>
    private static Dictionary<string, object?> ValueAttrs(ForecastValueExplanation value) => new()
    {
        ["total"] = Number(value.Ledger.Total), ["hunger"] = Number(value.Ledger.Hunger), ["rest"] = Number(value.Ledger.Rest),
        ["raw_ingredients"] = Number(value.Ledger.RawIngredients),
        ["food_poisoning"] = Number(value.Ledger.FoodPoisoning), ["thermal"] = Number(value.Ledger.Thermal),
        ["mood_breaks"] = Number(value.Ledger.MoodBreaks), ["wellbeing"] = Number(value.Ledger.Wellbeing),
        ["harm"] = Number(value.Ledger.Harm), ["harm_detail"] = value.Ledger.HarmDetail is { } detail ? new Dictionary<string, object?>
        {
            ["raids_per_day"] = detail.RaidsPerDay, ["exposure_range"] = new[] { detail.ExposureLo, detail.ExposureHi },
            ["down_probability"] = detail.DownProbability, ["death_probability"] = detail.DeathProbability, ["cvar"] = detail.Cvar,
            ["raid_loss"] = detail.RaidLoss,
            ["illness"] = detail.Illness is { } illness ? new Dictionary<string, object?>
            {
                ["incidents_per_day"] = illness.IncidentsPerDay, ["loss"] = illness.Loss, ["patient_days_per_day"] = illness.PatientDaysPerDay,
                ["tends_per_day"] = illness.TendsPerDay, ["deaths_per_day"] = illness.DeathsPerDay,
                ["pawns"] = illness.Pawns.Select(p => new Dictionary<string, object?>
                {
                    ["pawn"] = p.Pawn, ["patient_days_per_day"] = p.PatientDaysPerDay, ["tends_per_day"] = p.TendsPerDay, ["deaths_per_day"] = p.DeathsPerDay,
                }).ToArray(),
            } : null,
            ["escape"] = new Dictionary<string, object?>
            {
                ["escapes_per_day"] = detail.Escape.EscapesPerDay, ["loss"] = detail.Escape.Loss, ["deaths_per_day"] = detail.Escape.DeathsPerDay,
            },
            ["creepjoiner"] = new Dictionary<string, object?>
            {
                ["loss"] = detail.CreepJoiner.Loss, ["deaths_per_day"] = detail.CreepJoiner.DeathsPerDay,
                ["pawns"] = detail.CreepJoiner.Pawns.Select(p => new Dictionary<string, object?>
                {
                    ["pawn"] = p.Pawn, ["agony"] = p.Agony, ["aggressive"] = p.Aggressive, ["leaves"] = p.Leaves, ["hediffs"] = p.Hediffs,
                }).ToArray(),
            },
        } : null, ["investment"] = value.Ledger.Investment,
        ["investment_contributions"] = value.Ledger.InvestmentContributions.Select(c => new Dictionary<string, object?> { ["activity"] = c.Activity, ["pawn"] = c.Pawn, ["key"] = c.Key, ["units"] = c.Units, ["value"] = c.Value }).ToArray(),
        ["goals"] = value.Ledger.Goals,
        ["goal_contributions"] = value.Ledger.GoalContributions.Select(c => new Dictionary<string, object?> { ["goal"] = c.Goal, ["progress"] = c.Progress, ["worth"] = c.Worth, ["value"] = c.Value }).ToArray(),
        ["unknown"] = value.Ledger.Findings.ToArray(),
        ["floors"] = new Dictionary<string, object?>
        {
            ["urgent_zero"] = FloorAttrs(value.UrgentZero), ["break_risk_cap"] = FloorAttrs(value.BreakRisk), ["death_risk_floor"] = FloorAttrs(value.Death)
        },
        ["policy"] = new Dictionary<string, object?>
        {
            ["death_terminal_value"] = value.Policy.DeathTerminalValue, ["wellbeing_weight"] = value.Policy.WellbeingWeight,
            ["harm_risk_alpha"] = value.Policy.HarmRiskAlpha, ["harm_risk_weight"] = value.Policy.HarmRiskWeight,
            ["death_risk_floor"] = value.Policy.DeathRiskFloor, ["break_risk_cap"] = value.Policy.BreakRiskCap,
            ["tie_resolution"] = value.Policy.TieResolution, ["goal_value_low"] = value.Policy.GoalImportanceValue.Low, ["goal_value_normal"] = value.Policy.GoalImportanceValue.Normal,
            ["goal_value_high"] = value.Policy.GoalImportanceValue.High, ["goal_deadline_decay"] = value.Policy.GoalDeadlineDecay, ["goal_deadline_floor"] = value.Policy.GoalDeadlineFloor
        }
    };

    private static Dictionary<string, object?> FloorAttrs(ForecastFloor floor) => new() { ["assessed"] = floor.Assessed, ["passed"] = floor.Passed, ["excess"] = floor.Excess };

    private static Dictionary<string, object?> ConsequenceAttrs(ForecastConsequenceVector vector) => new()
    {
        ["urgent"] = vector.Urgent.Select(v => (object?)Number(v)).ToArray(),
        ["needs"] = vector.Needs.Select(v => (object?)Number(v)).ToArray(),
        ["mood_exposure"] = Number(vector.MoodExposure),
        ["mood_pressure"] = Number(vector.MoodDepth),
        ["mood_break_risk"] = Number(vector.BreakRisk),
        ["unassessed_terms"] = vector.Urgent.Concat(vector.Needs).Append(vector.MoodExposure).Append(vector.MoodDepth).Append(vector.BreakRisk)
            .OfType<ForecastTerm.Unassessed>().Select(t => t.Reason).Distinct().ToArray(),
        ["thermal_occupancy"] = vector.ThermalOccupancy,
        ["support_urgent_first_care_ticks"] = vector.Support.UrgentFirstCareTicks,
        ["support_pending_care_batches"] = vector.Support.PendingCareBatches,
        ["support_first_care_ticks"] = vector.Support.FirstCareTicks,
        ["support_unsupplied_demand"] = vector.Support.UnsuppliedDemand,
        ["support_food_poison_exposure"] = vector.Support.FoodPoisonExposure,
        ["support_food_production"] = vector.Support.FoodProduction,
        ["support_care_services"] = vector.Support.CareServices,
        ["support_care_room_factor"] = vector.Support.CareRoom is CareRoomRead.Assessed(var room) ? room : null,
        ["support_tend_quality"] = vector.Support.TendQuality,
        ["support_surgery_room_factor"] = vector.Support.SurgeryRoomFactor,
        ["maintenance"] = vector.Maintenance.Select(m => new Dictionary<string, object?>
        { ["requirement"] = m.Requirement, ["missing_xp"] = m.MissingXp is ForecastMaintenanceGap.Projected(var xp) ? xp : null, ["unknown"] = m.Findings.ToArray() }).ToArray(),
        ["development"] = vector.Development, ["labor"] = vector.Labor, ["unknown"] = vector.Unknown.ToArray(),
        ["value"] = ValueAttrs(vector.Value)
    };
}
