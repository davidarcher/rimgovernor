using RimGovernor.Brain.Policy;
using Fx = RimGovernor.Fixtures.Contracts;
using Obs = RimGovernor.Brain.GameFacts.Observations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>power/configure-and-route</c> (task 6.5 of redefine-habitat-requirements): a fuelled wood-fired generator (1000 W) wired to
/// a cooler and three idle electric stoves (1250 W of draw), and a standing lamp out of the conduit's reach. Under the served
/// power family's normal review, admission and Hands:
/// <list type="number">
/// <item>the short network's comparison configures it: power/basic works a <c>configure-</c> Method whose PowerSwitch orders
/// switch an idle stove off, a colonist flicks it natively, and the building read observes its switch off;</item>
/// <item>the cooler stays powered through it;</item>
/// <item>the unconnected lamp's network routes: a conduit is built to it and the read observes it powered.</item>
/// </list>
/// The comparisons are PowerSwitchTests and PowerUtilityTests; only the game proves the native power gizmo, the flick and the
/// readback of the switch.
/// </summary>
internal static class PowerConfigureAndRoute
{
    private const string Generator = "generator";
    private const string Cooler = "cooler";
    private const string Stoves = "stoves";
    private const string Lamp = "lamp";

    private static readonly Fx.StageSpec Spec = new()
    {
        Paused = true,
        World = new Fx.StageWorld { Hour = 10 },
        Site = new Fx.StageSite
        {
            Width = 20,
            Height = 8,
            Near = Fx.StageAnchor.Colonist,
            OffsetX = 4,
            Radius = 40,
            Require = [Fx.StageGround.Standable, Fx.StageGround.Empty, Fx.StageGround.Unzoned, Fx.StageGround.Unfogged],
        },
        Cells = [new Fx.StageCells { Rect = new Fx.StageRect(0, 0, 20, 8), Terrain = "Concrete", Home = true }],
        Buildings =
        [
            new Fx.StageBuilding { Name = Generator, Def = "WoodFiredGenerator", Cells = [new(2, 3)], Fuel = 75 },
            new Fx.StageBuilding { Name = "conduit", Def = "HiddenConduit", Cells = [.. Enumerable.Range(1, 11).Select(x => new Fx.StageCell(x, 1))] },
            new Fx.StageBuilding { Name = Cooler, Def = "Cooler", Cells = [new(4, 5)] },
            new Fx.StageBuilding { Name = Stoves, Def = "ElectricStove", Cells = [new(6, 3), new(10, 3), new(10, 5)] },
            new Fx.StageBuilding { Name = Lamp, Def = "StandingLamp", Cells = [new(19, 7)] },
        ],
        Things = [new Fx.StageThing { Name = "steel", Def = "Steel", Count = 100, Cell = new(16, 6), Near = true }],
        Pawns =
        [
            new Fx.StagePawn
            {
                Name = "worker",
                Colonist = 0,
                Cell = new(8, 6),
                Timetable = Fx.StageTimeAssignment.Work,
            },
        ],
    };

    internal static TypedCase Case() => new()
    {
        Name = "power/configure-and-route",
        Scope = "Task 6.5: a fuelled WoodFiredGenerator wired to a Cooler and three idle ElectricStoves, and a StandingLamp out of "
            + "the conduit's reach. Under the served power family's normal review the power/basic Need works a configure- Method "
            + "whose PowerSwitch orders switch an idle stove off; observations_list_buildings reads that stove's switch off and the "
            + "cooler powered, and a conduit routed to the lamp reads it powered.",
        Ticks = 30_000, // a hang guard: one flick and one short conduit run
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = 1 },
        Crew = new Crew { Size = 1 },
        Stage = Spec,
        Ops = [FixtureStage.Tool],
        QuietWorld = true,
        Serve = new ServeSpec { Families = ["power"], NativeTimeout = TimeSpan.FromSeconds(60), Prefix = "configure-and-route" },
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var staged = s.Staged ?? throw new AcceptanceException("power/configure-and-route stages its colony through Stage");
        var coolerId = staged.Buildings[Cooler].Ids.Single();
        var lampId = staged.Buildings[Lamp].Ids.Single();
        var stoveIds = staged.Buildings[Stoves].Ids;
        string[] ids = [coolerId, lampId, .. stoveIds];

        async Task<IReadOnlyList<Obs.BuildingState>> ReadAsync(string label, CancellationToken ct)
        {
            var rows = await s.BuildingsAsync(label, new RimGovernor.Brain.GameOrders.Observations.ListBuildingsRequest { Ids = [.. ids] }, ct).ConfigureAwait(false);
            return [.. ids.Select(id => rows.FirstOrDefault(r => r.Building.Id == id && r.Status == Obs.BuildingStatus.Built)
                ?? throw new AcceptanceException($"{label}: no built row for {id}"))];
        }

        var before = await ReadAsync("before", cancellationToken).ConfigureAwait(false);
        foreach (var b in before) s.RecordWire("before_" + b.Building.Id, b.Service);
        if (before.Any(b => b.Service.Switch is not Obs.BuildingSwitch.Flick { On: true, Wanted: true }) || PowerOn(before[1]))
        {
            throw new AcceptanceException("before: want every switch on and the lamp unpowered: the precondition hides the claim");
        }

        var methods = new SortedSet<string>(StringComparer.Ordinal);
        var last = before;
        _ = await s.ServeUntilAsync("configure-and-route", s.TicksLeft, async ct =>
        {
            var review = await s.ReviewAsync(ct).ConfigureAwait(false);
            foreach (var row in review?.Progress ?? [])
            {
                if (row.Concern == PowerFamily.Basic.FilingKey && row.Method.Length > 0) methods.Add(row.Method);
            }
            last = await ReadAsync("buildings", ct).ConfigureAwait(false);
            var switchedOff = last.Skip(2).Any(b => b.Service.Switch is Obs.BuildingSwitch.Flick { On: false, Wanted: false });
            return (Wait.Signature(string.Join(",", last.Select(b => b.Service.Switch + ":" + PowerOn(b))), string.Join(",", methods)),
                switchedOff && PowerOn(last[0]) && PowerOn(last[1]));
        }, cancellationToken).ConfigureAwait(false);

        foreach (var t in await s.NeedTransitionsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (t.Need == PowerFamily.Basic.Value && t.Method.Length > 0) methods.Add(t.Method);
        }
        foreach (var b in last) s.RecordWire("after_" + b.Building.Id, b.Service);
        s.Record("methods", string.Join(",", methods));
        if (!methods.Any(m => m.StartsWith("configure-", StringComparison.Ordinal)))
        {
            throw new AcceptanceException($"power/basic never worked a configure- Method ({string.Join(",", methods)})");
        }
    }

    private static bool PowerOn(Obs.BuildingState b) => b.Service.Power is Obs.BuildingPower.Trader { PowerOn: true };
}
