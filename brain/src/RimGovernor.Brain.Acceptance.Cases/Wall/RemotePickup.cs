using System.Globalization;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>wall/remote_pickup</c> (#2517, #3046): under the served brain, a construction delivery whose source stack is remote
/// (in storage nearer than the site) takes one full carry load, collecting a same-def stack inside the n*D/capacity
/// radius but not one outside it; the surplus lands beside the frame and every item is counted once. A local delivery
/// with no storage takes exactly the frame's need. Either path may deliver, both by one rule (<see cref="RemotePickup"/>): the
/// brain's Supply step (ConstructionMethodCandidates.RemoteLoad) or, for a colonist with no committed order, vanilla's own
/// delivery sized by native's RemotePickupGuard. <c>test/remote_pickup</c> stages and reads it in the lab; each remote stack
/// sits in its own stockpile and the frame has one beside it, so vanilla hauling moves no stack. Each frame closes a cold hut
/// the injected layout plan names a planned barracks, so the shelter values its finish; a free-standing frame is held by the
/// thermal-relief gate.
/// </summary>
internal static class WallRemotePickup
{
    private const string Tool = "test/remote_pickup";

    private const int Settle = 9000;

    private static readonly FixtureOp<string, Fx.RemotePickupReply> Pickup = new(
        Tool,
        action => WallKit.Args(("action", action)),
        (label, reply) => reply is not null && Fx.FixtureJson.Deserialize<Fx.RemotePickupReply>(reply.ToJsonString()) is { Success: true, Site: not null, Interior: not null, Door: not null } read
            ? read
            : throw new AcceptanceException($"{label}: {Tool} refused: {reply?.ToJsonString()}"));

    internal static TypedCase Case() => new()
    {
        Name = "wall/remote_pickup",
        Scope = "Under the served brain a remote construction delivery takes one full load, collecting the in-radius stack and "
            + "leaving the out-of-radius one, drops the surplus beside the frame and counts every item once (#2517, #3046); a "
            + "local delivery takes exactly the frame's need. Native carrying and vanilla's surplus drop are the signal.",
        Ticks = 2 * Settle,
        Tier = CaseTier.OnDemand,
        Stage = new Fx.StageSpec { World = new Fx.StageWorld { OutdoorTemperature = -10 } },
        QuietWorld = true,
        Ops = [Tool],
        Crew = new Crew { Size = 1 },
        Serve = new ServeSpec { Prefix = "remote-pickup" },
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var rooms = new List<PlannedRoom>();

        // Each staged hut is a planned barracks around its campfire; the plan keeps the rooms staged before it.
        async Task<Fx.RemotePickupReply> ServeAsync(string phase)
        {
            var staged = await s.FixtureAsync("stage-" + phase, Pickup, phase, cancellationToken).ConfigureAwait(false);
            var (ix, iz) = (staged.Interior!.X, staged.Interior.Z);
            rooms.Add(new PlannedRoom
            {
                Id = rooms.Count + 1,
                Role = PlannedRole.Barracks,
                Region = RegionRules.RectRegion(new(ix, iz, 3, 3)),
                Door = new(staged.Door!.X, staged.Door.Z),
                DoorSide = new DoorSide.Doored(Rotation.South),
            });
            await TypedLayoutPlan.InjectAsync(s, new LayoutPlan
            {
                Cold = true,
                NextRoomId = rooms.Count + 1,
                Rooms = [.. rooms],
                Pieces = rooms.ToDictionary(r => r.Id, r => (IReadOnlyList<InteriorPiece>)
                    [new InteriorPiece { Def = "Campfire", Rect = new Rectangle { X = r.Region.Bounds().X + 2, Z = r.Region.Bounds().Z + 2, Width = 1, Height = 1 } }]),
            }, cancellationToken).ConfigureAwait(false);
            // The staged geometry separates the stacks only for some carry capacities: the second stack (20 items, 8 cells
            // from the first 15, D=40) must be worth the detour and the third (14 cells) not.
            if (phase == "remote" && RemotePickup.Collect(40, staged.Capacity, 15, [("near", 8.0, 20.0), ("far", 14.0, 20.0)]) is not ["near"])
            {
                throw new AcceptanceException($"lab carry capacity {staged.Capacity} does not separate the staged stacks");
            }

            Fx.RemotePickupReply? row = null;
            _ = await s.ServeUntilAsync(phase, Settle, async ct =>
            {
                row = await s.FixtureAsync(phase + "-audit", Pickup, "audit", ct).ConfigureAwait(false);
                return (string.Create(CultureInfo.InvariantCulture, $"{row.Delivered}/{row.Carrying}/{Describe(row)}"), Settled(row));
            }, cancellationToken).ConfigureAwait(false);
            row = await s.FixtureAsync(phase + "-held", Pickup, "audit", cancellationToken).ConfigureAwait(false);
            s.RecordFixture(phase, row);
            return Settled(row)
                ? row
                : throw new AcceptanceException($"{phase}: delivered {row.Delivered} of {row.Cost} with {row.Carrying} carrying after {Settle} ticks: {Describe(row)}");
        }

        var remote = await ServeAsync("remote").ConfigureAwait(false);
        var (cx, cz) = (remote.Site!.X, remote.Site.Z);
        var surplus = remote.Loose.Where(st => Math.Abs(st.X - cx) <= 3 && Math.Abs(st.Z - cz) <= 3).Sum(st => st.Count);
        if (At(remote, cx + 40, cz) != 0 || At(remote, cx + 40, cz + 8) != 0)
        {
            throw new AcceptanceException($"remote pickup left the source or the in-radius stack behind: {Describe(remote)}");
        }

        if (At(remote, cx + 40, cz - 14) != 20)
        {
            throw new AcceptanceException($"the stack outside the radius must stay untouched: {Describe(remote)}");
        }

        var want = 15 + 20 - remote.Cost;
        if (surplus != want)
        {
            throw new AcceptanceException($"surplus beside the frame is {surplus}, want {want} from one full-load trip: {Describe(remote)}");
        }

        if (remote.Loose.Sum(st => st.Count) + remote.Delivered != 55)
        {
            throw new AcceptanceException($"items are not counted exactly once (want 55): {Describe(remote)} delivered {remote.Delivered}");
        }

        var local = await ServeAsync("local").ConfigureAwait(false);
        var (sx, sz) = (local.Site!.X, local.Site.Z);
        if (At(local, sx + 4, sz) != 30 - local.Cost || At(local, sx + 4, sz + 4) != 20 || local.Loose.Sum(st => st.Count) != 50 - local.Cost)
        {
            throw new AcceptanceException($"a local delivery must take exactly the frame's need from the first stack: {Describe(local)}");
        }
    }

    private static bool Settled(Fx.RemotePickupReply row) => row.Delivered == row.Cost && row.Carrying == 0;

    private static int At(Fx.RemotePickupReply row, int x, int z) => row.Loose.FirstOrDefault(st => st.X == x && st.Z == z)?.Count ?? 0;

    private static string Describe(Fx.RemotePickupReply row) => string.Join(' ', row.Loose.Select(st => string.Create(CultureInfo.InvariantCulture, $"{{{st.X} {st.Z} {st.Count}}}")));
}
