using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Facts;
namespace RimGovernor.Brain.GameAccess;

public interface IZonesNative
{
    Task<RimGovernor.Brain.GameFacts.Observations.ZonesSnapshot> ReadZoneSectionAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, CancellationToken cancellationToken);
}

public interface IZonesSource
{
    Task<Held<RimGovernor.Brain.GameFacts.Observations.ZonesSnapshot>> ZonesAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, CancellationToken cancellationToken);
}

