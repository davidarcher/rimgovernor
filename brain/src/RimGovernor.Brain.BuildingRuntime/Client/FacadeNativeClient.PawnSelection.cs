using RimGovernor.Brain.Bridge.Contracts;

namespace RimGovernor.Brain.BuildingRuntime;

internal sealed partial class FacadeNativeClient
{
    /// <summary>Go <c>pawnDetails</c>: the PawnDetails families one read asks for.</summary>
    internal readonly record struct PawnDetailSelection(bool Combat = false, bool Work = false, bool Care = false, bool Schedule = false, bool Social = false, bool Tend = false);

    /// <summary>Go <c>Client.ReadClockStatus</c> as <c>ReadStep</c> uses it: the owned clock status.</summary>
    private async Task<PbClock.Status> ReadOwnedClockStatusAsync(PbCommon.Identity identity, CancellationToken cancellationToken)
    {
        var reply = await ReadClockStatusAsync(identity, cancellationToken).ConfigureAwait(false);
        return FacadeWriteChecks.ClockStatusReply(reply);
    }
}
