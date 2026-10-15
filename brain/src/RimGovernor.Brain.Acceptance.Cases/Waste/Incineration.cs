using System.Globalization;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>Prepared disposal mechanics and explicit unsupported automatic labor; normal layout and zone policy remain exercised.</summary>
internal static class WasteIncineration
{
    private const string Stage = "test/disposal_stage";
    private const string Seed = "test/disposal_room";
    private const string Read = "test/disposal_read";






    internal static AcceptanceCase Case() => new()
    {
        Name = "waste/incineration",
        Scope = "LabStart normal layout and stockpile policy, with its incinerator shell and six burnable cells prepared. Normal review exposes unsupported ignition and admits no burner. Explicit native equip/draft/ignite and ash-cleaning operations prove actual disposal, nonflammable room containment, zone filters/priorities, ash removal and fresh-corpse retention. No automatic labor or construction claim.",
        Start = new FixtureStart { Op = Stage, On = new LabStart() },
        RequiredOps = [Stage, Seed, Read],
        Keep = [NeedDef.Food.Value],
        Serve = new ServeSpec
        {
            Families = ["incineration", "stockpiles", "flooring", "equip", "defense", "fire", "work", "supply"],
            NativeTimeout = TimeSpan.FromSeconds(30),
            Prefix = "waste-incineration",
        },
        Budget = TimeSpan.FromMinutes(10),
        Crew = new Crew { Size = 3 },
        Reason = "prepared disposal mechanics after explicit unsupported normal review",
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



        // Layout decisions remain normal policy; only the expensive construction precondition is prepared.
        var initial = await RoutineService.StartAsync(s, "disposal-layout", ["incineration", "stockpiles"], cancellationToken).ConfigureAwait(false);
        PlannedRoom room;
        PlannedRoom yard;
        try
        {
            await new Wait { Ceiling = TimeSpan.FromMinutes(2), Terminal = initial.Service.Exited }.WaitProgressAsync(async ct =>
            {
                var (_, layout) = await ObservedSession.ReadLayoutPlanAsync(s.Config, ct).ConfigureAwait(false);
                return (Wait.Signature(layout?.Rooms.Count), layout?.IncineratorRooms().Count == 1 && layout.WasteYardRooms().Count == 1);
            }, cancellationToken).ConfigureAwait(false);
            (room, yard) = await PlannedWasteYardAsync(s, cancellationToken).ConfigureAwait(false);
        }
        finally { _ = await initial.ReleaseAsync(s, cancellationToken).ConfigureAwait(false); }
        var h = s.Harness;
        var inner = room.Region.Bounds();
        var preparedRoom = await h.CallAsync("disposal-room", "test/disposal_room", new JsonObject {
            ["minX"] = inner.X, ["minZ"] = inner.Z, ["maxX"] = inner.X + inner.Width - 1, ["maxZ"] = inner.Z + inner.Height - 1,
            ["doorX"] = room.Door.X, ["doorZ"] = room.Door.Z,
            ["freshId"] = staged["freshStranger"],
            ["ids"] = string.Join(',', new[] { staged["animal"], staged["rottenStranger"], staged["worn"] }) }, cancellationToken).ConfigureAwait(false);
        var burnIds = preparedRoom["ids"].AsSlice().Select(x => x.AsString()).ToArray();
        if (preparedRoom["success"].AsBool() != true || burnIds.Length != 6) throw new AcceptanceException("Disposal room preparation failed.");
        report["prepared_room"] = preparedRoom.DeepClone();
        h = await UnsupportedLabor.ReviewAsync(s, "unsupported_forecast_incinerator_ignition", ActionKind.Ignite, cancellationToken).ConfigureAwait(false);
        await CheckDumpAsync(s, h, report, animalDef, yard.Region.Bounds(), inner, cancellationToken).ConfigureAwait(false);
        await CheckIncineratorZoneAsync(s, h, report, inner, cancellationToken).ConfigureAwait(false);
        var burner = preparedRoom["burner"].AsString();
        var weapon = preparedRoom["weapon"].AsString();
        var allIds = burnIds.Append(staged["freshStranger"]).ToArray();
        var equip = await ApplyIntents.ApplyOneAsync(h, "disposal-equip", s.Identity, "disposal-equip", ApplyIntents.GiveJob(burner, "Equip", weapon), cancellationToken).ConfigureAwait(false);
        _ = ApplyIntents.AppliedJob("equip", equip);
        _ = await Authority.ReleaseAuthorityAsync(h, s.Identity, cancellationToken).ConfigureAwait(false);
        _ = await RunAt.RunUntilAsync(h, "disposal-equipped", 2000, new Wait { Ceiling = TimeSpan.FromMinutes(1) }, async ct => {
            var read = await ReadDisposalAsync(h, allIds, room, ct).ConfigureAwait(false);
            return (Wait.Signature(read.Tick), read.Pawns.Any(p => p["id"].AsString() == burner && p["weapon"].AsString() == weapon));
        }, cancellationToken).ConfigureAwait(false);
        await NativeReads.PauseAsync(h, "disposal-draft-pause", cancellationToken).ConfigureAwait(false);
        _ = await ApplyIntents.ApplyDraftAsync(h, "disposal-draft", s.Identity, "disposal-draft", burner, true, cancellationToken).ConfigureAwait(false);
        var ignite = await ApplyIntents.ApplyOneAsync(h, "disposal-ignite", s.Identity, "disposal-ignite",
            new JsonObject { ["ignite"] = new JsonObject { ["pawnId"] = burner, ["cell"] = new JsonObject { ["x"] = inner.X + 1, ["z"] = inner.Z + 1 } } }, cancellationToken).ConfigureAwait(false);
        if (ignite["refused"] is not null) throw new AcceptanceException("Explicit ignition refused: " + ignite.ToJson());
        report["ignite_receipt"] = ignite.DeepClone();
        _ = await Authority.ReleaseAuthorityAsync(h, s.Identity, cancellationToken).ConfigureAwait(false);
        _ = await RunAt.RunUntilAsync(h, "disposal-fire", 2000, new Wait { Ceiling = TimeSpan.FromMinutes(1) }, async ct => {
            var read = await ReadDisposalAsync(h, allIds, room, ct).ConfigureAwait(false);
            return (Wait.Signature(read.Tick, read.Fires), read.Fires > 0);
        }, cancellationToken).ConfigureAwait(false);
        await NativeReads.PauseAsync(h, "disposal-undraft-pause", cancellationToken).ConfigureAwait(false);
        _ = await ApplyIntents.ApplyDraftAsync(h, "disposal-undraft", s.Identity, "disposal-undraft", burner, false, cancellationToken).ConfigureAwait(false);
        _ = await Authority.ReleaseAuthorityAsync(h, s.Identity, cancellationToken).ConfigureAwait(false);
        _ = await RunAt.RunUntilAsync(h, "disposal-burned", 15000, new Wait { Ceiling = TimeSpan.FromMinutes(3) }, async ct => {
            var read = await ReadDisposalAsync(h, allIds, room, ct).ConfigureAwait(false);
            return (Wait.Signature(read.Tick, read.Fires), read.Fires == 0 && burnIds.All(read.Gone));
        }, cancellationToken).ConfigureAwait(false);
        await NativeReads.PauseAsync(h, "disposal-clean-pause", cancellationToken).ConfigureAwait(false);
        var dirty = await ReadDisposalAsync(h, allIds, room, cancellationToken).ConfigureAwait(false);
        if (dirty.AshIds.Count == 0) throw new AcceptanceException("Actual burning produced no ash to exercise cleaning.");
        var cleanCount = 0;
        foreach (var target in dirty.AshIds)
        {
            var now = await ReadDisposalAsync(h, allIds, room, cancellationToken).ConfigureAwait(false);
            if (!now.AshIds.Contains(target)) continue;
            var clean = await ApplyIntents.ApplyOneAsync(h, "disposal-clean-" + cleanCount, s.Identity, "disposal-clean-" + cleanCount,
                ApplyIntents.GiveJob(burner, "Clean", target), cancellationToken).ConfigureAwait(false);
            _ = ApplyIntents.AppliedJob("clean", clean); cleanCount++;
            _ = await Authority.ReleaseAuthorityAsync(h, s.Identity, cancellationToken).ConfigureAwait(false);
            _ = await RunAt.RunUntilAsync(h, "disposal-cleaned-" + cleanCount, 2000, new Wait { Ceiling = TimeSpan.FromMinutes(1) }, async ct => {
                var read = await ReadDisposalAsync(h, allIds, room, ct).ConfigureAwait(false);
                return (Wait.Signature(read.Tick, read.Ash), !read.AshIds.Contains(target));
            }, cancellationToken).ConfigureAwait(false);
            await NativeReads.PauseAsync(h, "disposal-clean-next", cancellationToken).ConfigureAwait(false);
        }
        var final = await ReadDisposalAsync(h, allIds, room, cancellationToken).ConfigureAwait(false);
        report["disposal_read"] = final.ToJson(); report["explicit_cleans"] = cleanCount;
        if (!burnIds.All(final.Gone) || final.Ash != 0 || final.Fires != 0 || cleanCount == 0)
            throw new AcceptanceException("Disposal or physical ash removal incomplete.");
        if (!final.Things.TryGetValue(staged["freshStranger"], out var fresh) || fresh["spawned"].AsBool() != true || fresh["inside"].AsBool() == true)
            throw new AcceptanceException("Fresh corpse was not retained outside disposal.");
        CheckFreshStranger(final, staged["freshStranger"], report, "burned");
        CheckRoom(final, room);
    }
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
        internal long Tick { get; set; }
        internal List<string> AshIds { get; } = [];
        internal List<JsonObject> Pawns { get; } = [];
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
            Tick = (long)reply["tick"].AsNumber(),
            Ash = (int)reply["ash"].AsNumber(),
            Fires = (int)reply["fires"].AsNumber(),
            Beyond = (int)reply["beyondDoorRoom"].AsNumber(),
            Door = (int)reply["doorRoom"].AsNumber(),
            Room = reply["room"].AsMap() ?? [],
        };
        read.AshIds.AddRange(reply["ashIds"].AsSlice().Select(x => x.AsString()));
        read.Pawns.AddRange(reply["pawns"].Rows());
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

}
