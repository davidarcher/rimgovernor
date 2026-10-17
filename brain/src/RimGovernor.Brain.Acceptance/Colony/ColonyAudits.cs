using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Telemetry;
using RimGovernor.Contracts.Wire;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// What <c>acceptance colony</c> checks once its window ends, on the reattached game and the service's log ring: the
/// keep-alive resumes, every colonist's worst malnutrition and the stockpile zones. The colony review (<c>acceptance colony-review</c>) shares the
/// Concerns.
/// </summary>
public static class ColonyAudits
{
    /// <summary>
    /// The Concerns every colony sample reads beside the food/supply Need: the foothold gates and the first
    /// maintenance-tier projects, so the timeline shows which one stalls, thrashes or starves the others.
    /// </summary>
    public static readonly IReadOnlyList<string> ColonyConcerns =
    [
        ShelterFamily.Starter.Value,
        ShelterFamily.Sleeping.Value,
        ShelterFamily.Expansion.Value,
        FoodRules.CookingNeed.Value,
        ShelterFamily.Temperature.Value,
        EconomyFamily.Resource.Value,
        AdminFamily.WorkAssignments.Value,
        MilitaryFamily.Defense.Value,
        EconomyFamily.Research.Value,
        RoomsFamily.Comfort.Value,
        HomeUpkeepFamily.Repairs.Value,
    ];

    /// <summary>The worst Malnutrition severity the window may leave on any colonist: 0.3 is "hungry" on the health tab.</summary>
    private const double MalnutritionLimit = 0.3;

    // The acceptance harness references no game assembly, so the defs it reads by name are named here.
    private const string MalnutritionDef = "Malnutrition";
    private const string PemmicanDef = "Pemmican";
    private const string WoodLogDef = "WoodLog";

    /// <summary>
    /// The most stockpile zones allowing human food a colony window may end with: the food stockpile, the raw-food
    /// freezer and the larder, with room for the further warehouse a full store asks for. A playtest ended its second
    /// day with about twenty.
    /// </summary>
    private const int ColonyFoodZoneLimit = 6;

    /// <summary>
    /// The food gate: the tribal8 baseline holds under two days of pemmican, so a window that ends with a colonist past
    /// hungry means the foothold food methods (butcher spot, bill, hunting, fields) did not stack in time. The report
    /// keeps every colonist's worst malnutrition either way.
    /// </summary>
    internal static async Task AuditNutritionAsync(Harness harness, Report report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentNullException.ThrowIfNull(report);
        var identity = Harness.Context(await harness.LoadedAsync("audit-nutrition-identity", cancellationToken).ConfigureAwait(false), "audit-nutrition-identity").Identity;
        var reply = await harness.CallAsync(
            "audit-nutrition",
            NativeOps.ListPawns,
            new Observations.ListPawnsRequest
            {
                Scope = TypedReads.Scope(identity),
                Filter = new Observations.PawnFilter { Colonist = true },
                Details = new Observations.PawnDetails { Health = true },
            },
            cancellationToken).ConfigureAwait(false);
        var rows = new JsonArray();
        var over = new List<string>();
        foreach (var pawn in TypedReads.Observed("audit-nutrition", NativeOps.ListPawns, reply, reply.Outcome?.Observed).Pawns)
        {
            var worst = 0.0;
            foreach (var hediff in pawn.Health?.Hediffs ?? [])
            {
                if (hediff.DefName == MalnutritionDef)
                {
                    worst = Math.Max(worst, hediff.Severity);
                }
            }

            rows.Add(new JsonObject { { "pawn", pawn.Pawn.Id }, { "malnutrition", worst } });
            if (worst > MalnutritionLimit)
            {
                over.Add(string.Create(CultureInfo.InvariantCulture, $"{pawn.Pawn.Id} {worst:F2}"));
            }
        }

        report["colonist_nutrition"] = new JsonObject { { "colonists", rows.Count }, { "limit", MalnutritionLimit }, { "rows", rows } };
        if (over.Count > 0)
        {
            throw new AcceptanceException(string.Create(
                CultureInfo.InvariantCulture,
                $"{over.Count} colonist(s) ended the window past Malnutrition {MalnutritionLimit:F1}: [{string.Join(' ', over)}]"));
        }
    }

    /// <summary>
    /// Every keep-alive resume from an acknowledged hold re-acquires in place, one native generation each, not the
    /// Manual-to-Auto pair that rebound every prepared action to a new generation. Reads the service's stop record.
    /// </summary>
    internal static void AuditReacquisitions(ColonyReacquisitions? keep)
    {
        if (keep is { GenerationsOverOne: > 0 })
        {
            throw new AcceptanceException(string.Create(
                CultureInfo.InvariantCulture,
                $"{keep.GenerationsOverOne} of {keep.Reacquired} keep-alive resumes cost more than one native generation (advance {keep.GenerationAdvance} over {keep.Attempts} attempts)"));
        }
    }

    /// <summary>
    /// Counts the stockpile zones the window ended with, those allowing human food apart, checks the starting pemmican
    /// was allowed and reads the zone churn from the service's log ring. The report keeps all three either way.
    /// </summary>
    internal static async Task AuditStockpilesAsync(Harness harness, JsonObject identity, string output, Report report, CancellationToken cancellationToken)
    {
        var scope = TypedReads.Scope(Harness.IdentityOf(identity));
        var zones = await harness.CallAsync("audit-zones", NativeOps.ListZones, new Observations.ListZonesRequest { Scope = scope, IncludeFilter = true }, cancellationToken).ConfigureAwait(false);
        var stockpiles = 0;
        var food = 0;
        var rows = new JsonArray();
        foreach (var zone in TypedReads.Observed("audit-zones", NativeOps.ListZones, zones, zones.Outcome?.Observed).Zones)
        {
            if (zone.Type != "stockpile")
            {
                continue;
            }

            stockpiles++;
            var isFood = zone.FoodStorage == true;
            if (isFood)
            {
                food++;
            }

            rows.Add(new JsonObject
            {
                { "allowed", new JsonArray([.. (zone.Filter?.AllowedDefNames ?? []).Select(d => (JsonNode?)JsonValue.Create(d))]) },
                { "id", zone.Id },
                { "label", zone.Label },
                { "priority", zone.Priority },
                { "bounds", zone.Bounds is { } bounds ? JsonNode.Parse(WireJson.Serialize(bounds)) : null },
                { "food_storage", isFood },
            });
        }

        var supplies = await harness.CallAsync(
            "audit-supplies",
            NativeOps.ListSupplies,
            new Observations.ListSuppliesRequest { Scope = scope, Filter = new Observations.StockFilter { DefNames = [PemmicanDef, WoodLogDef] } },
            cancellationToken).ConfigureAwait(false);
        var forbidden = new List<string>();
        var stocks = new JsonArray();
        foreach (var stock in TypedReads.Observed("audit-supplies", NativeOps.ListSupplies, supplies, supplies.Outcome?.Observed).Stocks)
        {
            var name = stock.Definition.DefName ?? "";
            stocks.Add(new JsonObject { { "def", name }, { "ours", stock.Ours }, { "ours_unforbidden", stock.OursUnforbidden } });

            // Wood stacks outside the base stay forbidden on purpose (admin/supply-safety, outside_base:insufficient_defense),
            // so wood is only reported.
            if (stock.Ours > stock.OursUnforbidden && name == PemmicanDef)
            {
                forbidden.Add(string.Create(CultureInfo.InvariantCulture, $"{name} {stock.Ours - stock.OursUnforbidden} of {stock.Ours} still forbidden"));
            }
        }

        var churn = ZoneChurn.Read(ServiceLogs.ServiceLogsPath(output));
        var deletes = new JsonObject();
        foreach (var (role, count) in churn.Deletes.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            deletes.Add(role, count);
        }

        report["stockpile_zones"] = new JsonObject
        {
            { "stockpiles", stockpiles },
            { "food", food },
            { "limit", ColonyFoodZoneLimit },
            { "rows", rows },
            { "starting_supplies", stocks },
            { "deletes", deletes },
            { "creates", new JsonArray([.. churn.Creates]) },
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
    /// The admitted stockpile deletes and creates of one window (<see cref="TelemetryKinds.LayoutEdit"/> rows, family
    /// stockpile): a delete follows a purpose the department declared gone, so a later create of the same role is churn.
    /// A log ring that cannot be read leaves the counts empty.
    /// </summary>
    private sealed class ZoneChurn
    {
        /// <summary>The admitted zone deletes by the role they name.</summary>
        public Dictionary<string, int> Deletes { get; } = new(StringComparer.Ordinal);

        /// <summary>The admitted zone creates in journal order with their tick and cells: the first is the earliest a store can open.</summary>
        public List<JsonNode?> Creates { get; } = [];

        /// <summary>The roles created again after a delete in the window.</summary>
        public List<string> Flagged { get; } = [];

        public static ZoneChurn Read(string logsPath)
        {
            var churn = new ZoneChurn();
            IReadOnlyList<LogRow> logs;
            try
            {
                logs = ServiceLogs.ReadLogs(logsPath);
            }
            catch (IOException)
            {
                return churn;
            }

            var deleted = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in logs)
            {
                if (row.Kind != TelemetryKinds.LayoutEdit.Name || Text(row.Payload.GetValueOrDefault("verdict")) != "admitted")
                {
                    continue;
                }

                var attrs = row.Payload.GetValueOrDefault("attrs") as IReadOnlyDictionary<string, object?>;
                var role = Text(attrs?.GetValueOrDefault("role"));
                if (Text(attrs?.GetValueOrDefault("family")) != "stockpile" || role.Length == 0)
                {
                    continue;
                }

                switch (Text(attrs?.GetValueOrDefault("kind")))
                {
                    case "delete":
                        churn.Deletes[role] = churn.Deletes.GetValueOrDefault(role) + 1;
                        deleted.Add(role);
                        break;
                    case "create":
                        churn.Creates.Add(new JsonObject
                        {
                            { "role", role },
                            { "tick", row.Tick },
                            { "cells", JsonSerializer.SerializeToNode(attrs?.GetValueOrDefault("cells")) },
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

        private static string Text(object? value) => value as string ?? "";
    }
}
