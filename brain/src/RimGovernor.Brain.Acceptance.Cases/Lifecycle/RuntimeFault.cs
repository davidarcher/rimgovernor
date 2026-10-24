using System.Text.Json.Nodes;
using RimGovernor.Brain.Bridge;
using Clock = RimGovernor.Contracts.Wire.Clock;
using Control = RimGovernor.Contracts.Wire.Authority;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// <c>lifecycle/runtime-fault</c>: a required authority invalidation hook that goes missing at runtime invalidates
/// authority as HOOKS_UNAVAILABLE at the next generation, and the next admission reinstalls it without a restart.
/// The fault is injected by <c>test/runtime_fault_unpatch</c>; the recovery is production.
/// </summary>
internal static class LifecycleRuntimeFault
{
    private const string Tool = "test/runtime_fault_unpatch";

    /// <summary>The required authority hook this case removes.</summary>
    private const string Hook = "Pawn_DraftController.Drafted";

    private static readonly FixtureOp<string, Fx.RuntimeFaultReply> Unpatch = new(
        Tool,
        hook => new JsonObject([new KeyValuePair<string, JsonNode?>("hook", hook)]),
        (label, reply) => reply is not null && Fx.FixtureJson.Deserialize<Fx.RuntimeFaultReply>(reply.ToJsonString()) is { Success: true, Removed: true } read
            ? read
            : throw new AcceptanceException($"{label}: {Tool} refused: {reply?.ToJsonString()}"));

    internal static TypedCase Case() => new()
    {
        Name = "lifecycle/runtime-fault",
        Scope = "A required authority hook removed at runtime (test/runtime_fault_unpatch): the game's own update poll "
            + "invalidates authority as Inactive(HOOKS_UNAVAILABLE) at generation+1, and a SetMode(Auto) at that generation "
            + "reinstalls the hook and is granted. Harmony hook health lives in the game process. NoKeep: the fixture changes "
            + "process-scoped hook state no later case should inherit.",
        Ticks = 600,
        Tier = CaseTier.Nightly,
        Ops = [Tool],
        NoKeep = true,
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        // A clock status read installs the clock watchers, as a controller's first call does; the authority seam publishes
        // journal rows only once they are.
        _ = await s.CallAsync("clock-status", NativeOps.ReadClockStatus, new Clock.StatusRequest { Identity = s.Identity }, cancellationToken).ConfigureAwait(false);
        var expected = await TypedAuthority.ExpectedGenerationAsync(s, "status-fresh", s.Identity, cancellationToken).ConfigureAwait(false);
        var granted = await TypedAuthority.GrantAsync(s, "grant", s.Identity, expected, cancellationToken).ConfigureAwait(false);

        // The fixture reads the hook count on the game thread the instant after removal.
        var unpatched = await s.FixtureAsync("unpatch", Unpatch, Hook, cancellationToken).ConfigureAwait(false);
        s.RecordFixture("unpatched", unpatched);
        if (unpatched.Required < 1 || unpatched.Health != unpatched.Required - 1)
        {
            throw new AcceptanceException($"unpatch: expected {unpatched.Required - 1}/{unpatched.Required} hooks right after removal, got {unpatched}");
        }

        // The game's update poll invalidates authority on its own; a hooks-not-ready status may also read unavailable.
        Control.Status broken = null!;
        _ = await s.RunUntilAsync(
            "status-broken",
            600,
            async ct =>
            {
                broken = await TypedAuthority.StatusAsync(s, "status-broken", s.Identity, ct).ConfigureAwait(false);
                return (Wait.Signature(broken.State), broken.State?.Active is null);
            },
            cancellationToken).ConfigureAwait(false);
        s.RecordWire("status_broken", broken);
        if (broken.State?.Inactive is { } inactive && inactive.Reason != Control.RevocationReason.HooksUnavailable)
        {
            throw new AcceptanceException($"status-broken: expected HOOKS_UNAVAILABLE, got {broken}");
        }

        var after = (broken.Context ?? throw new AcceptanceException($"status-broken: no context: {broken}")).NativeGeneration;
        if (!after.IsAt(granted + 1))
        {
            throw new AcceptanceException($"status-broken: expected generation {granted + 1}, got {broken}");
        }

        // The next admission reinstalls the hook and is granted.
        var regranted = await TypedAuthority.GrantAsync(s, "regrant", s.Identity, granted + 1, cancellationToken).ConfigureAwait(false);
        if (regranted != granted + 2)
        {
            throw new AcceptanceException($"regrant: expected generation {granted + 2}, got {regranted}");
        }
    }
}
