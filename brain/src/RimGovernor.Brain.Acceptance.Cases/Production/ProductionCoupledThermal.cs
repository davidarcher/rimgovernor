using System.Globalization;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Fx = RimGovernor.Fixtures.Contracts;
using static RimGovernor.Brain.Policy.PolicyFunctions;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>production/coupled-thermal</c>: a powered but leaky cold bedroom the governor's layout plan describes, under the
/// served brain. The native game builds the missing roof, the room warms, and the occupant sleeps warmer in its bed.
/// test/portfolio_thermal_prepare builds the room on the staged site (a quarter of its roof open, a powered heater, a bed
/// and a 600-tick leaky baseline; no task or Method) and records the roof work and occupied temperature, which the
/// primitives cannot express. The plan is staged instead of played from a layout review, and once the roof is closed
/// the occupant is staged tired instead of waiting most of a day for it to tire.
/// </summary>
internal static class ProductionCoupledThermal
{
    private const string Prepare = "test/portfolio_thermal_prepare";
    private const string ReadTool = "test/portfolio_thermal_read";

    // The 3x3 interior sits five cells into the 15x5 site: the generator and conduit run west of it, the crew waits east.
    private const int InteriorX = 5;
    private const int InteriorZ = 1;
    private const int Side = 3;

    // How much warmer than the leaky baseline the occupied bed must get.
    private const float Warming = 3;

    private static readonly FixtureOp<Fx.StageRect, Fx.ThermalRead> Prepared = new(
        Prepare,
        room => ServedOps.Args(("x", room.X), ("z", room.Z), ("width", room.Width), ("height", room.Height), ("doorX", room.X + room.Width), ("doorZ", room.Z + 1)),
        ServedOps.Reply<Fx.ThermalRead>(Prepare));

    private static readonly FixtureOp<NoArgs, Fx.ThermalRead> Read = ServedOps.Read<Fx.ThermalRead>(ReadTool);

    private static readonly Fx.StageSpec Spec = new()
    {
        Paused = true,
        World = new Fx.StageWorld { OutdoorTemperature = -10 },
        Site = new Fx.StageSite
        {
            Width = 15,
            Height = 5,
            Radius = 30,
            Require = [Fx.StageGround.Standable, Fx.StageGround.Heavy, Fx.StageGround.Unzoned, Fx.StageGround.Unfogged],
        },
        Clear = [new Fx.StageClear { Rect = new Fx.StageRect(0, 0, 15, 5), Kinds = [Fx.StageClearKind.Things, Fx.StageClearKind.Zones, Fx.StageClearKind.Roofs] }],
    };

    // The occupant (the prepare's Colonist 0) made tired once the roof is closed.
    private static readonly Fx.StageSpec Tired = new()
    {
        Pawns = [new Fx.StagePawn { Name = "sleeper", Colonist = 0, Needs = new Dictionary<string, float> { ["Rest"] = .15f } }],
    };

    internal static TypedCase Case() => new()
    {
        Name = "production/coupled-thermal",
        Scope = "Under the served brain a powered but leaky cold bedroom, described by the governor's layout plan, gets its "
            + "missing roof built natively, and its occupant then sleeps in its bed at least three degrees warmer than the "
            + "leaky baseline. Unit tests cannot prove native roof jobs, temperature or occupied use.",
        Ticks = 20000,
        Tier = CaseTier.OnDemand,
        Stage = Spec,
        Ops = [Prepare, ReadTool, FixtureStage.Tool],
        Keep = ["Food", "Rest", "Joy"],
        QuietWorld = true,
        Serve = new ServeSpec { Prefix = "coupled-thermal" },
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var site = s.Staged?.Site ?? throw new AcceptanceException("thermal: the stage found no site");
        var room = new Fx.StageRect(site.X + InteriorX, site.Z + InteriorZ, Side, Side);
        var prepared = await s.FixtureAsync("thermal-prepare", Prepared, room, cancellationToken).ConfigureAwait(false);
        s.Record("prepared", ServedOps.Json(prepared));
        await TypedLayoutPlan.InjectAsync(s, new LayoutPlan
        {
            Cold = true,
            NextRoomId = 2,
            Rooms =
            [
                new PlannedRoom
                {
                    Id = 1,
                    Role = PlannedRole.Bedroom,
                    Region = RectRegion(new(room.X, room.Z, room.Width, room.Height)),
                    Door = new(room.X + room.Width, room.Z + 1),
                    DoorRot = Rotation.East,
                },
            ],
        }, cancellationToken).ConfigureAwait(false);
        var tired = false;
        _ = await s.ServeUntilAsync("coupled-thermal", s.TicksLeft, async ct =>
        {
            var read = await s.FixtureAsync("thermal-observe", Read, NoArgs.Value, ct).ConfigureAwait(false);
            s.Record("thermal", ServedOps.Json(read));
            var closed = read.RoofComplete && read.OpenRoof == 0;
            if (closed && !tired)
            {
                _ = await s.StageAsync("thermal-tired", Tired, ct).ConfigureAwait(false);
                tired = true;
            }

            var done = read.Methods.Count > 0 && closed && read.Powered && read.Slept && read.OccupiedPeak >= read.Baseline + Warming;
            return (string.Create(CultureInfo.InvariantCulture, $"{read.Tick} tired={tired}"), done);
        }, cancellationToken).ConfigureAwait(false);
    }
}
