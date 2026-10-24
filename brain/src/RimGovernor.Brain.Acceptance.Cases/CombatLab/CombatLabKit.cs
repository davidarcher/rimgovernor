using RimGovernor.Contracts.Wire;
using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Bridge;
using Common = RimGovernor.Contracts.Wire.Common;
using Fx = RimGovernor.Fixtures.Contracts;
using Lifecycle = RimGovernor.Contracts.Wire.Lifecycle;
using Mirror = RimGovernor.Contracts.Wire.Mirror;
using Observations = RimGovernor.Contracts.Wire.Observations;
using Operations = RimGovernor.Contracts.Wire.Operations;
using Receipts = RimGovernor.Contracts.Wire.Receipts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// What the typed <c>combatlab/*</c> cases share: a combat fixture as a stage spec on a site centred on the lab, its
/// staged ids and absolute cells, combat orders through operations_apply, the production pawn and area reads, and the
/// snapshot stream's combat sections read inside a tick wait.
/// </summary>
internal static class CombatLabKit
{
    /// <summary>The site's half edge: every fixture stays within ±30 cells of its centre.</summary>
    internal const int Half = 40;

    /// <summary>The longest a probe of a running lab is apart in game ticks (a 250 ms poll at the run speed): a wait's floor.</summary>
    internal const int ProbeTicks = 3000;

    /// <summary>The lab-centred site every fixture is staged on: its centre is the lab centre the fixtures were drawn around.</summary>
    internal static readonly Fx.StageSite Site = new() { Width = (2 * Half) + 1, Height = (2 * Half) + 1, OffsetX = -Half, OffsetZ = -Half };

    /// <summary>The named fixture around the site centre, adapted by <paramref name="edit"/> (which receives the centre).</summary>
    internal static LabFixture Fixture(string name, Action<LabFixture, int, int>? edit = null)
    {
        var f = CombatFixtures.Build(name, Half, Half);
        edit?.Invoke(f, Half, Half);
        return f;
    }

    /// <summary>The fixture's stage spec on the lab-centred site, staged paused.</summary>
    internal static Fx.StageSpec Spec(LabFixture f) => f.ToSpec() with { Paused = true, Site = Site };

    /// <summary>A pawn named by id, as an order's subject or target.</summary>
    internal static Operations.EntityPrecondition Pawn(string id) => new() { EntityId = id };

    /// <summary>One order for <paramref name="pawn"/> (null for a door order).</summary>
    internal static Operations.CombatOrder Order(string? pawn, Operations.CombatOrderOrder order) =>
        new() { Pawn = pawn is null ? null : Pawn(pawn), Order = order };

    /// <summary>
    /// Sends one combat_orders batch through operations_apply and returns its per-order results; a refused or failed
    /// action, or a result count off the batch, fails the label.
    /// </summary>
    internal static async Task<IReadOnlyList<Receipts.CombatOrderResult>> OrdersAsync(TypedSession s, string label, CancellationToken cancellationToken, params Operations.CombatOrder[] orders)
    {
        var reply = await s.CallAsync(label, NativeOps.Apply, new Operations.ApplyRequest
        {
            Identity = s.Identity,
            Actions = [new Operations.Action { Key = label, Intent = new Operations.CombatOrders { Orders = [.. orders] } }],
        }, cancellationToken).ConfigureAwait(false);
        var outcome = reply.Results.Count == 1 ? reply.Results[0].Outcome?.Applied?.Outcome : null;
        var observed = outcome?.Applied?.Observed ?? (outcome as Receipts.ReceiptOutcome.NoChangeCase)?.Value.Observed;
        var results = observed?.Effect?.CombatOrders?.Results
            ?? throw new AcceptanceException($"{label}: combat orders not applied: {reply.BatchFailure} {string.Join("; ", reply.Results)}");
        if (results.Count != orders.Length)
        {
            throw new AcceptanceException($"{label}: {results.Count} results for {orders.Length} orders: {string.Join("; ", results)}");
        }

        s.RecordWire(label, results);
        return [.. results];
    }

    /// <summary>Drafts <paramref name="pawns"/> in one batch; an order that does not apply fails.</summary>
    internal static async Task DraftAsync(TypedSession s, IEnumerable<string> pawns, CancellationToken cancellationToken)
    {
        foreach (var r in await OrdersAsync(s, "draft", cancellationToken, [.. pawns.Select(p => Order(p, new Operations.CombatOrderOrder.DraftCase(new Operations.Clear())))]).ConfigureAwait(false))
        {
            Applied("draft", r);
        }
    }

    /// <summary>Sends one order and returns its result.</summary>
    internal static async Task<Receipts.CombatOrderResult> OrderAsync(TypedSession s, string label, string? pawn, Operations.CombatOrderOrder order, CancellationToken cancellationToken) =>
        (await OrdersAsync(s, label, cancellationToken, Order(pawn, order)).ConfigureAwait(false))[0];

    /// <summary>Checks an order applied (with <paramref name="job"/> when given).</summary>
    internal static void Applied(string label, Receipts.CombatOrderResult r, string? job = null)
    {
        if (r.Applied != true || (job is not null && (r.JobDef ?? "") != job))
        {
            throw new AcceptanceException($"{label}: want applied{(job is null ? "" : " with job " + job)}, got {r}");
        }
    }

    /// <summary>Checks an order refused with <paramref name="reason"/>.</summary>
    internal static void Refused(string label, Receipts.CombatOrderResult r, string reason)
    {
        if (r.Applied == true || r.Refusal != reason)
        {
            throw new AcceptanceException($"{label}: want refusal {reason}, got {r}");
        }
    }

    /// <summary>The production pawn rows of <paramref name="ids"/> (dead ones included) by id, with health, gear and settings.</summary>
    internal static async Task<Dictionary<string, Observations.PawnState>> PawnsAsync(TypedSession s, string label, IEnumerable<string> ids, CancellationToken cancellationToken)
    {
        var reply = await s.CallAsync(label, NativeOps.ListPawns, new Observations.ListPawnsRequest
        {
            Scope = s.Scope,
            Filter = new Observations.PawnFilter { Ids = [.. ids], IncludeDead = true },
        }, cancellationToken).ConfigureAwait(false);
        var snapshot = reply;
        return snapshot.Pawns.ToDictionary(p => p.Pawn.Id, StringComparer.Ordinal);
    }

    /// <summary>The production pawn row of <paramref name="id"/>; a pawn the read does not list fails the label.</summary>
    internal static async Task<Observations.PawnState> PawnAsync(TypedSession s, string label, string id, CancellationToken cancellationToken) =>
        (await PawnsAsync(s, label, [id], cancellationToken).ConfigureAwait(false)).GetValueOrDefault(id)
            ?? throw new AcceptanceException($"{label}: pawn {id} not listed");

    /// <summary>The pawn's current job def, empty without one.</summary>
    internal static string Job(Observations.PawnState p) => p.Job.DefName ?? "";

    /// <summary>The thing the pawn's job targets, null for none or a cell.</summary>
    internal static string? JobThing(Observations.PawnState p) => p.Job.TargetA?.Target?.Entity?.Id;

    /// <summary>The colony's allowed areas (the typed colony read's policy section).</summary>
    internal static async Task<IReadOnlyList<Observations.AllowedArea>> AreasAsync(TypedSession s, string label, CancellationToken cancellationToken)
    {
        var facts = await s.CallAsync(label, NativeOps.ReadColonyFacts, new Observations.ColonyFactsRequest { Scope = s.Scope, Planning = false }, cancellationToken).ConfigureAwait(false);
        return [.. facts.Policies.AllowedAreas];
    }

    /// <summary>The player building at <paramref name="cell"/> (a door, a mortar), from the production building read.</summary>
    internal static async Task<Observations.BuildingState> BuildingAtAsync(TypedSession s, string label, Common.Cell cell, CancellationToken cancellationToken)
    {
        var rows = await s.BuildingsAsync(label, new Observations.ListBuildingsRequest(), cancellationToken).ConfigureAwait(false);
        return rows.FirstOrDefault(b => b.Building.Place is RimGovernor.Contracts.Wire.Observations.EntityPlace.OnMap { Cell: var at } && at == cell)
            ?? throw new AcceptanceException($"{label}: no player building at {Show(cell)}");
    }

    /// <summary>A geometry read of <paramref name="request"/>, checked against its request.</summary>
    internal static async Task<Mirror.CombatGeometry> GeometryAsync(TypedSession s, string label, Mirror.CombatGeometryRequest request, CancellationToken cancellationToken)
    {
        var g = await s.CallAsync(label, NativeOps.ReadCombatGeometry, request, cancellationToken).ConfigureAwait(false);
        CombatGeometryContract.Validate(label, g, request);
        s.RecordWire(label, g);
        return g;
    }

    /// <summary>The loaded game's tick.</summary>
    internal static async Task<long> TickAsync(TypedSession s, string label, CancellationToken cancellationToken)
    {
        var loaded = await s.CallAsync(label, NativeOps.ReadTick, new Lifecycle.TickRequest(), cancellationToken).ConfigureAwait(false);
        return loaded.Context?.Tick ?? throw new AcceptanceException($"{label}: no loaded tick: {WireJson.Serialize(loaded)}");
    }

    /// <summary>A cell as text.</summary>
    /// <summary>Load ids as a geometry request's hostile ids.</summary>
    internal static RimGovernor.Brain.Domain.EquatableArray<RimGovernor.Brain.Domain.PawnId> PawnIds(IEnumerable<string> ids) => [.. ids.Select(id => new RimGovernor.Brain.Domain.PawnId(id))];

    internal static string Show(Common.Cell? c) => c is null ? "<none>" : string.Create(CultureInfo.InvariantCulture, $"({c.X},{c.Z})");

    /// <summary>The Chebyshev distance between two cells.</summary>
    internal static int Chebyshev(Common.Cell a, Common.Cell b) =>
        Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Z - b.Z));

    /// <summary>A report object of the named values (a write-only summary; replies are read as typed records).</summary>
    internal static JsonObject Obj(params (string Key, JsonNode? Value)[] values) => new([.. values.Select(v => KeyValuePair.Create(v.Key, v.Value))]);
}

/// <summary>
/// One staged fixture: its spec-order ids by side and the absolute cells of the fixture's centre-relative offsets. The
/// fixture was drawn around the site centre, so <c>(Cx + dx, Cz + dz)</c> is the cell the fixture's <c>(cx + dx, cz + dz)</c> became.
/// </summary>
internal sealed class StagedLab
{
    private readonly int _x;
    private readonly int _z;

    public StagedLab(LabFixture fixture, Fx.StageReply staged)
    {
        Fixture = fixture;
        Staged = staged;
        var site = staged.Site ?? throw new AcceptanceException("test/stage replied no site for a sited spec");
        _x = site.X;
        _z = site.Z;
        for (var i = 0; i < fixture.Pawns.Count; i++)
        {
            _ = staged.Pawns.GetValueOrDefault(LabFixture.PawnName(i)) ?? throw new AcceptanceException($"{fixture.Name}: pawn {i} not staged");
        }
    }

    public LabFixture Fixture { get; }

    public Fx.StageReply Staged { get; }

    /// <summary>The fixture centre's absolute x.</summary>
    public int Cx => _x + CombatLabKit.Half;

    /// <summary>The fixture centre's absolute z.</summary>
    public int Cz => _z + CombatLabKit.Half;

    /// <summary>The absolute cell at an offset from the fixture centre.</summary>
    public Common.Cell Cell(int dx, int dz) => new() { X = Cx + dx, Z = Cz + dz };

    /// <summary>The absolute cell of a fixture coordinate (a pawn's or a thing's X, Z).</summary>
    public Common.Cell At(int x, int z) => new() { X = _x + x, Z = _z + z };

    /// <summary>The staged pawn <paramref name="i"/> (fixture order).</summary>
    public Fx.StagedPawn Pawn(int i) => Staged.Pawns[LabFixture.PawnName(i)];

    /// <summary>The staged ids of one side, fixture order.</summary>
    public List<string> Ids(string side)
    {
        var output = new List<string>();
        for (var i = 0; i < Fixture.Pawns.Count; i++)
        {
            if (Fixture.Pawns[i].Side == side)
            {
                output.Add(Pawn(i).Id);
            }
        }

        return output;
    }

    public List<string> Colonists() => Ids(CombatFixtures.Colonist);

    public List<string> Hostiles() => Ids(CombatFixtures.Hostile);

    public List<string> Animals() => Ids(CombatFixtures.Animal);

    public List<string> Manhunters() => Ids(CombatFixtures.Manhunter);

    public List<string> Mechs() => Ids(CombatFixtures.Mech);

    /// <summary>The id of fixture thing <paramref name="i"/> (a building or an item).</summary>
    public string Thing(int i)
    {
        var key = LabFixture.ThingName(i);
        var made = Staged.Buildings.GetValueOrDefault(key) ?? Staged.Things.GetValueOrDefault(key);
        return made is { Ids.Count: > 0 } ? made.Ids[0] : throw new AcceptanceException($"{Fixture.Name}: thing {i} not staged");
    }

    /// <summary>Every id of the fixture's staged pawns.</summary>
    public IEnumerable<string> AllPawns() => Enumerable.Range(0, Fixture.Pawns.Count).Select(i => Pawn(i).Id);
}

/// <summary>
/// The snapshot stream's frames over observations_read_snapshot, read from inside a tick wait's probe: the newest frame
/// published since the last one read (the stream keeps only the latest), and the combat events not seen before (every
/// frame carries the whole event ring).
/// </summary>
internal sealed class LabFrames
{
    private readonly TypedSession _s;
    private readonly HashSet<(long, uint)> _seen = [];
    private ulong _head;
    private int _reads;

    public LabFrames(TypedSession s) => _s = s;

    /// <summary>The newest frame read so far.</summary>
    public Observations.BundleSnapshot? Last { get; private set; }

    /// <summary>Every combat event first seen in a frame read so far, oldest first.</summary>
    public List<Mirror.CombatEventRow> Events { get; } = [];

    /// <summary>The newest frame's tick, -1 before any.</summary>
    public long Tick => Last?.Context?.Tick ?? -1;

    /// <summary>
    /// Reads the newest frame published since the last read (subscribing the stream on the first) and returns it, or
    /// null when none is newer; its unseen combat events join <see cref="Events"/>.
    /// </summary>
    public async Task<Observations.BundleSnapshot?> NextAsync(CancellationToken cancellationToken)
    {
        var label = "frame-" + (++_reads).ToString(CultureInfo.InvariantCulture);
        var reply = await _s.CallAsync(label, NativeOps.ReadSnapshot, new Observations.SnapshotReadRequest { AfterNumber = _head }, cancellationToken).ConfigureAwait(false);
        var read = reply.Outcome?.Read ?? throw new AcceptanceException($"{label}: snapshot read failed: {reply.Outcome}");
        if (read.Frame is not { } frame)
        {
            return null;
        }

        if (frame.Context?.Identity != _s.Identity)
        {
            throw new AcceptanceException($"{label}: frame describes another world: {frame.Context?.Identity}");
        }

        _head = read.Head;
        Last = frame;
        foreach (var e in frame.CombatEvents)
        {
            if (_seen.Add((e.At.Tick, e.At.Seq)))
            {
                Events.Add(e);
            }
        }

        return frame;
    }

    /// <summary>
    /// Runs the lab until a frame captured at or past <paramref name="fromTick"/> satisfies <paramref name="done"/>, at most
    /// <paramref name="ticks"/> ticks; returns that frame. <paramref name="done"/> may throw to fail on a frame.
    /// </summary>
    public async Task<Observations.BundleSnapshot> UntilAsync(string label, int ticks, long fromTick, Func<Observations.BundleSnapshot, bool> done, CancellationToken cancellationToken)
    {
        Observations.BundleSnapshot? hit = null;
        _ = await _s.RunUntilAsync(label, ticks, async ct =>
        {
            var frame = await NextAsync(ct).ConfigureAwait(false);
            if (frame is not null && (frame.Context?.Tick ?? -1) >= fromTick && done(frame))
            {
                hit = frame;
            }

            return (Wait.Signature(Tick, Events.Count), hit is not null);
        }, cancellationToken).ConfigureAwait(false);
        return hit ?? throw new AcceptanceException($"{label}: no frame satisfied the wait");
    }

    /// <summary>The first frame captured with the combat sections live: a frame past the staging the claim reads.</summary>
    public Task<Observations.BundleSnapshot> CombatAsync(string label, CancellationToken cancellationToken) =>
        UntilAsync(label, CombatLabKit.ProbeTicks, 0, f => f.CombatPawns.Count > 0, cancellationToken);

    /// <summary>The frame's combat row of <paramref name="id"/>, null when it has none.</summary>
    public static Mirror.CombatPawn? Row(Observations.BundleSnapshot frame, string id) => frame.CombatPawns.FirstOrDefault(p => p.Id.Value == id);
}

/// <summary>A geometry reply checked against its request (the combat_geometry contract the cases rely on).</summary>
internal static class CombatGeometryContract
{
    internal const int MaxCells = 64;

    internal const int MaxHostiles = 16;

    internal const int MaxRadius = 12;

    /// <summary>The main-thread budget of one geometry read at the caps.</summary>
    internal const double BudgetMs = 50.0;

    /// <summary>
    /// The request's world, every cell in order with every hostile's line in order and cover in 0..1, and proposals
    /// only for a propose block: standable, distinct from each other and the named cells
    /// (a rescue route excepted), within the cells cap.
    /// </summary>
    internal static void Validate(string label, Mirror.CombatGeometry g, Mirror.CombatGeometryRequest request)
    {
        if (g.Context.Identity != request.Identity)
        {
            throw Contract(label, "combat geometry world mismatch");
        }

        if (g.Cells.Count != request.Cells.Count)
        {
            throw Contract(label, "combat geometry cell count");
        }

        var seen = new HashSet<(int, int)>();
        for (var i = 0; i < g.Cells.Count; i++)
        {
            var row = g.Cells[i];
            if (row.Cell != request.Cells[i])
            {
                throw Contract(label, $"combat geometry cell {i} off its request");
            }

            _ = seen.Add(Key(row.Cell));
            Lines(label, row, request, i);
        }

        if (request.Propose is null)
        {
            if (g.Proposed.Count != 0)
            {
                throw Contract(label, "combat geometry proposals without propose");
            }

            return;
        }

        if (g.Proposed.Count > MaxCells - request.Cells.Count)
        {
            throw Contract(label, "combat geometry proposals over the cells cap");
        }

        var route = request.Propose is Mirror.CombatGeometryPropose.RescuePath;
        for (var i = 0; i < g.Proposed.Count; i++)
        {
            var row = g.Proposed[i];
            var valid = row.Cell is { X: >= 0, Z: >= 0 };
            if (!valid || (!route && (!row.Standable || !seen.Add(Key(row.Cell)))))
            {
                throw Contract(label, $"combat geometry proposal {i} invalid or repeated");
            }

            Lines(label, row, request, request.Cells.Count + i);
        }
    }

    /// <summary>A proposal's cells as a set.</summary>
    internal static HashSet<(int X, int Z)> Cells(IEnumerable<Mirror.CombatGeometryCell> rows) => [.. rows.Select(r => Key(r.Cell))];

    internal static (int X, int Z) Key(Common.Cell? c) => (c?.X ?? 0, c?.Z ?? 0);

    private static void Lines(string label, Mirror.CombatGeometryCell row, Mirror.CombatGeometryRequest request, int i)
    {
        if (row.Lines.Count != request.HostileIds.Count)
        {
            throw Contract(label, $"combat geometry cell {i} lines off its request");
        }

        for (var j = 0; j < row.Lines.Count; j++)
        {
            var line = row.Lines[j];
            if (line.HostileId != request.HostileIds[j] || double.IsNaN(line.Cover) || line.Cover < 0 || line.Cover > 1)
            {
                throw Contract(label, $"combat geometry line {i}.{j} invalid");
            }
        }
    }

    private static AcceptanceException Contract(string label, string message) => new($"{label}: contract: {message}");
}
