using RimGovernor.Brain.Acceptance;
using RimGovernor.Brain.Acceptance.Cases;
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
}