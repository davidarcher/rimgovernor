using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Runtime;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Bridge;

namespace RimGovernor.Brain.Runtime;

// The rounds sources that stay interfaces (a test fake or a second implementer satisfies them): see GameClient.Interfaces.cs.
public sealed partial class GameClient
{
    /// <summary>The recipe catalog the gear bench census reads with: the load's definition catalog, read after the bills reply validates.</summary>
    internal Task<IReadOnlyList<GearBenchRead>> ReadGearBenchesAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, CancellationToken ct) =>
        ReadGearBenchesAsync(identity, async token => new GearRecipeCatalogAdapter(await DefinitionCatalogAsync(identity, token).ConfigureAwait(false)), ct);

    Task<IReadOnlyList<GearBenchRead>> IReviewWorkBenchSource.ReadGearBenchesAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, CancellationToken ct) =>
        ReadGearBenchesAsync(identity, ct);

    Task<BuildingPreview> IReviewBuildingSource.PreviewBuildingAsync(DomainAction action, GenerationSnapshot snapshot, CancellationToken ct) =>
        PreviewBuildingAsync(action, snapshot, ct);

    /// <remarks>
    /// <c>CombatGeometry</c> is synchronous; the one native call behind it is awaited here, so the planner's
    /// worker blocks for the reply.
    /// </remarks>
    RimGovernor.Brain.GameFacts.Mirror.CombatGeometry IReviewDefenseSource.CombatGeometry(RimGovernor.Brain.GameOrders.Mirror.CombatGeometryRequest request, CancellationToken ct) =>
        CombatGeometryAsync(request, ct).GetAwaiter().GetResult();

    Task<BridgeCombat> IReviewDefenseSource.ReadCombatAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, CancellationToken ct) =>
        ReadCombatAsync(identity, ct);

    Task<RimGovernor.Brain.GameFacts.Observations.DefenseSiteSnapshot> IReviewDefenseSource.ReadDefenseSiteAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, BridgeCellRect region, CancellationToken ct) =>
        ReadDefenseSiteAsync(identity, region, ct);
}
