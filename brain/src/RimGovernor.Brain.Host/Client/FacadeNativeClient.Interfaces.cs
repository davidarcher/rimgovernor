using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.NativeFacade;
using RimGovernor.Brain.Observation;
using BridgeCatalog = RimGovernor.Brain.Bridge.DefinitionCatalog;
using C = RimGovernor.Contracts.Wire.Common;
using LifecyclePb = RimGovernor.Contracts.Wire.Lifecycle;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Host;

// Binding of the typed core methods (FacadeNativeClient.*.cs) to the Native/Source interfaces the planners are
// written against. Go's one bridge.Client satisfied all of these implicitly; here every core method is internal and
// returns the (Value, Receipt) tuple, so each interface member is an explicit implementation that adapts the shape:
// a tuple-returning interface passes the core task through (tuple element names are not part of the type), a
// bare-value interface unwraps the tuple's value. The members live in Interfaces.Observation.cs (observation, clock
// and colony), Interfaces.Rounds.cs (the building runtime's rounds sources) and Interfaces.Trade.cs (trade, world,
// supplies and lifecycle).
internal sealed partial class FacadeNativeClient :
    // Observation (Go observation.RoundsSource and the pieces it is made of).
    IRoundsSource,
    IGroundClearanceSource,
    IRefrigerationSource,
    IShrineSource,
    IZonesNative,
    // Building runtime: clock and colony natives.
    IClockNative,
    IEntityNative,
    IClockWindowNative,
    IClockDriverNative,
    IColonyFactsReader,
    IColonyStatusNative,
    ILayoutOverlayNative,
    IMapFocusNative,
    IPackedSource,
    IArrivalPawns,
    IBoundaryNative,
    IHaulNative,
    IBreakResponseSource,
    // Building runtime: rounds sources.
    IRoundsBuildingSource,
    IRoundsRescueSource,
    IRoundsTendSource,
    IRoundsWorkBenchSource,
    IArtBenchSource,
    IMechSource,
    IRoundsBlightSource,
    IRoundsClearanceSource,
    ILoadoutWeaponSource,
    IDeepDrillBuildingSource,
    IRoundsDefenseSource,
    IRoundsDefenseLayoutSource,
    IRoundsDialogSource,
    IRoundsEquipSource,
    IRoundsExcavationSource,
    IFieldBuildingPreview,
    IFieldGrowerCrop,
    IRoundsFirebreakSource,
    IRoundsFireSafetySource,
    IDefenseSiteReader,
    IFoodReserveSupplyReader,
    IRoundsFoodStorageUpkeepSource,
    IRoundsGearSource,
    IRoundsMedicalSource,
    IFrameThingsSource,
    IRoundsHospitalSource,
    IRoundsIncinerationSource,
    IOverRockPreviewer,
    IRoundsCustodySource,
    IRoundsPsylinkSource,
    IRoundsMonumentPreviewSource,
    IRoundsRepairSource,
    IRoundsResearchSource,
    IRoundsResourceSource,
    IResourceRunwaySource,
    IConsumptionSource,
    IShellBatchPreviewer,
    IRoundsShrineSource,
    IWeaponCensus,
    IRoundsStoneShellSource,
    IRoundsSupplySource,
    IRoundsSupplyForbidSource,
    IRoundsStockpileSource,
    IShelfTargetSource,
    IZonePreviewSource,
    // Building runtime: trade, world and lifecycle.
    IRoundsTradeSource,
    IExportSource,
    IRoundsWorkshopSource,
    ILifecycleSaver,
    ITradeAcquisitionSource,
    ITradeMissionPackSource,
    ITradeNative,
    ICommsTradeRequestNative,
    ITradeMissionNative,
    ISettlementOptionsSource
{
    /// <summary>
    /// The client over <paramref name="facade"/>: the facade is wrapped in <see cref="CountingNativeFacade"/> so every
    /// native call is tallied, and <see cref="FrameSections"/> is bound to the decoders of the other core files.
    /// </summary>
    /// <param name="facade">The in-process native facade.</param>
    /// <param name="catalogFactory">
    /// Builds the C# definition catalog of a load from its validated wire catalog and the game's Defs
    /// (<see cref="CatalogFactory"/>). The game's Defs are not on the wire, so only the host composition that holds them
    /// can supply it; without one <c>DefinitionCatalogAsync</c> (and the reads that need the catalog) refuse.
    /// </param>
    internal static FacadeNativeClient Create(INativeFacade facade, Func<PbObservations.DefinitionCatalog, PbCommon.Identity, BridgeCatalog>? catalogFactory = null)
    {
        ArgumentNullException.ThrowIfNull(facade);
        FacadeNativeClient? client = null;
        client = new FacadeNativeClient(new CountingNativeFacade(facade))
        {
            FrameSections = new FrameSectionsAdapter(() => client!),
            CatalogFactory = catalogFactory,
        };
        return client;
    }

    /// <summary>The value half of a core method's (Value, Receipt) result: what Go callers got when they dropped the Result.</summary>
    private static async Task<T> Only<T>(Task<(T Value, BridgeResult Receipt)> call) =>
        (await call.ConfigureAwait(false)).Value;

    // ---- Observation ----------------------------------------------------------------------------------------

    Task<(LifecyclePb.IdentityReply Reply, BridgeResult Receipt)> IColonySource.IdentityAsync(CancellationToken cancellationToken) =>
        IdentityAsync(cancellationToken);

    Task<(O.ColonyFactsReply Reply, BridgeResult Receipt)> IColonySource.ReadColonyFactsAsync(C.Identity identity, bool planning, CancellationToken cancellationToken) =>
        ReadColonyFactsAsync(identity, planning, cancellationToken);

    Task<Tables> IColonySource.FrameTablesAsync(C.Identity identity, CancellationToken cancellationToken) =>
        FrameTablesAsync(identity, cancellationToken);

    Task<(LifecyclePb.TickReply Reply, BridgeResult Receipt)> ISource.TickAsync(CancellationToken cancellationToken) =>
        TickAsync(cancellationToken);

    async Task<BridgeCatalog?> IDefinitionSource.DefinitionCatalogAsync(C.Identity id, CancellationToken cancellationToken) =>
        await DefinitionCatalogAsync(id, cancellationToken).ConfigureAwait(false);

    Task<(ResearchRead Read, BridgeResult Receipt)> IResearchSource.ReadResearchAsync(C.Identity id, CancellationToken cancellationToken) =>
        ReadResearchAsync(id, cancellationToken);

    Task<RoundsFrame> IRoundsSource.ReadRoundsFrameAsync(C.Identity identity, CancellationToken cancellationToken) =>
        ReadRoundsFrameAsync(identity, cancellationToken);

    Task<(O.ClearanceTargetsReply Reply, BridgeResult Receipt)> IClearanceSource.ReadClearanceTargetsAsync(C.Identity identity, bool includeSalvage, CancellationToken cancellationToken) =>
        ReadClearanceTargetsAsync(identity, includeSalvage, cancellationToken);

    Task<(O.ClearanceTargetsReply Reply, BridgeResult Receipt)> IGroundClearanceSource.ReadClearanceTargetsOnGroundAsync(C.Identity identity, bool includeSalvage, IReadOnlyList<O.Rectangle> ground, CancellationToken cancellationToken) =>
        ReadClearanceTargetsOnGroundAsync(identity, includeSalvage, ground, cancellationToken);

    Task<(O.ListBuildingsReply Reply, BridgeResult Receipt)> IRefrigerationSource.ReadConstructionBuildingsAsync(C.Identity identity, IReadOnlyList<string> ids, CancellationToken cancellationToken) =>
        ReadConstructionBuildingsAsync(identity, ids, cancellationToken);

    Task<(O.AncientShrinesReply Reply, BridgeResult Receipt)> IShrineSource.ReadAncientShrinesAsync(C.Identity identity, CancellationToken cancellationToken) =>
        ReadAncientShrinesAsync(identity, cancellationToken);

    Task<(ZonesRead Read, BridgeResult Receipt)> IZonesNative.ReadZoneSectionAsync(C.Identity identity, CancellationToken cancellationToken) =>
        ReadZoneSectionAsync(identity, cancellationToken);

    // ---- Clock ----------------------------------------------------------------------------------------------

    Task<LifecyclePb.IdentityReply> IClockNative.IdentityAsync(CancellationToken ct) =>
        Only(IdentityAsync(ct));

    Task<PbClock.StatusReply> IClockNative.ReadClockStatusAsync(C.Identity identity, CancellationToken ct) =>
        Only(ReadClockStatusAsync(identity, ct));

    Task<PbClock.AttemptReply> IClockNative.ReadClockAttemptAsync(PbClock.AttemptRequest request, CancellationToken ct) =>
        Only(ReadClockAttemptAsync(request, ct));

    Task<EntityRows<O.BuildingState>> IEntityNative.ReadBuildingsAsync(C.Identity identity, CancellationToken ct) =>
        Only(ReadBuildingsAsync(identity, ct));

    Task<EntityRows<O.BillStackRow>> IEntityNative.ReadBillStacksAsync(C.Identity identity, CancellationToken ct) =>
        Only(ReadBillStacksAsync(identity, ct));

    Task<O.BundleSnapshot?> IClockEventNative.ReadStepAsync(BridgeFunctions.StepRequest request, CancellationToken ct) =>
        ReadStepAsync(request, ct);

    Task<O.BundleSnapshot?> IClockWindowNative.ReadStepAsync(BridgeFunctions.StepRequest request, CancellationToken ct) =>
        ReadStepAsync(request, ct);

    Task<PbClock.EventsReply> IClockEventNative.ReadClockEventsAsync(PbClock.EventsRequest request, CancellationToken ct) =>
        Only(ReadClockEventsAsync(request, ct));

    Task<Bridge.Contracts.PlanningWindow> IPlanningWindowNative.ReadPlanningWindowAsync(C.Identity identity, Policy.Rectangle region, CancellationToken ct) =>
        Only(ReadPlanningWindowAsync(identity, region, ct));

    Task<EmergencyObservation> IClockDriverNative.ReadEmergencyAsync(C.Identity identity, CancellationToken ct) =>
        Only(ReadEmergencyAsync(identity, FramePawnsAsync, ct));

    // ---- Colony and presentation ----------------------------------------------------------------------------

    Task<O.ColonyFactsReply> IColonyFactsReader.ReadColonyFactsAsync(C.Identity identity, bool planning, CancellationToken ct) =>
        Only(ReadColonyFactsAsync(identity, planning, ct));

    Task<LifecyclePb.IdentityReply> IColonyStatusNative.IdentityAsync(CancellationToken ct) =>
        Only(IdentityAsync(ct));

    Task<O.ColonyFactsReply> IColonyStatusNative.ReadColonyFactsAsync(C.Identity identity, bool full, CancellationToken ct) =>
        Only(ReadColonyFactsAsync(identity, full, ct));

    Task<O.ListPawnsReply> IColonyStatusNative.ReadHomeColonistsAsync(C.Identity identity, CancellationToken ct) =>
        Only(ReadHomeColonistsAsync(identity, ct));

    Task<PbPresentation.OverlayApplied> ILayoutOverlayNative.DrawOverlayAsync(C.Identity identity, string layer, Policy.LayoutOverlay overlay, bool visible, CancellationToken ct) =>
        Only(DrawOverlayAsync(identity, layer, overlay, visible, ct));

    Task<PbAuthority.ControlReply> IMapFocusNative.FocusMapAsync(PbAuthority.FocusMap request, CancellationToken ct) =>
        Only(FocusMapAsync(request, ct));

    Task<IReadOnlyList<PackedItem>> IPackedSource.ReadPackedItemsAsync(C.Identity identity, string packedDef, CancellationToken ct) =>
        Only(ReadPackedItemsAsync(identity, packedDef, ct));

    // ---- Pawn reads of the boundary, haul and break paths ---------------------------------------------------

    Task<O.ListPawnsReply> IArrivalPawns.ReadCombatPawnsAsync(C.Identity identity, IReadOnlyList<string> pawnIds, CancellationToken ct) =>
        Only(ReadCombatPawnsAsync(identity, pawnIds, ct));

    Task<O.ListPawnsReply> IHaulNative.ReadPawnsAsync(C.Identity identity, IReadOnlyList<string> pawnIds, CancellationToken ct) =>
        Only(ReadPawnsAsync(identity, pawnIds, ct));

    Task<EmergencyObservation> IBreakResponseSource.ReadEmergencyAsync(C.Identity identity, CancellationToken ct) =>
        Only(ReadEmergencyAsync(identity, FramePawnsAsync, ct));

    Task<O.ListPawnsReply> IBreakResponseSource.ReadCombatPawnsAsync(C.Identity identity, IReadOnlyList<string> pawnIds, CancellationToken ct) =>
        Only(ReadCombatPawnsAsync(identity, pawnIds, ct));

    Task<EmergencyObservation> IBoundaryNative.ReadEmergencyAsync(C.Identity identity, CancellationToken ct) =>
        Only(ReadEmergencyAsync(identity, FramePawnsAsync, ct));

    Task<BuildingPreview> IBoundaryNative.PreviewBuildingAsync(DomainAction action, GenerationSnapshot snapshot, CancellationToken ct) =>
        Only(PreviewBuildingAsync(action, snapshot, ct));

    Task<MapBounds> IBoundaryNative.ReadMapBoundsAsync(C.Identity identity, Cell anchor, CancellationToken ct) =>
        Only(ReadMapBoundsAsync(identity, anchor, ct));

    Task<Things> IFrameThingsSource.FrameThingsAsync(C.Identity identity, CancellationToken ct) =>
        FrameThingsAsync(identity, ct);
}
