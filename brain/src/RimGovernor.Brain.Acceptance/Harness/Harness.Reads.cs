using System.Text.Json;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Bridge;
using RimGovernor.Contracts.Wire;
using Common = RimGovernor.Contracts.Wire.Common;
using Wl = RimGovernor.Contracts.Wire.Lifecycle;

namespace RimGovernor.Brain.Acceptance;

/// <summary>The lifecycle reads every launch and wait step makes, over the typed edge.</summary>
public sealed partial class Harness
{
    /// <summary>Reads the current game tick through lifecycle_read_identity; an unloaded game is an error.</summary>
    public async Task<ulong> TickAsync(CancellationToken cancellationToken) =>
        (ulong)Context(await LoadedAsync("tick", cancellationToken).ConfigureAwait(false), "tick").Tick;

    /// <summary>lifecycle_read_identity's <c>loaded</c> arm; any other arm fails <paramref name="label"/> with the reply.</summary>
    public async Task<Wl.LoadedIdentity> LoadedAsync(string label, CancellationToken cancellationToken)
    {
        var reply = await CallAsync(label, NativeOps.ReadIdentity, new Wl.IdentityRequest(), cancellationToken).ConfigureAwait(false);
        return reply.Outcome?.Loaded ?? throw new AcceptanceException($"{label}: {NativeOps.ReadIdentity.Name} is not loaded: {WireJson.Serialize(reply)}");
    }

    /// <summary>lifecycle_read_tick's <c>loaded</c> arm; any other arm fails <paramref name="label"/> with the reply.</summary>
    public async Task<Wl.LoadedTick> LoadedTickAsync(string label, CancellationToken cancellationToken)
    {
        var reply = await CallAsync(label, NativeOps.ReadTick, new Wl.TickRequest(), cancellationToken).ConfigureAwait(false);
        return reply.Outcome?.Loaded ?? throw new AcceptanceException($"{label}: {NativeOps.ReadTick.Name} is not loaded: {WireJson.Serialize(reply)}");
    }

    /// <summary>
    /// Saves the game as <paramref name="saveName"/> through lifecycle_save at the identity and tick <paramref name="loaded"/>
    /// observed; any outcome but <c>completed</c> fails with the reply.
    /// </summary>
    public async Task<Wl.SaveCompleted> SaveGameAsync(string label, Wl.LoadedIdentity loaded, string saveName, string requestId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        var request = new Wl.SaveRequest
        {
            Player = new() { Identity = loaded.Context?.Identity, PlayerDirection = 1, RequestId = requestId },
            SaveName = saveName,
            ExpectedTick = loaded.Context?.Tick ?? -1,
        };
        var reply = await CallAsync(label, NativeOps.SaveGame, request, cancellationToken).ConfigureAwait(false);
        return reply.Outcome?.Completed ?? throw new AcceptanceException($"{NativeOps.SaveGame.Name} did not complete: {WireJson.Serialize(reply)}");
    }

    /// <summary>A loaded reply's observation context; a reply without one fails <paramref name="label"/>.</summary>
    public static Common.ObservationContext Context(Wl.LoadedIdentity loaded, string label)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        return loaded.Context ?? throw new AcceptanceException($"{label}: {NativeOps.ReadIdentity.Name} loaded without a context");
    }

    /// <summary>
    /// The JSON document of <paramref name="identity"/>, as the session, reuse state and service hold it (an identity has
    /// no choice fields, so this is its wire form).
    /// </summary>
    public static JsonObject IdentityNode(Common.Identity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return JsonNode.Parse(WireJson.Serialize(identity))!.AsObject();
    }

    /// <summary>The Contracts identity of an <see cref="IdentityNode"/> document.</summary>
    public static Common.Identity IdentityOf(JsonNode identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        try
        {
            return WireJson.Deserialize<Common.Identity>(identity.ToJsonString());
        }
        catch (JsonException e)
        {
            throw new AcceptanceException($"identity {identity.ToJsonString()} is not a wire identity: {e.Message}", e);
        }
    }
}
