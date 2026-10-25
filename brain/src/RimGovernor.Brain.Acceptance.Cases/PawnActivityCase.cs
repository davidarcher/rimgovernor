using System.Globalization;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Telemetry;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>observations/pawn-activity</c>: the native activity tracker splits one grower's walk to a mature crop from the
/// harvest work, and the delivery ledger attributes the harvested output to that grower.
/// </summary>
internal static class PawnActivityCase
{
    private const string Crop = "Plant_Potato";

    // A 14x3 strip: the grower at its west end, one mature potato in a one-cell growing zone twelve cells east. The
    // other two colonists have every work type off, so the harvest is the grower's.
    private static readonly Fx.StageSpec Spec = new()
    {
        Paused = true,
        Site = new Fx.StageSite
        {
            Width = 14,
            Height = 3,
            OffsetX = 4,
            Radius = 15,
            Require = [Fx.StageGround.Standable, Fx.StageGround.Empty, Fx.StageGround.Unzoned, Fx.StageGround.Unfogged],
        },
        Clear = [new Fx.StageClear { Rect = new Fx.StageRect(0, 0, 14, 3), Kinds = [Fx.StageClearKind.Things, Fx.StageClearKind.Zones, Fx.StageClearKind.Roofs] }],
        Plants = [new Fx.StagePlant { Name = "crop", Def = Crop, Cells = [new(12, 1)], Growth = 1f }],
        Pawns =
        [
            new Fx.StagePawn { Name = "grower", Colonist = 0, Cell = new(0, 1), AllWork = 0, Work = new Dictionary<string, int> { ["Growing"] = 1 } },
            new Fx.StagePawn { Name = "idle1", Colonist = 1, AllWork = 0 },
            new Fx.StagePawn { Name = "idle2", Colonist = 2, AllWork = 0 },
        ],
        Zones = [new Fx.StageZone { Name = "field", Kind = Fx.StageZoneKind.Growing, Cells = [new(12, 1)], Plant = Crop }],
    };

    internal static TypedCase Case() => new()
    {
        Name = "observations/pawn-activity",
        Scope = "Native job transitions separate the grower's travel from its harvest work, and the delivery ledger links the "
            + "harvested crop to that grower: one staged mature potato in a growing zone, read from the pawn_activity_interval "
            + "and pawn_activity_output rows.",
        Ticks = 3600,
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = 3 },
        Stage = Spec,
        QuietWorld = true,
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var grower = s.Staged!.Pawns["grower"].Id;
        var crop = s.Staged!.Plants["crop"].Ids.Single();

        // The facts read installs the delivery ledger's hooks and stamps the tick the run starts from.
        var facts = await s.CallAsync("facts", NativeOps.ReadColonyFacts, new Observations.ColonyFactsRequest { Scope = s.Scope }, cancellationToken).ConfigureAwait(false);
        var from = facts.Context?.Tick
            ?? throw new AcceptanceException("facts: no observation context");

        IReadOnlyList<LogRow> intervals = [];
        IReadOnlyList<LogRow> outputs = [];
        bool Has(string activity) => intervals.Any(r => Text(r, PawnActivityTelemetry.ActivityKey) == activity);
        bool Harvested() => intervals.Any(r => Text(r, PawnActivityTelemetry.ActivityKey) == "work" && Text(r, PawnActivityTelemetry.TargetsKey).Split('|').Contains(crop));
        _ = await s.RunUntilAsync(
            "harvest",
            3600,
            _ =>
            {
                intervals = [.. s.Telemetry(TelemetryKinds.PawnActivityInterval).Where(r => Text(r, PawnActivityTelemetry.PawnKey) == grower
                    && r.Payload.GetValueOrDefault(PawnActivityTelemetry.StartTickKey) is double start && start >= from)];
                outputs = [.. s.Telemetry(TelemetryKinds.PawnActivityOutput).Where(r => Text(r, PawnActivityTelemetry.PawnKey) == grower)];
                var signature = string.Create(CultureInfo.InvariantCulture, $"intervals={intervals.Count} outputs={outputs.Count}");
                return Task.FromResult((signature, outputs.Count > 0 && Has("travel") && Harvested()));
            },
            cancellationToken).ConfigureAwait(false);

        if (outputs.Count == 0 || !Has("travel") || !Harvested())
        {
            throw new AcceptanceException(string.Create(
                CultureInfo.InvariantCulture,
                $"grower {grower}: want travel, work on {crop} and a crop output, got activities [{string.Join(", ", intervals.Select(r => Text(r, PawnActivityTelemetry.ActivityKey)))}] and {outputs.Count} outputs"));
        }

        s.Record("crop", crop);
        s.Record("from", from);
        s.Record("activities", string.Join(",", intervals.Select(r => Text(r, PawnActivityTelemetry.ActivityKey))));
        s.Record("output", $"{Text(outputs[0], PawnActivityTelemetry.ResourceKey)} x{outputs[0].Payload.GetValueOrDefault(PawnActivityTelemetry.UnitsKey)}");
    }

    private static string Text(LogRow row, string key) => row.Payload.GetValueOrDefault(key) as string ?? "";
}
