using System.Threading.Channels;
using RimGovernor.Brain.GameAccess;

namespace RimGovernor.Brain.Runtime;

// The clock status read, and the colony events native pushes.
public sealed partial class GameClient
{
    /// <summary>The clock status of the identity's world; a failure or unavailable status throws.</summary>
    internal async Task<RimGovernor.Brain.GameFacts.Clock.Status> ReadClockStatusAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, CancellationToken cancellationToken) =>
        await Facade.ReadClockStatusAsync(new RimGovernor.Brain.GameOrders.Clock.StatusRequest { Identity = identity }, cancellationToken).ConfigureAwait(false);

    /// <summary>Opens the colony events native pushes, empty: each carries its original context; reading one acknowledges nothing.</summary>
    internal Task<ChannelReader<RimGovernor.Brain.Bridge.ColonyEvent>> OpenColonyEventsAsync(CancellationToken cancellationToken) =>
        Facade.OpenColonyEventsAsync(cancellationToken);

    /// <summary>The colony events native dropped since the open.</summary>
    internal long ColonyEventsLost => Facade.ColonyEventsLost;
}
