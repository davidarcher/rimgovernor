using RimGovernor.Brain.Bridge;
using Clock = RimGovernor.Contracts.Wire.Clock;
using Common = RimGovernor.Contracts.Wire.Common;
using Control = RimGovernor.Contracts.Wire.Authority;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>Typed authority steps a <see cref="TypedCase"/> takes before it applies actions itself.</summary>
internal static class TypedAuthority
{
    /// <summary>SetMode(Auto) at the generation the status reads: operations_apply admits a controller's actions only.</summary>
    internal static async Task GrantAutoAsync(TypedSession s, CancellationToken cancellationToken) =>
        _ = await GrantAutoGenerationAsync(s, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// <see cref="GrantAutoAsync"/>, returning the granted native generation: the generation a clock_run is admitted against.
    /// </summary>
    internal static async Task<ulong> GrantAutoGenerationAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var status = await StatusAsync(s, "acquire-status", s.Identity, cancellationToken).ConfigureAwait(false);
        var generation = status.Context?.NativeGeneration
            ?? throw new AcceptanceException($"acquire-status: no native generation: {status}");
        return await GrantAsync(s, "acquire", s.Identity, generation, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The authority status <c>authority_read_status</c> reports for <paramref name="identity"/>; a refusal fails <paramref name="label"/>.</summary>
    internal static async Task<Control.Status> StatusAsync(TypedSession s, string label, Common.Identity identity, CancellationToken cancellationToken)
    {
        var reply = await s.CallAsync(label, NativeOps.ReadAuthorityStatus, new Control.StatusRequest { Identity = identity }, cancellationToken).ConfigureAwait(false);
        return reply.Outcome?.Status ?? throw new AcceptanceException($"{label}: expected a status, got {reply.Outcome}");
    }

    /// <summary>The clock status <c>clock_read_status</c> reports for <paramref name="identity"/>; a refusal fails <paramref name="label"/>.</summary>
    internal static async Task<Clock.Status> ClockStatusAsync(TypedSession s, string label, Common.Identity identity, CancellationToken cancellationToken)
    {
        var reply = await s.CallAsync(label, NativeOps.ReadClockStatus, new Clock.StatusRequest { Identity = identity }, cancellationToken).ConfigureAwait(false);
        return reply.Outcome?.Status ?? throw new AcceptanceException($"{label}: expected a clock status, got {reply.Outcome}");
    }

    /// <summary>
    /// The generation a fresh SetMode expects: the status's native generation, or 1 when authority is not yet initialized
    /// (the game's own update poll initializes it; a read before that is unavailable, and the first generation is 1).
    /// </summary>
    internal static async Task<ulong> ExpectedGenerationAsync(TypedSession s, string label, Common.Identity identity, CancellationToken cancellationToken)
    {
        var status = await StatusAsync(s, label, identity, cancellationToken).ConfigureAwait(false);
        return status.State?.Unavailable is not null ? 1 : status.Context?.NativeGeneration ?? throw new AcceptanceException($"{label}: no native generation: {status}");
    }

    /// <summary>SetMode(Auto) for <paramref name="identity"/> at <paramref name="expected"/>; returns the granted generation.</summary>
    internal static async Task<ulong> GrantAsync(TypedSession s, string label, Common.Identity identity, ulong expected, CancellationToken cancellationToken)
    {
        var control = await s.CallAsync(
            label,
            NativeOps.ControlAuthority,
            new Control.ControlRequest { Operation = new Control.SetMode { Identity = identity, ExpectedGeneration = expected, Mode = Control.Mode.Auto } },
            cancellationToken).ConfigureAwait(false);
        var granted = control.Outcome?.Granted;
        if (granted?.Authority?.Mode != Control.Mode.Auto)
        {
            throw new AcceptanceException($"{label}: expected MODE_AUTO, got {control.Outcome}");
        }

        return granted.Context?.NativeGeneration ?? throw new AcceptanceException($"{label}: no granted generation: {granted}");
    }

    /// <summary>Revokes the grant manually at the generation the status reads: the supervisor releases the work it owns.</summary>
    internal static async Task RevokeManualAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var status = await s.CallAsync("release-status", NativeOps.ReadAuthorityStatus, new Control.StatusRequest { Identity = s.Identity }, cancellationToken).ConfigureAwait(false);
        var generation = status.Outcome?.Status?.Context?.NativeGeneration
            ?? throw new AcceptanceException($"release-status: no native generation: {status.Outcome}");
        var control = await s.CallAsync(
            "release",
            NativeOps.ControlAuthority,
            new Control.ControlRequest { Operation = new Control.Revoke { Identity = s.Identity, ExpectedGeneration = generation, Reason = Control.RevocationReason.Manual } },
            cancellationToken).ConfigureAwait(false);
        if (control.Outcome?.Revoked is null)
        {
            throw new AcceptanceException($"release: expected revoked, got {control.Outcome}");
        }
    }
}
