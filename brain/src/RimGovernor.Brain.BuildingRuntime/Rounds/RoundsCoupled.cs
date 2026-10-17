namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class Rounder
{
    /// <summary>Normal review sequencing only. Policy selected the graph; the admin/work-assignments Need's
    /// claimed commit, the Method journal and Hands retain admission, claims, execution and observed continuation.</summary>
    /// <returns>The pawns whose orders this review just committed or released: standby must not take them.</returns>
    private HashSet<string> AdmitCoupledSelection(RoundsResult review, RoundsFacts facts, EquatableArray<ForecastObservedIncumbent> incumbents, GenerationSnapshot snapshot,
        CancellationToken call, CancellationToken epoch)
    {
        var taken = new HashSet<string>(StringComparer.Ordinal);
        if (review.Needs.CoupledSelection is not { } selection || facts.CoupledAlternatives is not { } candidates) return taken;
        var execution = ForecastCoupledExecution.ReviewCoupledExecution(selection, incumbents, facts.Tick.Value);
        if (execution.Releases.Count > 0)
        {
            Player.Current(call, epoch);
            if (Player.Session.State().Snapshot != snapshot) throw new ControlException("coupled activity scope changed");
            foreach (var release in execution.Releases)
            {
                var next = PlanIds.MintPlanId();
                taken.UnionWith(release.Batch.Orders.Select(o => o.Pawn));
                Player.Journal.ContinueActivityMethod(release.Incumbent.Plan, snapshot, facts.Tick,
                    [release.Incumbent.Ownership, .. release.Incumbent.ReleasedOwnership], new PlanSpec(next, new PlanRevision(1),
                        [DomainAction.NewPawnActivitiesAction(new ActionId($"{next}-0"), release.Batch)]), call);
            }
            RecordCoupledSelection(selection, facts.Tick.Value, "release_for_reconsideration");
            return taken; // Fresh native ownership evidence precedes replacement admission.
        }
        if (execution.Dispatch is not { } dispatch)
        { RecordCoupledSelection(selection, facts.Tick.Value, "retained_method_future_work", true); return taken; }
        selection = dispatch;
        if (selection.Selection.Selected.Length == 0 && selection.Deferred.Count == 0)
        { RecordCoupledSelection(selection, facts.Tick.Value, "continue", facts.CoupledWorkActive); return taken; }
        // The activity is the admin/work-assignments Need's Method (#3304); a runtime without the Need starts none.
        if (!Needs.Contains(AdminFamily.WorkAssignments)) return taken;
        var id = PlanIds.MintPlanId();
        var method = new MethodId($"coupled-{id}");
        var activity = ForecastCoupledSelection.StartCoupledActivity(selection, candidates, snapshot.Native.Value, method.Value, facts.Tick.Value);
        if (activity is null)
        {
            // Selected planned sites start no order: the room reconcile places them from the review's RoomSelection (#3278).
            if (selection.Selected.PlannedRooms.Count > 0) RecordCoupledSelection(selection, facts.Tick.Value, "planned_unplaced");
            return taken;
        }
        Player.Current(call, epoch);
        if (Player.Session.State().Snapshot != snapshot) throw new ControlException("coupled activity scope changed");
        try
        {
            var commit = Player.Journal.CommitClaimedMethod(AdminFamily.WorkAssignments, snapshot, method, "shared colony consequence portfolio",
                new PlanSpec(id, new PlanRevision(1), [DomainAction.NewPawnActivitiesAction(new ActionId($"{id}-0"), activity)]), call);
            RecordCoupledSelection(selection, facts.Tick.Value, method.Value, admissionFailure: commit.Conflict?.Reason);
            if (commit.Outcome != ClaimOutcome.Conflict) taken.UnionWith(activity.Orders.Select(o => o.Pawn));
        }
        catch (NotAdmittedException refusal)
        {
            // A proposal owns nothing until admitted. Refusal must not revoke authority
            // or interrupt the useful Methods whose current claims prevented it.
            RecordCoupledSelection(selection, facts.Tick.Value, method.Value, admissionFailure: refusal.Message);
        }
        return taken;
    }

    /// <summary>
    /// The explicit idle (#3322): after this review's coupled admission, each controlled colonist still without a Hands
    /// assignment gets standby, one claimed Method per colonist under the admin/work-assignments Need. Without it an
    /// unassigned colonist holds the clock (unassigned_voluntary_pawn) and a quiet colony never advances.
    /// </summary>
    private void AdmitStandby(RoundsFacts facts, WorkActionCensus actions, IReadOnlySet<string> taken, GenerationSnapshot snapshot,
        CancellationToken call, CancellationToken epoch)
    {
        if (!Needs.Contains(AdminFamily.WorkAssignments)) return;
        var pawns = PolicyFunctions.StandbyPawnsOf(facts, actions, taken);
        if (pawns.Count == 0) return;
        Player.Current(call, epoch);
        if (Player.Session.State().Snapshot != snapshot) throw new ControlException("standby scope changed");
        foreach (var pawn in pawns)
        {
            var id = PlanIds.MintPlanId();
            var method = new MethodId($"standby-{id}");
            try
            {
                var commit = Player.Journal.CommitClaimedMethod(AdminFamily.WorkAssignments, snapshot, method, "explicit idle: nothing selected",
                    new PlanSpec(id, new PlanRevision(1), [DomainAction.NewPawnActivitiesAction(new ActionId($"{id}-0"),
                        PolicyFunctions.StandbyActivity(pawn, snapshot.Native.Value, method.Value))]), call);
                Tel.Decide(new Decision { Kind = "standby", Component = "rounds", Target = pawn,
                    Verdict = commit.Outcome == ClaimOutcome.Conflict ? "deferred" : "selected",
                    Reason = commit.Conflict?.Reason ?? "nothing_selected", Attrs = new Dictionary<string, object?> { ["method"] = method.Value } });
            }
            catch (NotAdmittedException refusal)
            {
                Tel.Decide(new Decision { Kind = "standby", Component = "rounds", Target = pawn, Verdict = "deferred",
                    Reason = "method_not_admitted", Attrs = new Dictionary<string, object?> { ["method"] = method.Value, ["refusal"] = refusal.Message } });
            }
        }
    }

    /// <summary>
    /// The planned-room selection record after this review (#3278): the comparison's selected planned sites and assessed
    /// rooms replace the previous record (kept when the review compared nothing), and a room observed started clears.
    /// </summary>
    internal static PlannedSelection PlannedSelectionReviewed(PlannedSelection previous, ColonyProjection colony, ForecastCoupledSelection? selection)
    {
        var facts = colony.Facts;
        var latest = PolicyFunctions.PlannedSelectionOf(facts.CoupledAlternatives, selection, facts.PlannedSites, facts.Tick.Value);
        var rooms = colony.LayoutPlan.TryGetValue(out var plan) ? plan.Rooms.DistinctBy(r => r.Id).ToDictionary(r => r.Id) : [];
        return PolicyFunctions.PlannedSelectionMemory(previous, latest,
            room => rooms.TryGetValue(room, out var planned) && PlannedRoomStartedOf(colony, plan!, planned) == true);
    }

    private static void RecordCoupledSelection(ForecastCoupledSelection selection, long tick, string method, bool protectedContinuation = false, string? admissionFailure = null)
        => Tel.Decide(new Decision
        {
            Kind = "coupled_selection", Component = "rounds", Target = method,
            Verdict = admissionFailure is not null ? "deferred" : protectedContinuation || selection.Selection.Selected.Length == 0 ? "continue" : "selected",
            Reason = admissionFailure is not null ? "method_not_admitted" : protectedContinuation ? "protected_method_ownership" : selection.Selection.SearchTruncated ? "best_found_bounded" : "shared_consequences",
            Attrs = new Dictionary<string, object?>
            {
                ["observed_tick"] = tick, ["horizon_ticks"] = selection.Horizon,
                ["protected_continuation"] = protectedContinuation, ["admission_failure"] = admissionFailure,
                ["selected_ids"] = selection.Selection.Selected,
                ["deferred_pawns"] = selection.Deferred.Select(t => t.Pawn).ToArray(),
                ["baseline"] = ConsequenceAttrs(selection.BaselineVector), ["selected"] = ConsequenceAttrs(selection.SelectedVector),
                ["rejected"] = selection.Rejected.Take(16).Select(r => new Dictionary<string, object?>
                { ["ids"] = r.Commitments.ToArray(), ["reason"] = r.Reason, ["vector"] = r.Consequences is { } v ? ConsequenceAttrs(v) : null }).ToArray(),
                ["explanation_truncated"] = selection.Rejected.Count > 16,
                ["generation_truncated"] = selection.GenerationTruncated, ["search_truncated"] = selection.Selection.SearchTruncated,
                ["composition_expansions"] = selection.CompositionExpansions, ["search_expansions"] = selection.Selection.Expansions,
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
                        ["conditional_work_ticks"] = learning.WorkTicks, ["raw_xp"] = Number(learning.Callback.RawXp),
                        ["before_level"] = learning.Before.Level.IsKnown ? learning.Before.Level.Value : null,
                        ["after_level"] = learning.After.Level.IsKnown ? learning.After.Level.Value : null,
                        ["applied_gain"] = Number(learning.After.AppliedGain), ["decay_loss"] = Number(learning.After.DecayLoss),
                        ["provenance"] = learning.Provenance
                    } : null }).ToArray()
            }
        });

    private static double? Number(Fact<double> value) => value.IsKnown ? value.Value : null;

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
        ["development"] = vector.Development, ["labor"] = vector.Labor, ["unknown"] = vector.Unknown.ToArray()
    };
}
