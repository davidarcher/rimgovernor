using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Facts;
using C = RimGovernor.Contracts.Wire.Common;
using O = RimGovernor.Contracts.Wire.Observations;
using L = RimGovernor.Contracts.Wire.Lifecycle;
namespace RimGovernor.Brain.Bridge.Contracts;

public interface IRefrigerationSource
{
    Task<O.BuildingsSnapshot> ReadConstructionBuildingsAsync(C.Identity identity, IReadOnlyList<string> ids, CancellationToken cancellationToken);
}

