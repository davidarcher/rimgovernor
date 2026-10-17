using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>lifecycle/new_colony</c> (Go <c>new_colony.go</c>): the production new-colony start on a game the case launches
/// itself, twice (the second launch proves the same pinned spec gives the same colony), plus a forced save failure and
/// a timeout.
/// </summary>
internal static class LifecycleNewColony
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "lifecycle/new_colony",
        Scope = "Production new-colony start (#2021): a small spec (150 map, 0.05 coverage) reaches a live map with the requested colonist count, biome and map size, "
            + "and the poll reports phases in order; the same pinned spec on a second launch gives the same tile, biome and colonist names and skills; "
            + "refusals name a bad scenario/biome/difficulty defName (listing the valid ones), an unsatisfiable temperature band, and a start over a loaded game. "
            + "the start completes paused with the names confirmed and a save in profile/Saves (byte length reported, seed echoed) that lifecycle_load restores to the same colonist count, tick and pause; "
            + "a forced save failure and a timeout each return a failure, not a hung pending.",
        Start = new OwnedStart(),
        Reason = "the start needs a fresh main menu process and the determinism check needs a second launch; the case opens and retires its own games",
        NoKeep = true,
        Budget = TimeSpan.FromMinutes(30),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var first = await LaunchAsync(s, "first", withRefusals: true, cancellationToken).ConfigureAwait(false);
        var second = await LaunchAsync(s, "second", withRefusals: false, cancellationToken).ConfigureAwait(false);
        if (first != second)
        {
            throw new AcceptanceException($"the same pinned spec gave different colonies:\nfirst:  {first}\nsecond: {second}");
        }

        s.Report["case_same_spec_same_colony"] = first;
        await SaveFailureAsync(s, cancellationToken).ConfigureAwait(false);
        await TimeoutAsync(s, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Starts with a directory where the save file must go: the save cannot be written, and the start must fail in the saving phase.</summary>
    private static async Task SaveFailureAsync(ISession s, CancellationToken cancellationToken)
    {
        var spec = NewColonyKit.Spec();
        const string Save = "newcolony-accept-savefail";
        spec["saveName"] = Save;
        var squat = s.Config.ProfileSave(Save);
        _ = Directory.CreateDirectory(squat);
        try
        {
            await NewColonyKit.MenuAsync(
                s,
                "savefail",
                NewColonyKit.MenuTools,
                async (h, row) =>
                {
                    AcceptanceException? failure = null;
                    try
                    {
                        _ = await NewColonyKit.RunAsync(h, $"newcolony-savefail-{LifecycleCases.Stamp()}", spec, "savefail", cancellationToken).ConfigureAwait(false);
                    }
                    catch (AcceptanceException e)
                    {
                        failure = e;
                    }

                    if (failure is null || !failure.Message.Contains("FAILURE_CODE_NATIVE_FAILURE", StringComparison.Ordinal) || !failure.Message.Contains("Saving", StringComparison.Ordinal))
                    {
                        throw new AcceptanceException($"forced save failure: want a native failure in the saving phase, got {failure?.Message ?? "no error"}");
                    }

                    row["failure"] = failure.Message;
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (Directory.Exists(squat))
            {
                Directory.Delete(squat, recursive: true);
            }
        }
    }

    /// <summary>Asks for the minimum timeout: the start must end with a failure naming the timeout rather than stay pending.</summary>
    private static Task TimeoutAsync(ISession s, CancellationToken cancellationToken) =>
        NewColonyKit.MenuAsync(
            s,
            "timeout",
            NewColonyKit.MenuTools,
            async (h, row) =>
            {
                var request = NewColonyKit.Request($"newcolony-timeout-{LifecycleCases.Stamp()}", NewColonyKit.Spec());
                request["timeoutMs"] = 1000;
                AcceptanceException? failure = null;
                try
                {
                    _ = await NewColony.RunNewColonyAsync(h, request, "timeout", cancellationToken).ConfigureAwait(false);
                }
                catch (AcceptanceException e)
                {
                    failure = e;
                }

                if (failure is null || !failure.Message.Contains("timeout", StringComparison.Ordinal))
                {
                    throw new AcceptanceException($"timeout: want a failure naming the timeout, got {failure?.Message ?? "no error"}");
                }

                row["failure"] = failure.Message;
            },
            cancellationToken);

    /// <summary>Opens a game at the main menu, runs the checks and retires the game; returns the colony digest.</summary>
    private static async Task<string> LaunchAsync(ISession s, string name, bool withRefusals, CancellationToken cancellationToken)
    {
        var digest = "";
        await NewColonyKit.MenuAsync(
            s,
            name,
            NewColonyKit.MenuTools,
            async (h, row) => digest = await ChecksAsync(s, h, row, name, withRefusals, cancellationToken).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
        return digest;
    }

    private static async Task<string> ChecksAsync(ISession s, Harness h, JsonObject row, string name, bool withRefusals, CancellationToken cancellationToken)
    {
        if (withRefusals)
        {
            await RefusalsAsync(h, row, cancellationToken).ConfigureAwait(false);
        }

        var id = $"newcolony-{name}-{LifecycleCases.Stamp()}";
        var started = Stopwatch.StartNew();
        var run = await NewColonyKit.RunAsync(h, id, NewColonyKit.Spec(), name, cancellationToken).ConfigureAwait(false);
        var completed = run.Completed;
        row["phases"] = NewColonyKit.Strings(run.Phases);
        row["finish_ms"] = started.ElapsedMilliseconds;

        var spec = NewColonyKit.Spec();
        var saveName = spec["saveName"].AsString();
        if (completed["saveName"].AsString() != saveName)
        {
            throw new AcceptanceException($"completed save name \"{completed["saveName"].AsString()}\", want \"{saveName}\"");
        }

        if (completed["seed"].AsString() != spec["seed"].AsString())
        {
            throw new AcceptanceException($"completed seed \"{completed["seed"].AsString()}\", want \"{spec["seed"].AsString()}\"");
        }

        if (completed["paused"].AsBool() != true)
        {
            throw new AcceptanceException($"the start completed unpaused: {completed.ToJson()}");
        }

        var info = new FileInfo(s.Config.ProfileSave(saveName));
        if (!info.Exists)
        {
            throw new AcceptanceException("the completed start left no save in profile/Saves");
        }

        if ((long)completed["byteLength"].AsNumber() != info.Length || info.Length == 0)
        {
            throw new AcceptanceException($"reported byte length {completed["byteLength"].ToJson()}, save file is {info.Length} bytes");
        }

        row["save_bytes"] = info.Length;
        var completedContext = completed["context"].AsMap();
        var identity = completedContext?["identity"].AsMap() ?? [];
        var tick = (int)completedContext.Member("tick").AsNumber();

        var digest = await ReadAsync(h, name, row, cancellationToken).ConfigureAwait(false);
        if (!withRefusals)
        {
            return digest;
        }

        // A second start over the live colony is refused.
        var reply = await h.WireAsync("loaded-refusal", "lifecycle_new_colony", NewColonyKit.Request(id + "-again", NewColonyKit.Spec()), cancellationToken).ConfigureAwait(false);
        try
        {
            NewColonyKit.WantFailure(reply, "FAILURE_CODE_UNAVAILABLE", "main menu");
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"start over a loaded game: {e.Message}", e);
        }

        row["loaded_refusal"] = true;

        // The save loads through lifecycle_load and restores the same colony.
        var loadId = $"newcolony-load-{LifecycleCases.Stamp()}";
        var loadReply = await h.WireAsync(
            "load-start",
            "lifecycle_load",
            new JsonObject
            {
                ["requestId"] = loadId,
                ["saveName"] = saveName,
                ["readiness"] = "READINESS_MAP",
                ["expectedPlayer"] = new JsonObject { ["identity"] = identity.DeepClone(), ["playerDirection"] = 1, ["requestId"] = loadId },
                ["playerDirection"] = 1,
            },
            cancellationToken).ConfigureAwait(false);
        try
        {
            _ = await GameReuse.PollLoadAsync(h, loadReply, loadId, "load-poll", cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"load of the generated save: {e.Message}", e);
        }

        var (loaded, _, reloadedTick) = await LifecycleCases.ReadIdentityAsync(h, "loaded-identity", cancellationToken).ConfigureAwait(false);
        if ((int)reloadedTick != tick)
        {
            throw new AcceptanceException($"reloaded colony is at tick {(int)reloadedTick}, generated at {tick}");
        }

        if (loaded["paused"].AsBool() != true)
        {
            throw new AcceptanceException("reloaded colony is not paused");
        }

        var reloaded = await ReadAsync(h, "reloaded", row, cancellationToken).ConfigureAwait(false);
        if (reloaded != digest)
        {
            throw new AcceptanceException($"reloaded colony differs from the generated one:\ngenerated: {digest}\nreloaded:  {reloaded}");
        }

        row["reload_matches"] = true;
        return digest;
    }

    /// <summary>
    /// Probes the refusals that leave the menu untouched, then one that fails during generation (an unsatisfiable
    /// temperature band) and must still leave the menu usable for the real start.
    /// </summary>
    private static async Task RefusalsAsync(Harness h, JsonObject row, CancellationToken cancellationToken)
    {
        var refusals = new JsonObject();
        row["refusals"] = refusals;
        foreach (var (field, value, contains) in new[]
        {
            ("scenario", "NoSuchScenario", "Crashlanded"),
            ("difficulty", "NoSuchDifficulty", "Rough"),
            ("storyteller", "NoSuchStoryteller", "Cassandra"),
        })
        {
            var spec = NewColonyKit.Spec();
            spec[field] = value;
            var reply = await h.WireAsync("refuse-" + field, "lifecycle_new_colony", NewColonyKit.Request("newcolony-bad-" + field, spec), cancellationToken).ConfigureAwait(false);

            // The failure lists the valid names and names the bad one's field.
            try
            {
                NewColonyKit.WantFailure(reply, "FAILURE_CODE_INVALID_REQUEST", contains);
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException($"bad {field}: {e.Message}", e);
            }

            refusals[field] = true;
        }

        var biomeSpec = NewColonyKit.Spec();
        biomeSpec["biomes"] = new JsonArray("NoSuchBiome");
        var biomeReply = await h.WireAsync("refuse-biome", "lifecycle_new_colony", NewColonyKit.Request("newcolony-bad-biome", biomeSpec), cancellationToken).ConfigureAwait(false);
        try
        {
            NewColonyKit.WantFailure(biomeReply, "FAILURE_CODE_INVALID_REQUEST", "TemperateForest");
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"bad biome: {e.Message}", e);
        }

        refusals["biome"] = true;

        // Unsatisfiable temperature: passes validation, fails once the tiles are known.
        var hot = NewColonyKit.Spec();
        hot["minTemperature"] = 90;
        hot["maxTemperature"] = 100;
        AcceptanceException? failure = null;
        try
        {
            _ = await NewColonyKit.RunAsync(h, "newcolony-hot", hot, "unsatisfiable", cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            failure = e;
        }

        if (failure is null || !failure.Message.Contains("temperature", StringComparison.Ordinal))
        {
            throw new AcceptanceException($"unsatisfiable temperature band: want a failure naming the temperature, got {failure?.Message ?? "no error"}");
        }

        refusals["temperature"] = failure.Message;
    }

    /// <summary>
    /// Asserts the live map matches the spec and returns a digest of what a pinned seed must reproduce: the tile,
    /// biome and each colonist's name and skill levels.
    /// </summary>
    private static async Task<string> ReadAsync(Harness h, string label, JsonObject row, CancellationToken cancellationToken)
    {
        var identity = await Session.ReadIdentityAsync(h, label + "-identity", cancellationToken).ConfigureAwait(false);
        var scope = new JsonObject { ["expectedIdentity"] = identity };

        var factsReply = await h.WireAsync(label + "-facts", "observations_read_colony_facts", new JsonObject { ["scope"] = scope.DeepClone() }, cancellationToken).ConfigureAwait(false);
        var (_, facts) = LifecycleCases.Outcome("colony facts", factsReply, "observed");
        var count = (int)facts["colonistCount"].AsNumber();
        if (count != 3)
        {
            throw new AcceptanceException($"colony has {count} colonists, want 3");
        }

        var size = facts["mapSize"].AsMap();
        if ((int)size.Member("width").AsNumber() != 150 || (int)size.Member("height").AsNumber() != 150)
        {
            throw new AcceptanceException($"map size {size.ToJson()}, want 150x150");
        }

        var biome = facts["biome"].AsString();
        if (Array.IndexOf(["TemperateForest", "BorealForest", "AridShrubland"], biome) < 0)
        {
            throw new AcceptanceException($"biome \"{biome}\" is none of the requested {NewColonyKit.Spec()["biomes"].ToJson()}");
        }

        var worldReply = await h.WireAsync(label + "-world", "observations_read_world", new JsonObject { ["scope"] = scope.DeepClone() }, cancellationToken).ConfigureAwait(false);
        var (_, world) = LifecycleCases.Outcome("world", worldReply, "observed");
        var tile = world["tile"].AsMap();
        var tileId = (int)tile.Member("tile").AsNumber();
        var hilliness = tile.Member("hilliness").AsString();

        var colonists = new List<string>();
        foreach (var raw in await NewColonyKit.ListColonistsAsync(h, label + "-pawns", scope, cancellationToken).ConfigureAwait(false))
        {
            var pawn = raw.AsMap();
            var skills = (pawn?["biography"].AsMap()?["skills"]).AsSlice()
                .Select(skill => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{skill.Member("defName").AsString()}={(int)Math.Max(skill.Member("level").AsNumber(), 0)}/{skill.Member("passion").AsString()}"))
                .Order(StringComparer.Ordinal);
            colonists.Add($"{pawn?["pawn"].AsMap()?["label"].AsString()}[{string.Join(',', skills)}]");
        }

        colonists.Sort(StringComparer.Ordinal);
        if (colonists.Count != 3)
        {
            throw new AcceptanceException($"pawn list has {colonists.Count} colonists, want 3");
        }

        row["biome"] = biome;
        row["tile"] = tileId;
        row["hilliness"] = hilliness;
        row["colonists"] = NewColonyKit.Strings(colonists);
        row["threat_difficulty_scale"] = ThreatScale(facts);
        return string.Create(CultureInfo.InvariantCulture, $"tile={tileId} biome={biome} hilliness={hilliness} colonists={string.Join(';', colonists)}");
    }

    /// <summary>The facts' difficulty threat scale when the threat section is present; reported, not asserted (no difficulty read exists).</summary>
    private static JsonNode? ThreatScale(JsonObject facts)
    {
        if (facts["threat"].AsMap() is not { } section)
        {
            return null;
        }

        try
        {
            var (_, threat) = Harness.Outcome(section, "observed");
            return threat["difficultyThreatScale"]?.DeepClone();
        }
        catch (AcceptanceException)
        {
            return null;
        }
    }
}
