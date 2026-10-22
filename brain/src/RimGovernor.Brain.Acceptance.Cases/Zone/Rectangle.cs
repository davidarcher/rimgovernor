using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>zone/rectangle</c>: a stockpile rectangle create through operations_apply over a staged site.</summary>
internal static class ZoneRectangle
{
    private const string Tool = "test/zone_rectangle_case";
    internal static TypedCase Case() => new()
    {
        Name = "zone/rectangle",
        Scope = "Native stockpile rectangle placement uses vanilla cell eligibility, keeps both components across a wall barrier and ground holding Steel, skips an existing player stockpile without changing it, and refuses repeated creation without changing zone IDs or cells after 260 newer actions. A snapshot cannot verify the native designator, zone grid, hauling settings or refusal behavior.",
        Ticks = 0,
        Tier = CaseTier.OnDemand,
        Stage = Fixture,
        Ops = [Tool],
        NoKeep = true,
        Run = RunAsync,
    };

    // The pinned lab rectangle, 7x5 cells 14 east and 4 north of the map centre, cleared to bare soil: a wall barrier
    // splitting it at x=3 with an obstacle at (1,2), five Steel stored at its corner, and an existing low-priority player
    // stockpile allowing nothing on its far corner.
    private static readonly Fx.StageSpec Fixture = new()
    {
        Paused = true,
        Site = new Fx.StageSite { Width = 7, Height = 5, OffsetX = 14, OffsetZ = 4 },
        Clear = [new Fx.StageClear
        {
            Rect = new Fx.StageRect(0, 0, 7, 5),
            Kinds = [Fx.StageClearKind.Things, Fx.StageClearKind.Pawns, Fx.StageClearKind.Zones, Fx.StageClearKind.Roofs, Fx.StageClearKind.Fog],
        }],
        Cells = [new Fx.StageCells { Rect = new Fx.StageRect(0, 0, 7, 5), Terrain = "Soil" }],
        Buildings = [new Fx.StageBuilding { Name = "walls", Def = "Wall", Stuff = "WoodLog", Cells = [new(3, 0), new(3, 1), new(3, 2), new(3, 3), new(3, 4), new(1, 2)] }],
        Things = [new Fx.StageThing { Name = "steel", Def = "Steel", Count = 5, Cell = new(0, 0) }],
        Zones = [new Fx.StageZone { Name = "player", Kind = Fx.StageZoneKind.Stockpile, Label = "Player stockpile", Cells = [new(6, 4)], Priority = Fx.StageStoragePriority.Low, Allow = [] }],
    };


    private static readonly FixtureOp<Fx.RectangleCaseRequest, Fx.RectangleCaseResult> Execute = new(
        Tool, request => new System.Text.Json.Nodes.JsonObject { { "spec", Fx.FixtureJson.Serialize(request) } },
        (label, reply) => reply is null ? throw new AcceptanceException(label + ": missing result") : Fx.FixtureJson.Deserialize<Fx.RectangleCaseResult>(reply.ToJsonString()));

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var faultText = Environment.GetEnvironmentVariable("RIMGOVERNOR_RECTANGLE_PILOT_FAULT");
        var fault = string.IsNullOrEmpty(faultText) ? Fx.RectangleCaseFault.None : Enum.Parse<Fx.RectangleCaseFault>(faultText, ignoreCase: true);
        var runId = Guid.NewGuid().ToString("N");
        var request = new Fx.RectangleCaseRequest
        {
            RunId = runId, ColonyId = s.Identity.ColonyId!, LoadToken = s.Identity.LoadToken!, MapId = s.Identity.MapId,
            Site = s.Staged?.Site ?? throw new AcceptanceException("stage: no site"),
            PlayerZone = s.Staged.Zones["player"], Fault = fault,
        };
        s.Record("pilot_run_id", System.Text.Json.Nodes.JsonValue.Create(runId));
        try
        {
            var result = await s.FixtureAsync("rectangle-body", Execute, request, cancellationToken).ConfigureAwait(false);
            s.RecordFixture("rectangle_result", result);
            RectanglePilotResults.RequireSuccess(runId, result);
        }
        finally
        {
            // Local OTLP survives a lost HTTP reply and is read even after the body token expires.
            var evidence = RectanglePilotResults.Last(s.Telemetry(RimGovernor.Brain.Telemetry.TelemetryKinds.AcceptanceCase), runId);
            if (evidence is not null)
            {
                s.RecordFixture("rectangle_partial", evidence);
                foreach (var (label, json) in evidence.Evidence)
                    s.Record(label, System.Text.Json.Nodes.JsonNode.Parse(json));
            }
        }
    }
}
