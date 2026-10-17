using System.Threading.Channels;
using RimGovernor.Brain.Bridge.Contracts;

namespace RimGovernor.Brain.BuildingRuntime;

// Go clock.go (ReadClockStatus): the clock status read, and the colony events native pushes.
public sealed partial class FacadeNativeClient
{
    /// <summary>Go <c>Client.ReadClockStatus</c>: the clock status of the identity's world; a failure or unavailable status throws.</summary>
    internal async Task<PbClock.StatusReply> ReadClockStatusAsync(PbCommon.Identity identity, CancellationToken cancellationToken)
    {
        BridgeFunctions.AuthorityIdentity(identity);
        var reply = await Facade.ReadClockStatusAsync(new PbClock.StatusRequest { Identity = identity }, cancellationToken).ConfigureAwait(false);
        FacadeWriteChecks.ClockStatusReply(reply);
        return reply;
    }

    /// <summary>Opens the colony events native pushes, empty: each carries its original context; reading one acknowledges nothing.</summary>
    internal Task<ChannelReader<RimGovernor.Brain.Bridge.ColonyEvent>> OpenColonyEventsAsync(CancellationToken cancellationToken) =>
        Facade.OpenColonyEventsAsync(cancellationToken);

    /// <summary>The colony events native dropped since the open.</summary>
    internal long ColonyEventsLost => Facade.ColonyEventsLost;
}
