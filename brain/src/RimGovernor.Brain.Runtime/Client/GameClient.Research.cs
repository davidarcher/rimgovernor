namespace RimGovernor.Brain.Runtime;

// Client.ReadResearch.
public sealed partial class GameClient
{
    /// <summary>The native research snapshot: the current projects, every started or knowledge project with its lock reasons, and the finished list.</summary>
    internal Task<RimGovernor.Brain.GameFacts.Observations.ResearchSnapshot> ReadResearchAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, CancellationToken cancellationToken) =>
        Facade.ReadResearchAsync(new RimGovernor.Brain.GameOrders.Observations.ResearchRequest { Scope = new RimGovernor.Brain.GameFacts.Observations.ReadScope { ExpectedIdentity = identity }, ProgressOnly = true }, cancellationToken);
}
