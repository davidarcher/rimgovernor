using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Telemetry;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>control/home-fire-release</c> (#3672): a fire inside the home area releases the committed order of a capable
/// colonist near it (reason <c>home_fire</c>), and vanilla job selection sends that colonist to beat the fire; a nearby
/// colonist on a sleep step keeps its order. No brain runs, so no review reinstalls anything. A unit test cannot drive the
/// native job tracker or a burning fire.
/// </summary>
internal static class ControlHomeFire
{
    private const string InstallTool = "test/home_fire_install";
    private const string ReadTool = "test/vanilla_chop_read";

    // A release lands within one 60-tick check, and the fighter stands five cells from the fire.
    private const int Budget = 600;

    private static readonly FixtureOp<Fx.StageReply, Fx.FixtureDone> Install = new(
        InstallTool,
        r => ServedOps.Args(
            ("fighter", r.Pawns["fighter"].Id),
            ("sleeper", r.Pawns["sleeper"].Id),
            ("bed", r.Buildings["bed"].Ids.Single()),
            ("kindling", r.Things["kindling"].Ids.Single())),
        ServedOps.Done(InstallTool));

    private static readonly FixtureOp<string, Fx.VanillaJobRead> Read = new(
        ReadTool, pawn => ServedOps.Args(("pawn", pawn)), ServedOps.Reply<Fx.VanillaJobRead>(ReadTool));

    // A 14x3 home strip: the fighter at its west end, wood on a plank floor five cells east, the sleeper and its bed at the
    // east end. Both may only fight fires; the third colonist does no work, so it cannot take the fire first.
    private static readonly Fx.StageSpec Spec = new()
    {
        Paused = true,
        Site = new Fx.StageSite
        {
            Width = 14,
            Height = 3,
            Near = Fx.StageAnchor.Colonist,
            OffsetX = 3,
            Radius = 30,
            Require = [Fx.StageGround.Standable, Fx.StageGround.Unzoned, Fx.StageGround.Unfogged],
        },
        Clear = [new Fx.StageClear { Rect = new Fx.StageRect(0, 0, 14, 3), Kinds = [Fx.StageClearKind.Things, Fx.StageClearKind.Roofs] }],
        Cells =
        [
            new Fx.StageCells { Rect = new Fx.StageRect(0, 0, 14, 3), Home = true },
            new Fx.StageCells { Rect = new Fx.StageRect(6, 1, 1, 1), Terrain = "WoodPlankFloor" },
        ],
        Buildings = [new Fx.StageBuilding { Name = "bed", Def = "Bed", Cells = [new(12, 0)] }],
        Things = [new Fx.StageThing { Name = "kindling", Def = "WoodLog", Count = 75, Cell = new(6, 1) }],
        Pawns =
        [
            new Fx.StagePawn
            {
                Name = "fighter", Colonist = 0, Cell = new(1, 1), ClearTraits = true, AllWork = 0,
                Work = new Dictionary<string, int> { ["Firefighter"] = 1 }, Needs = new Dictionary<string, float> { ["Food"] = 1f, ["Rest"] = 1f },
            },
            new Fx.StagePawn
            {
                Name = "sleeper", Colonist = 1, Cell = new(11, 1), ClearTraits = true, AllWork = 0,
                Work = new Dictionary<string, int> { ["Firefighter"] = 1 }, Needs = new Dictionary<string, float> { ["Food"] = 1f, ["Rest"] = .2f },
            },
            new Fx.StagePawn { Name = "idle", Colonist = 2, AllWork = 0 },
        ],
    };

    internal static TypedCase Case() => new()
    {
        Name = "control/home-fire-release",
        Scope = "Issue #3672: a home-area fire releases a nearby capable colonist's committed hold with reason home_fire and "
            + "vanilla starts BeatFire, while a nearby colonist's committed sleep is kept. A unit test cannot drive the native "
            + "job tracker or a burning fire.",
        Ticks = Budget,
        Tier = CaseTier.OnDemand,
        Stage = Spec,
        Ops = [InstallTool, ReadTool],
        QuietWorld = true,
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var fighter = s.Staged!.Pawns["fighter"].Id;
        var sleeper = s.Staged.Pawns["sleeper"].Id;
        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);
        _ = await s.FixtureAsync("install", Install, s.Staged, cancellationToken).ConfigureAwait(false);
        _ = await s.RunUntilAsync("beat-fire", s.TicksLeft, async ct =>
        {
            var kept = await s.FixtureAsync("sleeper", Read, sleeper, ct).ConfigureAwait(false);
            if (!kept.Owned)
            {
                throw new AcceptanceException($"the sleeping colonist lost its committed order: {ServedOps.Json(kept)}");
            }

            var read = await s.FixtureAsync("fighter", Read, fighter, ct).ConfigureAwait(false);
            return (read.Job, !read.Owned && read.Job == "BeatFire");
        }, cancellationToken).ConfigureAwait(false);

        bool HomeFire(LogRow r, string pawn) => Text(r, PawnActivityTelemetry.PawnKey) == pawn
            && Text(r, PawnActivityTelemetry.StateKey) == PawnActivityTelemetry.ReleasedState
            && Text(r, PawnActivityTelemetry.ReasonKey) == PawnActivityTelemetry.HomeFireReason;
        var rows = s.Telemetry(TelemetryKinds.PawnActivityExecution);
        if (!rows.Any(r => HomeFire(r, fighter)) || rows.Any(r => HomeFire(r, sleeper)))
        {
            throw new AcceptanceException($"want one home_fire release, the fighter's ({fighter}), and none for the sleeper ({sleeper})");
        }
    }

    private static string Text(LogRow row, string key) => row.Payload.GetValueOrDefault(key) as string ?? "";
}
