using System.Text.Json.Nodes;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store.Codec;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The harness side of the mod's read-only acceptance tools (<c>rimgovernor/acceptance_read_*</c>, registered by the mod
/// from <c>AcceptanceReads</c>): the decoded colony facts, first layout plan and the
/// Impressiveness catalog stages exist only inside the game process, so each seam calls its tool through the generic native route and
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

    /// <summary>Plans the first layout from the mod's read: the plan in the store codec and the map bounds.</summary>
    internal sealed class StarterSiteReader : IStarterSitePlanner
    {
        public async Task<StarterSitePlan> PlanAsync(Harness harness, CancellationToken cancellationToken)
        {
            var read = await ReadAsync(harness, "starter-site-plan", "starter_site", null, cancellationToken).ConfigureAwait(false);
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
