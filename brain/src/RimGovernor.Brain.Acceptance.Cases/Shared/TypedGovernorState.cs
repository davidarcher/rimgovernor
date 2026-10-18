using System.Text.Json.Nodes;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Store.Codec;
using Lifecycle = RimGovernor.Contracts.Wire.Lifecycle;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// Stages one family record in the save-owned governor state, so a served case starts from state the player or an
/// earlier session would have left (a layout plan, the player's goals) instead of playing it in.
/// </summary>
internal static class TypedGovernorState
{
    /// <summary>
    /// Replaces the save's blob <paramref name="key"/> with <paramref name="record"/> (the service reads the blobs at
    /// start), keeping every other blob. A <paramref name="scoped"/> blob carries the loaded colony and map; a
    /// colony-scoped record names its own world.
    /// </summary>
    public static async Task InjectAsync<T>(TypedSession s, string key, T record, bool scoped, CancellationToken cancellationToken)
    {
        // The batch put replaces the whole set, so read the save's blobs and add the record to them.
        var read = await s.CallAsync("read-governor-state", NativeOps.ReadGovernorState, new Lifecycle.GovernorStateRequest(), cancellationToken).ConfigureAwait(false);
        var loaded = read.Loaded ?? throw new AcceptanceException("read-governor-state: not loaded");
        var fields = new List<(string Key, JsonNode? Value)> { ("schemaVersion", Store.Store.GovernorStateSchemaVersion) };
        if (scoped)
        {
            fields.Add(("scope", ServedOps.Args(("colony", s.Identity.ColonyId), ("map", s.Identity.MapId), ("tick", 0))));
        }

        fields.Add(("record", JsonNode.Parse(StoreJson.Serialize(record))));
        var blobs = new Dictionary<string, string>(loaded.Blobs, StringComparer.Ordinal) { [key] = ServedOps.Args([.. fields]).ToJsonString() };
        var put = await s.CallAsync("put-governor-state", NativeOps.PutGovernorStateBatch, new Lifecycle.PutGovernorStateBatchRequest { Blobs = blobs }, cancellationToken).ConfigureAwait(false);
        if (put.Loaded is null)
        {
            throw new AcceptanceException("put-governor-state: not loaded");
        }
    }
}
