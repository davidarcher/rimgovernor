using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary><c>wall/remote_pickup</c> (Go <c>remote_pickup.go</c>).</summary>
internal static class WallRemotePickup
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "wall/remote_pickup",
        Scope = "A construction delivery whose source stack is remote (nearer storage than the site) takes one full carry load and collects a same-def stack inside the n*D/capacity radius but not one outside it; surplus is dropped beside the frame and every item is counted once (#2517). A local delivery with no storage keeps vanilla's exact-need pickup. A Go snapshot cannot prove the Harmony postfix on WorkGiver_ConstructDeliverResources.ResourceDeliverJobFor, pawn carrying or vanilla's surplus drop.",
        Start = new LabStart(),
        RequiredOps = ["test/remote_pickup"],
        Budget = TimeSpan.FromMinutes(4),
        Crew = new Crew { Size = 1 },
        Run = RunAsync,
    };

    private readonly record struct PickupStack(int X, int Z, int Count);

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        _ = await Authority.GrantAutoAsync(h.WireFunc(), "remote-pickup-authority", s.Identity, cancellationToken).ConfigureAwait(false);

        async Task<JsonObject> Call(string label, string action)
        {
            var row = await h.CallAsync(label, "test/remote_pickup", new JsonObject { ["action"] = action }, cancellationToken).ConfigureAwait(false);
            return row["success"].AsBool() == true ? row : throw new AcceptanceException($"remote_pickup {action} refused: {row.ToJson()}");
        }

        async Task<JsonObject> Settle(string phase, JsonObject start)
        {
            var row = start;
            for (var ticks = 0; ticks <= 9000; ticks += 300)
            {
                if (row["delivered"].AsNumber() == row["cost"].AsNumber() && row["carrying"].AsNumber() == 0)
                {
                    s.Report[phase] = row;
                    return row;
                }

                _ = await s.AdvanceAsync(300, null, cancellationToken).ConfigureAwait(false);
                row = await Call($"{phase}-{CaseSupport.Tag(ticks)}", "audit").ConfigureAwait(false);
            }

            throw new AcceptanceException($"{phase} delivery did not finish within 9000 ticks: {row.ToJson()}");
        }

        var row = await Call("stage-remote", "remote").ConfigureAwait(false);
        var capacity = (int)row["capacity"].AsNumber();

        // The second stack (20 items, 8 cells from the first, D=40) is worth the detour while 8 < 20*40/capacity; the third (14 cells) is not.
        // The geometry only separates them for a capacity between 58 and 99.
        if (capacity is < 58 or > 99)
        {
            throw new AcceptanceException($"lab carry capacity {capacity} does not separate the staged stacks");
        }

        row = await Settle("remote", row).ConfigureAwait(false);
        var remote = Stacks(row);
        var (centreX, centreZ) = PickupCell(row["centre"]);
        var cost = (int)row["cost"].AsNumber();
        var surplus = 0;
        foreach (var st in remote)
        {
            if (Math.Abs(st.X - centreX) <= 3 && Math.Abs(st.Z - centreZ) <= 3)
            {
                surplus += st.Count;
            }
        }

        if (At(remote, centreX + 40, centreZ) != 0 || At(remote, centreX + 40, centreZ + 8) != 0)
        {
            throw new AcceptanceException($"remote pickup left the source or the in-radius stack behind: {Describe(remote)}");
        }

        var outside = At(remote, centreX + 40, centreZ - 14);
        if (outside != 20)
        {
            throw new AcceptanceException($"stack outside the radius must stay untouched, has {outside}: {Describe(remote)}");
        }

        var want = 15 + 20 - cost;
        if (surplus != want)
        {
            throw new AcceptanceException($"surplus beside the frame is {surplus}, want {want} from one full-load trip: {Describe(remote)}");
        }

        var sum = remote.Sum(st => st.Count) + (int)row["delivered"].AsNumber();
        if (sum != 55)
        {
            throw new AcceptanceException($"items are not counted exactly once: {sum} of 55 ({Describe(remote)})");
        }

        row = await Call("stage-local", "local").ConfigureAwait(false);
        row = await Settle("local", row).ConfigureAwait(false);
        var local = Stacks(row);
        var (siteX, siteZ) = PickupCell(row["site"]);
        if (At(local, siteX + 4, siteZ) != 30 - cost || At(local, siteX + 4, siteZ + 4) != 20 || local.Sum(st => st.Count) != 50 - cost)
        {
            throw new AcceptanceException($"local delivery must take exactly the frame's need from the first stack: {Describe(local)}");
        }
    }

    private static List<PickupStack> Stacks(JsonObject row) =>
        [.. row["loose"].AsSlice().Select(item => new PickupStack((int)item.Member("x").AsNumber(), (int)item.Member("z").AsNumber(), (int)item.Member("count").AsNumber()))];

    private static (int X, int Z) PickupCell(JsonNode? value) => ((int)value.Member("x").AsNumber(), (int)value.Member("z").AsNumber());

    private static int At(List<PickupStack> list, int x, int z) => list.FirstOrDefault(st => st.X == x && st.Z == z).Count;

    private static string Describe(List<PickupStack> list) =>
        string.Join(' ', list.Select(st => $"{{{CaseSupport.Tag(st.X)} {CaseSupport.Tag(st.Z)} {CaseSupport.Tag(st.Count)}}}"));
}
