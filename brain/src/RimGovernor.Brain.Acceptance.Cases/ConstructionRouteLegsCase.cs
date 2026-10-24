using System.Globalization;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Domain;
using Fx = RimGovernor.Fixtures.Contracts;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>observations/construction-route-legs</c> (#3556): the construction activity read's legs, searched batched on the
/// game's path finder. A builder, a steel stack and three steel wall blueprints across an open site: every option
/// prices its site approach, source approach and delivery; a route probe to an open cell is priced and one to a cell
/// walled in on every side is unknown, paused and again while the game runs. Whether the finder's batched requests
/// resolve with their paths is a game mechanic a unit test cannot establish.
/// </summary>
internal static class ConstructionRouteLegsCase
{
    private const string Stuff = "Steel";
    private const string Wall = "Wall";

    // Reads while the game runs, at least this many ticks apart; the headless game runs thousands of ticks between
    // two probes, so the budget is generous.
    private const int TickingReads = 2;
    private const int TicksBetweenReads = 60;
    private const int TickingTicks = 12000;

    // A 13x7 clear site: the builder at (0,3), the steel at (1,5), blueprints down the east side at x=11, a complete
    // wall ring over (5..7, 0..2) walling in (6,1) on every side. No colonist is assigned work and every hour is a work
    // hour, so while the game runs the colonists idle (free for the rows) and leave the blueprints and the stack alone.
    private static readonly Fx.StageSpec Spec = new()
    {
        Paused = true,
        Site = new Fx.StageSite
        {
            Width = 13,
            Height = 7,
            OffsetX = 6,
            Radius = 15,
            Require = [Fx.StageGround.Standable, Fx.StageGround.Empty, Fx.StageGround.Unzoned, Fx.StageGround.Unfogged, Fx.StageGround.Heavy],
        },
        Clear = [new Fx.StageClear { Rect = new Fx.StageRect(0, 0, 13, 7), Kinds = [Fx.StageClearKind.Things, Fx.StageClearKind.Zones, Fx.StageClearKind.Roofs] }],
        Buildings =
        [
            new Fx.StageBuilding { Name = "plans", Def = Wall, Stuff = Stuff, Cells = [new(11, 1), new(11, 3), new(11, 5)], State = Fx.StageBuildState.Blueprint },
            new Fx.StageBuilding
            {
                Name = "ring",
                Def = Wall,
                Stuff = Stuff,
                Cells = [new(5, 0), new(6, 0), new(7, 0), new(5, 1), new(7, 1), new(5, 2), new(6, 2), new(7, 2)],
            },
        ],
        Things = [new Fx.StageThing { Name = "steel", Def = Stuff, Count = 75, Cell = new(1, 5) }],
        Pawns =
        [
            new Fx.StagePawn { Name = "builder", Colonist = 0, Cell = new(0, 3), AllWork = 0, Timetable = Fx.StageTimeAssignment.Work },
            new Fx.StagePawn { Name = "second", Colonist = 1, AllWork = 0, Timetable = Fx.StageTimeAssignment.Work },
            new Fx.StagePawn { Name = "third", Colonist = 2, AllWork = 0, Timetable = Fx.StageTimeAssignment.Work },
        ],
    };

    internal static TypedCase Case() => new()
    {
        Name = "observations/construction-route-legs",
        Scope = "A builder, a steel stack and three steel wall blueprints on an open site: the construction activity read prices every "
            + "blueprint's site approach, source approach and delivery for the builder, and prices a FromStart route probe to an open "
            + "cell while leaving one to a cell walled in on every side unknown; the same read while the game runs keeps pricing the "
            + "open legs. Does not prove the legs' tick values against the pawn's walk or the brain's use of the rows.",
        Ticks = TickingTicks,
        Tier = CaseTier.Nightly,
        Start = new LabStart { Colonists = 3 },
        Stage = Spec,
        Ops = [FixtureStage.Tool],
        QuietWorld = true,
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var staged = s.Staged!;
        var site = staged.Site ?? throw new AcceptanceException("test/stage replied no site");
        var builder = staged.Pawns["builder"].Id;
        Cell At(int x, int z) => new(site.X + x, site.Z + z);
        var plans = new[] { At(11, 1), At(11, 3), At(11, 5) };
        var open = At(12, 3);
        var walledIn = At(6, 1);
        var request = new Observations.ConstructionActivityRequest(s.Identity, EquatableArray.From(plans.Select(c => new Building(Wall, c, Rotation.North, Stuff))))
        {
            Routes = EquatableArray.From(new Observations.PlannedRouteProbe[]
            {
                new(default, open, default) { FromStart = true },
                new(default, walledIn, default) { FromStart = true },
            }),
        };

        var read = await s.CallAsync("legs", NativeOps.ReadConstructionActivity, request, cancellationToken).ConfigureAwait(false);
        s.RecordWire("read", read);
        var problems = Problems(read, builder, plans, open, walledIn);
        if (problems.Count > 0)
        {
            throw new AcceptanceException("the batched legs disagree with the site: " + string.Join("; ", problems) + "; findings " + string.Join(",", read.Findings));
        }

        // The same read while the game runs and its own path finder ticks (#3556: the live game's batched requests
        // resolved empty where the paused lab's did not), TickingReads reads at least TicksBetweenReads apart, judged
        // over the colonists free at each read.
        var reads = 0;
        var lastTick = long.MinValue;
        _ = await s.RunUntilAsync("ticking", TickingTicks, async ct =>
        {
            var again = await s.CallAsync($"ticking-{reads}", NativeOps.ReadConstructionActivity, request, ct).ConfigureAwait(false);
            var tick = again.Context.Tick;
            if (tick < lastTick + TicksBetweenReads) return ($"waiting {tick}", false);
            // No route row at all: no colonist was free at that tick (a row with unknown ticks is a leg not priced).
            if (again.Routes.Count == 0 && again.Options.Count == 0) return ($"no free colonist at {tick}", false);
            lastTick = tick;
            reads++;
            s.RecordWire($"ticking-read-{reads}", again);
            var found = Problems(again, null, plans, open, walledIn);
            if (found.Count > 0)
            {
                throw new AcceptanceException($"ticking read {reads} at tick {tick}: " + string.Join("; ", found) + "; findings " + string.Join(",", again.Findings));
            }

            return ($"read {reads} at {tick}", reads >= TickingReads);
        }, cancellationToken).ConfigureAwait(false);
        if (reads < TickingReads)
        {
            throw new AcceptanceException($"only {reads} of {TickingReads} reads ran while the game ticked");
        }
    }

    // What the read gets wrong about the site. Paused, the builder's: every blueprint's row priced in full, the open
    // route priced and the walled-in route unknown. Ticking (builder null), every free colonist's open route priced and
    // walled-in route unknown; the game's own job search may be delivering to the blueprints by then.
    private static List<string> Problems(Observations.ConstructionActivityRead read, string? builder, Cell[] plans, Cell open, Cell walledIn)
    {
        var problems = new List<string>();
        if (builder == null)
        {
            // A colonist that wandered next to the open cell is priced 0.
            var openRoutes = read.Routes.Where(r => r.To == open).ToList();
            if (openRoutes.Count == 0 || openRoutes.Any(r => !r.Ticks.IsKnown))
            {
                problems.Add($"open route to {open.X},{open.Z}: {Describe(openRoutes)}");
            }

            var walledRoutes = read.Routes.Where(r => r.To == walledIn).ToList();
            if (walledRoutes.Count != openRoutes.Count || walledRoutes.Any(r => r.Ticks.IsKnown))
            {
                problems.Add($"walled-in route to {walledIn.X},{walledIn.Z}: {Describe(walledRoutes)}");
            }

            return problems;
        }

        foreach (var plan in plans)
        {
            var rows = read.Options.Where(o => o.Location == plan && o.Pawn == builder).ToList();
            if (rows.Count != 1)
            {
                problems.Add($"blueprint {plan.X},{plan.Z}: want one row for the builder, got {rows.Count}");
                continue;
            }

            var row = rows[0];
            if (row.Resource != Stuff || !(row.SiteApproachTicks > 0) || row.Delivery is not Observations.ConstructionDelivery.Carried { ApproachTicks: > 0, DeliveryTicks: > 0 })
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture,
                    $"blueprint {plan.X},{plan.Z}: resource {row.Resource}, site approach {row.SiteApproachTicks}, delivery {row.Delivery}"));
            }
        }

        var openRows = read.Routes.Where(r => r.To == open && r.Pawn == builder).ToList();
        if (openRows.Count != 1 || !Priced(openRows[0].Ticks))
        {
            problems.Add($"open route to {open.X},{open.Z}: {Describe(openRows)}");
        }

        var walledRows = read.Routes.Where(r => r.To == walledIn && r.Pawn == builder).ToList();
        if (walledRows.Count != 1 || walledRows[0].Ticks.IsKnown)
        {
            problems.Add($"walled-in route to {walledIn.X},{walledIn.Z}: {Describe(walledRows)}");
        }

        return problems;
    }

    private static bool Priced(Fact<double> ticks) => ticks.IsKnown && ticks.Value > 0;

    private static string Describe(List<Observations.CoupledRoute> rows) =>
        rows.Count == 0 ? "no row" : string.Join(", ", rows.Select(r => string.Create(CultureInfo.InvariantCulture, $"{r.Pawn} {r.Ticks}")));
}
