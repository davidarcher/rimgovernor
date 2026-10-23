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
            if (selection.Selected.PlannedRooms.Count > 0) RecordCoupledSelection(selection, facts.Tick.Value, "planned_unplaced");
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
        var rooms = colony.LayoutPlan.TryGetValue(out var plan) ? plan.Rooms.DistinctBy(r => r.Id).ToDictionary(r => r.Id) : [];
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
    /// outcome evaluations, schedule wall, infeasible, unsupported, swaps, improved). -1 and null when nothing was compared.</summary>
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
                    ["mood"] = p.Value.Mood is { } mood ? new Dictionary<string, object?>
                    {
                        ["target_pressure_ticks"] = Number(mood.PressureTicks), ["target_exposure_ticks"] = Number(mood.TargetExposureTicks),
                        ["actual_exposure_ticks"] = Number(mood.ActualExposureTicks), ["actual_depth_ticks"] = Number(mood.ActualDepthTicks),
                        ["terminal"] = Number(mood.TerminalMood), ["unknown"] = mood.Findings.ToArray()
                    } : null
                }).ToArray(),
                ["service_investments"] = selection.ServiceInvestments.Select(i => new Dictionary<string, object?>
                { ["owner"] = i.Target.Owner.Value, ["predicate"] = i.Target.Predicate, ["horizon_ticks"] = i.HorizonTicks,
                    ["beneficiaries"] = i.Beneficiaries.ToArray(), ["tasks"] = i.Tasks.ToArray(),
                    ["metric"] = i.Metric, ["avoided_exposure"] = Number(i.AvoidedExposure), ["retirement"] = i.Retirement }).ToArray(),
                ["predicted_tasks"] = selection.Selected.Plan.Allocations.Select(a => new Dictionary<string, object?>
                { ["task"] = a.Activity, ["pawn"] = a.Pawn, ["start"] = a.Start, ["end"] = a.End,
                    ["productive_learning"] = a.Learning is { } learning ? new Dictionary<string, object?>
                    {
                        ["skill"] = learning.Skill, ["observed_rate_work_ticks"] = learning.ObservedWorkTicks,
                        ["conditional_work_ticks"] = learning.WorkTicks, ["raw_xp"] = learning.Callbacks.Sum(c => c.RawXp.IsKnown ? c.RawXp.Value * c.Count : 0),
                        ["before_level"] = learning.Before.Level.IsKnown ? learning.Before.Level.Value : null,
                        ["after_level"] = learning.After.Level.IsKnown ? learning.After.Level.Value : null,
                        ["applied_gain"] = Number(learning.After.AppliedGain), ["decay_loss"] = Number(learning.After.DecayLoss),
                        ["provenance"] = learning.Provenance
                    } : null }).ToArray()
            };

    private static object[] ThermalAttrs(ForecastCoupledOutcome outcome) => outcome.ThermalExposure.Select(e => (object)new Dictionary<string, object?>
    {
        ["pawn"] = e.Pawn, ["assessed_ticks"] = e.AssessedTicks, ["unknown_ticks"] = e.UnknownTicks,
        ["cold_degree_ticks"] = e.AssessedColdDegreeTicks,
        ["initial_temperature"] = e.Spans.MinBy(s => s.Start)?.Initial,
        ["initial_comfortable_min"] = e.Spans.MinBy(s => s.Start)?.ComfortableMin,
        ["final_temperature"] = e.Spans.MaxBy(s => s.End)?.Final
    }).ToArray();

    private static double? Number(Fact<double> value) => value.IsKnown ? value.Value : null;

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
        } : null, ["investment"] = Number(value.Ledger.Investment),
        ["investment_contributions"] = value.Ledger.InvestmentContributions.Select(c => new Dictionary<string, object?> { ["activity"] = c.Activity, ["pawn"] = c.Pawn, ["key"] = c.Key, ["units"] = c.Units, ["value"] = c.Value }).ToArray(),
        ["goals"] = Number(value.Ledger.Goals),
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
        ["urgent"] = vector.Urgent.Select(v => v.IsKnown ? (object)v.Value : null).ToArray(),
        ["needs"] = vector.Needs.Select(v => v.IsKnown ? (object)v.Value : null).ToArray(),
        ["mood_exposure"] = vector.MoodExposure.IsKnown ? vector.MoodExposure.Value : null,
        ["mood_pressure"] = vector.MoodDepth.IsKnown ? vector.MoodDepth.Value : null,
        ["mood_break_risk"] = Number(vector.BreakRisk),
        ["thermal_occupancy"] = vector.ThermalOccupancy,
        ["support_urgent_first_care_ticks"] = Number(vector.Support.UrgentFirstCareTicks),
        ["support_pending_care_batches"] = vector.Support.PendingCareBatches,
        ["support_first_care_ticks"] = vector.Support.FirstCareTicks,
        ["support_unsupplied_demand"] = vector.Support.UnsuppliedDemand,
        ["support_food_poison_exposure"] = Number(vector.Support.FoodPoisonExposure),
        ["support_food_production"] = vector.Support.FoodProduction,
        ["support_care_services"] = vector.Support.CareServices,
        ["support_care_room_factor"] = Number(vector.Support.CareRoomFactor),
        ["support_tend_quality"] = Number(vector.Support.TendQuality),
        ["support_surgery_room_factor"] = Number(vector.Support.SurgeryRoomFactor),
        ["maintenance"] = vector.Maintenance.Select(m => new Dictionary<string, object?>
        { ["requirement"] = m.Requirement, ["missing_xp"] = Number(m.MissingXp), ["unknown"] = m.Findings.ToArray() }).ToArray(),
        ["development"] = vector.Development, ["labor"] = vector.Labor, ["unknown"] = vector.Unknown.ToArray(),
        ["value"] = ValueAttrs(vector.Value)
    };
}
