using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Facts;
using C = RimGovernor.Contracts.Wire.Common;
using O = RimGovernor.Contracts.Wire.Observations;
using L = RimGovernor.Contracts.Wire.Lifecycle;
namespace RimGovernor.Brain.Bridge.Contracts;

public interface IZonesNative
{
    Task<O.ZonesSnapshot> ReadZoneSectionAsync(C.Identity identity, CancellationToken cancellationToken);
}

public interface IZonesSource
{
    Task<Held<O.ZonesSnapshot>> ZonesAsync(C.Identity identity, CancellationToken cancellationToken);
}

