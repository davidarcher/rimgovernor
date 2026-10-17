using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Observation;

namespace RimGovernor.Brain.BuildingRuntime;

// The rounds sources that stay interfaces (a test fake or a second implementer satisfies them): see FacadeNativeClient.Interfaces.cs.
public sealed partial class FacadeNativeClient
{
    /// <summary>The recipe catalog the gear bench census reads with: the load's definition catalog, read after the bills reply validates.</summary>
    internal Task<IReadOnlyList<GearBenchRead>> ReadGearBenchesAsync(C.Identity identity, CancellationToken ct) =>
        ReadGearBenchesAsync(identity, async token => new GearRecipeCatalogAdapter(await DefinitionCatalogAsync(identity, token).ConfigureAwait(false)), ct);

    Task<IReadOnlyList<GearBenchRead>> IRoundsWorkBenchSource.ReadGearBenchesAsync(C.Identity identity, CancellationToken ct) =>
        ReadGearBenchesAsync(identity, ct);

    Task<BuildingPreview> IRoundsBuildingSource.PreviewBuildingAsync(DomainAction action, GenerationSnapshot snapshot, CancellationToken ct) =>
        PreviewBuildingAsync(action, snapshot, ct);

    /// <remarks>
    /// Go's <c>CombatGeometry</c> was synchronous; the one native call behind it is awaited here, so the planner's
    /// worker blocks for the reply exactly as the Go goroutine did.
    /// </remarks>
    M.CombatGeometry IRoundsDefenseSource.CombatGeometry(M.CombatGeometryRequest request, CancellationToken ct) =>
        CombatGeometryAsync(request, ct).GetAwaiter().GetResult();

    Task<BridgeCombat> IRoundsDefenseSource.ReadCombatAsync(C.Identity identity, CancellationToken ct) =>
        ReadCombatAsync(identity, ct);

    Task<BridgeDefenseSite> IRoundsDefenseSource.ReadDefenseSiteAsync(C.Identity identity, BridgeCellRect region, CancellationToken ct) =>
        ReadDefenseSiteAsync(identity, region, ct);
}
