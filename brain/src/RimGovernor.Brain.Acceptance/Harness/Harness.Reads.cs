using System.Text.Json;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;

namespace RimGovernor.Brain.Acceptance;

/// <summary>The lifecycle reads every launch and wait step makes, over the typed edge.</summary>
public sealed partial class Harness
{
    /// <summary>Reads the current game tick through lifecycle_read_identity; an unloaded game is an error.</summary>
    public async Task<ulong> TickAsync(CancellationToken cancellationToken) =>
        (ulong)Context(await LoadedAsync("tick", cancellationToken).ConfigureAwait(false), "tick").Tick;

    /// <summary>lifecycle_read_identity's loaded identity; an unavailable game fails <paramref name="label"/> with its reason.</summary>
    public async Task<RimGovernor.Brain.GameFacts.Lifecycle.LoadedIdentity> LoadedAsync(string label, CancellationToken cancellationToken)
    {
        try
        {
            return await CallAsync(label, NativeOps.ReadIdentity, new RimGovernor.Brain.GameOrders.Lifecycle.IdentityRequest(), cancellationToken).ConfigureAwait(false);
        }
        catch (BridgeUnavailableException e)
        {
            throw new AcceptanceException($"{label}: {NativeOps.ReadIdentity.Name} is not loaded: {e.Message}", e);
        }
    }

    /// <summary>lifecycle_read_tick's loaded tick; an unavailable game fails <paramref name="label"/> with its reason.</summary>
    public async Task<RimGovernor.Brain.GameFacts.Lifecycle.LoadedTick> LoadedTickAsync(string label, CancellationToken cancellationToken)
    {
        try
        {
            return await CallAsync(label, NativeOps.ReadTick, new RimGovernor.Brain.GameOrders.Lifecycle.TickRequest(), cancellationToken).ConfigureAwait(false);
        }
        catch (BridgeUnavailableException e)
        {
            throw new AcceptanceException($"{label}: {NativeOps.ReadTick.Name} is not loaded: {e.Message}", e);
        }
    }

    /// <summary>
    /// Saves the game as <paramref name="saveName"/> through lifecycle_save at the identity and tick <paramref name="loaded"/>
    /// observed; any outcome but <c>completed</c> fails with the reply.
    /// </summary>
    public async Task<RimGovernor.Brain.GameFacts.Lifecycle.SaveCompleted> SaveGameAsync(string label, RimGovernor.Brain.GameFacts.Lifecycle.LoadedIdentity loaded, string saveName, string requestId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        var request = new RimGovernor.Brain.GameOrders.Lifecycle.SaveRequest
        {
            Player = new() { Identity = loaded.Context?.Identity, PlayerDirection = 1, RequestId = requestId },
            SaveName = saveName,
            ExpectedTick = new RimGovernor.Brain.GameOrders.Lifecycle.ExpectedSaveTick.AtCase((loaded.Context ?? throw new AcceptanceException($"{label}: the loaded identity carried no context")).Tick),
        };
        var reply = await CallAsync(label, NativeOps.SaveGame, request, cancellationToken).ConfigureAwait(false);
        return reply.Outcome?.Completed ?? throw new AcceptanceException($"{NativeOps.SaveGame.Name} did not complete: {OrdersJson.Serialize(reply)}");
    }

    /// <summary>A loaded reply's observation context; a reply without one fails <paramref name="label"/>.</summary>
    public static RimGovernor.Brain.GameFacts.Common.ObservationContext Context(RimGovernor.Brain.GameFacts.Lifecycle.LoadedIdentity loaded, string label)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        return loaded.Context ?? throw new AcceptanceException($"{label}: {NativeOps.ReadIdentity.Name} loaded without a context");
    }

    /// <summary>
    /// The JSON document of <paramref name="identity"/>, as the session, reuse state and service hold it (an identity has
    /// no choice fields, so this is its wire form).
    /// </summary>
    public static JsonObject IdentityNode(RimGovernor.Brain.GameFacts.Common.Identity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return JsonNode.Parse(OrdersJson.Serialize(identity))!.AsObject();
    }

    /// <summary>The Contracts identity of an <see cref="IdentityNode"/> document.</summary>
    public static RimGovernor.Brain.GameFacts.Common.Identity IdentityOf(JsonNode identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        try
        {
            return OrdersJson.Deserialize<RimGovernor.Brain.GameFacts.Common.Identity>(identity.ToJsonString());
        }
        catch (JsonException e)
        {
            throw new AcceptanceException($"identity {identity.ToJsonString()} is not a wire identity: {e.Message}", e);
        }
    }
}
