using System.Text.Json.Nodes;
using RimGovernor.Brain.NativeFacade;
using RimGovernor.Contracts.Wire;
using Wa = RimGovernor.Contracts.Wire.Authority;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// The authority release a native-driven harness makes after killing its service (Go <c>authority.go</c>): authority
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
        var reply = await harness.CallAsync("release-authority-status", NativeOps.ReadAuthorityStatus, new Wa.StatusRequest { Identity = wire }, cancellationToken).ConfigureAwait(false);
        var status = reply.Outcome?.Status ?? throw new AcceptanceException($"authority status: {WireJson.Serialize(reply)}");
        if (status.State?.Active is null)
        {
            return null;
        }

        var revoke = new Wa.Revoke { Identity = wire, ExpectedGeneration = status.Context?.NativeGeneration, Reason = Wa.RevocationReason.Manual };
        var control = await harness.CallAsync("release-authority", NativeOps.ControlAuthority, new Wa.ControlRequest { Operation = revoke }, cancellationToken).ConfigureAwait(false);
        var revoked = control.Outcome?.Revoked ?? throw new AcceptanceException($"release-authority: {WireJson.Serialize(control)}");
        return JsonNode.Parse(WireJson.Serialize(revoked))!.AsObject();
    }
}
