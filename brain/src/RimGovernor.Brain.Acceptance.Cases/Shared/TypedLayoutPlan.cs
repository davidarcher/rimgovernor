using System.Text.Json.Nodes;
using RimGovernor.Brain.NativeFacade;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store.Codec;
using Lifecycle = RimGovernor.Contracts.Wire.Lifecycle;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// Stages the governor's layout plan in the save-owned governor state, so a served case starts from a plan describing
/// room geometry that already stands instead of playing a layout review into it.
/// </summary>
internal static class TypedLayoutPlan
{
    /// <summary>Replaces the save's layout plan blob with <paramref name="plan"/> (the service reads it at start), keeping every other blob.</summary>
    public static async Task InjectAsync(ITypedSession s, LayoutPlan plan, CancellationToken cancellationToken)
    {
        // The batch put replaces the whole set, so read the save's blobs and add the plan to them.
        var read = await s.CallAsync("read-governor-state", NativeOps.ReadGovernorState, new Lifecycle.GovernorStateRequest(), cancellationToken).ConfigureAwait(false);
        var loaded = read.Outcome?.Loaded ?? throw new AcceptanceException($"read-governor-state: not loaded: {read.Outcome}");
        var blobs = new Dictionary<string, string>(loaded.Blobs, StringComparer.Ordinal)
        {
            [Store.Store.GovernorLayoutPlanKey] = ServedOps.Args(
                ("schemaVersion", Store.Store.GovernorStateSchemaVersion),
                ("scope", ServedOps.Args(("colony", s.Identity.ColonyId), ("map", s.Identity.MapId), ("tick", 0))),
                ("record", JsonNode.Parse(StoreJson.Serialize(plan)))).ToJsonString(),
        };
        var put = await s.CallAsync("put-governor-state", NativeOps.PutGovernorStateBatch, new Lifecycle.PutGovernorStateBatchRequest { Blobs = blobs }, cancellationToken).ConfigureAwait(false);
        if (put.Outcome?.Loaded is null)
        {
            throw new AcceptanceException($"put-governor-state: not loaded: {put.Outcome}");
        }
    }
}
