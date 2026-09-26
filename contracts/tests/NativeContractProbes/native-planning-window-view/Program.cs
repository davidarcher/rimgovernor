using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using HomeBridge.BridgeTools;
using Common = RimGovernor.Protocol.Common;

// The published planning-window view (#650): immutable roots swapped in
// whole, old readers holding a stable graph while newer roots publish,
// captured values detached from their builder, stale incarnations and
// incomplete roots refused, reader capacity released exactly once.
// Ordering is proven with gates, never with sleeps; waits carry a hang
// guard only.
internal static partial class NativePlanningWindowViewProbe
{
    private static int checks;
    private static void Check(bool value, string name) { checks++; if (!value) throw new Exception(name); }
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(60);

    private static Common.Identity Identity(string load) => new Common.Identity { ColonyId = "colony", LoadToken = load, MapId = 0 };

    // One region of width x rows, in chunks of two rows; every cell's glow
    // is the revision that built it, so a reader can tell roots apart.
    private const int MinX = 10, MinZ = 20, Width = 3, Rows = 5;

    private static PlanningViewCell[] Cells(int rows, double glow)
    {
        var cells = new PlanningViewCell[rows * Width];
        for (var i = 0; i < cells.Length; i++) cells[i] = new PlanningViewCell(false, PlanningViewCell.Walkable, null, "7", null, glow, 0);
        return cells;
    }

    private static PlanningViewRoot Root(PlanningWindowViewPublisher publisher, Common.Identity identity, long tick, PlanningViewRoot previous = null, bool gap = false)
    {
        var ticket = publisher.Begin(identity);
        var chunks = new List<PlanningViewChunk>();
        for (var z = MinZ; z < MinZ + Rows; z += 2)
        {
            if (gap && z == MinZ + 2) continue;
            var maxZ = Math.Min(z + 1, MinZ + Rows - 1);
            // The middle chunk is carried over from the previous root.
            chunks.Add(previous != null && z == MinZ + 2 ? previous.Chunk(1).Revalidated(tick)
                : new PlanningViewChunk(z, maxZ, ticket.Revision, tick, tick, Cells(maxZ - z + 1, ticket.Revision)));
        }
        return new PlanningViewRoot(identity, 1, ticket, 250, 250, MinX, MinZ, MinX + Width - 1, MinZ + Rows - 1, PlanningViewRoot.PlanningMask, tick, chunks.ToArray());
    }

    private static Common.ObservationContext Context(Common.Identity identity, long tick) => new Common.ObservationContext { Identity = identity.Clone(), Tick = tick, NativeGeneration = 1 };

    // Everything a reader can see of a root, as one string.
    private static string Fingerprint(PlanningViewRoot root)
    {
        var parts = new List<string> { root.Incarnation + "/" + root.Revision + "@" + root.PublishedTick + (root.Complete ? "+" : "-") };
        for (var i = 0; i < root.ChunkCount; i++)
        {
            var chunk = root.Chunk(i);
            parts.Add(chunk.MinZ + ".." + chunk.MaxZ + " r" + chunk.Revision + " c" + chunk.CapturedTick + " v" + chunk.ValidatedTick + ":"
                + string.Join(",", Enumerable.Range(0, chunk.Count).Select(j => chunk[j].Glow + chunk[j].ZoneId)));
        }
        return string.Join("|", parts);
    }

    internal static void Invoke()
    {
        OldReadersKeepTheirGraph();
        CaptureIsDetached();
        StaleAndIncompleteRootsRefused();
        ReadersAreBounded();
        RefreshFollowsTheLedger();
        Console.WriteLine("native-planning-window-view: " + checks + " checks passed");
    }

    // Readers acquire a root once and hold it while the writer publishes
    // three newer ones; each still sees its own graph, and its projection
    // is byte-identical before and after.
    private static void OldReadersKeepTheirGraph()
    {
        var publisher = new PlanningWindowViewPublisher(4);
        var identity = Identity("load");
        var first = Root(publisher, identity, 100);
        Check(publisher.TryPublish(first), "first root publishes");
        const int readers = 3;
        var acquired = new CountdownEvent(readers);
        var published = new ManualResetEventSlim();
        var tasks = Enumerable.Range(0, readers).Select(_ => Task.Run(() => {
            var root = publisher.Acquire();
            var before = Fingerprint(root);
            var wire = PlanningWindowViewProjection.Project(root, Context(identity, root.PublishedTick)).ToByteArray();
            acquired.Signal();
            if (!published.Wait(Guard)) throw new TimeoutException("writer");
            return before == Fingerprint(root) && wire.SequenceEqual(PlanningWindowViewProjection.Project(root, Context(identity, root.PublishedTick)).ToByteArray()) && root.Revision == first.Revision;
        })).ToArray();
        Check(acquired.Wait(Guard), "readers acquired");
        var previous = first;
        for (var tick = 101; tick <= 103; tick++)
        {
            var next = Root(publisher, identity, tick, previous);
            Check(publisher.TryPublish(next), "newer root publishes");
            Check(ReferenceEquals(publisher.Acquire(), next), "the newest root is current");
            previous = next;
        }
        published.Set();
        Check(Task.WaitAll(tasks, Guard), "readers finish");
        Check(tasks.All(t => t.Result), "every reader saw one stable graph");
        // The carried-over chunk kept its content revision and capture tick
        // and moved only its validation tick; the others are new captures.
        var middle = previous.Chunk(1);
        Check(middle.Revision == first.Revision && middle.CapturedTick == 100 && middle.ValidatedTick == 103, "carried chunk keeps its provenance");
        Check(previous.Chunk(0).Revision == previous.Revision && previous.Chunk(0).CapturedTick == 103, "recaptured chunk is new");
        Check(first.Chunk(1).ValidatedTick == 100, "revalidation leaves the old root's chunk alone");
        var wire = PlanningWindowViewProjection.Project(previous, Context(identity, 103));
        Check(wire.Revision == previous.Revision && wire.Incarnation == previous.Incarnation && wire.PublishedTick == 103 && wire.Complete && wire.Chunks.Count == 3
            && wire.Chunks[1].Revision == first.Revision && wire.Chunks[1].CapturedTick == 100 && wire.Chunks[1].ValidatedTick == 103
            && wire.Chunks[0].Cells.Rows.Count == 2 && wire.Chunks[2].Cells.Rows.Count == 1, "projection carries per-chunk provenance");
    }

    // A capture's builder array can change after the chunk is built
    // without changing the chunk, and a root's chunk list likewise.
    private static void CaptureIsDetached()
    {
        var publisher = new PlanningWindowViewPublisher(1);
        var ticket = publisher.Begin(Identity("load"));
        var built = Cells(1, 1);
        var chunk = new PlanningViewChunk(MinZ, MinZ, ticket.Revision, 5, 5, built);
        built[0] = new PlanningViewCell(true, 0, "RoofRockThick", null, null, 0.9, 0);
        Check(!chunk[0].Fogged && chunk[0].Glow == 1 && chunk[0].Roof == null, "chunk detached from its builder");
        var list = new[] { chunk };
        var root = new PlanningViewRoot(Identity("load"), 1, ticket, 250, 250, MinX, MinZ, MinX + Width - 1, MinZ, PlanningViewRoot.PlanningMask, 5, list);
        list[0] = new PlanningViewChunk(MinZ, MinZ, ticket.Revision, 5, 5, Cells(1, 2));
        Check(ReferenceEquals(root.Chunk(0), chunk) && root.Complete, "root detached from its chunk list");
    }

    private static void StaleAndIncompleteRootsRefused()
    {
        var publisher = new PlanningWindowViewPublisher(2);
        var identity = Identity("load");
        var pending = Root(publisher, identity, 100);
        var newer = Root(publisher, identity, 101);
        Check(publisher.TryPublish(newer), "newer publishes");
        Check(!publisher.TryPublish(pending), "an older revision cannot replace the current root");
        // A completion that lost the race still describes its own hop.
        var served = PlanningWindowViewProjection.Serve(publisher, pending, Context(identity, 100));
        Check(served != null && served.Revision == pending.Revision && ReferenceEquals(publisher.Acquire(), newer), "superseded candidate served, not published");

        // Reload: a capture begun before it can neither publish nor serve.
        var beforeReload = Root(publisher, identity, 102);
        var reloaded = Identity("reloaded");
        var fresh = Root(publisher, reloaded, 5);
        Check(publisher.Acquire() == null, "a new identity drops the view");
        Check(!publisher.TryPublish(beforeReload) && PlanningWindowViewProjection.Serve(publisher, beforeReload, Context(identity, 102)) == null, "old-load completion refused");
        Check(publisher.TryPublish(fresh) && fresh.Incarnation > beforeReload.Incarnation, "the new load publishes");
        // Unload drops the view and every pending capture.
        var beforeUnload = Root(publisher, reloaded, 6);
        publisher.Invalidate();
        Check(publisher.Acquire() == null && !publisher.TryPublish(beforeUnload), "unload refuses pending captures");

        var incomplete = Root(publisher, reloaded, 7, gap: true);
        Check(!incomplete.Complete && !publisher.TryPublish(incomplete) && PlanningWindowViewProjection.Serve(publisher, incomplete, Context(reloaded, 7)) == null, "incomplete coverage refused");
        var ticket = publisher.Begin(reloaded);
        var late = new PlanningViewChunk(MinZ, MinZ + Rows - 1, ticket.Revision, 8, 9, Cells(Rows, 1));
        var stale = new PlanningViewRoot(reloaded, 1, ticket, 250, 250, MinX, MinZ, MinX + Width - 1, MinZ + Rows - 1, PlanningViewRoot.PlanningMask, 8, new[] { late });
        Check(!stale.Complete && !publisher.TryPublish(stale), "a chunk validated after publication refused");

        var current = Root(publisher, reloaded, 10);
        Check(current.Serves(reloaded, MinX, MinZ, MinX + Width - 1, MinZ + Rows - 1, PlanningViewRoot.PlanningMask), "the root serves its own region and mask");
        Check(!current.Serves(reloaded, MinX + 1, MinZ, MinX + Width, MinZ + Rows - 1, PlanningViewRoot.PlanningMask), "another region is not served");
        Check(!current.Serves(reloaded, MinX, MinZ, MinX + Width - 1, MinZ + Rows - 1, "things"), "another mask is not served");
        Check(!current.Serves(identity, MinX, MinZ, MinX + Width - 1, MinZ + Rows - 1, PlanningViewRoot.PlanningMask), "another load is not served");
    }

    private static void ReadersAreBounded()
    {
        var publisher = new PlanningWindowViewPublisher(2);
        var identity = Identity("load");
        var root = Root(publisher, identity, 1);
        Check(publisher.TryPublish(root), "root publishes");
        var a = publisher.Open(root); var b = publisher.Open(root);
        Check(a != null && b != null && publisher.Open(root) == null && publisher.ReadersAvailable == 0, "readers bounded");
        Check(PlanningWindowViewProjection.Serve(publisher, root, Context(identity, 1)) == null, "no slot, no projection");
        a.Dispose(); a.Dispose();
        Check(publisher.ReadersAvailable == 1, "a reader releases exactly once");
        b.Dispose();
        Check(PlanningWindowViewProjection.Serve(publisher, root, Context(identity, 1)) != null && publisher.ReadersAvailable == 2, "serving releases its reader");
    }
}
