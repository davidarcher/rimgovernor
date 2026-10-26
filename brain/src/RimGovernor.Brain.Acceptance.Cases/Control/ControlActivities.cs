using System.Globalization;
using RimGovernor.Brain.Bridge;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The control cases over Hands executions installed straight into the native execution table (#3398). A staged
/// producer, helper, ripe crop, meal and bed; test/pawn_control_prepare binds them, and test/pawn_control_install, under
/// Auto authority, installs the producer's harvest, meal and sleep and the helper's hold. <c>control/pawn-activities</c> runs that sequence;
/// each other case makes one native lifecycle claim over the installed executions.
/// </summary>
internal static class ControlActivities
{
    private const string PrepareTool = "test/pawn_control_prepare";
    private const string InstallTool = "test/pawn_control_install";
    private const string ReadTool = "test/pawn_control_read";
    private const string ProbeTool = "test/pawn_control_probe";
    private const string LoadedTool = "test/pawn_control_loaded";

    // How long the executions get to run the whole sequence, and the settle before the first read.
    private const int SequenceTicks = 6000;
    private const int SettleTicks = 30;

    private static readonly FixtureOp<Fx.StageReply, Fx.FixtureDone> Prepare = new(
        PrepareTool,
        r => ServedOps.Args(
            ("worker", r.Pawns["worker"].Id),
            ("helper", r.Pawns["helper"].Id),
            ("crop", r.Plants["crop"].Ids.Single()),
            ("meal", r.Things["meal"].Ids.Single()),
            ("bed", r.Buildings["bed"].Ids.Single())),
        ServedOps.Done(PrepareTool));

    private static readonly FixtureOp<NoArgs, Fx.FixtureDone> Install = ServedOps.Inject(InstallTool);
    private static readonly FixtureOp<NoArgs, Fx.ControlRead> Read = ServedOps.Read<Fx.ControlRead>(ReadTool);
    private static readonly FixtureOp<string, Fx.FixtureDone> Probe = new(ProbeTool, probe => ServedOps.Args(("probe", probe)), ServedOps.Done(ProbeTool));
    private static readonly FixtureOp<NoArgs, Fx.FixtureDone> Loaded = ServedOps.Inject(LoadedTool);

    internal static IEnumerable<TypedCase> Cases()
    {
        yield return Case(
            "pawn-activities",
            "A bounded initial harvest yields to native hunger and rest selection: the producer harvests, then "
                + "eats and sleeps, never player-forced. A unit test cannot prove the native selection boundary.",
            SettleTicks + SequenceTicks,
            [ReadTool],
            RunSequenceAsync);
        yield return Lifecycle("batch-atomic", "atomic", "A batch with one invalid order is refused whole: the producer's valid revision is not applied.");
        yield return Lifecycle("native-takeover", "takeover", "Considering a native alternative preserves the exact running handle; actually installing a replacement detaches it, and ending the replacement never reinstalls the stale attempt.");
        yield return Lifecycle("dependency-yields-native", "deadline", "A waiting dependency installs no eternal bot wait; native work starts and invalidates the stale execution handle.");
        yield return Lifecycle("work-refusal", "refusal", "Work the native job tracker refuses (repairing an undamaged bed) fails the execution visibly.");
        yield return Lifecycle("emergency-preempts", "preemption", "Selected recreation starts as a normal, unforced job, and a selected fire-fighting emergency preempts it.");
        yield return Lifecycle(
            "release",
            "release",
            "A repeated release drops ownership; the released pawn runs vanilla job selection and starts a direct job (#3669), "
                + "and an old or colliding release cannot erase a newer assignment.");
        yield return Lifecycle(
            "native-overrides",
            "overrides",
            "Native involuntary sleep, vomiting and an imposed sleep each invalidate the old execution, and ordinary replacement "
                + "also runs without retaining the old handle.");
        yield return Lifecycle("player-takeover", "player", "A player's ordered job and a direct native menu command each take the pawn from Hands ownership.");
        yield return Case(
            "activity-reload",
            "A real save and reload discards every installed Hands execution: no pawn is owned after the load. A unit test cannot "
                + "load a save.",
            0,
            [LoadedTool],
            async (s, ct) =>
            {
                await s.ReloadAsync("reload", ct).ConfigureAwait(false);
                _ = await s.FixtureAsync("loaded", Loaded, NoArgs.Value, ct).ConfigureAwait(false);
            });
        yield return Case(
            "work-resources",
            "observations_read_colony_facts reports each colonist's native work ownership with the installed assignments, its "
                + "carried input (thing and count together) and the map's local hour. A unit test cannot read the native frame.",
            0,
            [],
            ReadWorkResourcesAsync);
    }

    // The staged precondition: the producer on the meal's cell, hungry and tired, the helper, a ripe rice crop and a bed,
    // on cleared ground near the colonists.
    private static Fx.StageSpec Stage() => new()
    {
        Paused = true,
        Site = new Fx.StageSite
        {
            Width = 8,
            Height = 3,
            Near = Fx.StageAnchor.Colonist,
            OffsetX = 3,
            Radius = 30,
            Require = [Fx.StageGround.Standable, Fx.StageGround.Unzoned, Fx.StageGround.Unfogged],
        },
        Clear = [new Fx.StageClear { Rect = new Fx.StageRect(0, 0, 8, 3), Kinds = [Fx.StageClearKind.Things] }],
        Buildings = [new Fx.StageBuilding { Name = "bed", Def = "Bed", Cells = [new(6, 0)] }],
        Things = [new Fx.StageThing { Name = "meal", Def = "MealSimple", Cell = new(3, 1) }],
        Plants = [new Fx.StagePlant { Name = "crop", Def = "Plant_Rice", Cells = [new(0, 1)] }],
        Pawns =
        [
            new Fx.StagePawn { Name = "worker", Colonist = 0, Cell = new(3, 1), Needs = new Dictionary<string, float> { ["Food"] = .1f, ["Rest"] = .1f } },
            new Fx.StagePawn { Name = "helper", Colonist = 1 },
        ],
    };

    private static TypedCase Lifecycle(string name, string probe, string claim) => Case(
        "activity-" + name,
        claim + " A unit test cannot drive the native job tracker.",
        0,
        [ProbeTool],
        async (s, ct) => _ = await s.FixtureAsync(probe, Probe, probe, ct).ConfigureAwait(false));

    private static TypedCase Case(string name, string scope, int ticks, IReadOnlyList<string> ops, Func<TypedSession, CancellationToken, Task> claim) => new()
    {
        Name = "control/" + name,
        Scope = scope,
        Ticks = ticks,
        Tier = CaseTier.OnDemand,
        Stage = Stage(),
        Ops = [PrepareTool, InstallTool, .. ops],
        Keep = ["Food", "Rest"],
        QuietWorld = true,
        Run = async (s, ct) =>
        {
            _ = await s.FixtureAsync("prepare", Prepare, s.Staged!, ct).ConfigureAwait(false);
            await TypedAuthority.GrantAutoAsync(s, ct).ConfigureAwait(false);
            _ = await s.FixtureAsync("install", Install, NoArgs.Value, ct).ConfigureAwait(false);
            await claim(s, ct).ConfigureAwait(false);
        },
    };

    private static async Task RunSequenceAsync(TypedSession s, CancellationToken cancellationToken)
    {
        await s.RunTicksAsync("settle", SettleTicks, cancellationToken).ConfigureAwait(false);
        var slept = false;
        _ = await s.RunUntilAsync("sequence", SequenceTicks, async ct =>
        {
            var read = await s.FixtureAsync("activity", Read, NoArgs.Value, ct).ConfigureAwait(false);
            s.Record("activity", ServedOps.Json(read));
            if (read.Worker?.State == "failed" || read.Forced)
            {
                throw new AcceptanceException($"the producer's execution failed or became player-forced: {ServedOps.Json(read)}");
            }

            var asleep = read is { Job: "LayDown", Food: > .3f, Rest: > .12f };
            if (asleep && !read.CropHarvested)
            {
                throw new AcceptanceException($"the producer slept without the harvested sequence: {ServedOps.Json(read)}");
            }

            slept = asleep;
            return (string.Create(CultureInfo.InvariantCulture, $"{read.Job} {read.Worker?.State} {read.CropHarvested}"), asleep);
        }, cancellationToken).ConfigureAwait(false);
        if (!slept)
        {
            throw new AcceptanceException("the producer never reached a fed sleep within the sequence");
        }
    }

    private static async Task ReadWorkResourcesAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var reply = await s.CallAsync("colony-facts", NativeOps.ReadColonyFacts, new RimGovernor.Brain.GameOrders.Observations.ColonyFactsRequest { Scope = s.Scope }, cancellationToken).ConfigureAwait(false);
        var rows = reply.WorkResources.ToList();
        s.RecordWireRows("work_resources", rows);
        if (rows.Count < 3 || !rows.Any(r => r.Assignment.Length > 0)
            || rows.Any(r => r.LocalHour is not (>= 0 and < 24) || r.CarriedCount < 0 || (r.CarriedThing.Length > 0) != (r.CarriedCount > 0)))
        {
            throw new AcceptanceException("missing native work ownership, carried-input or local-hour observations");
        }
    }
}
