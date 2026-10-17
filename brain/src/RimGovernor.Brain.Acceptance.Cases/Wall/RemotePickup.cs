using System.Globalization;
using System.Text.Json.Nodes;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>wall/remote_pickup</c> (#2517): a construction delivery whose source stack is remote (in storage nearer the
/// stockpile than the site) takes one full carry load, collecting a same-def stack inside the n*D/capacity radius but not
/// one outside it; the surplus lands beside the frame and every item is counted once. A local delivery with no storage
/// keeps vanilla's exact-need pickup. Native: the Harmony postfix on
/// WorkGiver_ConstructDeliverResources.ResourceDeliverJobFor, pawn carrying and vanilla's surplus drop, which
/// <c>test/remote_pickup</c> stages and reads in the lab.
/// </summary>
internal static class WallRemotePickup
{
    private const string Tool = "test/remote_pickup";

    private const int Settle = 9000;

    private static readonly FixtureOp<string, Fx.RemotePickupReply> Pickup = new(
        Tool,
        action => WallKit.Args(("action", action)),
        (label, reply) => reply is not null && Fx.FixtureJson.Deserialize<Fx.RemotePickupReply>(reply.ToJsonString()) is { Success: true, Site: not null, Centre: not null } read
            ? read
            : throw new AcceptanceException($"{label}: {Tool} refused: {reply?.ToJsonString()}"));

    internal static TypedCase Case() => new()
    {
        Name = "wall/remote_pickup",
        Scope = "A remote construction delivery takes one full load, collecting the in-radius stack and leaving the out-of-radius one, "
            + "drops the surplus beside the frame and counts every item once (#2517); a local delivery takes exactly the frame's need.",
        Ticks = 2 * Settle,
        Tier = CaseTier.OnDemand,
        QuietWorld = true,
        Ops = [Tool],
        Crew = new Crew { Size = 1 },
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        await TypedAuthority.GrantAutoAsync(s, cancellationToken).ConfigureAwait(false);

        async Task<Fx.RemotePickupReply> SettleAsync(string phase)
        {
            Fx.RemotePickupReply? row = null;
            _ = await s.RunUntilAsync(phase, Settle, async ct =>
            {
                row = await s.FixtureAsync(phase + "-audit", Pickup, "audit", ct).ConfigureAwait(false);
                return (Wait.Signature(row.Delivered, row.Carrying), row.Delivered == row.Cost && row.Carrying == 0);
            }, cancellationToken).ConfigureAwait(false);
            s.RecordWire(phase, row);
            return row!;
        }

        var staged = await s.FixtureAsync("stage-remote", Pickup, "remote", cancellationToken).ConfigureAwait(false);

        // The second stack (20 items, 8 cells from the first, D=40) is worth the detour while 8 < 20*40/capacity; the third (14 cells) is not.
        // The geometry only separates them for a capacity between 58 and 99.
        if (staged.Capacity is < 58 or > 99)
        {
            throw new AcceptanceException($"lab carry capacity {staged.Capacity} does not separate the staged stacks");
        }

        var remote = await SettleAsync("remote").ConfigureAwait(false);
        var (cx, cz) = (remote.Centre!.X, remote.Centre.Z);
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

        _ = await s.FixtureAsync("stage-local", Pickup, "local", cancellationToken).ConfigureAwait(false);
        var local = await SettleAsync("local").ConfigureAwait(false);
        var (sx, sz) = (local.Site!.X, local.Site.Z);
        if (At(local, sx + 4, sz) != 30 - local.Cost || At(local, sx + 4, sz + 4) != 20 || local.Loose.Sum(st => st.Count) != 50 - local.Cost)
        {
            throw new AcceptanceException($"a local delivery must take exactly the frame's need from the first stack: {Describe(local)}");
        }
    }

    private static int At(Fx.RemotePickupReply row, int x, int z) => row.Loose.FirstOrDefault(st => st.X == x && st.Z == z)?.Count ?? 0;

    private static string Describe(Fx.RemotePickupReply row) => string.Join(' ', row.Loose.Select(st => string.Create(CultureInfo.InvariantCulture, $"{{{st.X} {st.Z} {st.Count}}}")));
}
