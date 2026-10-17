using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>anomaly/horror-incident</c> (#1748): a colony of four riflemen behind a sandbag line survives the game's own Anomaly
/// threat incident under the served defense planner: before the clock runs out every arrival is dead, downed or gone and
/// every colonist is alive. The fixture picks the incident from the game's defs by what it spawns (a melee entity pack),
/// not by name; the tactic choice itself is policy's.
/// </summary>
internal static class AnomalyHorror
{
    private const string IncidentTool = "test/horror_incident";
    private const string InspectTool = "test/horror_inspect";

    // Sets Prefs.AutomaticPauseMode (scripts/fixtures/LetterFixture.cs): a threat letter would pause the served game.
    private const string PauseModeTool = "test/letter_pause_mode";
    private const string Rifle = "Gun_AssaultRifle";
    private const int Colonists = 4;

    private static readonly FixtureOp<NoArgs, Fx.LetterPauseMode> PauseNever = new(
        PauseModeTool,
        _ => ServedOps.Args(("mode", "Never")),
        ServedOps.Succeeded<Fx.LetterPauseMode>(PauseModeTool, r => r.Success && r.Mode == "Never"));

    private static readonly FixtureOp<NoArgs, Fx.HorrorIncidentFired> Fire = new(
        IncidentTool,
        _ => ServedOps.Args(),
        ServedOps.Succeeded<Fx.HorrorIncidentFired>(IncidentTool, r => r.Success && r.ColonistIds.Count == Colonists && r.ThreatIds.Count > 0));

    private static readonly FixtureOp<Fx.HorrorIncidentFired, Fx.HorrorInspectRead> Inspect = new(
        InspectTool,
        f => ServedOps.Args(("colonistIds", string.Join(',', f.ColonistIds)), ("threatIds", string.Join(',', f.ThreatIds))),
        ServedOps.Succeeded<Fx.HorrorInspectRead>(InspectTool, r => r.Success));

    // Lab-ranged without its gunners: four riflemen one row south of a seven-bag line, south of the lab centre.
    private static readonly Fx.StageSpec Line = new()
    {
        Site = new Fx.StageSite { Width = 7, Height = 2, Near = Fx.StageAnchor.MapCenter, OffsetX = -3, OffsetZ = -9, Radius = 10, Require = [Fx.StageGround.Standable] },
        Clear = [new Fx.StageClear { Rect = new Fx.StageRect(0, 0, 7, 2), Kinds = [Fx.StageClearKind.Things] }],
        Buildings = [new Fx.StageBuilding { Name = "sandbags", Def = "Sandbags", Cells = [.. Enumerable.Range(0, 7).Select(x => new Fx.StageCell(x, 1))] }],
        Pawns = [.. Enumerable.Range(0, Colonists).Select(i => new Fx.StagePawn { Name = $"rifleman{i}", Colonist = i, Cell = new(i * 2, 0), Gear = new Fx.StageGear { Weapon = Rifle } })],
    };

    internal static TypedCase Case() => new()
    {
        Name = "anomaly/horror-incident",
        Scope = "Issue #1748: four riflemen behind a sandbag line survive an Anomaly threat incident whose arrivals are a melee entity "
            + "pack, fired through the game's own incident worker: under the served defense planner every arrival ends dead, downed or "
            + "gone and every colonist alive. The arrivals' AI, the colonists' fire and the deaths are vanilla's, read on the live pawns.",
        Ticks = 30_000,
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = Colonists },
        Expansions = ["ludeon.rimworld.anomaly"],
        NoKeep = true,
        QuietWorld = true,
        Stage = Line,
        Ops = [PauseModeTool, IncidentTool, InspectTool],
        Serve = new ServeSpec
        {
            Families = ["defense", "tend", "rescue"],
            ClockSpeed = "Fast",
            Prefix = "anomaly-horror-incident",
        },
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        _ = await s.FixtureAsync("pause-mode-never", PauseNever, NoArgs.Value, cancellationToken).ConfigureAwait(false);
        var fired = await s.FixtureAsync("horror-incident", Fire, NoArgs.Value, cancellationToken).ConfigureAwait(false);
        s.Record("incident", ServedOps.Json(fired));

        // A colonist lost fails at once; the fight is over when no arrival is still up on the map.
        _ = await s.ServeUntilAsync("arrivals-resolved", s.TicksLeft, async ct =>
        {
            var read = await s.FixtureAsync("horror-inspect", Inspect, fired, ct).ConfigureAwait(false);
            if (read.Colonists.FirstOrDefault(c => !c.Found || c.Dead) is { } lost)
            {
                throw new AcceptanceException($"colonist {lost.Id} died or is gone in {fired.Incident}: {ServedOps.Json(read)}");
            }

            var up = read.Threats.Count(t => t is { Found: true, Dead: false, Destroyed: false, Spawned: true, Downed: false });
            return ($"{read.Tick} {up} up", up == 0);
        }, cancellationToken).ConfigureAwait(false);
    }
}
