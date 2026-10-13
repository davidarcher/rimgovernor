using System.Text.Json;
using System.Text.Json.Nodes;
using RimGovernor.Contracts.Wire;
using RimGovernor.Contracts.Wire.Presentation;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>presentation/roster</c> (Go <c>roster.go</c>): the colonist roster's dossier against a real headless game. Native
/// never fills a dossier; with include_dossier the controller joins every row to its pawn table row, with the families a
/// colony view renders (needs, health, biography, equipment) read from the live colony and the settings family kept out.
/// </summary>
internal static class PresentationRoster
{
    internal static AcceptanceCase Case() => new()
    {
        Name = "presentation/roster",
        Scope = "presentation_colonists carries no dossier; the controller's include_dossier join attaches each colonist's pawn table row "
            + "(mood, health summary, skills, equipment present; settings absent). Native: the roster read and the pawn table list read.",
        Start = new LabStart(),
        Budget = TimeSpan.FromMinutes(3),
        Crew = new Crew { Size = 3 },
        Run = RunAsync,
    };

    private static async Task RunAsync(ISession s, CancellationToken cancellationToken)
    {
        var report = s.Report;
        var h = s.Harness;
        var identity = s.Identity;
        if (!Dyn.Contains(s.Names, "rimgovernor/presentation_colonists"))
        {
            throw new AcceptanceException("missing rimgovernor/presentation_colonists in discovery");
        }

        var plain = await RosterRowsAsync(h, "colonists", new JsonObject { ["identity"] = identity.DeepClone(), ["currentMapOnly"] = true }, cancellationToken).ConfigureAwait(false);
        if (plain.Count == 0)
        {
            throw new AcceptanceException("colonists: fresh debug game has no spawned colonists");
        }

        foreach (var row in plain)
        {
            if (row.ContainsKey("dossier"))
            {
                throw new AcceptanceException($"colonists: native \"{row["pawnId"].AsString()}\" carries a dossier");
            }
        }

        var detailed = await JoinedRosterAsync(s, cancellationToken).ConfigureAwait(false);
        if (detailed.Count != plain.Count)
        {
            throw new AcceptanceException($"colonists-dossier: {detailed.Count} rows, plain roster had {plain.Count}");
        }

        var summary = new JsonObject();
        for (var i = 0; i < detailed.Count; i++)
        {
            var row = detailed[i];
            var id = row["pawnId"].AsString();
            if (id.Length == 0 || id != plain[i]["pawnId"].AsString())
            {
                throw new AcceptanceException($"colonists-dossier: row {i} names \"{id}\", plain roster names \"{plain[i]["pawnId"].AsString()}\"");
            }

            var dossier = row["dossier"].AsMap() ?? throw new AcceptanceException($"colonists-dossier: \"{id}\" has no dossier");
            var pawn = dossier["pawn"].AsMap();
            if (pawn.Member("id").AsString() != id)
            {
                throw new AcceptanceException($"colonists-dossier: dossier pawn \"{pawn.Member("id").AsString()}\" differs from row \"{id}\"");
            }

            if (dossier["colonist"].AsBool() != true)
            {
                throw new AcceptanceException($"colonists-dossier: \"{id}\" dossier is not flagged colonist");
            }

            var needs = dossier["needs"].AsMap();
            var health = dossier["health"].AsMap();
            var biography = dossier["biography"].AsMap();
            if (needs?.ContainsKey("mood") != true)
            {
                throw new AcceptanceException($"colonists-dossier: \"{id}\" dossier has no mood");
            }

            if (!needs.ContainsKey("food"))
            {
                throw new AcceptanceException($"colonists-dossier: \"{id}\" dossier has no food need");
            }

            if (health?.ContainsKey("summaryFraction") != true)
            {
                throw new AcceptanceException($"colonists-dossier: \"{id}\" dossier has no health summary");
            }

            if (biography.Member("skills").AsSlice().Count == 0)
            {
                throw new AcceptanceException($"colonists-dossier: \"{id}\" dossier has no skills");
            }

            if (dossier["equipment"].AsMap() is null)
            {
                throw new AcceptanceException($"colonists-dossier: \"{id}\" dossier has no equipment");
            }

            if (dossier.ContainsKey("settings"))
            {
                throw new AcceptanceException($"colonists-dossier: \"{id}\" dossier carries settings");
            }

            if (dossier.ContainsKey("animalState"))
            {
                throw new AcceptanceException($"colonists-dossier: \"{id}\" dossier carries animal state");
            }

            summary[id] = new JsonObject
            {
                ["mood"] = needs["mood"]?.DeepClone(),
                ["health"] = health["summaryFraction"]?.DeepClone(),
                ["skills"] = biography.Member("skills").AsSlice().Count,
                ["traits"] = biography.Member("traits").AsSlice().Count,
            };
        }

        report["dossiers"] = summary;
    }

    // The roster the controller serves: the native roster with each colonist's pawn table row joined. The Go case called
    // the controller's client; the in-mod brain serves the same join on the player API's colonists route.
    private static async Task<IReadOnlyList<JsonObject>> JoinedRosterAsync(ISession s, CancellationToken cancellationToken)
    {
        var service = await s.ServeAsync(new ServeSpec { Prefix = "roster" }, cancellationToken).ConfigureAwait(false);
        try
        {
            var reply = await service.ApiAsync("GET", "/api/presentation/colonists", null, "", cancellationToken).ConfigureAwait(false);
            if (reply.Status != 200)
            {
                throw new AcceptanceException($"colonists-dossier: status {reply.Status} body={reply.Body.ToJson()}");
            }

            ColonistRosterReply decoded;
            try
            {
                decoded = WireJson.Deserialize<ColonistRosterReply>(reply.Body?.ToJsonString() ?? "null");
            }
            catch (JsonException e)
            {
                throw new AcceptanceException($"colonists-dossier: {e.Message}", e);
            }

            var roster = decoded.Outcome?.Roster ?? throw new AcceptanceException($"colonists-dossier: expected a roster outcome: {reply.Body.ToJson()}");
            var node = JsonSerializer.SerializeToNode(roster, WireJson.Info<ColonistRoster>());
            return [.. node.Member("colonists").AsSlice().Select(row => row.AsMap() ?? [])];
        }
        finally
        {
            _ = await service.StopAsync().ConfigureAwait(false);
        }
    }

    private static async Task<IReadOnlyList<JsonObject>> RosterRowsAsync(Harness h, string label, JsonObject request, CancellationToken cancellationToken)
    {
        var reply = await h.WireAsync(label, "presentation_colonists", request, cancellationToken).ConfigureAwait(false);
        JsonObject roster;
        try
        {
            (_, roster) = Harness.Outcome(reply, "roster");
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"{label}: expected a roster outcome: {e.Message}", e);
        }

        var rows = new List<JsonObject>();
        foreach (var raw in roster["colonists"].AsSlice())
        {
            rows.Add(raw.AsMap() ?? throw new AcceptanceException($"{label}: colonist row must be an object"));
        }

        return rows;
    }
}
