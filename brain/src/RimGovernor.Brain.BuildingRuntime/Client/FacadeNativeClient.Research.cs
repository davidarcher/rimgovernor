namespace RimGovernor.Brain.BuildingRuntime;

// Client.ReadResearch.
public sealed partial class FacadeNativeClient
{
    /// <summary>The native research snapshot: the current projects, every started or knowledge project with its lock reasons, and the finished list.</summary>
    internal Task<PbObservations.ResearchSnapshot> ReadResearchAsync(PbCommon.Identity identity, CancellationToken cancellationToken) =>
        Facade.ReadResearchAsync(new PbObservations.ResearchRequest { Scope = new PbObservations.ReadScope { ExpectedIdentity = identity }, ProgressOnly = true }, cancellationToken);
}
