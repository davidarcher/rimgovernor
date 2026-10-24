namespace RimGovernor.Brain.BuildingRuntime;

public sealed partial class Session : IPlayerMapFocusConfirmation
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
        var status = reply;
        Control.ControlStatus(reply, target);
        if ((status.Context?.NativeGeneration).GetValueOrDefault() != generation.Value || status.State?.Inactive?.Reason != PbAuthority.RevocationReason.IdentityChanged)
        {
            throw new ControlException();
        }
        ct.ThrowIfCancellationRequested();
    }
}
