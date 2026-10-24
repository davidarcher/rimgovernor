using RimGovernor.Brain.Bridge;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>smoke/identity</c>: the runner opens the blank lab (#731) and hands the case a loaded identity that holds under
/// it; the clock advances; the lab carries no building of another faction; the clearance read decodes for the identity.
/// </summary>
internal static class SmokeIdentity
{
    internal static TypedCase Case() => new()
    {
        Name = "smoke/identity",
        Scope = "Runner smoke: the lab opens with a loaded identity (colony id and load token) that a second "
            + "lifecycle_read_identity agrees with; the clock advances 300 ticks; observations_list_buildings with anyFaction "
            + "finds no building beyond the player's; observations_get_clearance_targets observes for that identity. A game "
            + "that loads but does not tick fails here, not in a later case.",
        Ticks = 300,
        Tier = CaseTier.Smoke,
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        if (s.Identity.ColonyId.Length == 0 || s.Identity.LoadToken.Length == 0)
        {
            throw new AcceptanceException($"the runner's identity has no colony id or load token: {s.Identity}");
        }

        var (again, before, _) = await TypedLifecycle.IdentityAsync(s, "identity-again", cancellationToken).ConfigureAwait(false);
        s.RecordWire("identity_again", again);
        if (again.LoadToken != s.Identity.LoadToken)
        {
            throw new AcceptanceException($"the load token changed under the case: {s.Identity.LoadToken} then {again.LoadToken}");
        }

        await s.RunTicksAsync("advance", 300, cancellationToken).ConfigureAwait(false);
        var (_, after, _) = await TypedLifecycle.IdentityAsync(s, "identity-advanced", cancellationToken).ConfigureAwait(false);
        if (after < before + 300)
        {
            throw new AcceptanceException($"the clock did not advance 300 ticks: {before} then {after}");
        }

        // The lab is wiped: every building on it, any faction, is the player's.
        var player = await s.BuildingsAsync("buildings-player", new Observations.ListBuildingsRequest(), cancellationToken).ConfigureAwait(false);
        var any = await s.BuildingsAsync("buildings-any", new Observations.ListBuildingsRequest { AnyFaction = true }, cancellationToken).ConfigureAwait(false);
        if (any.Count != player.Count)
        {
            throw new AcceptanceException($"the lab is not blank: {any.Count - player.Count} buildings of another faction");
        }

        var clearance = await s.CallAsync("clearance", NativeOps.ReadClearanceTargets, new Observations.ClearanceTargetsRequest { Scope = s.Scope }, cancellationToken).ConfigureAwait(false);
        s.RecordWire("clearance", clearance);
        if (clearance.Context.Identity != s.Identity)
        {
            throw new AcceptanceException($"clearance: the read is not for the observed world: {clearance.Context}");
        }
    }
}
