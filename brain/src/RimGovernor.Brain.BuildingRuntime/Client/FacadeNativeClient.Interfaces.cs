using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Observation;
using BridgeCatalog = RimGovernor.Brain.Bridge.DefinitionCatalog;

namespace RimGovernor.Brain.BuildingRuntime;

// Explicit bindings of the typed core methods (FacadeNativeClient.*.cs) to the interfaces that stay: the Observation
// sources, and the building runtime's interfaces that a test fake or a second implementer also satisfies. Every other
// consumer calls the core methods directly.
public sealed partial class FacadeNativeClient :
    IRoundsSource,
    IResearchSource,
    IGroundClearanceSource,
    IRefrigerationSource,
    IZonesNative,
    IClockNative,
    IEntityNative,
    IClockStepNative,
    IClockDriverNative,
    IColonyStatusNative,
    IArrivalPawns,
    IBoundaryNative,
    IMapFocusNative,
    IRoundsBuildingSource,
    IRoundsWorkBenchSource,
    IRoundsDefenseSource
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
            CatalogFactory = catalogFactory,
        };
        return client;
    }

    // ---- Observation ----------------------------------------------------------------------------------------

    Task<LifecyclePb.IdentityReply> IColonySource.IdentityAsync(CancellationToken cancellationToken) =>
        IdentityAsync(cancellationToken);

    Task<O.ColonyFactsReply> IColonySource.ReadColonyFactsAsync(C.Identity identity, bool planning, CancellationToken cancellationToken) =>
        ReadColonyFactsAsync(identity, planning, cancellationToken);

    Task<Tables> IColonySource.FrameTablesAsync(C.Identity identity, CancellationToken cancellationToken) =>
        FrameTablesAsync(identity, cancellationToken);

    Task<LifecyclePb.TickReply> ISource.TickAsync(CancellationToken cancellationToken) =>
        TickAsync(cancellationToken);

    async Task<BridgeCatalog?> IDefinitionSource.DefinitionCatalogAsync(C.Identity id, CancellationToken cancellationToken) =>
        await DefinitionCatalogAsync(id, cancellationToken).ConfigureAwait(false);

    Task<ResearchRead> IResearchSource.ReadResearchAsync(C.Identity id, CancellationToken cancellationToken) =>
        ReadResearchAsync(id, cancellationToken);

    Task<RoundsFrame> IRoundsSource.ReadRoundsFrameAsync(C.Identity identity, CancellationToken cancellationToken) =>
        ReadRoundsFrameAsync(identity, cancellationToken);

    Task<O.ClearanceTargetsReply> IClearanceSource.ReadClearanceTargetsAsync(C.Identity identity, bool includeSalvage, CancellationToken cancellationToken) =>
        ReadClearanceTargetsAsync(identity, includeSalvage, cancellationToken);

    Task<O.ClearanceTargetsReply> IGroundClearanceSource.ReadClearanceTargetsOnGroundAsync(C.Identity identity, bool includeSalvage, IReadOnlyList<O.Rectangle> ground, CancellationToken cancellationToken) =>
        ReadClearanceTargetsOnGroundAsync(identity, includeSalvage, ground, cancellationToken);

    Task<O.ListBuildingsReply> IRefrigerationSource.ReadConstructionBuildingsAsync(C.Identity identity, IReadOnlyList<string> ids, CancellationToken cancellationToken) =>
        ReadConstructionBuildingsAsync(identity, ids, cancellationToken);

    Task<ZonesRead> IZonesNative.ReadZoneSectionAsync(C.Identity identity, CancellationToken cancellationToken) =>
        ReadZoneSectionAsync(identity, cancellationToken);

    // ---- Clock ----------------------------------------------------------------------------------------------

    Task<LifecyclePb.IdentityReply> IClockNative.IdentityAsync(CancellationToken ct) =>
        IdentityAsync(ct);

    Task<PbClock.StatusReply> IClockNative.ReadClockStatusAsync(C.Identity identity, CancellationToken ct) =>
        ReadClockStatusAsync(identity, ct);

    Task<EntityRows<O.BuildingState>> IEntityNative.ReadBuildingsAsync(C.Identity identity, CancellationToken ct) =>
        ReadBuildingsAsync(identity, ct);

    Task<EntityRows<O.BillStackRow>> IEntityNative.ReadBillStacksAsync(C.Identity identity, CancellationToken ct) =>
        ReadBillStacksAsync(identity, ct);

    Task<O.BundleSnapshot?> IColonyEventNative.ReadStepAsync(StepRequest request, CancellationToken ct) =>
        ReadStepAsync(request, ct);

    Task<O.BundleSnapshot?> IClockStepNative.ReadStepAsync(StepRequest request, CancellationToken ct) =>
        ReadStepAsync(request, ct);

    Task<WorldProgressionRead> IClockStepNative.ReadWorldProgressionAsync(C.Identity identity, CancellationToken ct) =>
        ReadWorldProgressionAsync(identity, ct);

    Task<System.Threading.Channels.ChannelReader<ColonyEvent>> IColonyEventNative.OpenColonyEventsAsync(CancellationToken ct) =>
        OpenColonyEventsAsync(ct);

    long IColonyEventNative.ColonyEventsLost => ColonyEventsLost;

    Task<Bridge.Contracts.PlanningWindow> IPlanningWindowNative.ReadPlanningWindowAsync(C.Identity identity, Policy.Rectangle region, CancellationToken ct) =>
        ReadPlanningWindowAsync(identity, region, ct);

    Task<EmergencyObservation> IClockDriverNative.ReadEmergencyAsync(C.Identity identity, CancellationToken ct) =>
        ReadEmergencyAsync(identity, ct);

    // ---- Colony and presentation ----------------------------------------------------------------------------

    Task<LifecyclePb.IdentityReply> IColonyStatusNative.IdentityAsync(CancellationToken ct) =>
        IdentityAsync(ct);

    Task<O.ColonyFactsReply> IColonyStatusNative.ReadColonyFactsAsync(C.Identity identity, bool full, CancellationToken ct) =>
        ReadColonyFactsAsync(identity, full, ct);

    Task<O.ListPawnsReply> IColonyStatusNative.ReadHomeColonistsAsync(C.Identity identity, CancellationToken ct) =>
        ReadHomeColonistsAsync(identity, ct);

    // ---- Pawn reads of the boundary, haul and break paths ---------------------------------------------------

    Task<O.ListPawnsReply> IArrivalPawns.ReadCombatPawnsAsync(C.Identity identity, IReadOnlyList<string> pawnIds, CancellationToken ct) =>
        ReadCombatPawnsAsync(identity, pawnIds, ct);

    Task<PbAuthority.ControlReply> IMapFocusNative.FocusMapAsync(PbAuthority.FocusMap request, CancellationToken ct) =>
        FocusMapAsync(request, ct);

    Task<EmergencyObservation> IBoundaryNative.ReadEmergencyAsync(C.Identity identity, CancellationToken ct) =>
        ReadEmergencyAsync(identity, ct);

    Task<BuildingPreview> IBoundaryNative.PreviewBuildingAsync(DomainAction action, GenerationSnapshot snapshot, CancellationToken ct) =>
        PreviewBuildingAsync(action, snapshot, ct);

    Task<MapBounds> IBoundaryNative.ReadMapBoundsAsync(C.Identity identity, Cell anchor, CancellationToken ct) =>
        ReadMapBoundsAsync(identity, anchor, ct);

}
