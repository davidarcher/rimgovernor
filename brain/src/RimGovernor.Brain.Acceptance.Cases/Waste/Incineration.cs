using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// waste/incineration proves MaintainIncineration: corpse disposal end to end on the tribal baseline colony. The fixture
/// lays a rotten animal corpse, a rotten and a fresh stranger corpse and a worn apparel in the open, plus stone blocks
/// for the walls. MaintainStockpiles creates the waste yard's one dump zone from the plan (a declared Sanitation store)
/// and MaintainIncineration shells the planned incinerator in the waste yard (first window), whose declared Sanitation
/// zone takes burnable waste at Preferred priority; the case fills the incinerator with worn apparel and a loose molotov
/// and MaintainIncineration burns it and has the ash cleaned (second window) (Go <c>incineration.go</c>).
/// </summary>
internal static class WasteIncineration
{
    private const string Stage = "test/disposal_stage";
    private const string Seed = "test/disposal_seed";
    private const string Read = "test/disposal_read";

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(25);

    /// <summary>Seeded worn apparel in the interior: with the two rotten corpses the room reaches <c>BurnStoredCells</c>.</summary>
    private const int Seeded = 4;

    internal static AcceptanceCase Case() => new()
    {
        Name = "waste/incineration",
        Scope = "Issues #1817 and #2197 on the tribal " + Baselines.BaselineName + " colony: rotten animal and rotten stranger corpses are burned in the incinerator " +
            "and a fresh stranger is never put there; the waste yard holds one Low dump zone over the yard interior outside the incinerator outline, " +
            "allowing corpses and apparel (the not-burnable special is refused) and unroofed; a full incinerator (6+ cells) is burned once by a molotov-equipped burner with a standby and its ash cleaned; " +
            "the enclosed unroofed incinerator interior is its own room (cell.GetRoom) walled and doored with non-flammable edifices, " +
            "and its stockpile zone is the declared Preferred store over the whole interior. " +
            "A native end-to-end signal (vanilla fire, rot, rooms and hauling); no Go snapshot covers it.",
        Start = new FixtureStart { Op = Stage, On = new SaveStart { Name = Baselines.BaselineName } },
        RequiredOps = [Stage, Seed, Read],
        Keep = [NeedDef.Food.Value],
        Serve = new ServeSpec
        {
            Families = ["incineration", "stockpiles", "flooring", "equip", "defense", "fire", "work", "supply"],
            NativeTimeout = TimeSpan.FromSeconds(30),
            Prefix = "waste-incineration",
        },
        Budget = (2 * Window) + TimeSpan.FromMinutes(10),
        Crew = new Crew { Size = 3 },
        Reason = "two serve windows: the incinerator is shelled by colonists, then burned and cleaned",
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var prepared = s.Prepared ?? [];
        report["fixture"] = prepared.DeepClone();
        var staged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in new[] { "animal", "rottenStranger", "freshStranger", "worn" })
        {
            var id = prepared[key].AsString();
            if (id.Length == 0)
            {
                throw new AcceptanceException($"prepare: no {key} id: {prepared.ToJson()}");
            }

            staged[key] = id;
        }

        var animalDef = prepared["animalDef"].AsString();
        var ids = new List<string> { staged["animal"], staged["rottenStranger"], staged["freshStranger"], staged["worn"] };
        var observed = ObservedSession.Of(s);
        var concern = PolicyConstants.MaintainIncineration.Value;

        // Window one: the layout plan reserves the incinerator and MaintainIncineration shells it.
        PlannedRoom? room = null;
        PlannedRoom? yard = null;
        try
        {
            _ = await SustainedFood.ObserveAsync(
                observed,
                new SustainedObservation
                {
                    Watch = new WatchConfig { Watch = Window, Concern = concern, Until = sample => MethodCompleted(sample, "incinerator-shell-") },
                    Audit = async (h, rep, ct) =>
                    {
                        (room, yard) = await PlannedWasteYardAsync(s, ct).ConfigureAwait(false);
                        rep["incinerator"] = room.ToString();
                        await CheckDumpAsync(s, h, rep, animalDef, yard.Region.Bounds(), room.Region.Bounds(), ct).ConfigureAwait(false);
                        var read = await ReadDisposalAsync(h, ids, room, ct).ConfigureAwait(false);
                        await CheckIncineratorZoneAsync(s, h, rep, room.Region.Bounds(), ct).ConfigureAwait(false);
                        CheckFreshStranger(read, staged["freshStranger"], rep, "shelled");
                    },
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException("incinerator window: " + e.Message, e);
        }

        // Window two: a full room with a molotov on the ground and a standby.
        var seeded = new List<string>();
        try
        {
            _ = await SustainedFood.ObserveAsync(
                observed,
                new SustainedObservation
                {
                    Watch = new WatchConfig { Watch = Window, Concern = concern, Until = sample => MethodCompleted(sample, "burn-ash-") },
                    Prepare = async (h, rep, ct) =>
                    {
                        var cells = PolicyFunctions.RectangleCells(room!.Region.Bounds());
                        if (cells.Count < Seeded)
                        {
                            throw new AcceptanceException($"incinerator interior {room.Region.Bounds()} holds fewer than {Seeded} cells");
                        }

                        var spots = cells.Take(Seeded).Select(c => string.Create(CultureInfo.InvariantCulture, $"{c.X},{c.Z}"));
                        var reply = await h.CallAsync("disposal-seed", Seed, new JsonObject { ["cells"] = string.Join(';', spots), ["molotovs"] = 1 }, ct).ConfigureAwait(false);
                        foreach (var raw in reply["ids"].AsSlice())
                        {
                            seeded.Add(raw.AsString());
                        }

                        if (seeded.Count != Seeded + 1)
                        {
                            throw new AcceptanceException($"disposal seed: want {Seeded + 1} things, got {reply.ToJson()}");
                        }

                        rep["seeded"] = new JsonArray([.. seeded.Select(id => (JsonNode?)id)]);
                    },
                    Audit = async (h, rep, ct) =>
                    {
                        var read = await ReadDisposalAsync(h, [.. ids, .. seeded.Take(Seeded)], room!, ct).ConfigureAwait(false);
                        rep["disposal_read"] = read.ToJson();
                        await CheckBurnedAsync(s, read, staged, [.. seeded.Take(Seeded)], room!, rep, ct).ConfigureAwait(false);
                    },
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException("burn window: " + e.Message, e);
        }
    }

    /// <summary>Reports a plan of the sampled goal, active or retired, whose method has the prefix and whose actions all completed.</summary>
    private static bool MethodCompleted(JsonObject sample, string prefix)
    {
        foreach (var plan in Plans(sample))
        {
            var method = plan["method"].AsString();
            var actions = (int)plan["actions"].AsNumber();
            var completed = (int)plan["stages"].AsMap()?["completed"].AsNumber()!;
            if (method.StartsWith(prefix, StringComparison.Ordinal) && actions > 0 && completed == actions)
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<JsonObject> Plans(JsonObject sample) =>
        sample["plans"].AsSlice().Concat(sample["retired_plans"].AsSlice()).Select(p => p.AsMap() ?? []);

    /// <summary>The layout plan's incinerator room and the waste yard that holds it.</summary>
    private static async Task<(PlannedRoom Room, PlannedRoom Yard)> PlannedWasteYardAsync(ISession s, CancellationToken cancellationToken)
    {
        var (tick, plan) = await ObservedSession.ReadLayoutPlanAsync(s.Config, cancellationToken).ConfigureAwait(false);
        if (plan is null)
        {
            throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"no layout plan recorded by tick {tick}"));
        }

        var rooms = plan.IncineratorRooms();
        var yards = plan.WasteYardRooms();
        if (rooms.Count != 1 || yards.Count != 1)
        {
            throw new AcceptanceException($"the layout plan holds {rooms.Count} incinerators and {yards.Count} waste yards, want one each");
        }

        return (rooms[0], yards[0]);
    }

    /// <summary>
    /// Proves the one dump zone: a Low stockpile over exactly the yard interior outside the incinerator's 5x5 outline,
    /// unroofed, allowing humanlike and animal corpses and apparel (everything but the not-burnable special), and the
    /// only Low zone outside the incinerator.
    /// </summary>
    private static async Task CheckDumpAsync(ISession s, Harness h, Report report, string animalDef, Rectangle yard, Rectangle incinerator, CancellationToken cancellationToken)
    {
        const string humanCorpse = "Corpse_Human";
        const string pants = "Apparel_Pants";
        var reply = await h.WireAsync(
            "dump-zones",
            "observations_list_zones",
            new JsonObject
            {
                ["scope"] = new JsonObject { ["expectedIdentity"] = s.Identity.DeepClone() },
                ["includeFilter"] = true,
            },
            cancellationToken).ConfigureAwait(false);
        var (_, observed) = Harness.Outcome(reply, "observed");
        var grid = await h.MapCellsAsync("dump-cells", s.Identity, cancellationToken).ConfigureAwait(false);
        var roofed = new Dictionary<Cell, bool>();
        foreach (var c in grid.Cells())
        {
            if (c.Roofed is { } r)
            {
                roofed[c.Cell] = r;
            }
        }

        var zoneCells = grid.ZoneCells();
        var outline = PolicyFunctions.RectangleCells(new Rectangle(incinerator.X - 1, incinerator.Z - 1, incinerator.Width + 2, incinerator.Height + 2)).ToHashSet();
        var want = PolicyFunctions.RectangleCells(yard).Where(c => !outline.Contains(c)).ToHashSet();
        var inside = PolicyFunctions.RectangleCells(incinerator).ToHashSet();
        var dumps = new List<string>();
        foreach (var raw in observed["zones"].AsSlice())
        {
            var row = raw.AsMap() ?? [];
            if (!string.Equals(row["type"].AsString(), "stockpile", StringComparison.OrdinalIgnoreCase)
                || !row["priority"].AsString().Contains("low", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var id = row["id"].AsString();
            var cells = zoneCells.GetValueOrDefault(id) ?? [];
            if (cells.Count == 0 || inside.Contains(cells[0]))
            {
                continue;
            }

            var allowed = row["filter"].AsMap()?["allowedDefNames"].AsSlice().Select(d => d.AsString()).ToHashSet(StringComparer.Ordinal) ?? [];
            dumps.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{row["id"].ToJson()} cells={cells.Count} human={Bool(allowed.Contains(humanCorpse))} animal={Bool(allowed.Contains(animalDef))} apparel={Bool(allowed.Contains(pants))}"));
            if (cells.Count != want.Count)
            {
                throw new AcceptanceException($"dump zone {id} holds {cells.Count} cells, want the {want.Count} of the yard outside the incinerator");
            }

            foreach (var c in cells)
            {
                if (!want.Contains(c))
                {
                    throw new AcceptanceException($"dump zone {id} has cell {c} outside the yard interior or inside the incinerator outline");
                }

                if (roofed.GetValueOrDefault(c))
                {
                    throw new AcceptanceException($"dump zone {id} has a roofed cell {c}");
                }
            }

            if (!allowed.Contains(humanCorpse) || !allowed.Contains(animalDef) || !allowed.Contains(pants))
            {
                throw new AcceptanceException($"dump zone {id} refuses corpses or apparel: [{string.Join(' ', dumps)}]");
            }
        }

        report["dump_zones"] = new JsonArray([.. dumps.Select(d => (JsonNode?)d)]);
        if (dumps.Count != 1)
        {
            throw new AcceptanceException($"want exactly one Low dump zone, got {dumps.Count}: [{string.Join(' ', dumps)}]");
        }
    }

    private static string Bool(bool value) => value ? "true" : "false";

    /// <summary>
    /// Proves the declared Sanitation store: a stockpile zone over the whole incinerator interior at Preferred priority,
    /// above the Low dump so waste hauls in from it.
    /// </summary>
    private static async Task CheckIncineratorZoneAsync(ISession s, Harness h, Report report, Rectangle interior, CancellationToken cancellationToken)
    {
        var reply = await h.WireAsync(
            "incinerator-zone",
            "observations_list_zones",
            new JsonObject { ["scope"] = new JsonObject { ["expectedIdentity"] = s.Identity.DeepClone() } },
            cancellationToken).ConfigureAwait(false);
        var (_, observed) = Harness.Outcome(reply, "observed");
        var grid = await h.MapCellsAsync("incinerator-zone-cells", s.Identity, cancellationToken).ConfigureAwait(false);
        var zoneCells = grid.ZoneCells();
        var want = PolicyFunctions.RectangleCells(interior).ToHashSet();
        foreach (var raw in observed["zones"].AsSlice())
        {
            var row = raw.AsMap() ?? [];
            if (!string.Equals(row["type"].AsString(), "stockpile", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var cells = zoneCells.GetValueOrDefault(row["id"].AsString()) ?? [];
            if (cells.Count != want.Count || !cells.All(want.Contains))
            {
                continue;
            }

            report["incinerator_zone"] = row.DeepClone();
            if (!row["priority"].AsString().Contains("preferred", StringComparison.OrdinalIgnoreCase))
            {
                throw new AcceptanceException($"incinerator zone {row["id"].ToJson()} is not Preferred: {row["priority"].ToJson()}");
            }

            return;
        }

        throw new AcceptanceException($"no stockpile zone covers exactly the incinerator interior {interior}");
    }

    /// <summary>The native read of the staged things and the incinerator.</summary>
    private sealed class DisposalReading
    {
        internal Dictionary<string, JsonObject> Things { get; } = new(StringComparer.Ordinal);

        internal List<JsonObject> Interior { get; } = [];

        internal List<JsonObject> Ring { get; } = [];

        internal JsonObject Room { get; set; } = [];

        internal int Ash { get; set; }

        internal int Fires { get; set; }

        internal int Beyond { get; set; }

        internal int Door { get; set; }

        /// <summary>A thing that is neither standing on the map nor carried: burned.</summary>
        internal bool Gone(string id)
        {
            if (!Things.TryGetValue(id, out var row))
            {
                return false;
            }

            return row["spawned"].AsBool() != true && row["carried"].AsBool() != true;
        }

        internal JsonObject ToJson() => new()
        {
            ["things"] = new JsonObject(Things.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value.DeepClone()))),
            ["interior"] = new JsonArray([.. Interior.Select(r => r.DeepClone())]),
            ["ring"] = new JsonArray([.. Ring.Select(r => r.DeepClone())]),
            ["room"] = Room.DeepClone(),
            ["ash"] = Ash,
            ["fires"] = Fires,
            ["beyond"] = Beyond,
            ["door"] = Door,
        };
    }

    private static async Task<DisposalReading> ReadDisposalAsync(Harness h, IReadOnlyList<string> ids, PlannedRoom room, CancellationToken cancellationToken)
    {
        var inner = room.Region.Bounds();
        var reply = await h.CallAsync(
            "disposal-read",
            Read,
            new JsonObject
            {
                ["ids"] = string.Join(',', ids),
                ["minX"] = inner.X,
                ["minZ"] = inner.Z,
                ["maxX"] = inner.X + inner.Width - 1,
                ["maxZ"] = inner.Z + inner.Height - 1,
                ["doorX"] = room.Door.X,
                ["doorZ"] = room.Door.Z,
            },
            cancellationToken).ConfigureAwait(false);
        if (reply["success"].AsBool() != true)
        {
            throw new AcceptanceException($"{Read} refused: {reply.ToJson()}");
        }

        var read = new DisposalReading
        {
            Ash = (int)reply["ash"].AsNumber(),
            Fires = (int)reply["fires"].AsNumber(),
            Beyond = (int)reply["beyondDoorRoom"].AsNumber(),
            Door = (int)reply["doorRoom"].AsNumber(),
            Room = reply["room"].AsMap() ?? [],
        };
        foreach (var raw in reply["things"].AsSlice())
        {
            var row = raw.AsMap() ?? [];
            read.Things[row["id"].AsString()] = row;
        }

        foreach (var raw in reply["interior"].AsSlice())
        {
            read.Interior.Add(raw.AsMap() ?? []);
        }

        foreach (var raw in reply["ring"].AsSlice())
        {
            read.Ring.Add(raw.AsMap() ?? []);
        }

        return read;
    }

    /// <summary>Proves a stranger corpse that is still fresh is not in the incinerator: it waits for the butcher or the morgue.</summary>
    private static void CheckFreshStranger(DisposalReading read, string id, Report report, string when)
    {
        var row = read.Things.GetValueOrDefault(id);
        report["fresh_stranger_" + when] = row?.DeepClone();
        if (row?["inside"].AsBool() == true && row["rot"].AsString() == "Fresh")
        {
            throw new AcceptanceException($"the fresh stranger corpse {id} lies in the incinerator: {row.ToJson()}");
        }
    }

    /// <summary>
    /// Proves the burn window: the rotten corpses and the seeded apparel burned, the journal's burn was one ignite by a
    /// drafted burner, the ash is cleaned, and the room is its own walled, doored, unroofed room.
    /// </summary>
    private static async Task CheckBurnedAsync(ISession s, DisposalReading read, Dictionary<string, string> staged, IReadOnlyList<string> seeded, PlannedRoom room, Report report, CancellationToken cancellationToken)
    {
        foreach (var id in new[] { staged["animal"], staged["rottenStranger"] }.Concat(seeded))
        {
            if (!read.Gone(id))
            {
                throw new AcceptanceException($"thing {id} survived the burn: {read.Things.GetValueOrDefault(id).ToJson()}");
            }
        }

        CheckFreshStranger(read, staged["freshStranger"], report, "burned");
        if (read.Ash != 0 || read.Fires != 0)
        {
            throw new AcceptanceException($"the burn left {read.Ash} ash and {read.Fires} fires");
        }

        var burn = await BurnPlansAsync(s, cancellationToken).ConfigureAwait(false);
        report["burn_plans"] = new JsonObject { ["ignites"] = burn.Ignites, ["drafts"] = burn.Drafts, ["cleans"] = burn.Cleans, ["equips"] = burn.Equips };
        if (burn.Ignites != 1 || burn.Drafts < 1)
        {
            throw new AcceptanceException($"want one completed ignite behind a draft, got {burn}");
        }

        if (burn.Cleans < 1)
        {
            throw new AcceptanceException($"no completed ash cleaning: {burn}");
        }

        CheckRoom(read, room);
    }

    /// <summary>
    /// Proves the incinerator interior resolves to one unroofed room of its own (cell.GetRoom) and every wall and the
    /// door are non-flammable.
    /// </summary>
    private static void CheckRoom(DisposalReading read, PlannedRoom room)
    {
        var id = (int)read.Room["id"].AsNumber();
        if (id < 0 || (int)read.Room["cellCount"].AsNumber() != read.Interior.Count)
        {
            throw new AcceptanceException($"the incinerator room is not its 3x3 interior: {read.Room.ToJson()} over {read.Interior.Count} cells");
        }

        foreach (var cell in read.Interior)
        {
            if ((int)cell["room"].AsNumber() != id)
            {
                throw new AcceptanceException($"interior cell {cell.ToJson()} is in room {cell["room"].ToJson()}, not {id}");
            }

            if (cell["roofed"].AsBool() == true)
            {
                throw new AcceptanceException($"interior cell {cell.ToJson()} is roofed");
            }
        }

        if (read.Beyond == id || read.Door == id)
        {
            throw new AcceptanceException($"the room {id} continues past the door: door {read.Door}, beyond {read.Beyond}");
        }

        var inner = room.Region.Bounds();
        var doors = 0;
        foreach (var e in read.Ring)
        {
            var x = (int)e["x"].AsNumber();
            var z = (int)e["z"].AsNumber();
            var corner = (x < inner.X || x >= inner.X + inner.Width) && (z < inner.Z || z >= inner.Z + inner.Height);
            var def = e["def"].AsString();
            if (def.Length == 0)
            {
                if (!corner)
                {
                    throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"ring cell {x},{z} has no wall or door"));
                }

                continue;
            }

            var flammability = e["flammability"].AsNumber();
            if (flammability > 0)
            {
                throw new AcceptanceException(string.Create(CultureInfo.InvariantCulture, $"ring {def} at {x},{z} ({e["stuff"].ToJson()}) is flammable: {flammability}"));
            }

            if (e["door"].AsBool() == true)
            {
                doors++;
                if (new Cell(x, z) != room.Door)
                {
                    throw new AcceptanceException($"door at {x},{z}, planned at {room.Door}");
                }
            }
        }

        if (doors != 1)
        {
            throw new AcceptanceException($"the ring holds {doors} doors, want one");
        }
    }

    /// <summary>The completed action kinds of the burn and ash plans.</summary>
    private readonly record struct BurnCounts(int Ignites, int Drafts, int Cleans, int Equips);

    /// <summary>Counts the completed actions of the journal's MaintainIncineration burn-wave and burn-ash plans by kind.</summary>
    private static async Task<BurnCounts> BurnPlansAsync(ISession s, CancellationToken cancellationToken)
    {
        var store = await ObservedSession.OpenStoreAsync(s.Config, cancellationToken).ConfigureAwait(false);
        try
        {
            var goal = await store.SampleStandardAsync(PolicyConstants.MaintainIncineration.Value, cancellationToken).ConfigureAwait(false);
            int ignites = 0, drafts = 0, cleans = 0, equips = 0;
            foreach (var plan in Plans(goal))
            {
                var method = plan["method"].AsString();
                var kinds = plan["kinds"].AsMap() ?? [];
                var actions = (int)plan["actions"].AsNumber();
                var completed = (int)(plan["stages"].AsMap()?["completed"]).AsNumber();
                if (!method.StartsWith("burn-", StringComparison.Ordinal) || actions == 0 || completed != actions)
                {
                    continue;
                }

                ignites += Count(kinds, Wire.Name(ActionKind.Ignite));
                drafts += Count(kinds, Wire.Name(ActionKind.OwnedDraft));
                cleans += Count(kinds, Wire.Name(ActionKind.Clean));
                equips += Count(kinds, Wire.Name(ActionKind.Equip));
            }

            return new BurnCounts(ignites, drafts, cleans, equips);
        }
        finally
        {
            if (store is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    private static int Count(JsonObject kinds, string kind) => kinds.ContainsKey(kind) ? (int)kinds[kind].AsNumber() : 0;
}
