using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>The stockpile-zone audit of the colony window: the zone count, the zone churn and the starting supplies (Go <c>sustained/stockpiles.go</c>).</summary>
internal static class SustainedStockpiles
{
    /// <summary>
    /// The most stockpile zones allowing human food a colony window may end with: the food stockpile, the raw-food
    /// freezer and the larder, with room for the further warehouse a full store asks for. A playtest ended its second
    /// day with about twenty.
    /// </summary>
    private const int ColonyFoodZoneLimit = 6;

    /// <summary>
    /// The tribal start stacks the scenario forbids. Only Pemmican gates: wood stacks outside the base stay forbidden
    /// on purpose (ManageSupplySafety, outside_base:insufficient_defense), so wood is only reported.
    /// </summary>
    private static readonly string[] StartingSupplyDefs = ["Pemmican", "WoodLog"];

    /// <summary>
    /// Counts the stockpile zones the window ended with, those allowing human food apart, and checks the starting
    /// supplies were allowed. The report keeps both either way.
    /// </summary>
    internal static async Task AuditAsync(ISession s, Report report, CancellationToken cancellationToken)
    {
        var h = s.Harness;
        var identity = s.Identity;
        var reply = await h.WireAsync(
            "audit-zones",
            "observations_list_zones",
            new JsonObject { ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() }, ["includeFilter"] = true },
            cancellationToken).ConfigureAwait(false);
        var (_, observed) = Harness.Outcome(reply, "observed");
        var stockpiles = 0;
        var food = 0;
        var rows = new JsonArray();
        foreach (var raw in observed["zones"].AsSlice())
        {
            var row = raw.AsMap() ?? [];
            if (row["type"].AsString() != "stockpile")
            {
                continue;
            }

            stockpiles++;
            var isFood = row["foodStorage"].AsBool() == true;
            if (isFood)
            {
                food++;
            }

            rows.Add(new JsonObject
            {
                ["allowed"] = row["filter"].Member("allowedDefNames")?.DeepClone(),
                ["id"] = row["id"]?.DeepClone(),
                ["label"] = row["label"]?.DeepClone(),
                ["priority"] = row["priority"]?.DeepClone(),
                ["bounds"] = row["bounds"]?.DeepClone(),
                ["food_storage"] = isFood,
            });
        }

        var supplies = await h.WireAsync(
            "audit-supplies",
            "observations_list_supplies",
            new JsonObject
            {
                ["scope"] = new JsonObject { ["expectedIdentity"] = identity.DeepClone() },
                ["filter"] = new JsonObject { ["defNames"] = new JsonArray([.. StartingSupplyDefs.Select(d => (JsonNode?)JsonValue.Create(d))]) },
            },
            cancellationToken).ConfigureAwait(false);
        var (_, stock) = Harness.Outcome(supplies, "observed");
        var forbidden = new List<string>();
        var stocks = new JsonArray();
        foreach (var raw in stock["stocks"].AsSlice())
        {
            var row = raw.AsMap() ?? [];
            var ours = row["ours"].AsNumber();
            var unforbidden = row["oursUnforbidden"].AsNumber();
            var name = row["definition"].Member("defName").AsString();
            stocks.Add(new JsonObject { ["def"] = name, ["ours"] = ours, ["ours_unforbidden"] = unforbidden });
            if (ours > unforbidden && name == "Pemmican")
            {
                forbidden.Add(string.Create(CultureInfo.InvariantCulture, $"{name} {ours - unforbidden} of {ours} still forbidden"));
            }
        }

        var churn = AuditZoneChurn(s);
        var deletes = new JsonObject();
        foreach (var (role, count) in churn.Deletes.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            deletes[role] = count;
        }

        report["stockpile_zones"] = new JsonObject
        {
            ["stockpiles"] = stockpiles,
            ["food"] = food,
            ["limit"] = ColonyFoodZoneLimit,
            ["rows"] = rows,
            ["starting_supplies"] = stocks,
            ["deletes"] = deletes,
            ["creates"] = new JsonArray([.. churn.Creates]),
        };
        var failures = new List<string>();
        if (churn.Flagged.Count > 0)
        {
            failures.Add($"stockpile zones deleted while their purpose still stands (churn): [{string.Join(' ', churn.Flagged)}]");
        }

        if (food > ColonyFoodZoneLimit)
        {
            failures.Add(string.Create(CultureInfo.InvariantCulture, $"{food} food stockpile zones ({stockpiles} stockpiles) at the window's end, limit {ColonyFoodZoneLimit}"));
        }

        if (forbidden.Count > 0)
        {
            failures.Add($"starting supplies left forbidden: [{string.Join(' ', forbidden)}]");
        }

        if (failures.Count > 0)
        {
            throw new AcceptanceException(string.Join('\n', failures));
        }
    }

    /// <summary>
    /// The zone-count and churn reading of one window: one zone per store, and a delete only when the store's
    /// purpose is gone.
    /// </summary>
    private sealed class ZoneChurn
    {
        /// <summary>The admitted zone deletes by the role they name.</summary>
        public Dictionary<string, int> Deletes { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// The admitted zone creates in journal order with their tick and size: the first one is the earliest a store
        /// can open, the order is the per-store creation order.
        /// </summary>
        public List<JsonNode?> Creates { get; } = [];

        /// <summary>The roles created again after a delete in the window: the delete did not follow a retired purpose.</summary>
        public List<string> Flagged { get; } = [];
    }

    /// <summary>
    /// Reads the OTLP log ring's admitted stockpile deletes and creates (layout_edit, family stockpile, attrs kind and
    /// role: a delete follows a purpose the department declared gone, so a later create of the same role is churn). It
    /// is post-hoc like the rest of the audit: a recorder that cannot be read leaves the counts empty.
    /// </summary>
    private static ZoneChurn AuditZoneChurn(ISession s)
    {
        var churn = new ZoneChurn();
        IReadOnlyList<LogRow> logs;
        try
        {
            logs = ServiceLogs.ReadLogs(ServiceLogs.ServiceLogsPath(s.Config.Output));
        }
        catch (IOException)
        {
            return churn;
        }

        var deleted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in logs)
        {
            if (row.Kind != "layout_edit" || SustainedRows.Text(row.Payload.GetValueOrDefault("verdict")) != "admitted")
            {
                continue;
            }

            var attrs = row.Payload.GetValueOrDefault("attrs") as IReadOnlyDictionary<string, object?>;
            var role = SustainedRows.Text(attrs?.GetValueOrDefault("role"));
            if (SustainedRows.Text(attrs?.GetValueOrDefault("family")) != "stockpile" || role.Length == 0)
            {
                continue;
            }

            switch (SustainedRows.Text(attrs?.GetValueOrDefault("kind")))
            {
                case "delete":
                    churn.Deletes[role] = churn.Deletes.GetValueOrDefault(role) + 1;
                    deleted.Add(role);
                    break;
                case "create":
                    churn.Creates.Add(new JsonObject
                    {
                        ["role"] = role,
                        ["tick"] = row.Tick,
                        ["cells"] = JsonSerializer.SerializeToNode(attrs?.GetValueOrDefault("cells")),
                    });
                    if (deleted.Contains(role))
                    {
                        churn.Flagged.Add(role);
                    }

                    break;
            }
        }

        churn.Flagged.Sort(StringComparer.Ordinal);
        return churn;
    }
}

/// <summary>Loose reads of the decoded values a <see cref="LogRow"/> holds (Go <c>AsString</c>/<c>AsNumber</c> over <c>any</c>), for the sustained audits.</summary>
internal static class SustainedRows
{
    /// <summary>The string a value holds; empty when it is not a string.</summary>
    internal static string Text(object? value) => value as string ?? "";

    /// <summary>The number a value holds; -1 when it is not numeric.</summary>
    internal static double Number(object? value) => value switch
    {
        double d => d,
        long l => l,
        int i => i,
        string t when double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => -1,
    };
}
