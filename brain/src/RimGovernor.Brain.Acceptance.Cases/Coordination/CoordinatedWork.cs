using System.Globalization;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>coordination/*</c>: work the served brain coordinates through Hands. The food cases harvest food to the selected
/// storage with tasks spread over more than one pawn; the carried-recovery one also ends one carrying delivery's job and
/// needs a later revision to carry it again. The construction case is one urgent build (#3537): on a colony with no bed
/// the normal rounds place the sleeping shelter and select, bind and finish it as coupled work before deferred vanilla
/// builders take it, one carrying Supply's job is ended and a later revision carries it again, and every participant is
/// released. test/coordinated_work_prepare prepares the facts (no plan, order or task) and records what the native
/// activities did, which the primitives cannot express.
/// </summary>
internal static class CoordinatedWork
{
    private const string Prepare = "test/coordinated_work_prepare";
    private const string ReadTool = "test/coordinated_work_read";

    private static readonly FixtureOp<(string Mode, bool Interrupt), Fx.CoordinationPrepared> Prepared = new(
        Prepare,
        r => ServedOps.Args(("mode", r.Mode), ("interrupt", r.Interrupt)),
        (label, reply) => ServedOps.Reply<Fx.CoordinationPrepared>(Prepare)(label, reply) is { Success: true } prepared
            ? prepared
            : throw new AcceptanceException($"{label}: {Prepare} refused: {reply?.ToJsonString()}"));

    private static readonly FixtureOp<NoArgs, Fx.CoordinationRead> Read = ServedOps.Read<Fx.CoordinationRead>(ReadTool);

    internal static TypedCase Food(bool interrupted = false) => new()
    {
        Name = "coordination/food" + (interrupted ? "-carried-recovery" : "-delegated"),
        Scope = "Prepared specialist and helper facts are coordinated by the served brain through Hands: the native game "
            + "harvests food to the selected storage with tasks on more than one pawn, then releases every participant"
            + (interrupted ? "; one carried input's job is ended and a later revision carries it again" : "")
            + ". A unit test cannot prove native carry conservation or actual delivery.",
        Ticks = 6000,
        Tier = CaseTier.Nightly,
        Ops = [Prepare, ReadTool],
        QuietWorld = true,
        Serve = new ServeSpec { Prefix = "coordination" },
        Run = (s, ct) => RunAsync(s, "food", interrupted, ct),
    };

    internal static TypedCase Construction() => new()
    {
        Name = "coordination/construction-delegated",
        Scope = "A bedless colony's urgent build is brain-directed (#3537): the normal rounds place the sleeping shelter, "
            + "bind its sites as coupled work before deferred vanilla builders take them, actually transfer material, rebind "
            + "a frame's Supply to the stack its ended carrying job dropped, keep vanilla builders off the held site, finish "
            + "the building and release every participant. A unit "
            + "test cannot prove native carry conservation, frame rebinding or actual completion.",
        Ticks = 6000,
        Tier = CaseTier.Nightly,
        Stage = new Fx.StageSpec { World = new Fx.StageWorld { OutdoorTemperature = -10 } },
        Ops = [Prepare, ReadTool],
        QuietWorld = true,
        Serve = new ServeSpec { Prefix = "coordination" },
        Run = (s, ct) => RunAsync(s, "construction", true, ct),
    };

    private static async Task RunAsync(TypedSession s, string mode, bool interrupted, CancellationToken cancellationToken)
    {
        var prepared = await s.FixtureAsync("coordination-prepare", Prepared, (mode, interrupted), cancellationToken).ConfigureAwait(false);
        if (mode == "construction")
        {
            // The staged site is the colony's one planned barracks, heated by its standing campfire: the normal rounds place its
            // shell and value it by the warmth it gives at -10 C (#3537). Its bed slot is what the sleeping step reconciles the
            // stand-in sleeping spots' barracks against.
            await TypedLayoutPlan.InjectAsync(s, new LayoutPlan
            {
                Cold = true,
                NextRoomId = 2,
                Rooms =
                [
                    new PlannedRoom
                    {
                        Id = 1,
                        Role = PlannedRole.Barracks,
                        Region = RegionRules.RectRegion(new(prepared.X, prepared.Z, 3, 3)),
                        Door = new(prepared.DoorX, prepared.DoorZ),
                        DoorRot = Rotation.South,
                    },
                ],
                Pieces = new Dictionary<int, IReadOnlyList<InteriorPiece>>
                {
                    [1] =
                    [
                        new InteriorPiece { Def = "Campfire", Rect = new Rectangle { X = prepared.X + 1, Z = prepared.Z + 1, Width = 1, Height = 1 } },
                        new InteriorPiece { Def = "Bed", Rect = new Rectangle { X = prepared.X, Z = prepared.Z + 1, Width = 1, Height = 2 }, Size = new Cell(1, 2), Rot = Rotation.North },
                    ],
                },
            }, cancellationToken).ConfigureAwait(false);
        }

        _ = await s.ServeUntilAsync("coordination", s.TicksLeft, async ct =>
        {
            var read = await s.FixtureAsync("coordination-observe", Read, NoArgs.Value, ct).ConfigureAwait(false);
            s.Record("coordination", ServedOps.Json(read));
            if (read.Forced)
            {
                throw new AcceptanceException($"planner work became player-forced: {ServedOps.Json(read)}");
            }

            // Food also claims the tasks spread over more than one pawn; the urgent build claims only its coupled chain.
            var chain = mode == "food"
                ? read.Joint && read.FinishedKinds.Contains("Work:Harvest") && read.FinishedKinds.Contains("Deliver:") && read.PhysicalFood > 0
                : read.FinishedKinds.Contains("Supply:") && read.FinishedKinds.Contains("Work:FinishFrame") && read.PhysicalBuildings > 0;
            var done = chain && read.Delivered > 0 && read.Active == 0 && read.Participants.Count > 0
                && read.Released.Count == read.Participants.Count && (!interrupted || read.Interrupted && read.Recovered);
            return (string.Create(CultureInfo.InvariantCulture, $"{read.Tick}"), done);
        }, cancellationToken).ConfigureAwait(false);
    }
}
