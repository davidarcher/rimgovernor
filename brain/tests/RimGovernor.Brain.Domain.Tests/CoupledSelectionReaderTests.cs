using RimGovernor.Brain.Acceptance;
using RimGovernor.Brain.Acceptance.Cases;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Telemetry;
using RimGovernor.Brain.Telemetry.Otlp;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class CoupledSelectionReaderTests
{
    [Fact]
    public void OtlpIntegerRoomSurvivesTimelineNumericNormalization()
    {
        // #3705: remote run 39719313179 selected room 1, but the acceptance reader filtered
        // for long after OtlpTimeline had normalized every number (including room IDs) to double.
        var log = OtlpReader.DecodeLog("""
            {"resourceLogs":[{"scopeLogs":[{"logRecords":[{
              "eventName":"coupled_selection",
              "attributes":[
                {"key":"rimgovernor.row","value":{"stringValue":"decision"}},
                {"key":"rimgovernor.verdict","value":{"stringValue":"selected"}},
                {"key":"rimgovernor.attr.planned_rooms","value":{"arrayValue":{"values":[
                  {"kvlistValue":{"values":[{"key":"room","value":{"intValue":"1"}}]}}
                ]}}},
                {"key":"rimgovernor.attr.baseline","value":{"kvlistValue":{"values":[
                  {"key":"value","value":{"kvlistValue":{"values":[
                    {"key":"thermal","value":{"doubleValue":1.64704}}
                  ]}}}
                ]}}},
                {"key":"rimgovernor.attr.selected","value":{"kvlistValue":{"values":[
                  {"key":"value","value":{"kvlistValue":{"values":[
                    {"key":"thermal","value":{"doubleValue":1.05962666666667}}
                  ]}}}
                ]}}}
              ]
            }]}]}]}
            """u8);
        Assert.NotNull(log);
        var timeline = OtlpTimeline.FromLog(log);
        Assert.NotNull(timeline);
        var selection = CoupledSelections.Row(LogRow.From(timeline, 259));
        Assert.Equal("selected", selection.Verdict);
        Assert.Equal(1d, Assert.Single(selection.PlannedRooms));
        Assert.Equal(1.64704, selection.Baseline.Thermal);
        Assert.Equal(1.05962666666667, selection.Selected.Thermal);
        Assert.True(selection.Baseline.Thermal > selection.Selected.Thermal);
    }

    [Fact]
    public void MissingRoomIdentityDoesNotBecomeRoomZero()
    {
        var row = new LogRow { Payload = new Dictionary<string, object?>
        {
            ["planned_rooms"] = new List<object?> { new Dictionary<string, object?>() }
        } };
        Assert.Empty(CoupledSelections.Row(row).PlannedRooms);
    }

    /// <summary>#3722: the search's accounting travels under its own names, on <c>coupled_selection</c> (read back by the acceptance reader)
    /// and on <c>rounds_review</c> (keys in <see cref="RoundsReviewTelemetry"/>), the profile's schedule searches and outcome evaluations apart.</summary>
    [Fact]
    public void SearchAccountingIsWrittenAndReadUnderItsOwnNames()
    {
        var facts = ForecastCoupledSelectionTests.Review();
        var request = ForecastCoupled.CoupledForecastRequestOf(new("world/map/load", 100), 300d / GameTime.TicksPerDay,
            facts, new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, [], new());
        var selection = ForecastCoupledSelection.CompareActivityOutcomes(request, facts.CoupledAlternatives!, new(1, Traversal: 500));
        Assert.Equal(1, selection.OutcomeEvaluations - selection.BaselineEvaluations);

        var row = CoupledSelections.Row(new LogRow { Payload = Rounder.CoupledSelectionAttrs(selection, 100, false, null) });
        Assert.Equal(selection.Selection.Expansions, row.Search.Expansions);
        Assert.Equal(selection.OutcomeEvaluations, row.Search.Evaluations);
        Assert.Equal(selection.BaselineEvaluations, row.Search.BaselineEvaluations);
        Assert.Equal(selection.BaselineExpansions, row.Search.BaselineExpansions);
        Assert.Equal(1, row.Search.EvaluationBudget);
        Assert.Equal(500, row.Search.TraversalBound);
        // The pruning (#3724): the pruned count and the unavailable bounds travel and read back under their own names.
        Assert.Equal(selection.SearchPhases.Sum(p => p.Pruned), row.Search.Pruned);
        Assert.Equal(selection.BoundsUnavailable, row.Search.BoundsUnavailable);
        var empty = CoupledSelections.Row(new LogRow { Payload = new Dictionary<string, object?>() }).Search;
        Assert.Null(empty.Evaluations);
        Assert.Null(empty.Pruned);
        Assert.Null(empty.BoundsUnavailable);
        var withBounds = Rounder.CoupledSelectionAttrs(selection with { BoundsUnavailable = ["urgent_zero_unserved:goal_routes"] }, 100, false, null);
        Assert.Equal(["urgent_zero_unserved:goal_routes"], CoupledSelections.Row(new LogRow { Payload = withBounds }).Search.BoundsUnavailable);

        var review = Rounder.ReviewSearchAttrs(selection).ToDictionary(a => a.Key, a => a.Value);
        Assert.Equal(selection.Selection.Expansions, review[RoundsReviewTelemetry.CoupledSearchExpansionsKey]);
        Assert.Equal(selection.OutcomeEvaluations, review[RoundsReviewTelemetry.CoupledSearchEvaluationsKey]);
        Assert.Equal(selection.BaselineEvaluations, review[RoundsReviewTelemetry.CoupledBaselineEvaluationsKey]);
        Assert.Equal(selection.BaselineExpansions, review[RoundsReviewTelemetry.CoupledBaselineExpansionsKey]);
        Assert.Equal(1, review[RoundsReviewTelemetry.CoupledEvaluationBudgetKey]);
        Assert.Equal(500, review[RoundsReviewTelemetry.CoupledTraversalBoundKey]);
        var phases = Assert.IsType<Dictionary<string, object?>>(review[RoundsReviewTelemetry.SearchPhasesKey]);
        var profile = Assert.IsType<Dictionary<string, object?>>(review[RoundsReviewTelemetry.SearchProfileKey]);
        Assert.Equal(selection.SearchPhases.Select(p => p.Phase), phases.Keys);
        Assert.Equal(selection.SearchPhases.Select(p => p.Phase), profile.Keys);
        foreach (var phase in selection.SearchPhases)
        {
            Assert.Equal(phase.Expansions, phases[phase.Phase]);
            var profiled = Assert.IsType<Dictionary<string, object?>>(profile[phase.Phase]);
            Assert.Equal(phase.ScheduleSearches, profiled["schedule_searches"]);
            Assert.Equal(phase.OutcomeEvaluations, profiled["outcome_evaluations"]);
            Assert.Equal(phase.Pruned, profiled["pruned"]);
            Assert.Equal(phase.Unbounded, profiled["unbounded"]);
            Assert.False(profiled.ContainsKey("evaluations"));
        }
        Assert.Equal(selection.BaselineEvaluations, Assert.IsType<Dictionary<string, object?>>(profile["baseline"])["outcome_evaluations"]);

        var none = Rounder.ReviewSearchAttrs(null).ToDictionary(a => a.Key, a => a.Value);
        Assert.Equal(-1, none[RoundsReviewTelemetry.CoupledSearchExpansionsKey]);
        Assert.Equal(-1, none[RoundsReviewTelemetry.CoupledSearchEvaluationsKey]);
        Assert.Equal(-1, none[RoundsReviewTelemetry.CoupledEvaluationBudgetKey]);
        Assert.Equal(-1, none[RoundsReviewTelemetry.CoupledTraversalBoundKey]);
        Assert.Null(none[RoundsReviewTelemetry.SearchProfileKey]);
    }
}
