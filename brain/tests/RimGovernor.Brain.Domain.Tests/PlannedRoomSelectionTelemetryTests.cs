using RimGovernor.Brain.Runtime;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Telemetry;
using RimGovernor.Brain.Telemetry.Otlp;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class PlannedRoomSelectionTelemetryTests
{
    /// <summary>
    /// #3828: the live <c>planned_unplaced</c> coupled_selection rows passed the 64 KiB payload cap and kept only their correlation
    /// keys, so nothing said which rooms were assessed or why the barracks lost. The planned_room_selection companion keeps, within the
    /// cap, the selected ids, the planned rooms assessed, the rejected ids with reasons (those touching each room first) and the
    /// findings naming planned rooms, however large the comparison's explanation grows.
    /// </summary>
    [Fact]
    public async Task ALargePlannedSelectionKeepsItsRoomVerdictsWithinThePayloadCap()
    {
        var facts = PlannedShelterTests.UnplacedHeatedRoom();
        var candidates = facts.CoupledAlternatives!;
        var real = ForecastCoupledSelection.CompareActivityOutcomes(ForecastCoupled.CoupledForecastRequestOf(new(100),
            PlannedShelterTests.Horizon / GameTime.TicksPerDay, facts, new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, []), candidates);
        Assert.NotEmpty(real.Selected.PlannedRooms);
        var roomRejected = real.Rejected.Where(r => r.Commitments.Any(id => id.StartsWith("planned/room1/", StringComparison.Ordinal))).ToArray();
        Assert.NotEmpty(roomRejected);

        // Inflate the explanation past the cap: many long findings that name no room, a few that do, and hundreds of distinct
        // rejected subsets of unrelated roots ahead of the real ones, each carrying a full consequence vector.
        var noise = Enumerable.Range(0, 3000).Select(i => $"schedule_unassessed:pawn{i}:{new string('x', 40)}");
        string[] roomFindings = ["planned_room_alternative_unvalued:planned/room1/WoodLog/Full:stove", "portfolio_search_truncated"];
        var filler = Enumerable.Range(0, 600).Select(i => new ForecastPortfolioEvidence(
            [$"filler/{i}/{new string('y', 120)}", $"filler/{i}/b"], real.SelectedVector, ForecastPortfolioEvidence.LostToLeaderPrefix + "labor"));
        var large = real with
        {
            Selection = real.Selection with { Findings = [.. noise, .. roomFindings, .. real.Selection.Findings] },
            Rejected = EquatableArray.From([.. filler, .. real.Rejected]),
        };

        var full = RowJson.Encode(RowValues.Normalize(ColonyReview.CoupledSelectionAttrs(large, 2500, false, null)))!;
        Assert.True(full.Length > TelemetryEmitter.PayloadCapBytes, $"the full row is {full.Length} bytes");
        var attrs = ColonyReview.PlannedRoomSelectionAttrs(large, candidates, 2500);
        var compact = RowJson.Encode(RowValues.Normalize(attrs))!;
        Assert.True(compact.Length <= TelemetryEmitter.PayloadCapBytes, $"the companion is {compact.Length} bytes");

        // Through the production emitter and back: the full row is capped, the companion is whole.
        var directory = Path.Combine(Path.GetTempPath(), "planned-room-selection-" + Guid.NewGuid().ToString("N"));
        List<TimelineRecord> rows;
        try
        {
            await using (var writer = OtlpFileWriter.Start(new() { Dir = directory }))
            {
                var emitter = new TelemetryEmitter(writer);
                emitter.Decide(new() { Kind = TelemetryKinds.CoupledSelectionKind, Component = "rounds", Verdict = "selected", Reason = "shared_consequences",
                    Target = ColonyReview.PlannedUnplacedTarget, Attrs = ColonyReview.CoupledSelectionAttrs(large, 2500, false, null) });
                emitter.Decide(new() { Kind = TelemetryKinds.PlannedRoomSelectionKind, Component = "rounds", Verdict = "selected", Reason = "shared_consequences",
                    Target = ColonyReview.PlannedUnplacedTarget, Attrs = attrs });
            }
            rows = OtlpTimeline.Read(Path.Combine(directory, "logs.jsonl"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        Assert.Equal(true, rows.Single(r => r.Kind == TelemetryKinds.CoupledSelection.Name).Payload.GetValueOrDefault("truncated"));
        var payload = rows.Single(r => r.Kind == TelemetryKinds.PlannedRoomSelection.Name).Payload;
        Assert.False(payload.ContainsKey("truncated"));
        var row = Assert.IsType<Dictionary<string, object?>>(payload[RimGovernor.Brain.Telemetry.Decision.AttrsKey]);

        Assert.Equal(large.Selection.Selected, Strings(row["selected_ids"]));
        var planned = List(row["planned_rooms"]).Cast<Dictionary<string, object?>>().Select(r => r["room"]).ToArray();
        Assert.Equal(large.Selected.PlannedRooms.Select(r => (object?)(double)r.Room), planned);
        var room = Assert.IsType<Dictionary<string, object?>>(Assert.Single(List(row["rooms"]), r => Equals(((Dictionary<string, object?>)r!)["room"], 1d)));
        Assert.Equal(true, room["assessed"]);
        Assert.Equal(candidates.Context.PlannedRooms.Where(a => a.Room == 1).Select(a => a.Id), Strings(room["alternatives"]));
        Assert.Equal(true, room["selected"]);
        Assert.True((double)room["roots"]! > 0);

        // The rejected subsets touching room 1 lead, each with its ids and reason; the budget left filler out.
        var rejected = List(row["rejected"]).Cast<Dictionary<string, object?>>().ToArray();
        var firstRoom = roomRejected[0];
        Assert.Equal(firstRoom.Commitments, Strings(rejected[0]["ids"]));
        Assert.Equal(firstRoom.Reason, rejected[0]["reason"]);
        Assert.All(rejected, r => Assert.False(string.IsNullOrEmpty(r["reason"] as string)));
        Assert.True((double)row["rejected_omitted"]! > 0);

        // The findings naming a planned room (and the search truncation) survive; the unrelated noise does not.
        var uncertainty = Strings(row["uncertainty"]);
        Assert.Contains(roomFindings[0], uncertainty);
        Assert.Contains(roomFindings[1], uncertainty);
        Assert.DoesNotContain(uncertainty, f => f.StartsWith("schedule_unassessed:", StringComparison.Ordinal));
    }

    private static List<object?> List(object? value) => Assert.IsType<List<object?>>(value);

    private static string[] Strings(object? value) => List(value).Cast<string>().ToArray();
}
