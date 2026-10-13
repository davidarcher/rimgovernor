using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// The <c>wall/*</c> acceptance cases (Go <c>cases/wall</c>): deconstruction, clearance and wall-replacement
/// behaviours that live in the native handlers. <see cref="Register"/> replaces the Go package's <c>init()</c>.
/// </summary>
public static class WallCases
{
    /// <summary>Registers every <c>wall/*</c> case.</summary>
    public static void Register(CaseRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(WallAdoption.Case());
        registry.Register(WallDeconstruct.Case());
    }

    /// <summary>The enclosure-guarded DECONSTRUCT Designate arm of one building, with extra DesignateIntent fields (replaceWithWall).</summary>
    internal static JsonObject DeconstructIntent(string key, JsonNode? target, JsonObject? extra)
    {
        var intent = new JsonObject
        {
            ["designation"] = "THING_DESIGNATION_DECONSTRUCT",
            ["target"] = new JsonObject { ["id"] = target?.DeepClone() },
            ["guard"] = "DESIGNATION_GUARD_ENCLOSURE",
        };
        foreach (var (name, value) in extra ?? [])
        {
            intent[name] = value?.DeepClone();
        }

        return new JsonObject { ["key"] = key, ["designate"] = intent };
    }

    /// <summary>Sends one DECONSTRUCT Designate under <paramref name="key"/> and returns its ActionResult; a refusal or failure throws.</summary>
    internal static async Task<JsonObject> ApplyDeconstructAsync(ISession s, string key, JsonNode? target, CancellationToken cancellationToken)
    {
        var reply = await s.Harness.WireAsync(
            key,
            "operations_apply",
            new JsonObject { ["identity"] = s.Identity.DeepClone(), ["actions"] = new JsonArray(DeconstructIntent(key, target, null)) },
            cancellationToken).ConfigureAwait(false);
        var results = reply["results"].AsSlice();
        if (results.Count != 1)
        {
            throw new AcceptanceException($"{key}: expected one result: {reply.ToJson()}");
        }

        var result = results[0].AsMap() ?? [];
        return result["applied"].AsMap() is null ? throw new AcceptanceException($"{key}: not applied: {result.ToJson()}") : result;
    }
}
