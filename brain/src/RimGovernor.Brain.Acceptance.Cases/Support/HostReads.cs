using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store.Codec;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The harness side of the mod's read-only acceptance tools (<c>rimgovernor/acceptance_read_*</c>, registered by the mod
/// from <c>AcceptanceReads</c>): the decoded colony facts, blight census, first layout plan and map survey, and two
/// catalog tables exist only inside the game process, so each seam calls its tool through the generic native route and
/// parses the JSON in the reply's <c>payload</c>. <see cref="HostSeams.Install"/> assigns them.
/// </summary>
internal static class HostReads
{
    private const string Prefix = "rimgovernor/acceptance_read_";

    internal static async Task<ColonyFactsReading> ColonyFactsAsync(Harness harness, JsonObject identity, string label, CancellationToken cancellationToken)
    {
        _ = identity;
        var read = await ReadAsync(harness, label, "colony_facts", null, cancellationToken).ConfigureAwait(false);
        return new ColonyFactsReading(Number(read, "sleepingMin"), Number(read, "sleepingMax"), Flag(read, "powerRequired"), Number(read, "powerHeadroom"), Flag(read, "disabledConsumers"));
    }

    internal static async Task<BlightSurvey> BlightAsync(Harness harness, CancellationToken cancellationToken)
    {
        var read = await ReadAsync(harness, "blight-census", "blight", null, cancellationToken).ConfigureAwait(false);
        if (Flag(read, "unavailable") == true)
        {
            throw new AcceptanceException("the controller's blighted-plant census is unread on this load");
        }

        var plants = new List<BlightedPlant>();
        foreach (var raw in read["plants"] as JsonArray ?? [])
        {
            var row = (JsonObject)raw!;
            plants.Add(new BlightedPlant
            {
                ID = Text(row, "id"),
                Definition = Text(row, "def"),
                Cell = new Cell((int)Number(row, "x")!.Value, (int)Number(row, "z")!.Value),
                Zone = Text(row, "zone"),
                Designated = Flag(row, "designated") == true,
                Eligible = Flag(row, "eligible") == true,
            });
        }

        return new BlightSurvey((long)Number(read, "tick")!.Value, plants);
    }

    internal static async Task<StarterSiteSurvey> SurveyAsync(Harness harness, CancellationToken cancellationToken)
    {
        var read = await ReadAsync(harness, "starter-site-survey", "starter_site", new JsonObject { ["cells"] = true }, cancellationToken).ConfigureAwait(false);
        var survey = read["survey"] as JsonObject ?? throw new AcceptanceException("starter site read: no map survey in the reply");
        var terrains = (survey["terrains"] as JsonArray ?? []).Select(t => t!.GetValue<string>()).ToList();
        var cells = new List<SurveyCell>();
        foreach (var raw in survey["cells"] as JsonArray ?? [])
        {
            var c = (JsonArray)raw!;
            var flags = c[2]!.GetValue<int>();
            cells.Add(new SurveyCell
            {
                Cell = new Cell(c[0]!.GetValue<int>(), c[1]!.GetValue<int>()),
                Walkable = (flags & 1) != 0,
                Rock = (flags & 2) != 0,
                Built = (flags & 4) != 0,
                Bridgeable = (flags & 8) != 0,
                Dries = (flags & 16) != 0,
                Hazard = (flags & 32) != 0,
                ThickRoof = (flags & 64) != 0,
                Ore = (flags & 128) != 0,
                Tree = (flags & 256) != 0,
                Prop = (flags & 512) != 0,
                Ruin = (flags & 1024) != 0,
                Footing = (Footing)c[3]!.GetValue<int>(),
                Fertility = c[4]!.GetValue<double>(),
                Terrain = terrains[c[5]!.GetValue<int>()],
            });
        }

        var map = new MapSurvey
        {
            Bounds = new Bounds((int)Number(read, "width")!.Value, (int)Number(read, "height")!.Value),
            Cells = cells,
            Cold = Flag(survey, "cold") == true,
            Hot = Flag(survey, "hot") == true,
        };
        return new StarterSiteSurvey((ulong)Number(read, "tick")!.Value, (long)Number(read, "colonists")!.Value, map);
    }

    internal static async Task<ImpressivenessLevels> ImpressivenessAsync(Harness harness, JsonObject identity, CancellationToken cancellationToken)
    {
        _ = identity;
        var read = await ReadAsync(harness, "catalog-impressiveness", "impressiveness", null, cancellationToken).ConfigureAwait(false);
        return new ImpressivenessLevels
        {
            Dull = Number(read, "dull") ?? 0,
            Mediocre = Number(read, "mediocre") ?? 0,
            Decent = Number(read, "decent") ?? 0,
            SlightlyImpressive = Number(read, "slightlyImpressive") ?? 0,
            Stages = new([.. (read["stages"] as JsonArray ?? []).Select(s => s!.GetValue<double>())]),
        };
    }

    internal static async Task<IReadOnlyList<AnimalRaceRow>> AnimalRacesAsync(Harness harness, JsonObject identity, CancellationToken cancellationToken)
    {
        _ = identity;
        var read = await ReadAsync(harness, "catalog-animal-races", "animal_races", null, cancellationToken).ConfigureAwait(false);
        var rows = new List<AnimalRaceRow>();
        foreach (var raw in read["races"] as JsonArray ?? [])
        {
            var row = (JsonObject)raw!;
            var milk = row["milk"] as JsonObject;
            rows.Add(new AnimalRaceRow(
                Text(row, "def"),
                Number(row, "wildness"),
                Number(row, "bodySize"),
                Number(row, "minimumHandlingSkill") is { } skill ? (int)skill : null,
                milk is null ? null : new AnimalMilk(Number(milk, "amount")!.Value, Number(milk, "intervalDays")!.Value)));
        }

        return rows;
    }

    internal static async Task<IReadOnlyDictionary<string, FloorTerrainRow>> FloorTerrainsAsync(Harness harness, JsonObject identity, CancellationToken cancellationToken)
    {
        _ = identity;
        var read = await ReadAsync(harness, "catalog-floor-terrains", "floor_terrains", null, cancellationToken).ConfigureAwait(false);
        var rows = new Dictionary<string, FloorTerrainRow>(StringComparer.Ordinal);
        foreach (var raw in read["terrains"] as JsonArray ?? [])
        {
            var row = (JsonObject)raw!;
            rows[Text(row, "def")] = new FloorTerrainRow(Number(row, "cleanliness")!.Value, row["natural"]?.GetValue<bool>() ?? false);
        }

        return rows;
    }

    /// <summary>Plans the first layout from the mod's read: the plan in the store codec and the map bounds.</summary>
    internal sealed class StarterSiteReader : IStarterSitePlanner
    {
        public async Task<StarterSitePlan> PlanAsync(Harness harness, CancellationToken cancellationToken)
        {
            var read = await ReadAsync(harness, "starter-site-plan", "starter_site", new JsonObject { ["cells"] = false }, cancellationToken).ConfigureAwait(false);
            if (read["plan"] is not { } plan)
            {
                throw new AcceptanceException("no layout plan on this map");
            }

            return new StarterSitePlan(
                StoreJson.Deserialize<LayoutPlan>(plan.ToJsonString()),
                new Bounds((int)Number(read, "width")!.Value, (int)Number(read, "height")!.Value));
        }
    }

    private static async Task<JsonObject> ReadAsync(Harness harness, string label, string name, JsonObject? arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(harness);
        var reply = await harness.CallAsync(label, Prefix + name, arguments, cancellationToken).ConfigureAwait(false);
        if (reply["payload"] is not JsonValue value || !value.TryGetValue<string>(out var payload))
        {
            throw new AcceptanceException($"{Prefix}{name}: reply carried no payload: {reply.ToJsonString()}");
        }

        try
        {
            return JsonNode.Parse(payload) as JsonObject ?? throw new AcceptanceException($"{Prefix}{name}: payload is not an object");
        }
        catch (System.Text.Json.JsonException e)
        {
            throw new AcceptanceException($"{Prefix}{name}: invalid payload: {e.Message}", e);
        }
    }

    private static double? Number(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    private static bool? Flag(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    private static string Text(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
}
