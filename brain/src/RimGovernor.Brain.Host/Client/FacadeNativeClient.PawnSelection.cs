using RimGovernor.Brain.Bridge.Contracts;

namespace RimGovernor.Brain.Host;

internal sealed partial class FacadeNativeClient
{
    /// <summary>Go <c>pawnDetails</c>: the PawnDetails families one read asks for.</summary>
    internal readonly record struct PawnDetailSelection(bool Combat = false, bool Work = false, bool Care = false, bool Schedule = false, bool Social = false, bool Tend = false);

    /// <summary>Go <c>Client.ReadClockStatus</c> as <c>ReadStep</c> uses it: the owned clock status.</summary>
    private async Task<PbClock.Status> ReadOwnedClockStatusAsync(PbCommon.Identity identity, CancellationToken cancellationToken)
    {
        var (reply, _) = await ReadClockStatusAsync(identity, cancellationToken).ConfigureAwait(false);
        return FacadeWriteChecks.ClockStatusReply(reply, identity);
    }

    /// <summary>A pawn snapshot must describe the world it was asked about: a stale one is refused.</summary>
    internal static void SameWorld(PbObservations.PawnSnapshot? v, PbCommon.Identity? id)
    {
        if (v is null || !BridgeFunctions.SameIdentity(v.Context?.Identity, id))
        {
            throw BridgeFunctions.Contract("pawn world mismatch");
        }
    }
}
