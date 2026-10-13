namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>The native map-focus capability (Go <c>mapFocusNative</c>).</summary>
public interface IMapFocusNative
{
    Task<PbAuthority.ControlReply> FocusMapAsync(PbAuthority.FocusMap request, CancellationToken ct);
}

public sealed partial class Session : IPlayerMapFocus, IPlayerMapFocusConfirmation
{
    /// <summary>
    /// Proves vanilla changed the view while the prior Auto grant was live. Another native interruption cannot
    /// borrow that permission.
    /// </summary>
    public async Task ConfirmMapFocusAsync(GenerationSnapshot before, GenerationSnapshot target, NativeGeneration generation, CancellationToken ct)
    {
        if (generation.Value != before.Native.Value + 1)
        {
            throw new ControlException();
        }
        await _control.Config.StopWrites(ct).ConfigureAwait(false);
        var reply = await _control.Native.ReadAuthorityAsync(Control.ControlIdentity(target), ct).ConfigureAwait(false);
        var status = reply.Status;
        Control.ControlStatus(reply, target);
        if (status.Context.NativeGeneration != generation.Value || status.Inactive?.Reason != PbAuthority.RevocationReason.IdentityChanged)
        {
            throw new ControlException();
        }
        ct.ThrowIfCancellationRequested();
    }

    public async Task<NativeGeneration> FocusMapAsync(GenerationSnapshot before, GenerationSnapshot target, CancellationToken ct)
    {
        var c = _control;
        if (c.Native is not IMapFocusNative native)
        {
            throw new ControlException();
        }
        await c.Config.StopWrites(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var reply = await native.FocusMapAsync(new PbAuthority.FocusMap { Identity = Control.ControlIdentity(before), Target = Control.ControlIdentity(target), ExpectedGeneration = before.Native.Value }, ct).ConfigureAwait(false);
        return new NativeGeneration(reply.FocusedMap?.Context?.NativeGeneration ?? 0);
    }
}
