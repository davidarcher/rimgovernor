using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Observation;
using BridgeBuildingPreview = RimGovernor.Brain.Bridge.Contracts.BuildingPreview;
using BridgeCellRect = RimGovernor.Brain.Bridge.Contracts.CellRect;
using BridgeCombat = RimGovernor.Brain.Observation.Combat;
using BridgeDefenseSite = RimGovernor.Brain.Bridge.Contracts.DefenseSite;
using BridgeEmergencyObservation = RimGovernor.Brain.Bridge.Contracts.EmergencyObservation;
using BridgeEquipRead = RimGovernor.Brain.Bridge.Contracts.EquipRead;
using BridgeExcavationSite = RimGovernor.Brain.Bridge.Contracts.ExcavationSite;
using BridgeGrowerCropTarget = RimGovernor.Brain.Bridge.Contracts.GrowerCropTarget;
using BridgeMapBounds = RimGovernor.Brain.Bridge.Contracts.MapBounds;
using BridgeSpatialAccess = RimGovernor.Brain.Bridge.Contracts.SpatialAccess;
using C = RimGovernor.Contracts.Wire.Common;
using M = RimGovernor.Contracts.Wire.Mirror;
using O = RimGovernor.Contracts.Wire.Observations;
using OP = RimGovernor.Contracts.Wire.Operations;

namespace RimGovernor.Brain.Host;

// The building runtime's rounds sources (Go buildingruntime.Rounds*Source): see FacadeNativeClient.Interfaces.cs.
internal sealed partial class FacadeNativeClient
{
    /// <summary>The recipe catalog the gear bench census reads with: the load's definition catalog, read after the bills reply validates.</summary>
    private Task<IReadOnlyList<GearBenchRead>> GearBenchesAsync(C.Identity identity, CancellationToken ct) =>
        ReadGearBenchesAsync(identity, async token => new GearRecipeCatalogAdapter(await DefinitionCatalogAsync(identity, token).ConfigureAwait(false)), ct);

    // ---- Gear benches ---------------------------------------------------------------------------------------

    Task<IReadOnlyList<GearBenchRead>> IRoundsWorkBenchSource.ReadGearBenchesAsync(C.Identity identity, CancellationToken ct) =>
        GearBenchesAsync(identity, ct);

    Task<IReadOnlyList<GearBenchRead>> IArtBenchSource.ReadGearBenchesAsync(C.Identity identity, CancellationToken ct) =>
        GearBenchesAsync(identity, ct);

    Task<IReadOnlyList<GearBenchRead>> IRoundsGearSource.ReadGearBenchesAsync(C.Identity identity, CancellationToken ct) =>
        GearBenchesAsync(identity, ct);

    Task<IReadOnlyList<GearBenchRead>> IRoundsMedicalSource.ReadGearBenchesAsync(C.Identity identity, CancellationToken ct) =>
        GearBenchesAsync(identity, ct);

    Task<IReadOnlyList<GearBenchRead>> IRoundsResourceSource.ReadGearBenchesAsync(C.Identity identity, CancellationToken ct) =>
        GearBenchesAsync(identity, ct);

    Task<IReadOnlyList<GearBenchRead>> IRoundsWorkshopSource.ReadGearBenchesAsync(C.Identity identity, CancellationToken ct) =>
        GearBenchesAsync(identity, ct);

    Task<IReadOnlyList<Policy.MechInput>> IMechSource.ReadMechsAsync(C.Identity identity, IReadOnlyList<string> ids, CancellationToken ct) =>
        ReadMechsAsync(identity, ids, ct);

    Task<IReadOnlyList<Policy.Stock>> IRoundsResourceSource.ReadSupplyStockAsync(C.Identity identity, IReadOnlyList<string> names, CancellationToken ct) =>
        ReadSupplyStockAsync(identity, names, ct);

    // ---- Building previews ----------------------------------------------------------------------------------

    Task<BuildingPreview> IRoundsBuildingSource.PreviewBuildingAsync(DomainAction action, GenerationSnapshot snapshot, CancellationToken ct) =>
        PreviewBuildingAsync(action, snapshot, ct);

    Task<BridgeBuildingPreview> IFieldBuildingPreview.PreviewBuildingAsync(DomainAction action, GenerationSnapshot snapshot, CancellationToken ct) =>
        PreviewBuildingAsync(action, snapshot, ct);

    Task<BuildingPreview> IRoundsMonumentPreviewSource.PreviewBuildingAsync(DomainAction action, GenerationSnapshot snapshot, CancellationToken ct) =>
        PreviewBuildingAsync(action, snapshot, ct);

    Task<BuildingPreview> IRoundsStoneShellSource.PreviewBuildingAsync(DomainAction action, GenerationSnapshot snapshot, CancellationToken ct) =>
        PreviewBuildingAsync(action, snapshot, ct);

    Task<BuildingPreview> IOverRockPreviewer.PreviewBuildingOverRockAsync(DomainAction action, GenerationSnapshot snapshot, CancellationToken ct) =>
        PreviewBuildingOverRockAsync(action, snapshot, ct);

    Task<IReadOnlyList<BuildingPreview>> IShellBatchPreviewer.PreviewBuildingsAsync(IReadOnlyList<DomainAction> actions, GenerationSnapshot snapshot, CancellationToken ct) =>
        PreviewBuildingsAsync(actions, snapshot, ct);

    Task<IReadOnlyList<BridgeBuildingPreview>> IRoundsDefenseLayoutSource.PreviewBuildingsAsync(IReadOnlyList<DomainAction> actions, GenerationSnapshot snapshot, CancellationToken ct) =>
        PreviewBuildingsAsync(actions, snapshot, ct);

    Task<OP.ZonePreviewReply> IFieldNative.PreviewZoneAsync(C.Identity identity, ZoneCreate zone, CancellationToken ct) =>
        PreviewZoneAsync(identity, zone, ct);

    Task<OP.ZonePreviewReply> IZonePreviewSource.PreviewZoneAsync(C.Identity identity, ZoneCreate zone, CancellationToken ct) =>
        PreviewZoneAsync(identity, zone, ct);

    // ---- Buildings, zones and targets -----------------------------------------------------------------------

    Task<EntityRows<O.BuildingState>> IDeepDrillBuildingSource.ReadBuildingsAsync(C.Identity identity, CancellationToken ct) =>
        ReadBuildingsAsync(identity, ct);

    Task<BridgeGrowerCropTarget> IFieldGrowerCrop.ReadGrowerCropTargetAsync(C.Identity identity, string grower, CancellationToken ct) =>
        ReadGrowerCropTargetAsync(identity, grower, ct);

    Task<BedUseTarget> IRoundsHospitalSource.ReadBedUseTargetAsync(C.Identity identity, string bed, CancellationToken ct) =>
        ReadBedUseTargetAsync(identity, bed, ct);

    Task<WallUpgradeSites> IRoundsStoneShellSource.ReadWallUpgradeSitesAsync(C.Identity identity, string wall, CancellationToken ct) =>
        ReadWallUpgradeSitesAsync(identity, wall, ct);

    Task<ZoneDeleteTarget> IRoundsStockpileSource.ReadZoneDeleteTargetAsync(C.Identity identity, string zone, CancellationToken ct) =>
        ReadZoneDeleteTargetAsync(identity, zone, ct);

    Task<StorageBuildingTarget> IShelfTargetSource.ReadStorageBuildingTargetAsync(C.Identity identity, string building, CancellationToken ct) =>
        ReadStorageBuildingTargetAsync(identity, building, ct);

    Task<BridgeExcavationSite> IRoundsExcavationSource.ReadExcavationSiteAsync(C.Identity identity, IReadOnlyList<Cell> cells, Cell access, CancellationToken ct) =>
        ReadExcavationSiteAsync(identity, cells, access, ct);

    // ---- Defense and combat ---------------------------------------------------------------------------------

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

    Task<BridgeDefenseSite> IRoundsDefenseLayoutSource.ReadDefenseSiteAsync(C.Identity identity, BridgeCellRect region, CancellationToken ct) =>
        ReadDefenseSiteAsync(identity, region, ct);

    Task<BridgeDefenseSite> IRoundsFirebreakSource.ReadDefenseSiteAsync(C.Identity identity, BridgeCellRect rect, CancellationToken ct) =>
        ReadDefenseSiteAsync(identity, rect, ct);

    Task<DefenseSite> IDefenseSiteReader.ReadDefenseSiteAsync(C.Identity identity, CellRect region, CancellationToken ct) =>
        ReadDefenseSiteAsync(identity, region, ct);

    Task<BridgeSpatialAccess> IRoundsDefenseLayoutSource.ReadSpatialAccessAsync(C.Identity identity, IReadOnlyList<Cell> from, IReadOnlyList<Cell> toCells, IReadOnlyList<string> kinds, CancellationToken ct) =>
        ReadSpatialAccessAsync(identity, from, toCells, kinds, ct);

    Task<BridgeMapBounds> IRoundsEquipSource.ReadMapBoundsAsync(C.Identity identity, Cell origin, CancellationToken ct) =>
        ReadMapBoundsAsync(identity, origin, ct);

    Task<MapBounds> ILoadoutWeaponSource.ReadMapBoundsAsync(C.Identity identity, Cell origin, CancellationToken ct) =>
        ReadMapBoundsAsync(identity, origin, ct);

    Task<BridgeEquipRead> IRoundsEquipSource.ReadEquipWeaponsAsync(C.Identity identity, Cell from, Cell toCell, CancellationToken ct) =>
        ReadEquipWeaponsAsync(identity, from, toCell, ct);

    Task<EquipRead> ILoadoutWeaponSource.ReadEquipWeaponsAsync(C.Identity identity, Cell from, Cell to, CancellationToken ct) =>
        ReadEquipWeaponsAsync(identity, from, to, ct);

    // ---- Emergency and pawn reads ---------------------------------------------------------------------------

    Task<EmergencyObservation> IRoundsEquipSource.ReadEmergencyAsync(C.Identity identity, CancellationToken ct) =>
        ReadEmergencyAsync(identity, FramePawnsAsync, ct);

    Task<EmergencyObservation> IRoundsRescueSource.ReadEmergencyAsync(C.Identity identity, CancellationToken ct) =>
        ReadEmergencyAsync(identity, FramePawnsAsync, ct);

    Task<EmergencyObservation> IRoundsTendSource.ReadEmergencyAsync(C.Identity identity, CancellationToken ct) =>
        ReadEmergencyAsync(identity, FramePawnsAsync, ct);

    Task<O.ListPawnsReply> IRoundsDefenseLayoutSource.ReadCombatPawnsAsync(C.Identity identity, IReadOnlyList<string> ids, CancellationToken ct) =>
        ReadCombatPawnsAsync(identity, ids, ct);

    Task<O.ListPawnsReply> IRoundsEquipSource.ReadCombatPawnsAsync(C.Identity identity, IReadOnlyList<string> ids, CancellationToken ct) =>
        ReadCombatPawnsAsync(identity, ids, ct);

    Task<O.ListPawnsReply> IRoundsRescueSource.ReadCombatPawnsAsync(C.Identity identity, IReadOnlyList<string> ids, CancellationToken ct) =>
        ReadCombatPawnsAsync(identity, ids, ct);

    Task<O.ListPawnsReply> IRoundsTendSource.ReadTendPawnsAsync(C.Identity identity, IReadOnlyList<string> ids, CancellationToken ct) =>
        ReadTendPawnsAsync(identity, ids, ct);

    // ---- Resources and supplies -----------------------------------------------------------------------------

    Task<(IReadOnlyList<Policy.ResourceSource> Rows, Policy.ResourceStorage Storage)> IRoundsFoodStoresSource.ReadResourceSourcesAsync(C.Identity identity, string definition, CancellationToken ct) =>
        ReadResourceSourcesAsync(identity, definition, ct);

    Task<(IReadOnlyList<Policy.ResourceSource> Sources, Policy.ResourceStorage Storage)> IRoundsResourceSource.ReadResourceSourcesAsync(C.Identity identity, string resource, CancellationToken ct) =>
        ReadResourceSourcesAsync(identity, resource, ct);

    Task<(IReadOnlyList<Policy.ResourceSource> Sources, Policy.ResourceStorage Storage)> IResourceRunwaySource.ReadResourceSourcesAsync(C.Identity identity, string resource, CancellationToken ct) =>
        ReadResourceSourcesAsync(identity, resource, ct);

    Task<Policy.ConsumptionPage> IConsumptionSource.ReadConsumptionAsync(int sinceHour, CancellationToken ct) =>
        ReadConsumptionAsync(sinceHour, ct);

    Task<Policy.StoreOccupancy> IStorageOccupancySource.ReadStorageOccupancyAsync(IReadOnlyCollection<string> groups, int sinceHour, CancellationToken ct) =>
        ReadStorageOccupancyAsync(groups, sinceHour, ct);

    Task<SupplyRead> IFoodReserveSupplyReader.ReadFoodReserveSuppliesAsync(C.Identity identity, bool forbid, CancellationToken ct) =>
        ReadFoodReserveSuppliesAsync(identity, forbid, ct);

    Task<SupplyRead> IRoundsSupplySource.ReadAllowSuppliesAsync(C.Identity identity, Cell cell, CancellationToken ct) =>
        ReadAllowSuppliesAsync(identity, cell, ct);

    Task<SupplyRead> IRoundsSupplyForbidSource.ReadForbidSuppliesAsync(C.Identity identity, Cell cell, CancellationToken ct) =>
        ReadForbidSuppliesAsync(identity, cell, ct);

    Task<IReadOnlyList<string>> IRoundsPsylinkSource.ReadNeuroformerItemsAsync(C.Identity identity, string neuroformer, CancellationToken ct) =>
        ReadNeuroformerItemsAsync(identity, neuroformer, ct);
}
