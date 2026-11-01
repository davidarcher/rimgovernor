using System.Globalization;
using RimGovernor.Brain.Policy;
using Fx = RimGovernor.Fixtures.Contracts;
using Obs = RimGovernor.Brain.GameFacts.Observations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>upkeep/restore-service</c> (task 5.4 of redefine-habitat-requirements): a wood-fired generator big enough for its stove
/// stands empty with wood in stock, a wooden wall in Home is at about half its hit points, no components are staged, and one
/// colonist can construct and haul. Under the served power and repair families' normal review, admission and Hands:
/// <list type="number">
/// <item>the power deficit selects the refuel of the existing generator (PowerRestorationRules: the power/basic Need works a
/// <c>refuel-&lt;generator&gt;-&lt;pawn&gt;</c> Method, or the colonist is observed on the forced job on it) and no blueprint or
/// frame is placed: no new generator;</item>
/// <item>the home-upkeep/repairs Need selects the wall's repair (<c>repair-&lt;wall&gt;-&lt;pawn&gt;</c>, or the forced job);</item>
/// <item>each Need stays open while its order is only accepted: its progress row stands for as long as the fact it restores
/// reads lost;</item>
/// <item>each closes only once the native building read observes the restoration: the generator fuelled and powered with the
/// stove powered, the wall at its maximum hit points.</item>
/// </list>
/// The colonist's work priorities are all zero (not disabled): native-led labor never takes the refuel or the repair itself,
/// so only the brain's player-forced orders (TryTakeOrderedJobPrioritizedWork, which refuses only disabled work types) can
/// restore either, and a restoration nobody selected cannot pass. The comparisons are PowerRestorationTests and
/// HomeUpkeepFamilyTests; only the game proves the native servicing read, the forced jobs and the closing facts.
/// </summary>
internal static class UpkeepRestoreService
{
    private const string Generator = "generator";
    private const string Stove = "stove";
    private const string Wall = "wall";
    private const string Worker = "worker";

    /// <summary>About half a wooden wall's maximum: a repair the census measures, well short of whole.</summary>
    private const int WallHitPoints = 100;

    // A 20x8 concrete Home strip: power/fuel's generator, conduit and stove at the west end, the damaged wall in the middle and
    // the wood at the east end, so the refuel walks the strip and stays in flight across several polls.
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
            new Fx.StageBuilding { Name = Generator, Def = "WoodFiredGenerator", Cells = [new(2, 3)], Fuel = 0 },
            new Fx.StageBuilding { Name = "conduit", Def = "HiddenConduit", Cells = [.. Enumerable.Range(1, 11).Select(x => new Fx.StageCell(x, 1))] },
            new Fx.StageBuilding { Name = Stove, Def = "ElectricStove", Cells = [new(8, 2)] },
            new Fx.StageBuilding { Name = Wall, Def = "Wall", Stuff = "WoodLog", Cells = [new(12, 4)], HitPoints = WallHitPoints },
        ],
        Things = [new Fx.StageThing { Name = "wood", Def = "WoodLog", Count = 150, Cell = new(18, 6), Near = true }],
        Pawns =
        [
            new Fx.StagePawn
            {
                Name = Worker,
                Colonist = 0,
                Cell = new(6, 5),
                AllWork = 0,
                Timetable = Fx.StageTimeAssignment.Work,
            },
        ],
    };

    internal static TypedCase Case() => new()
    {
        Name = "upkeep/restore-service",
        Scope = "Task 5.4: an empty WoodFiredGenerator wired to an ElectricStove with 150 wood in stock, a wooden Wall in Home at "
            + "100 hit points, no components and one colonist whose work priorities are all zero. Under the served power and repair "
            + "families' normal review the power/basic Need selects the refuel of the existing generator (its refuel Method or the "
            + "forced job on it) and no blueprint or frame is placed; home-upkeep/repairs selects the wall's repair; each Need's "
            + "progress row stands while its building reads lost, and both close only once observations_list_buildings reads the "
            + "generator fuelled and powered, the stove powered and the wall at its maximum hit points.",
        Ticks = 15_000, // a hang guard: a one-trip refuel and a half-wall repair are short forced jobs, serialized on one colonist
        Tier = CaseTier.OnDemand,
        Start = new LabStart { Colonists = 1 },
        Crew = new Crew { Size = 1 },
        Stage = Spec,
        Ops = [FixtureStage.Tool],
        QuietWorld = true,
        // power composes power/basic and its restoration; repair composes home-upkeep/repairs alone (clearance stays off). No work
        // family: it would raise the colonist's priorities and let native-led labor restore what the brain must select.
        Serve = new ServeSpec { Families = ["power", "repair"], NativeTimeout = TimeSpan.FromSeconds(60), Prefix = "restore-service" },
        Run = RunAsync,
    };

    /// <summary>What the probe saw of one restoration: the Need row's lifetime, its selection and the order in flight.</summary>
    private sealed class Track
    {
        public int Seen { get; set; } = -1;
        public int Closed { get; set; } = -1;
        public int Selected { get; set; } = -1;
        public int OpenWhileOrdered { get; set; }
        public SortedSet<string> Methods { get; } = new(StringComparer.Ordinal);

        public string Show() => string.Create(CultureInfo.InvariantCulture,
            $"seen={Seen} selected={Selected} open_while_ordered={OpenWhileOrdered} closed={Closed} methods={string.Join(",", Methods)}");
    }

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var staged = s.Staged ?? throw new AcceptanceException("upkeep/restore-service stages its colony through Stage");
        var generatorId = staged.Buildings[Generator].Ids.Single();
        var stoveId = staged.Buildings[Stove].Ids.Single();
        var wallId = staged.Buildings[Wall].Ids.Single();
        var workerId = staged.Pawns[Worker].Id;

        async Task<(Obs.BuildingState Generator, Obs.BuildingState Stove, Obs.BuildingState Wall)> ReadAsync(string label, CancellationToken ct)
        {
            var rows = await s.BuildingsAsync(label, new RimGovernor.Brain.GameOrders.Observations.ListBuildingsRequest { Ids = [generatorId, stoveId, wallId] }, ct).ConfigureAwait(false);
            Obs.BuildingState Row(string id) => rows.FirstOrDefault(r => r.Building.Id == id && r.Status == Obs.BuildingStatus.Built)
                ?? throw new AcceptanceException($"{label}: no built row for {id}");
            return (Row(generatorId), Row(stoveId), Row(wallId));
        }

        var before = await ReadAsync("before", cancellationToken).ConfigureAwait(false);
        s.RecordWire("generator_before", before.Generator.Service);
        s.RecordWire("stove_before", before.Stove.Service);
        s.RecordWire("wall_before", before.Wall.HitPoints);
        if (before.Generator.Service.Fuel is not Obs.BuildingFuel.Refuelable { OutOfFuel: true }
            || before.Generator.Service.Power is not Obs.BuildingPower.Trader { RatedW: > 0 } || PowerOn(before.Stove))
        {
            throw new AcceptanceException($"before: want an out-of-fuel producer and an unpowered stove, got {before.Generator.Service} / {before.Stove.Service}: the precondition hides the claim");
        }
        if (before.Wall.HitPoints is not Obs.BuildingHitPoints.Tracked { } hp || hp.Current >= hp.Maximum)
        {
            throw new AcceptanceException($"before: the staged wall is not damaged ({before.Wall.HitPoints}): the precondition hides the claim");
        }

        var power = new Track();
        var repairs = new Track();
        var refuelPrefix = PowerRestorationRules.RefuelMethod + generatorId;
        var repairPrefix = HomeUpkeepFamily.RepairMethod + wallId;
        var round = 0;
        var last = before;
        var job = "";
        _ = await s.ServeUntilAsync("restore-service", s.TicksLeft, async ct =>
        {
            round++;
            // The review first, then the facts: facts only improve, so a row missing before the facts read restored is a premature close.
            var review = await s.ReviewAsync(ct).ConfigureAwait(false);
            ConcernProgress[] rows = review is null ? [] : [.. review.Progress];
            last = await ReadAsync("buildings", ct).ConfigureAwait(false);
            var worker = await TypedPawns.OneAsync(s, "worker", workerId, ct).ConfigureAwait(false);
            var forcedOn = worker.Job.PlayerForced ? worker.Job.TargetA?.Target?.Entity?.Id : null;
            job = (worker.Job.DefName ?? "") + (worker.Job.PlayerForced ? "!" : "") + "@" + (worker.Job.TargetA?.Target?.Entity?.Id ?? "");

            // (a) No new generation: power and repair place nothing, so any blueprint or frame is the power planner building instead of restoring.
            var construction = await s.BuildingsAsync("construction", new RimGovernor.Brain.GameOrders.Observations.ListBuildingsRequest { Statuses = [Obs.BuildingStatus.Blueprint, Obs.BuildingStatus.Frame] }, ct).ConfigureAwait(false);
            if (construction.Count > 0)
            {
                throw new AcceptanceException($"round {round}: construction was placed while the generator was restorable: {string.Join(", ", construction.Select(c => c.Building.DefName + ":" + c.Status))}");
            }

            Observe(power, "power/basic", rows.FirstOrDefault(p => p.Concern == PowerFamily.Basic.FilingKey), refuelPrefix, forcedOn == generatorId, Powered(last), round);
            Observe(repairs, "home-upkeep/repairs", rows.FirstOrDefault(p => p.Concern == HomeUpkeepFamily.Repairs.FilingKey), repairPrefix, forcedOn == wallId, Whole(last.Wall), round);
            return (Wait.Signature(FuelOf(last.Generator), PowerOn(last.Stove), last.Wall.HitPoints, job, power.Show(), repairs.Show()),
                power.Closed >= 0 && repairs.Closed >= 0 && Powered(last) && Whole(last.Wall));
        }, cancellationToken).ConfigureAwait(false);

        // need_transitions carry the full MethodId each change of cause worked: a selection the polls fell between.
        foreach (var t in await s.NeedTransitionsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (t.Need == PowerFamily.Basic.Value && t.Method.Length > 0) power.Methods.Add(t.Method);
            if (t.Need == HomeUpkeepFamily.Repairs.Value && t.Method.Length > 0) repairs.Methods.Add(t.Method);
        }

        s.RecordWire("generator_after", last.Generator.Service);
        s.RecordWire("stove_after", last.Stove.Service);
        s.RecordWire("wall_after", last.Wall.HitPoints);
        s.Record("power", power.Show());
        s.Record("repairs", repairs.Show());
        s.Record("last_job", job);
        Require(power, "power/basic", refuelPrefix, $"the refuel of {generatorId}", $"generator {last.Generator.Service}, stove powered {PowerOn(last.Stove)}");
        Require(repairs, "home-upkeep/repairs", repairPrefix, $"the repair of {wallId}", $"wall {last.Wall.HitPoints}");
        if (!Powered(last) || !Whole(last.Wall))
        {
            throw new AcceptanceException($"the restoration was not observed: generator {last.Generator.Service}, stove powered {PowerOn(last.Stove)}, wall {last.Wall.HitPoints}");
        }
    }

    // One poll of one restoration: the Need's progress row (present while unmet), its Method and the forced job on its building.
    private static void Observe(Track t, string need, ConcernProgress? row, string prefix, bool forced, bool restored, int round)
    {
        if (row is not null)
        {
            if (t.Seen < 0) t.Seen = round;
            if (row.Method.Length > 0) t.Methods.Add(row.Method);
            if (t.Closed >= 0)
            {
                // A restored building's Need reopening is a new deficit, not this claim's; only an unrestored one is suspicious.
                t.Closed = -1;
            }
        }
        var selected = forced || (row is not null && row.Method.StartsWith(prefix, StringComparison.Ordinal));
        if (selected && t.Selected < 0) t.Selected = round;
        // (c) the order is in flight and the building still reads lost: the Need must still be open.
        if (selected && !restored)
        {
            if (row is null && t.Seen >= 0)
            {
                throw new AcceptanceException($"round {round}: {need} closed while its order was in flight and the building still reads lost ({t.Show()})");
            }
            t.OpenWhileOrdered++;
        }
        if (row is null && t.Seen >= 0 && t.Closed < 0)
        {
            // (d) a Need closes only on the observed restoration.
            if (!restored)
            {
                throw new AcceptanceException($"round {round}: {need} closed before the native read observed its building restored ({t.Show()})");
            }
            t.Closed = round;
        }
    }

    private static void Require(Track t, string need, string prefix, string what, string facts)
    {
        if (t.Seen < 0)
        {
            throw new AcceptanceException($"{need} never stood unmet in a persisted review ({t.Show()}): the deficit was never derived");
        }
        if (t.Selected < 0 && !t.Methods.Any(m => m.StartsWith(prefix, StringComparison.Ordinal)))
        {
            throw new AcceptanceException($"{need} never selected {what} ({t.Show()}; {facts})");
        }
        if (t.Closed < 0)
        {
            throw new AcceptanceException($"{need} never closed ({t.Show()}; {facts})");
        }
    }

    private static bool PowerOn(Obs.BuildingState b) => b.Service.Power is Obs.BuildingPower.Trader { PowerOn: true };

    private static double FuelOf(Obs.BuildingState b) => b.Service.Fuel is Obs.BuildingFuel.Refuelable tank ? tank.Fuel : -1;

    // The generator restored: fuelled, producing, and its stove powered (the deficit the power Need measured is gone).
    private static bool Powered((Obs.BuildingState Generator, Obs.BuildingState Stove, Obs.BuildingState Wall) v) =>
        FuelOf(v.Generator) > 0 && PowerOn(v.Generator) && PowerOn(v.Stove);

    private static bool Whole(Obs.BuildingState wall) => wall.HitPoints is Obs.BuildingHitPoints.Tracked { } hp && hp.Current >= hp.Maximum;
}
