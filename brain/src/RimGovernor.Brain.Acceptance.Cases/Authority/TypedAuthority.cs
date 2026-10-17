using RimGovernor.Brain.NativeFacade;
using Control = RimGovernor.Contracts.Wire.Authority;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>Typed authority steps a <see cref="TypedCase"/> takes before it applies actions itself.</summary>
internal static class TypedAuthority
{
    /// <summary>SetMode(Auto) at the generation the status reads: operations_apply admits a controller's actions only.</summary>
    internal static async Task GrantAutoAsync(ITypedSession s, CancellationToken cancellationToken)
    {
        var status = await s.CallAsync("acquire-status", NativeOps.ReadAuthorityStatus, new Control.StatusRequest { Identity = s.Identity }, cancellationToken).ConfigureAwait(false);
        var generation = status.Outcome?.Status?.Context?.NativeGeneration
            ?? throw new AcceptanceException($"acquire-status: no native generation: {status.Outcome}");
        var control = await s.CallAsync(
            "acquire",
            NativeOps.ControlAuthority,
            new Control.ControlRequest { Operation = new Control.SetMode { Identity = s.Identity, ExpectedGeneration = generation, Mode = Control.Mode.Auto } },
            cancellationToken).ConfigureAwait(false);
        if (control.Outcome?.Granted?.Authority?.Mode != Control.Mode.Auto)
        {
            throw new AcceptanceException($"acquire: expected MODE_AUTO, got {control.Outcome}");
        }
    }
}
