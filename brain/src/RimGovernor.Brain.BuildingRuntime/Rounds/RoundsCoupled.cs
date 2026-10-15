namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class Rounder
{
    /// <summary>Normal review sequencing only. Policy selected the graph; the existing assignment
    /// Concern, Method journal and Hands retain admission, claims, execution and observed continuation.</summary>
    private void AdmitCoupledSelection(RoundsResult review, RoundsFacts facts, GenerationSnapshot snapshot,
        CancellationToken call, CancellationToken epoch)
    {
        if (review.Needs.CoupledSelection is not { } selection || facts.CoupledAlternatives is not { } candidates) return;
        if (selection.Selection.Selected.Length == 0)
        { RecordCoupledSelection(selection, facts.Tick.Value, "continue", facts.CoupledWorkActive); return; }
        var (owner, workable) = Player.Journal.Workable(review.Review, PolicyApi.EnsureWorkAssignments, call);
        if (owner is null || !workable) return;
        var id = PlanIds.MintPlanId();
        var method = new MethodId($"coupled-{id}");
        RecordCoupledSelection(selection, facts.Tick.Value, method.Value);
        var activity = PolicyFunctions.StartCoupledActivity(selection, candidates, snapshot.Native.Value, method.Value, facts.Tick.Value);
        if (activity is null) return;
        Player.Current(call, epoch);
        if (Player.Session.State().Snapshot != snapshot) throw new ControlException("coupled activity scope changed");
        Player.Journal.CommitMethodReason(owner.Standard.Id, owner.Revision, method, "shared colony consequence portfolio",
            new PlanSpec(id, new PlanRevision(1), [DomainAction.NewPawnActivitiesAction(new ActionId($"{id}-0"), activity)]), call);
    }

    private static void RecordCoupledSelection(ForecastCoupledSelection selection, long tick, string method, bool protectedContinuation = false)
        => Tel.Decide(new Decision
        {
            Kind = "coupled_selection", Component = "rounds", Target = method,
            Verdict = protectedContinuation || selection.Selection.Selected.Length == 0 ? "continue" : "selected",
            Reason = protectedContinuation ? "protected_method_ownership" : selection.Selection.SearchTruncated ? "best_found_bounded" : "shared_consequences",
            Attrs = new Dictionary<string, object?>
            {
                ["observed_tick"] = tick, ["horizon_ticks"] = selection.Horizon,
                ["protected_continuation"] = protectedContinuation,
                ["selected_ids"] = selection.Selection.Selected,
                ["baseline"] = ConsequenceAttrs(selection.BaselineVector), ["selected"] = ConsequenceAttrs(selection.SelectedVector),
                ["rejected"] = selection.Rejected.Take(128).Select(r => new Dictionary<string, object?>
                { ["ids"] = r.Commitments.ToArray(), ["reason"] = r.Reason, ["vector"] = r.Consequences is { } v ? ConsequenceAttrs(v) : null }).ToArray(),
                ["explanation_truncated"] = selection.Rejected.Count > 128,
                ["generation_truncated"] = selection.GenerationTruncated, ["search_truncated"] = selection.Selection.SearchTruncated,
                ["composition_expansions"] = selection.CompositionExpansions, ["search_expansions"] = selection.Selection.Expansions,
                ["uncertainty"] = selection.Selection.Findings,
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
