using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Simulation.CombatMetrics;
using RimGovernor.Brain.Store;
using RimGovernor.Brain.Store.Codec;
using RimGovernor.Brain.Telemetry.Otlp;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The <c>combatlab/metrics-*</c> cases (Go <c>metrics_case.go</c>).</summary>
internal static class CombatLabMetrics
{
    /// <summary>A fixture run's game-tick budget.</summary>
    internal const int Ticks = 10000;

    // How far short of the budget the service is stopped: the state refresh lag plus the reattach.
    private const int StopMargin = 2500;

    // How long a still served clock means the run is over. A cold service spends its first seconds in native calls that
    // the clock waits behind (#2901); 90 s is about four times the quiet-box first round (#2843).
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(90);

    // lab-open's metrics are the smoke scaffold for the clock, orders and recorder (#854), not a tactics benchmark.
    private const string SmokeOnly = " Smoke only: lab-open is an unstructured 3v3 whose outcome is not a tactics signal.";

    // The declared stage whose bundle a later run opens on, and the checkpoint state key of the staged record.
    private const string StageName = "staged";
    private const string StagedKey = "combatlab_staged";

    internal static IEnumerable<AcceptanceCase> Cases()
    {
        foreach (var name in CombatFixtures.Names)
        {
            var probe = CombatFixtures.Build(name, 0, 0);
            var scope = string.Create(
                CultureInfo.InvariantCulture,
                $"Combat metrics (#855, #869): stage {name}, serve the routine defense planner on it for up to {Ticks} game ticks, and record the reads before and after to {CombatLabBundle.ReadsFile} and the aggregate (damage, downs, deaths, fled, contact-to-resolution, friendly fire, orders, step latency) to {CombatLabBundle.MetricsFile}, with the shot tap (launches, impacts, jobs, stances, think ticks) to {CombatLabShots.ShotsFile}. Evidence, not a gate: native because the metrics are vanilla combat outcomes under the live planner.");
            if (name == "lab-open")
            {
                scope += SmokeOnly;
            }

            var fixture = name;
            yield return new AcceptanceCase
            {
                Name = "combatlab/metrics-" + name,
                Scope = scope,
                Start = new LabStart { Colonists = probe.Colonists },
                RequiredOps = [DebugGame.LabStartTool, CombatFixtures.StageTool, CombatServe.PauseModeTool],
                QuietWorld = false,
                // A ring capture pauses the served game mid-fight: the external pause revokes authority and drops the
                // fight plan (lab-open at t+2m). The stage bundle is taken right after staging, before the planner is
                // served (#2837), so a run loads it instead of booting and staging.
                NoCheckpoint = true,
                Stages = [StageName],
                Serve = new ServeSpec { Families = CombatServe.MetricsFamilies, Prefix = "combatlab" },
                Budget = AcceptanceCase.LabBudget,
                Crew = new Crew { Size = 3 },
                Run = (s, ct) => RunAsync(s, fixture, ct),
            };
        }
    }

    // What the code after the stage reads of the staging: the pawn sides, the lab centre the fixture was built around
    // (its layout cells are rebuilt from it) and the digest and seed the staging read back.
    private sealed class StagedRecord
    {
        internal Dictionary<string, string> Sides { get; set; } = [];

        internal LabCell Center { get; set; }

        internal string Digest { get; set; } = "";

        internal int Seed { get; set; }

        internal JsonObject ToJson()
        {
            var sides = new JsonObject();
            foreach (var (id, side) in Sides.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                sides[id] = side;
            }

            return new JsonObject
            {
                [nameof(Sides)] = sides,
                [nameof(Center)] = new JsonObject { ["X"] = Center.X, ["Z"] = Center.Z },
                [nameof(Digest)] = Digest,
                [nameof(Seed)] = Seed,
            };
        }

        internal static StagedRecord FromJson(JsonObject o)
        {
            var sides = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (id, side) in o[nameof(Sides)].AsMap() ?? [])
            {
                sides[id] = side.AsString();
            }

            var center = o[nameof(Center)].AsMap() ?? [];
            return new StagedRecord
            {
                Sides = sides,
                Center = new LabCell((int)center["X"].AsNumberOrZero(), (int)center["Z"].AsNumberOrZero()),
                Digest = o[nameof(Digest)].AsString(),
                Seed = (int)o[nameof(Seed)].AsNumberOrZero(),
            };
        }
    }

    // Stages a fixture, serves the planner on it until the tick budget is nearly spent, reads it back and writes the
    // reads, the service log and the metrics to the bundle.
    private static async Task RunAsync(ISession s, string name, CancellationToken cancellationToken)
    {
        var dir = s.Config.Output;
        var report = s.Report;

        // The staging is the stage: the save is captured right after it, before the planner is served, so a later run
        // loads it instead of booting and staging again. What the code after the stage needs travels in the stage's
        // checkpoint state.
        var record = new StagedRecord();
        await s.StageAsync(
            StageName,
            async ct =>
            {
                var probe = CombatFixtures.Build(name, 0, 0);
                var staged = probe.Arrival.Length > 0
                    ? await StagePodsAsync(s, name, ct).ConfigureAwait(false)
                    : await CombatStage.StageAsync(s.Harness, name, ct).ConfigureAwait(false);
                record = new StagedRecord { Sides = CombatLabBundle.StagedSides(staged), Center = staged.Center, Digest = staged.Digest, Seed = staged.Seed };
                Checkpoints.SetState(StagedKey, record.ToJson());
            },
            cancellationToken).ConfigureAwait(false);
        var resumed = false;
        if (NativeReads.RestoredState(s, StagedKey).AsMap() is { } restored)
        {
            record = StagedRecord.FromJson(restored);
            resumed = true;
        }

        if (record.Sides.Count == 0)
        {
            throw new AcceptanceException($"{name}: no staged record after the stage");
        }

        report["stage_resumed"] = resumed;
        report["staged_digest"] = record.Digest;
        report["staged_seed"] = record.Seed;
        var first = await CombatServe.RawReadAsync(s.Harness, new JsonObject { ["action"] = "read" }, cancellationToken).ConfigureAwait(false);
        var sides = new JsonObject();
        foreach (var (id, side) in record.Sides.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            sides[id] = side;
        }

        first["staged"] = sides;
        var start = (int)first["tick"].AsNumberOrZero();
        var fixture = CombatFixtures.Build(name, record.Center.X, record.Center.Z);
        if (fixture.Layout is { } layout)
        {
            report["layout"] = await StoreLayoutAsync(s.Harness, s.Identity, layout, cancellationToken).ConfigureAwait(false);
        }

        // A MajorThreat letter mid-fight (a berserk colonist) pauses the game as the player would; the authority flip
        // then drops the fight plan (lab-open at tick 1057). The metrics measure the fight.
        await CombatServe.NeverPauseAsync(s.Harness, cancellationToken).ConfigureAwait(false);

        // A run seed reseeds the RNG on the paused, loaded checkpoint, right before the planner is served: nothing
        // draws between (#2849).
        if (s.RunSeed != 0)
        {
            var draws = await CombatStage.ReseedAsync(s.Harness, s.RunSeed, cancellationToken).ConfigureAwait(false);
            report["run_seed"] = s.RunSeed;
            report["run_seed_draws"] = new JsonArray([.. draws.Select(d => (JsonNode?)JsonValue.Create(d))]);
        }

        var logPath = ServiceLogs.ServiceLogsPath(dir);
        var clock = new ServedClock();
        var service = await s.ServeAsync(s.Spec, cancellationToken).ConfigureAwait(false);
        try
        {
            _ = await service.AcquireAsync(cancellationToken).ConfigureAwait(false);
            service.KeepAuthority(cancellationToken);
            await ServeUntilAsync(service, logPath, start + Ticks - StopMargin, clock, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = await service.StopAsync().ConfigureAwait(false);
            report["servedTick"] = clock.Tick;
        }

        var h = s.Reattach();
        await CombatServe.PauseAfterAsync(h, cancellationToken).ConfigureAwait(false);
        var last = await CombatServe.RawReadAsync(h, new JsonObject { ["action"] = "read" }, cancellationToken).ConfigureAwait(false);
        await WriteReadsAsync(dir, [first, last], cancellationToken).ConfigureAwait(false);
        var shots = await CombatServe.RawReadAsync(h, new JsonObject { ["action"] = "shots" }, cancellationToken).ConfigureAwait(false);
        await CombatLabShots.WriteShotsAsync(dir, shots, cancellationToken).ConfigureAwait(false);
        await WriteCombatRowsAsync(logPath, Path.Combine(dir, CombatLabBundle.CombatRowsFile), cancellationToken).ConfigureAwait(false);
        var m = await CombatLabBundle.AggregateBundleAsync(dir, name, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(dir, CombatLabBundle.MetricsFile), CombatLabBundle.MetricsText(m), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        report["metrics"] = CombatLabBundle.MetricsJson(m);
        var response = ResponseEvidence.Create(name, CombatServe.MetricsSpeed, start);
        response.ReadLogs(logPath);
        foreach (var hit in last["damage"].AsSlice())
        {
            var row = hit.AsMap() ?? [];
            if (row["instigatorSide"].AsString() == CombatFixtures.Colonist && row["victimSide"].AsString() == CombatFixtures.Hostile && row["dealt"].AsNumberOrZero() > 0)
            {
                response.ObserveEffect((long)row["tick"].AsNumberOrZero(), "colonist_damaged_hostile");
            }
        }

        report["response"] = response.ToJson();
        if (m.Ticks > Ticks)
        {
            throw new AcceptanceException($"{name} ran {CaseSupport.Tag(m.Ticks)} ticks from {CaseSupport.Tag(start)}, over the {CaseSupport.Tag(Ticks)}-tick budget");
        }
    }

    // Stages a drop-pod fixture and checks its arrival row is in the frame. The native records combat events once a
    // frame capture has hooked them (a served game's stream is open long before a raid), so one frame is captured before
    // staging. The served run starts with the raiders still in their pods: the pending arrival is the threat the fight
    // forms on.
    private static async Task<StagedFixture> StagePodsAsync(ISession s, string name, CancellationToken cancellationToken)
    {
        return await StagePodsWindowsAsync(s, name, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<StagedFixture> StagePodsWindowsAsync(ISession s, string name, CancellationToken cancellationToken)
    {
        var frames = await CombatFrames.OpenAsync(s.Harness, s.Identity, cancellationToken).ConfigureAwait(false);
        _ = await frames.NextAsync(0, cancellationToken).ConfigureAwait(false);
        var staged = await CombatStage.StageAsync(s.Harness, name, cancellationToken).ConfigureAwait(false);
        var (_, tick) = await CombatStage.TickAsync(s.Harness, 1, cancellationToken).ConfigureAwait(false);
        var state = await frames.NextAsync(tick, cancellationToken).ConfigureAwait(false);
        foreach (var row in state.Events)
        {
            if (Geometry.DropPodArrival(row) && row["openTick"].AsNumberOrZero() > tick)
            {
                return staged;
            }
        }

        throw new AcceptanceException($"{name}: no pending drop-pod arrival row in {CaseSupport.Tag(state.Events.Count)} frame events");
    }

    // The last tick the served window saw, kept even when the wait fails.
    private sealed class ServedClock
    {
        internal int Tick { get; set; } = -1;
    }

    // Polls the service's state until the game tick reaches `until`, until the service records the end of the combat (a
    // combat_summary row at the log; the colony window after it would otherwise run the quiet lab to the budget), or
    // until the served clock has sat still for Idle.
    private static async Task ServeUntilAsync(ServiceProcess service, string logPath, int until, ServedClock clock, CancellationToken cancellationToken)
    {
        var moved = DateTimeOffset.UtcNow;
        var tail = LogTail.FromStart(logPath);
        while (true)
        {
            if (service.Exited() is { } exited)
            {
                throw new AcceptanceException($"service exited at tick {CaseSupport.Tag(clock.Tick)}: {exited.Message}", exited);
            }

            var n = await CombatServe.ServedTickAsync(service, clock.Tick, cancellationToken).ConfigureAwait(false);
            if (n != clock.Tick)
            {
                clock.Tick = n;
                moved = DateTimeOffset.UtcNow;
            }

            var ended = tail.Next().Any(row => row.Kind == "combat_summary");
            if (ended || clock.Tick >= until || (clock.Tick >= 0 && DateTimeOffset.UtcNow - moved > Idle))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
        }
    }

    // Copies the combat rows ScanLogs reads from the rotated segments (oldest first) and the live file of the service's
    // OTLP log ring into the bundle, one OTLP line each (the bundle is itself a log ring that ScanLogs reads back).
    private static async Task WriteCombatRowsAsync(string from, string to, CancellationToken cancellationToken)
    {
        var kept = new List<byte>();
        OtlpRing.ReadLines(
            from,
            line =>
            {
                var span = line.Span;
                if (LogTail.ParseLogRow(span) is { } row && CombatLabBundle.CombatRow(row))
                {
                    kept.AddRange(span.ToArray());
                    kept.Add(0x0a);
                }
            });
        if (kept.Count == 0)
        {
            return;
        }

        await File.WriteAllBytesAsync(to, [.. kept], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Puts the fixture's layout, as a complete, verified defense layout record for the staged world, into the save's
    /// governor state: the service rebuilds its family tables from the save on the world change, so combat reads it as
    /// the colony's standing layout (rounds_defense.go, LoadDefenseLayout). A row written into the journal before the
    /// serve was wiped by that rebuild.
    /// </summary>
    internal static async Task<JsonNode?> StoreLayoutAsync(Harness h, JsonObject identity, LabLayout l, CancellationToken cancellationToken)
    {
        var world = new World(
            new ColonyId(identity["colonyId"].AsString()),
            new LoadId(identity["loadToken"].AsString()),
            new MapId((int)identity["mapId"].AsNumberOrZero()));
        var record = new DefenseLayoutRecord
        {
            World = world,
            Standard = new StandardId("combatlab-layout"),
            Firing = [.. l.Firing.Select(c => new Cell(c.X, c.Z))],
            Retreat = [.. l.Retreat.Select(c => new Cell(c.X, c.Z))],

            // Validate wants a tier; a fixture's structures are staged, not built by the planner, so its one tier names
            // no buildings.
            Tiers = [new DefenseTierRecord { Name = PolicyFunctions.TierFiringLine, Built = true }],
            Complete = true,
        };
        if (l.Choke is { } choke)
        {
            // Combat's choke is the trap lane's last cell.
            record.TrapLane = [new Cell(choke.X, choke.Z)];
        }

        try
        {
            record.Validate();
        }
        catch (DomainException e)
        {
            throw new AcceptanceException(e.Message, e);
        }

        var blob = FamilyBlob(StoreJson.Serialize(record), null);

        // The batch put replaces the whole set, so read the save's blobs and add the layout to them.
        var read = await h.WireAsync("store-layout-read", "lifecycle_read_governor_state", new JsonObject(), cancellationToken).ConfigureAwait(false);
        JsonObject loaded;
        try
        {
            loaded = Harness.Outcome(read, "loaded").Value;
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"store-layout-read: {e.Message}", e);
        }

        var blobs = new JsonObject();
        foreach (var (key, value) in loaded["blobs"].AsMap() ?? [])
        {
            blobs[key] = value.AsString();
        }

        blobs[Store.Store.GovernorDefenseLayoutKey] = blob;

        // Combat reads the direction toward home from the plan's killbox opening, so the staged world holds a plan whose
        // only content is that opening.
        var plan = PolicyFunctions.OpeningPlan(Toward(l.Toward));
        blobs[Store.Store.GovernorLayoutPlanKey] = FamilyBlob(StoreJson.Serialize(plan), (world.Colony.Value, world.Map.Value));
        var reply = await h.WireAsync("store-layout", "lifecycle_put_governor_state_batch", new JsonObject { ["blobs"] = blobs }, cancellationToken).ConfigureAwait(false);
        try
        {
            _ = Harness.Outcome(reply, "loaded");
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"store-layout: {e.Message}", e);
        }

        return JsonNode.Parse(StoreJson.Serialize(record));
    }

    private static Rotation Toward(string name) => name switch
    {
        "north" => Rotation.North,
        "east" => Rotation.East,
        "south" => Rotation.South,
        "west" => Rotation.West,
        _ => throw new AcceptanceException($"layout toward \"{name}\" is not a rotation"),
    };

    // One governor state family blob: {schemaVersion, scope?, record}.
    private static string FamilyBlob(string recordJson, (string Colony, int Map)? scope)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteNumber("schemaVersion", Store.Store.GovernorStateSchemaVersion);
            if (scope is { } s)
            {
                w.WriteStartObject("scope");
                w.WriteString("colony", s.Colony);
                w.WriteNumber("map", s.Map);
                w.WriteNumber("tick", 0);
                w.WriteEndObject();
            }

            w.WritePropertyName("record");
            w.WriteRawValue(recordJson);
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static async Task WriteReadsAsync(string dir, IReadOnlyList<JsonObject> reads, CancellationToken cancellationToken)
    {
        _ = Directory.CreateDirectory(dir);
        var text = new StringBuilder();
        foreach (var read in reads)
        {
            _ = text.Append(read.ToJsonString()).Append('\n');
        }

        await File.WriteAllTextAsync(Path.Combine(dir, CombatLabBundle.ReadsFile), text.ToString(), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
    }
}
