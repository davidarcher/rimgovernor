using System.Text.Json.Nodes;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The authority release a native-driven harness makes after killing its service: authority
/// uses SetMode(Auto|Manual) and Revoke with native-generation continuity and has no acquire/renew lease.
/// </summary>
public static class Authority
{
    /// <summary>
    /// Revokes an active authority grant at the generation the game reports now (a keep-alive may have re-acquired
    /// since the first grant). Returns the revoked body, or null when nothing was active. A harness that kills its
    /// service rather than shutting it down uses this before a reuse EndCase, which otherwise retires the game over the
    /// lingering grant.
    /// </summary>
    public static async Task<JsonObject?> ReleaseAuthorityAsync(Harness harness, JsonObject identity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentNullException.ThrowIfNull(identity);
        var wire = Harness.IdentityOf(identity);
        var status = await harness.CallAsync("release-authority-status", NativeOps.ReadAuthorityStatus, new RimGovernor.Brain.GameOrders.Authority.StatusRequest { Identity = wire }, cancellationToken).ConfigureAwait(false);
        if (status.State?.Active is null)
        {
            return null;
        }

        var revoke = new RimGovernor.Brain.GameOrders.Authority.Revoke { Identity = wire, ExpectedGeneration = status.Context?.NativeGeneration is RimGovernor.Brain.GameFacts.Common.ObservedGeneration.AtCase { Value: var generation } ? generation : throw new AcceptanceException("release-authority: the status named no generation"), Reason = RimGovernor.Brain.GameFacts.Authority.RevocationReason.Manual };
        var control = await harness.CallAsync("release-authority", NativeOps.ControlAuthority, new RimGovernor.Brain.GameOrders.Authority.ControlRequest { Operation = revoke }, cancellationToken).ConfigureAwait(false);
        var revoked = control.Outcome?.Revoked ?? throw new AcceptanceException($"release-authority: {OrdersJson.Serialize(control)}");
        return JsonNode.Parse(OrdersJson.Serialize(revoked))!.AsObject();
    }
}
