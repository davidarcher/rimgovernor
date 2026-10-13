using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

// Stages a plan the controller has already derived (Go package planstage): a native case reads the layout plan from
// the journal of a first, inert service run, puts a room of that plan on the ground finished (walls and doors of a
// chosen stuff, a roof, a constructed floor, furniture of a fixed quality and hit points, stock and a warehouse
// stockpile through test/plan_stage) and lets the real controller reconcile the ground to the plan. Staging and
// native readbacks happen between service runs; a StagePhase is one run.
//
// The layout plan itself (policy.LayoutPlan) lives in the policy project, which this assembly does not reference: the
// geometry the staging needs is carried by PlannedRoomGeometry, which the case maps from its plan.

/// <summary>A map cell in the fixture's JSON.</summary>
public readonly record struct StageCell(int X, int Z)
{
    internal JsonObject ToJson() => new() { ["x"] = X, ["z"] = Z };
}

/// <summary>An inclusive cell rectangle.</summary>
public readonly record struct StageRect(int MinX, int MinZ, int MaxX, int MaxZ)
{
    internal JsonObject ToJson() => new() { ["minX"] = MinX, ["minZ"] = MinZ, ["maxX"] = MaxX, ["maxZ"] = MaxZ };
}

/// <summary>
/// A finished player building of <see cref="Stuff"/> at X,Z: a wall, a door (Rotation 0..3) or, in
/// <see cref="StageSpec.Things"/>, furniture with a fixed <see cref="Quality"/> (0..6, null leaves the rolled one) and
/// <see cref="HitFraction"/> (0..1, null leaves it undamaged).
/// </summary>
public sealed record StageBuilding
{
    public string Def { get; init; } = "";

    public string Stuff { get; init; } = "";

    public int X { get; init; }

    public int Z { get; init; }

    public int Rotation { get; init; }

    public int? Quality { get; init; }

    public double? HitFraction { get; init; }

    /// <summary>Leaves the thing unowned and a plant fully grown: a tree, a ruin or a casket, what a room's ground holds that the colony did not build.</summary>
    public bool Foreign { get; init; }

    /// <summary>Loads an ancient casket with friendly contents.</summary>
    public bool Filled { get; init; }

    internal JsonObject ToJson()
    {
        var row = new JsonObject();
        if (Def.Length > 0)
        {
            row["def"] = Def;
        }

        if (Stuff.Length > 0)
        {
            row["stuff"] = Stuff;
        }

        row["x"] = X;
        row["z"] = Z;
        if (Rotation != 0)
        {
            row["rotation"] = Rotation;
        }

        if (Quality is { } quality)
        {
            row["quality"] = quality;
        }

        if (HitFraction is { } hit)
        {
            row["hitFraction"] = hit;
        }

        if (Foreign)
        {
            row["foreign"] = true;
        }

        if (Filled)
        {
            row["filled"] = true;
        }

        return row;
    }
}

/// <summary>Lays <see cref="Def"/> on <see cref="Cells"/>.</summary>
public sealed record StageFloor(string Def, IReadOnlyList<StageCell> Cells)
{
    internal JsonObject ToJson() => new() { ["def"] = Def, ["cells"] = new JsonArray([.. Cells.Select(c => (JsonNode?)c.ToJson())]) };
}

/// <summary><see cref="Count"/> of <see cref="Def"/> lying near X,Z, unforbidden.</summary>
public sealed record StageDrop(string Def, int Count, int X, int Z)
{
    internal JsonObject ToJson() => new() { ["def"] = Def, ["count"] = Count, ["x"] = X, ["z"] = Z };
}

/// <summary>One staging call (Go <c>planstage.Spec</c>).</summary>
public sealed record StageSpec
{
    public IReadOnlyList<string> Research { get; init; } = [];

    public IReadOnlyList<StageBuilding> Walls { get; init; } = [];

    public IReadOnlyList<StageBuilding> Doors { get; init; } = [];

    public IReadOnlyList<StageRect> Roof { get; init; } = [];

    public IReadOnlyList<StageFloor> Floor { get; init; } = [];

    public IReadOnlyList<StageBuilding> Things { get; init; } = [];

    public IReadOnlyList<StageDrop> Drops { get; init; } = [];

    public StageRect? Stockpile { get; init; }

    /// <summary>The spec in the fixture's JSON shape (empty members omitted).</summary>
    public JsonObject ToJson()
    {
        var row = new JsonObject();
        if (Research.Count > 0)
        {
            row["research"] = new JsonArray([.. Research.Select(r => (JsonNode?)JsonValue.Create(r))]);
        }

        Add(row, "walls", Walls.Select(b => b.ToJson()));
        Add(row, "doors", Doors.Select(b => b.ToJson()));
        Add(row, "roof", Roof.Select(r => r.ToJson()));
        Add(row, "floor", Floor.Select(f => f.ToJson()));
        Add(row, "things", Things.Select(b => b.ToJson()));
        Add(row, "drops", Drops.Select(d => d.ToJson()));
        if (Stockpile is { } stockpile)
        {
            row["stockpile"] = stockpile.ToJson();
        }

        return row;
    }

    private static void Add(JsonObject row, string key, IEnumerable<JsonObject> items)
    {
        var array = new JsonArray([.. items.Select(i => (JsonNode?)i)]);
        if (array.Count > 0)
        {
            row[key] = array;
        }
    }
}

/// <summary>The stage reply: the load ids of the walls, doors and things spawned, in spec order.</summary>
public sealed record Staged(IReadOnlyList<string> Walls, IReadOnlyList<string> Doors, IReadOnlyList<string> Things, JsonObject Reply);

/// <summary>One cell of an audit: its edifice, terrain and room.</summary>
public sealed record CellRead
{
    public StageCell Cell { get; init; }

    public string Edifice { get; init; } = "";

    public string EdificeStuff { get; init; } = "";

    public string Terrain { get; init; } = "";

    public string Role { get; init; } = "";

    public bool Roofed { get; init; }

    public bool Enclosed { get; init; }
}

/// <summary>
/// One thing of an audit; <see cref="Found"/> is false when the id is neither spawned, packed nor carried.
/// <see cref="Packed"/> is the minified item holding it. <see cref="Quality"/> is -1 for a thing without one.
/// </summary>
public sealed record ThingRead
{
    public string Id { get; init; } = "";

    public string Def { get; init; } = "";

    public string Stuff { get; init; } = "";

    public bool Found { get; init; }

    public bool Packed { get; init; }

    public bool Spawned { get; init; }

    public int Quality { get; init; } = -1;

    public int HitPoints { get; init; }

    public int MaxHP { get; init; }

    public int X { get; init; }

    public int Z { get; init; }
}

/// <summary>The fixture's read of things and cells.</summary>
public sealed record StageAudit(IReadOnlyDictionary<string, ThingRead> Things, IReadOnlyDictionary<StageCell, CellRead> Cells, JsonObject Reply)
{
    /// <summary>The read of <paramref name="cell"/>; the empty read for a cell the audit did not report (Go's zero-value map lookup).</summary>
    public CellRead CellAt(StageCell cell) => Cells.TryGetValue(cell, out var read) ? read : new CellRead();
}

/// <summary>
/// The geometry of one planned room the staging needs (the layout plan's <c>PlannedRoom</c>): its role, the interior
/// rectangle, the primary door and the planned rotation of it (0..3, see <see cref="PlanStage.RotationNumber"/>), and
/// the shell doors of the whole plan (<c>plan.ShellDoors(room)</c>).
/// </summary>
public sealed record PlannedRoomGeometry
{
    public string Role { get; init; } = "";

    public int X { get; init; }

    public int Z { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public StageCell Door { get; init; }

    public int DoorRotation { get; init; }

    public IReadOnlyList<StageCell> ShellDoors { get; init; } = [];
}

/// <summary>
/// A planned room's ring on the ground: the interior and wall cells split into plain walls and doors, the roof
/// rectangle over both.
/// </summary>
public sealed record StageRing(IReadOnlyList<StageCell> Interior, IReadOnlyList<StageCell> Walls, IReadOnlyList<StageBuilding> Doors, StageRect Roof)
{
    /// <summary>
    /// The staging of the ring finished: walls and doors of <paramref name="stuff"/>, the roof over the ring and, with
    /// <paramref name="floor"/> set, the interior laid with that constructed floor. The caller adds what else the room holds.
    /// </summary>
    public StageSpec Spec(string stuff, string floor)
    {
        var doors = Doors.Select(d => d with { Stuff = stuff }).ToList();
        return new StageSpec
        {
            Roof = [Roof],
            Walls = [.. Walls.Select(c => new StageBuilding { X = c.X, Z = c.Z, Stuff = stuff })],
            Doors = doors,
            Floor = string.IsNullOrEmpty(floor) ? [] : [new StageFloor(floor, Interior)],
        };
    }

    /// <summary>Every cell of the ring, interior and walls and doors.</summary>
    public IReadOnlyList<StageCell> Cells() => [.. Interior, .. Walls, .. Doors.Select(d => new StageCell(d.X, d.Z))];

    /// <summary>
    /// Asserts every interior cell enclosed and roofed and every wall and door cell holding a building, in
    /// <paramref name="audit"/>, naming the first failure.
    /// <para>
    /// A ring cell holding Frame_Wall is an in-place wall swap in progress (the reconciler's wall_up): it counts as a
    /// wall, and the enclosure check is skipped while any frame stands, since a frame does not seal the room.
    /// </para>
    /// </summary>
    public void Sealed(StageAudit audit)
    {
        ArgumentNullException.ThrowIfNull(audit);
        var framed = Walls.Any(c => audit.CellAt(c).Edifice == "Frame_Wall");
        foreach (var c in Interior)
        {
            if (!audit.Cells.TryGetValue(c, out var read) || !read.Roofed || (!read.Enclosed && !framed))
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"interior cell {c.X},{c.Z} is not enclosed and roofed: {read}"));
            }
        }

        foreach (var c in Walls)
        {
            var read = audit.CellAt(c);
            if (read.Edifice is not ("Wall" or "Frame_Wall"))
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"ring cell {c.X},{c.Z} holds \"{read.Edifice}\", not a wall"));
            }
        }

        foreach (var d in Doors)
        {
            var read = audit.CellAt(new StageCell(d.X, d.Z));
            if (read.Edifice != "Door")
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"door cell {d.X},{d.Z} holds \"{read.Edifice}\", not a door"));
            }
        }
    }
}

/// <summary>The fixture ops and geometry helpers for staging a derived plan (Go package <c>planstage</c>).</summary>
public static class PlanStage
{
    /// <summary>The fixture op (scripts/fixtures/PlanStageFixture.cs); a case lists it in RequiredOps.</summary>
    public const string Tool = "test/plan_stage";

    /// <summary>The wall-clock bound of one service phase; the stall budget is what ends a broken one.</summary>
    public static readonly TimeSpan Ceiling = TimeSpan.FromMinutes(10);

    /// <summary>Runs <paramref name="spec"/> on the paused game.</summary>
    public static async Task<Staged> StageAsync(Harness harness, string label, StageSpec spec, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentNullException.ThrowIfNull(spec);
        var reply = await harness.CallAsync(label, Tool, new JsonObject { ["action"] = "stage", ["spec"] = spec.ToJson().ToJsonString() }, cancellationToken).ConfigureAwait(false);
        if (reply["success"].AsBool() != true)
        {
            throw new AcceptanceException($"{Tool} stage refused: {reply.ToJson()}");
        }

        var walls = new List<string>();
        var doors = new List<string>();
        foreach (var row in reply["spawned"].AsSlice())
        {
            (row.Member("def").AsString() == "Door" ? doors : walls).Add(row.Member("id").AsString());
        }

        var things = reply["things"].AsSlice().Select(row => row.Member("id").AsString()).ToList();
        return new Staged(walls, doors, things, reply);
    }

    /// <summary>Audits <paramref name="ids"/> and <paramref name="cells"/>.</summary>
    public static async Task<StageAudit> ReadAsync(Harness harness, string label, IEnumerable<string> ids, IEnumerable<StageCell> cells, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(cells);
        var spec = new JsonObject
        {
            ["ids"] = new JsonArray([.. ids.Select(id => (JsonNode?)JsonValue.Create(id))]),
            ["cells"] = new JsonArray([.. cells.Select(c => (JsonNode?)c.ToJson())]),
        };
        var reply = await harness.CallAsync(label, Tool, new JsonObject { ["action"] = "audit", ["spec"] = spec.ToJsonString() }, cancellationToken).ConfigureAwait(false);
        if (reply["success"].AsBool() != true)
        {
            throw new AcceptanceException($"{Tool} audit refused: {reply.ToJson()}");
        }

        var things = new Dictionary<string, ThingRead>(StringComparer.Ordinal);
        foreach (var row in reply["things"].AsSlice())
        {
            var def = row.Member("def").AsString();
            var t = new ThingRead
            {
                Id = row.Member("id").AsString(),
                Def = def,
                Stuff = row.Member("stuff").AsString(),
                Found = def.Length > 0,
                Packed = row.Member("packed").AsBool() == true,
                Spawned = row.Member("spawned").AsBool() == true,
                Quality = row.Member("quality") is { } q ? (int)q.AsNumber() : -1,
                HitPoints = (int)row.Member("hitPoints").AsNumber(),
                MaxHP = (int)row.Member("maxHitPoints").AsNumber(),
                X = (int)row.Member("x").AsNumber(),
                Z = (int)row.Member("z").AsNumber(),
            };
            things[t.Id] = t;
        }

        var cellReads = new Dictionary<StageCell, CellRead>();
        foreach (var row in reply["cells"].AsSlice())
        {
            var cell = new StageCell((int)row.Member("x").AsNumber(), (int)row.Member("z").AsNumber());
            cellReads[cell] = new CellRead
            {
                Cell = cell,
                Edifice = row.Member("edifice").AsString(),
                EdificeStuff = row.Member("edificeStuff").AsString(),
                Terrain = row.Member("terrain").AsString(),
                Role = row.Member("role").AsString(),
                Roofed = row.Member("roofed").AsBool() == true,
                Enclosed = row.Member("enclosed").AsBool() == true,
            };
        }

        return new StageAudit(things, cellReads, reply);
    }

    /// <summary>
    /// <paramref name="room"/>'s ring, its doors the plan's shell doors. The primary door keeps the planned rotation;
    /// any other faces by the wall it stands in.
    /// </summary>
    public static StageRing RingOf(PlannedRoomGeometry room)
    {
        ArgumentNullException.ThrowIfNull(room);
        var doors = new Dictionary<StageCell, int>();
        foreach (var d in room.ShellDoors)
        {
            doors[d] = -1;
        }

        doors[room.Door] = room.DoorRotation;
        var roof = new StageRect(room.X - 1, room.Z - 1, room.X + room.Width, room.Z + room.Height);
        var interior = new List<StageCell>();
        var walls = new List<StageCell>();
        var doorRows = new List<StageBuilding>();
        for (var x = room.X - 1; x <= room.X + room.Width; x++)
        {
            for (var z = room.Z - 1; z <= room.Z + room.Height; z++)
            {
                var cell = new StageCell(x, z);
                if (x >= room.X && x < room.X + room.Width && z >= room.Z && z < room.Z + room.Height)
                {
                    interior.Add(cell);
                    continue;
                }

                if (doors.TryGetValue(cell, out var rot))
                {
                    if (rot < 0)
                    {
                        // A door in a side wall opens east and west.
                        rot = z == room.Z - 1 || z == room.Z + room.Height ? 0 : 1;
                    }

                    doorRows.Add(new StageBuilding { X = x, Z = z, Rotation = rot });
                    continue;
                }

                walls.Add(cell);
            }
        }

        return new StageRing(interior, walls, doorRows, roof);
    }

    /// <summary>A rotation (north, east, south, west) as the fixture's 0..3.</summary>
    public static int RotationNumber(string rotation) => rotation?.ToLowerInvariant() switch
    {
        "east" => 1,
        "south" => 2,
        "west" => 3,
        _ => 0,
    };

    /// <summary>The first room of <paramref name="role"/> in plan order, the room the planners serve first; null when the plan has none.</summary>
    public static PlannedRoomGeometry? FirstRoom(IEnumerable<PlannedRoomGeometry> rooms, string role)
    {
        ArgumentNullException.ThrowIfNull(rooms);
        return rooms.FirstOrDefault(r => r.Role == role);
    }

    /// <summary>
    /// A width by height rectangle near <paramref name="centre"/> that no planned room (walls included, with a margin
    /// of two) touches, where a case lays stock and a stockpile the plan's own sites never meet; null when none fits.
    /// </summary>
    public static StageRect? FreeRect(IReadOnlyList<PlannedRoomGeometry> rooms, StageCell centre, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(rooms);
        bool Blocked(int x, int z) => rooms.Any(r => x >= r.X - 3 && x <= r.X + r.Width + 2 && z >= r.Z - 3 && z <= r.Z + r.Height + 2);

        for (var radius = 8; radius <= 40; radius++)
        {
            for (var dx = -radius; dx <= radius; dx++)
            {
                for (var dz = -radius; dz <= radius; dz++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != radius)
                    {
                        continue;
                    }

                    var x0 = centre.X + dx;
                    var z0 = centre.Z + dz;
                    if (x0 < 2 || z0 < 2 || x0 + width >= LabWorld.LabMapSize - 2 || z0 + height >= LabWorld.LabMapSize - 2)
                    {
                        continue;
                    }

                    var free = true;
                    for (var x = x0; x < x0 + width && free; x++)
                    {
                        for (var z = z0; z < z0 + height && free; z++)
                        {
                            free = !Blocked(x, z);
                        }
                    }

                    if (free)
                    {
                        return new StageRect(x0, z0, x0 + width - 1, z0 + height - 1);
                    }
                }
            }
        }

        return null;
    }
}

/// <summary>The persisted layout plan record: the review tick it was made at and the plan as JSON.</summary>
public sealed record LayoutPlanRecord(ulong Tick, JsonObject Plan);

/// <summary>
/// The durable-state reads a staging phase makes of the controller's journal (Go <c>*store.Store</c>: LoadRounds and
/// LayoutPlan), on top of the watch's.
/// </summary>
public interface IPhaseStore : IWatchStore, IDisposable
{
    /// <summary>The persisted Rounds record's revision and game tick; null when none was persisted yet.</summary>
    Task<(ulong Revision, ulong Tick)?> LoadRoundsAsync(CancellationToken cancellationToken);

    /// <summary>The layout plan recorded for the current review's world at <paramref name="reviewTick"/>; null when none.</summary>
    Task<LayoutPlanRecord?> LayoutPlanAsync(ulong reviewTick, CancellationToken cancellationToken);
}

/// <summary>What a staging phase needs from a running controller (Go <c>*na.ServiceProcess</c>: Restart, Spec.Families, StatePath).</summary>
public interface IPhaseService : IWatchedService
{
    /// <summary>Starts the service again on the same journal after a stop; <paramref name="families"/>, when set, replace the spec's for this launch.</summary>
    Task<IPhaseService> RestartAsync(IReadOnlyList<string>? families, CancellationToken cancellationToken);

    /// <summary>Opens the service's journal, retrying while the file is held.</summary>
    Task<IPhaseStore> OpenPhaseStoreAsync(CancellationToken cancellationToken);
}

/// <summary>The session members a staging phase uses (Go <c>cases.Session</c>).</summary>
public interface IPhaseSession : IObservedSession
{
    /// <summary>Starts the in-mod controller under <paramref name="spec"/> as a phase service.</summary>
    Task<IPhaseService> ServePhaseAsync(ServeSpec spec, CancellationToken cancellationToken);
}

/// <summary>One run of the controller over the staged game (Go <c>planstage.Phase</c>).</summary>
public sealed class StagePhase
{
    private readonly IPhaseSession _session;
    private readonly Wait _wait;

    private StagePhase(IPhaseSession session, IPhaseService service, IPhaseStore store)
    {
        _session = session;
        Service = service;
        Store = store;
        _wait = new Wait { Ceiling = PlanStage.Ceiling, Stall = Wait.StallBudget(), Terminal = service.Exited };
    }

    public IPhaseService Service { get; }

    public IPhaseStore Store { get; }

    /// <summary>
    /// Starts the controller (a fresh serve of the case's spec, or <paramref name="previous"/> restarted on the same
    /// journal) and takes authority. <paramref name="families"/>, when set, replace the spec's for this launch: an
    /// inert first run reads the plan without ordering any work.
    /// </summary>
    public static async Task<StagePhase> BeginAsync(IPhaseSession session, IPhaseService? previous, IReadOnlyList<string>? families, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        IPhaseService service;
        if (previous is null)
        {
            var spec = session.Spec;
            if (families is not null)
            {
                spec = spec with { Families = families };
            }

            service = await session.ServePhaseAsync(spec, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            service = await previous.RestartAsync(families, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await service.AcquireAsync(cancellationToken).ConfigureAwait(false);
            service.KeepAuthority(cancellationToken);
            var store = await service.OpenPhaseStoreAsync(cancellationToken).ConfigureAwait(false);
            return new StagePhase(session, service, store);
        }
        catch
        {
            await service.StopAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Polls the journal until <paramref name="done"/>; the signature is the review's game day and what done reports
    /// through detail, so a broken phase stalls and a long build does not.
    /// </summary>
    public async Task UntilAsync(string what, Func<(ulong Revision, ulong Tick), Task<(string Detail, bool Ok)>> done, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(done);
        try
        {
            await _wait.WaitProgressAsync(
                async ct =>
                {
                    if (await Store.LoadRoundsAsync(ct).ConfigureAwait(false) is not { } review)
                    {
                        return (Wait.Signature("no-review"), false);
                    }

                    var (detail, ok) = await done(review).ConfigureAwait(false);
                    return (Wait.Signature(detail, review.Tick / Wait.TicksPerDay), ok);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"{what}: {e.Message}", e);
        }
    }

    /// <summary>Waits for the recorded layout plan to satisfy <paramref name="want"/> and returns it (as JSON).</summary>
    public async Task<JsonObject> PlanAsync(string what, Func<JsonObject, bool> want, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(want);
        JsonObject? plan = null;
        await UntilAsync(
            what,
            async review =>
            {
                if (review.Revision == 0)
                {
                    // The controller has not reviewed yet: no world identity to scope the plan by.
                    return ("no-review", false);
                }

                if (await Store.LayoutPlanAsync(review.Tick, cancellationToken).ConfigureAwait(false) is not { } record)
                {
                    return ("no-plan", false);
                }

                if (!want(record.Plan))
                {
                    return (string.Create(CultureInfo.InvariantCulture, $"plan-{record.Tick}"), false);
                }

                plan = record.Plan;
                return ("plan", true);
            },
            cancellationToken).ConfigureAwait(false);
        return plan!;
    }

    /// <summary>Ends the phase and takes the harness back, paused.</summary>
    public async Task<Harness> StopAsync(string label, CancellationToken cancellationToken)
    {
        Store.Dispose();
        if (await Service.StopAsync().ConfigureAwait(false) is { } keep)
        {
            _session.Report[label] = keep;
        }

        var harness = await _session.ReattachAsync(cancellationToken).ConfigureAwait(false);
        await harness.CallAsync("pause-" + label, "rimgovernor/set_time_speed", new JsonObject { ["speed"] = "Paused", ["ultraSpeedBoost"] = false }, cancellationToken).ConfigureAwait(false);
        return harness;
    }

    /// <summary>Ends a failed phase without taking the harness back.</summary>
    public async Task AbortAsync()
    {
        Store.Dispose();
        await Service.StopAsync().ConfigureAwait(false);
    }
}
