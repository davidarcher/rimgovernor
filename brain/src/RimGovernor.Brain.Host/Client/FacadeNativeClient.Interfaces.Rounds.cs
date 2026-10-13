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
    private Task<(IReadOnlyList<GearBenchRead> Value, BridgeResult Receipt)> GearBenchesAsync(C.Identity identity, CancellationToken ct) =>
        ReadGearBenchesAsync(identity, async token => new GearRecipeCatalogAdapter(await DefinitionCatalogAsync(identity, token).ConfigureAwait(false)), ct);

    // ---- Gear benches ---------------------------------------------------------------------------------------

    Task<(IReadOnlyList<GearBenchRead> Reads, BridgeResult Result)> IRoundsWorkBenchSource.ReadGearBenchesAsync(C.Identity identity, CancellationToken ct) =>
        GearBenchesAsync(identity, ct);

    Task<(IReadOnlyList<GearBenchRead> Reads, BridgeResult Result)> IArtBenchSource.ReadGearBenchesAsync(C.Identity identity, CancellationToken ct) =>
        GearBenchesAsync(identity, ct);

    Task<(IReadOnlyList<GearBenchRead> Benches, BridgeResult Result)> IRoundsFoodStorageUpkeepSource.ReadGearBenchesAsync(C.Identity identity, CancellationToken ct) =>
        GearBenchesAsync(identity, ct);

    Task<(IReadOnlyList<GearBenchRead> Benches, BridgeResult Result)> IRoundsGearSource.ReadGearBenchesAsync(C.Identity identity, CancellationToken ct) =>
        GearBenchesAsync(identity, ct);

    Task<(IReadOnlyList<GearBenchRead> Reads, BridgeResult Result)> IRoundsMedicalSource.ReadGearBenchesAsync(C.Identity identity, CancellationToken ct) =>
        GearBenchesAsync(identity, ct);

    Task<(IReadOnlyList<GearBenchRead> Benches, BridgeResult Result)> IRoundsResourceSource.ReadGearBenchesAsync(C.Identity identity, CancellationToken ct) =>
        GearBenchesAsync(identity, ct);

    Task<(IReadOnlyList<GearBenchRead> Benches, BridgeResult Result)> IRoundsWorkshopSource.ReadGearBenchesAsync(C.Identity identity, CancellationToken ct) =>
        GearBenchesAsync(identity, ct);

    Task<IReadOnlyList<Policy.MechInput>> IMechSource.ReadMechsAsync(C.Identity identity, IReadOnlyList<string> ids, CancellationToken ct) =>
        ReadMechsAsync(identity, ids, ct);

    Task<(IReadOnlyList<Policy.Stock> Stock, BridgeResult Result)> IRoundsResourceSource.ReadSupplyStockAsync(C.Identity identity, IReadOnlyList<string> names, CancellationToken ct) =>
        ReadSupplyStockAsync(identity, names, ct);

    // ---- Building previews ----------------------------------------------------------------------------------

    Task<(BuildingPreview Preview, BridgeResult Result)> IRoundsBuildingSource.PreviewBuildingAsync(DomainAction action, GenerationSnapshot snapshot, CancellationToken ct) =>
        PreviewBuildingAsync(action, snapshot, ct);

    Task<(BridgeBuildingPreview Preview, BridgeResult Result)> IFieldBuildingPreview.PreviewBuildingAsync(DomainAction action, GenerationSnapshot snapshot, CancellationToken ct) =>
        PreviewBuildingAsync(action, snapshot, ct);

    Task<(BuildingPreview Preview, BridgeResult Result)> IRoundsMonumentPreviewSource.PreviewBuildingAsync(DomainAction action, GenerationSnapshot snapshot, CancellationToken ct) =>
        PreviewBuildingAsync(action, snapshot, ct);

    Task<(BuildingPreview Preview, BridgeResult Result)> IRoundsStoneShellSource.PreviewBuildingAsync(DomainAction action, GenerationSnapshot snapshot, CancellationToken ct) =>
        PreviewBuildingAsync(action, snapshot, ct);

    Task<(BuildingPreview Preview, BridgeResult Result)> IOverRockPreviewer.PreviewBuildingOverRockAsync(DomainAction action, GenerationSnapshot snapshot, CancellationToken ct) =>
        PreviewBuildingOverRockAsync(action, snapshot, ct);

    Task<(IReadOnlyList<BuildingPreview> Previews, BridgeResult Result)> IShellBatchPreviewer.PreviewBuildingsAsync(IReadOnlyList<DomainAction> actions, GenerationSnapshot snapshot, CancellationToken ct) =>
        PreviewBuildingsAsync(actions, snapshot, ct);

    Task<(IReadOnlyList<BridgeBuildingPreview> Previews, BridgeResult Result)> IRoundsDefenseLayoutSource.PreviewBuildingsAsync(IReadOnlyList<DomainAction> actions, GenerationSnapshot snapshot, CancellationToken ct) =>
        PreviewBuildingsAsync(actions, snapshot, ct);

    Task<(OP.ZonePreviewReply Reply, BridgeResult Result)> IFieldNative.PreviewZoneAsync(C.Identity identity, ZoneCreate zone, CancellationToken ct) =>
        PreviewZoneAsync(identity, zone, ct);

    Task<(OP.ZonePreviewReply Reply, BridgeResult Result)> IZonePreviewSource.PreviewZoneAsync(C.Identity identity, ZoneCreate zone, CancellationToken ct) =>
        PreviewZoneAsync(identity, zone, ct);

    // ---- Buildings, zones and targets -----------------------------------------------------------------------

    Task<(EntityRows<O.BuildingState> Rows, BridgeResult Result)> IDeepDrillBuildingSource.ReadBuildingsAsync(C.Identity identity, CancellationToken ct) =>
        ReadBuildingsAsync(identity, ct);

    Task<(BridgeGrowerCropTarget Target, BridgeResult Result)> IFieldGrowerCrop.ReadGrowerCropTargetAsync(C.Identity identity, string grower, CancellationToken ct) =>
        ReadGrowerCropTargetAsync(identity, grower, ct);

    Task<(BedUseTarget Target, BridgeResult Result)> IRoundsHospitalSource.ReadBedUseTargetAsync(C.Identity identity, string bed, CancellationToken ct) =>
        ReadBedUseTargetAsync(identity, bed, ct);

    Task<(ClaimBuildingTarget Target, BridgeResult Result)> IRoundsShrineSource.ReadClaimBuildingTargetAsync(C.Identity identity, string entity, CancellationToken ct) =>
        ReadClaimBuildingTargetAsync(identity, entity, ct);

    Task<(WallUpgradeSites Sites, BridgeResult Result)> IRoundsStoneShellSource.ReadWallUpgradeSitesAsync(C.Identity identity, string wall, CancellationToken ct) =>
        ReadWallUpgradeSitesAsync(identity, wall, ct);

    Task<(ZoneDeleteTarget Target, BridgeResult Result)> IRoundsStockpileSource.ReadZoneDeleteTargetAsync(C.Identity identity, string zone, CancellationToken ct) =>
        ReadZoneDeleteTargetAsync(identity, zone, ct);

    Task<(StorageBuildingTarget Target, BridgeResult Result)> IShelfTargetSource.ReadStorageBuildingTargetAsync(C.Identity identity, string building, CancellationToken ct) =>
        ReadStorageBuildingTargetAsync(identity, building, ct);

    Task<(BridgeExcavationSite Site, BridgeResult Result)> IRoundsExcavationSource.ReadExcavationSiteAsync(C.Identity identity, IReadOnlyList<Cell> cells, Cell access, CancellationToken ct) =>
        ReadExcavationSiteAsync(identity, cells, access, ct);

    // ---- Defense and combat ---------------------------------------------------------------------------------

    /// <remarks>
    /// Go's <c>CombatGeometry</c> was synchronous; the one native call behind it is awaited here, so the planner's
    /// worker blocks for the reply exactly as the Go goroutine did.
    /// </remarks>
    (M.CombatGeometry Geometry, BridgeResult Result) IRoundsDefenseSource.CombatGeometry(M.CombatGeometryRequest request, CancellationToken ct) =>
        CombatGeometryAsync(request, ct).GetAwaiter().GetResult();

    Task<BridgeCombat> IRoundsDefenseSource.ReadCombatAsync(C.Identity identity, CancellationToken ct) =>
        ReadCombatAsync(identity, ct);

    Task<(BridgeDefenseSite Site, BridgeResult Result)> IBurnFuelSource.ReadDefenseSiteAsync(C.Identity identity, BridgeCellRect region, CancellationToken ct) =>
        ReadDefenseSiteAsync(identity, region, ct);

    Task<(BridgeDefenseSite Site, BridgeResult Result)> IRoundsDefenseLayoutSource.ReadDefenseSiteAsync(C.Identity identity, BridgeCellRect region, CancellationToken ct) =>
        ReadDefenseSiteAsync(identity, region, ct);

    Task<BridgeDefenseSite> IRoundsFirebreakSource.ReadDefenseSiteAsync(C.Identity identity, BridgeCellRect rect, CancellationToken ct) =>
        Only(ReadDefenseSiteAsync(identity, rect, ct));

    Task<(DefenseSite Site, BridgeResult Result)> IDefenseSiteReader.ReadDefenseSiteAsync(C.Identity identity, CellRect region, CancellationToken ct) =>
        ReadDefenseSiteAsync(identity, region, ct);

    Task<(DefenseSite Site, BridgeResult Result)> IShrineReadinessNative.ReadDefenseSiteAsync(C.Identity identity, CellRect region, CancellationToken ct) =>
        ReadDefenseSiteAsync(identity, region, ct);

    Task<(BridgeSpatialAccess Access, BridgeResult Result)> IRoundsDefenseLayoutSource.ReadSpatialAccessAsync(C.Identity identity, IReadOnlyList<Cell> from, IReadOnlyList<Cell> toCells, IReadOnlyList<string> kinds, CancellationToken ct) =>
        ReadSpatialAccessAsync(identity, from, toCells, kinds, ct);

    Task<(BridgeMapBounds Bounds, BridgeResult Result)> IRoundsEquipSource.ReadMapBoundsAsync(C.Identity identity, Cell origin, CancellationToken ct) =>
        ReadMapBoundsAsync(identity, origin, ct);

    Task<(MapBounds Bounds, BridgeResult Result)> ILoadoutWeaponSource.ReadMapBoundsAsync(C.Identity identity, Cell origin, CancellationToken ct) =>
        ReadMapBoundsAsync(identity, origin, ct);

    Task<(BridgeEquipRead Read, BridgeResult Result)> IRoundsEquipSource.ReadEquipWeaponsAsync(C.Identity identity, Cell from, Cell toCell, CancellationToken ct) =>
        ReadEquipWeaponsAsync(identity, from, toCell, ct);

    Task<(EquipRead Read, BridgeResult Result)> ILoadoutWeaponSource.ReadEquipWeaponsAsync(C.Identity identity, Cell from, Cell to, CancellationToken ct) =>
        ReadEquipWeaponsAsync(identity, from, to, ct);

    Task<(EquipRead Read, BridgeResult Result)> IWeaponCensus.ReadEquipWeaponsAsync(C.Identity identity, Cell from, Cell toCell, CancellationToken ct) =>
        ReadEquipWeaponsAsync(identity, from, toCell, ct);

    // ---- Emergency and pawn reads ---------------------------------------------------------------------------

    Task<(EmergencyObservation Observation, BridgeResult Result)> IRoundsEquipSource.ReadEmergencyAsync(C.Identity identity, CancellationToken ct) =>
        ReadEmergencyAsync(identity, FramePawnsAsync, ct);

    Task<BridgeEmergencyObservation> IRoundsFireSafetySource.ReadEmergencyAsync(C.Identity identity, CancellationToken ct) =>
        Only(ReadEmergencyAsync(identity, FramePawnsAsync, ct));

    Task<(EmergencyObservation Emergency, BridgeResult Result)> IRoundsIncinerationSource.ReadEmergencyAsync(C.Identity identity, CancellationToken ct) =>
        ReadEmergencyAsync(identity, FramePawnsAsync, ct);

    Task<(EmergencyObservation Emergency, BridgeResult Result)> IRoundsRepairSource.ReadEmergencyAsync(C.Identity identity, CancellationToken ct) =>
        ReadEmergencyAsync(identity, FramePawnsAsync, ct);

    Task<(EmergencyObservation Emergency, BridgeResult Result)> IRoundsRescueSource.ReadEmergencyAsync(C.Identity identity, CancellationToken ct) =>
        ReadEmergencyAsync(identity, FramePawnsAsync, ct);

    Task<(EmergencyObservation Observation, BridgeResult Result)> IRoundsTendSource.ReadEmergencyAsync(C.Identity identity, CancellationToken ct) =>
        ReadEmergencyAsync(identity, FramePawnsAsync, ct);

    Task<(EmergencyObservation Emergency, BridgeResult Result)> IRoundsCustodySource.ReadEmergencyAsync(C.Identity identity, CancellationToken ct) =>
        ReadEmergencyAsync(identity, FramePawnsAsync, ct);

    Task<(EmergencyObservation Observed, BridgeResult Result)> IShrineReadinessNative.ReadEmergencyAsync(C.Identity identity, CancellationToken ct) =>
        ReadEmergencyAsync(identity, FramePawnsAsync, ct);

    Task<(O.ListPawnsReply Reply, BridgeResult Result)> IRoundsDefenseLayoutSource.ReadCombatPawnsAsync(C.Identity identity, IReadOnlyList<string> ids, CancellationToken ct) =>
        ReadCombatPawnsAsync(identity, ids, ct);

    Task<(O.ListPawnsReply Reply, BridgeResult Result)> IRoundsEquipSource.ReadCombatPawnsAsync(C.Identity identity, IReadOnlyList<string> ids, CancellationToken ct) =>
        ReadCombatPawnsAsync(identity, ids, ct);

    Task<(O.ListPawnsReply Reply, BridgeResult Result)> IRoundsRescueSource.ReadCombatPawnsAsync(C.Identity identity, IReadOnlyList<string> ids, CancellationToken ct) =>
        ReadCombatPawnsAsync(identity, ids, ct);

    Task<(O.ListPawnsReply Reply, BridgeResult Result)> IShrineReadinessNative.ReadCombatPawnsAsync(C.Identity identity, IReadOnlyList<string> pawns, CancellationToken ct) =>
        ReadCombatPawnsAsync(identity, pawns, ct);

    Task<O.ListPawnsReply> IRoundsFireSafetySource.ReadTendPawnsAsync(C.Identity identity, IReadOnlyList<string> ids, CancellationToken ct) =>
        Only(ReadTendPawnsAsync(identity, ids, ct));

    Task<(O.ListPawnsReply Reply, BridgeResult Result)> IRoundsIncinerationSource.ReadTendPawnsAsync(C.Identity identity, IReadOnlyList<string> pawns, CancellationToken ct) =>
        ReadTendPawnsAsync(identity, pawns, ct);

    Task<(O.ListPawnsReply Reply, BridgeResult Result)> IRoundsRepairSource.ReadTendPawnsAsync(C.Identity identity, IReadOnlyList<string> ids, CancellationToken ct) =>
        ReadTendPawnsAsync(identity, ids, ct);

    Task<(O.ListPawnsReply Reply, BridgeResult Result)> IRoundsTendSource.ReadTendPawnsAsync(C.Identity identity, IReadOnlyList<string> ids, CancellationToken ct) =>
        ReadTendPawnsAsync(identity, ids, ct);

    // ---- Resources and supplies -----------------------------------------------------------------------------

    Task<(IReadOnlyList<Policy.ResourceSource> Rows, Policy.ResourceStorage Storage, BridgeResult Result)> IRoundsFoodStorageUpkeepSource.ReadResourceSourcesAsync(C.Identity identity, string definition, CancellationToken ct) =>
        ReadResourceSourcesAsync(identity, definition, ct);

    Task<(IReadOnlyList<Policy.ResourceSource> Sources, Policy.ResourceStorage Storage, BridgeResult Result)> IRoundsResourceSource.ReadResourceSourcesAsync(C.Identity identity, string resource, CancellationToken ct) =>
        ReadResourceSourcesAsync(identity, resource, ct);

    Task<(IReadOnlyList<Policy.ResourceSource> Sources, Policy.ResourceStorage Storage, BridgeResult Result)> IResourceRunwaySource.ReadResourceSourcesAsync(C.Identity identity, string resource, CancellationToken ct) =>
        ReadResourceSourcesAsync(identity, resource, ct);

    Task<Policy.ConsumptionPage> IConsumptionSource.ReadConsumptionAsync(int sinceHour, CancellationToken ct) =>
        ReadConsumptionAsync(sinceHour, ct);

    Task<(SupplyRead Read, BridgeResult Result)> IFoodReserveSupplyReader.ReadFoodReserveSuppliesAsync(C.Identity identity, bool forbid, CancellationToken ct) =>
        ReadFoodReserveSuppliesAsync(identity, forbid, ct);

    Task<(SupplyRead Read, BridgeResult Result)> IRoundsSupplySource.ReadAllowSuppliesAsync(C.Identity identity, Cell cell, CancellationToken ct) =>
        ReadAllowSuppliesAsync(identity, cell, ct);

    Task<(SupplyRead Read, BridgeResult Result)> IRoundsSupplyForbidSource.ReadForbidSuppliesAsync(C.Identity identity, Cell cell, CancellationToken ct) =>
        ReadForbidSuppliesAsync(identity, cell, ct);

    Task<(IReadOnlyList<string> Items, BridgeResult Result)> IRoundsPsylinkSource.ReadNeuroformerItemsAsync(C.Identity identity, string neuroformer, CancellationToken ct) =>
        ReadNeuroformerItemsAsync(identity, neuroformer, ct);
}
