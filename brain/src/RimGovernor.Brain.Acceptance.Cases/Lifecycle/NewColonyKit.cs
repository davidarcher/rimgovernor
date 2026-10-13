using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// What the <c>lifecycle/new_colony*</c>, <c>lifecycle/quiet_storyteller</c> and <c>lifecycle/scenario-start-parts</c>
/// cases share (Go <c>new_colony.go</c> helpers): the pinned spec, the request wrapper, the main-menu game scope and the
/// failure assertion.
/// </summary>
internal static class NewColonyKit
{
    /// <summary>The discovery tools every new-colony case needs.</summary>
    internal static readonly string[] MenuTools =
    [
        "rimgovernor/lifecycle_new_colony",
        "rimgovernor/lifecycle_read_new_colony",
        "rimgovernor/lifecycle_load",
        "rimgovernor/lifecycle_read_load",
    ];

    /// <summary>The small pinned spec (150 map, 0.05 coverage) the new-colony cases start from.</summary>
    internal static JsonObject Spec() => new()
    {
        ["scenario"] = "Crashlanded",
        ["colonistCount"] = 3,
        ["seed"] = "newcolony-accept",
        ["biomes"] = new JsonArray("TemperateForest", "BorealForest", "AridShrubland"),
        ["flatTile"] = true,
        ["difficulty"] = "Rough",
        ["storyteller"] = "Cassandra",
        ["mapSize"] = 150,
        ["planetCoverage"] = 0.05,
        ["saveName"] = "newcolony-accept",
    };

    /// <summary>A <c>lifecycle_new_colony</c> request over a spec with the ten-minute timeout.</summary>
    internal static JsonObject Request(string id, JsonObject spec) => new() { ["requestId"] = id, ["spec"] = spec, ["timeoutMs"] = 600000 };

    /// <summary>Starts a colony and polls to completion (phases in order, the completed reply); a failed start throws.</summary>
    internal static Task<NewColonyRun> RunAsync(Harness h, string id, JsonObject spec, string label, CancellationToken cancellationToken) =>
        NewColony.RunNewColonyAsync(h, Request(id, spec), label, cancellationToken);

    /// <summary>Opens a game at the main menu, runs <paramref name="body"/> against it and retires the game (Go <c>newColonyMenu</c>).</summary>
    internal static Task MenuAsync(ISession s, string name, string[] tools, Func<Harness, JsonObject, Task> body, CancellationToken cancellationToken) =>
        MenuAsync(s.Config, s.Report, name, tools, body, cancellationToken);

    /// <summary><see cref="MenuAsync(ISession, string, string[], Func{Harness, JsonObject, Task}, CancellationToken)"/> over an explicit configuration; a null report keeps the row to the body.</summary>
    internal static async Task MenuAsync(Config config, Report? report, string name, string[] tools, Func<Harness, JsonObject, Task> body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        GameReuse reuse;
        try
        {
            reuse = await GameReuse.OpenReusableGameAsync(config, cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"{name}: {e.Message}", e);
        }

        try
        {
            var h = await reuse.SessionAsync(cancellationToken).ConfigureAwait(false);
            var names = await h.DiscoveryAsync(cancellationToken).ConfigureAwait(false) ?? [];
            foreach (var tool in tools)
            {
                if (!Dyn.Contains(names, tool))
                {
                    throw new AcceptanceException($"missing {tool} in discovery");
                }
            }

            var row = new JsonObject();
            if (report is not null)
            {
                report["new_colony_" + name] = row;
            }

            await body(h, row).ConfigureAwait(false);
        }
        finally
        {
            using var retire = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try
            {
                await reuse.RetireAsync(name + " launch complete", retire.Token).ConfigureAwait(false);
            }
            catch (AcceptanceException)
            {
                // Retiring is best effort; the case's own result stands.
            }
            catch (OperationCanceledException)
            {
                // Same.
            }
        }
    }

    /// <summary>Asserts a failure reply carries <paramref name="code"/> and a detail mentioning <paramref name="contains"/>.</summary>
    internal static void WantFailure(JsonObject reply, string code, string contains)
    {
        var failure = reply["failure"].AsMap() ?? throw new AcceptanceException($"want failure {code}, got {reply.ToJson()}");
        var got = failure["code"].AsString();
        var detail = failure["detail"].AsString();
        if (got != code || !detail.Contains(contains, StringComparison.Ordinal))
        {
            throw new AcceptanceException($"want {code} mentioning \"{contains}\", got {got}: {detail}");
        }
    }

    /// <summary>A JSON array of strings.</summary>
    internal static JsonArray Strings(IEnumerable<string> values) => new([.. values.Select(v => (JsonNode?)JsonValue.Create(v))]);

    /// <summary>The colonists <c>observations_list_pawns</c> reports (with biography) for the loaded colony.</summary>
    internal static async Task<IReadOnlyList<JsonNode?>> ListColonistsAsync(Harness h, string label, JsonObject scope, CancellationToken cancellationToken)
    {
        var reply = await h.WireAsync(
            label,
            "observations_list_pawns",
            new JsonObject
            {
                ["scope"] = scope.DeepClone(),
                ["filter"] = new JsonObject { ["colonist"] = true },
                ["details"] = new JsonObject { ["biography"] = true },
            },
            cancellationToken).ConfigureAwait(false);
        var (_, observed) = LifecycleCases.Outcome("pawns", reply, "observed");
        return observed["pawns"].AsSlice();
    }

    /// <summary>Asserts the colonist carries the forced Jogger trait (Go <c>newColonyPartsTrait</c>).</summary>
    internal static async Task CheckPartsTraitAsync(Harness h, JsonObject row, CancellationToken cancellationToken)
    {
        var identity = await Session.ReadIdentityAsync(h, "parts-identity", cancellationToken).ConfigureAwait(false);
        var pawns = await ListColonistsAsync(h, "parts-pawns", new JsonObject { ["expectedIdentity"] = identity }, cancellationToken).ConfigureAwait(false);
        if (pawns.Count != 1)
        {
            throw new AcceptanceException($"want 1 colonist, got {pawns.Count}");
        }

        var traits = (pawns[0].AsMap()?["biography"].AsMap()?["traits"]).AsSlice().Select(t => t.AsMap()?["defName"].AsString() ?? "").ToList();
        if (!traits.Contains("Jogger", StringComparer.Ordinal))
        {
            throw new AcceptanceException($"the forced trait Jogger did not land: colonist traits [{string.Join(' ', traits)}]");
        }

        row["traits"] = Strings(traits);
    }
}
