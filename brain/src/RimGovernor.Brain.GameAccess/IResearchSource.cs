using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Facts;
namespace RimGovernor.Brain.GameAccess;

public interface IResearchSource
{
    Task<RimGovernor.Brain.GameFacts.Observations.ResearchSnapshot> ReadResearchAsync(RimGovernor.Brain.GameFacts.Common.Identity id, CancellationToken cancellationToken);
}

