using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Facts;
namespace RimGovernor.Brain.GameAccess;

public interface IColonySource : IObservationSource
{
    Task<RimGovernor.Brain.GameFacts.Lifecycle.LoadedIdentity> IdentityAsync(CancellationToken cancellationToken);

    Task<RimGovernor.Brain.GameFacts.Observations.ColonyFactsSnapshot> ReadColonyFactsAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, CancellationToken cancellationToken);

    /// <summary>The keyed tables the facts' references resolve against.</summary>
    Task<Tables> FrameTablesAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, CancellationToken cancellationToken);
}

