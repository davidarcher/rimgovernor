using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Facts;
namespace RimGovernor.Brain.GameAccess;

public interface IRefrigerationSource
{
    Task<RimGovernor.Brain.GameFacts.Observations.BuildingsSnapshot> ReadConstructionBuildingsAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, IReadOnlyList<string> ids, CancellationToken cancellationToken);
}

