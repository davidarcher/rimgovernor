using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Facts;
using C = RimGovernor.Contracts.Wire.Common;
using O = RimGovernor.Contracts.Wire.Observations;
using L = RimGovernor.Contracts.Wire.Lifecycle;
namespace RimGovernor.Brain.Bridge.Contracts;

public interface IColonySource : IObservationSource
{
    Task<L.LoadedIdentity> IdentityAsync(CancellationToken cancellationToken);

    Task<O.ColonyFactsSnapshot> ReadColonyFactsAsync(C.Identity identity, CancellationToken cancellationToken);

    /// <summary>The keyed tables the facts' references resolve against.</summary>
    Task<Tables> FrameTablesAsync(C.Identity identity, CancellationToken cancellationToken);
}

