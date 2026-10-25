using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Runtime;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Bridge;
using BridgeCatalog = RimGovernor.Brain.Bridge.DefinitionCatalog;

namespace RimGovernor.Brain.Runtime;

// Explicit bindings of the typed core methods (GameClient.*.cs) to the interfaces that stay: the Observation
// sources, and the building runtime's interfaces that a test fake or a second implementer also satisfies. Every other
// consumer calls the core methods directly.
public sealed partial class GameClient :
    IReviewSource,
    IResearchSource,
    IClearanceSource,
    IRefrigerationSource,
    IZonesNative,
    IClockNative,
    IEntityNative,
    IPlannerStepNative,
    IClockDriverNative,
    IColonyStatusNative,
    IArrivalPawns,
    IBoundaryNative,
    IReviewBuildingSource,
    IReviewWorkBenchSource,
    IReviewDefenseSource
{
    /// <summary>
    /// The client over <paramref name="facade"/> (which tallies every native call itself), with
    /// <see cref="FrameSections"/> bound to the decoders of the other core files.
    /// </summary>
    /// <param name="facade">The in-process native facade.</param>
    /// <param name="catalogFactory">
    /// Builds the C# definition catalog of a load from the game's Defs
    /// (<see cref="CatalogFactory"/>). The game's Defs are not on the wire, so only the host composition that holds them
    /// can supply it; without one <c>DefinitionCatalogAsync</c> (and the reads that need the catalog) refuse.
    /// </param>
    internal static GameClient Create(IGameApi facade, Func<RimGovernor.Brain.GameFacts.Common.Identity, BridgeCatalog>? catalogFactory = null)
    {
        ArgumentNullException.ThrowIfNull(facade);
        GameClient? client = null;
        client = new GameClient(facade)
        {
            CatalogFactory = catalogFactory,
        };
        return client;
    }

    // ---- Observation ----------------------------------------------------------------------------------------

    Task<RimGovernor.Brain.GameFacts.Lifecycle.LoadedIdentity> IColonySource.IdentityAsync(CancellationToken cancellationToken) =>
        IdentityAsync(cancellationToken);

    Task<RimGovernor.Brain.GameFacts.Observations.ColonyFactsSnapshot> IColonySource.ReadColonyFactsAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, CancellationToken cancellationToken) =>
        ReadColonyFactsAsync(identity, cancellationToken);

    Task<Tables> IColonySource.FrameTablesAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, CancellationToken cancellationToken) =>
        FrameTablesAsync(identity, cancellationToken);

    Task<RimGovernor.Brain.GameFacts.Lifecycle.LoadedTick> IObservationSource.TickAsync(CancellationToken cancellationToken) =>
        TickAsync(cancellationToken);

    Task<BridgeCatalog> IDefinitionSource.DefinitionCatalogAsync(RimGovernor.Brain.GameFacts.Common.Identity id, CancellationToken cancellationToken) =>
        DefinitionCatalogAsync(id, cancellationToken);

    Task<RimGovernor.Brain.GameFacts.Observations.ResearchSnapshot> IResearchSource.ReadResearchAsync(RimGovernor.Brain.GameFacts.Common.Identity id, CancellationToken cancellationToken) =>
        ReadResearchAsync(id, cancellationToken);

    Task<ReviewFrame> IReviewSource.ReadReviewFrameAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, CancellationToken cancellationToken) =>
        ReadReviewFrameAsync(identity, cancellationToken);

    Task<RimGovernor.Brain.GameFacts.Observations.ClearanceTargetsSnapshot> IClearanceSource.ReadClearanceAsync(Identity expected, bool includeSalvage, IReadOnlyList<Rectangle> ground, CancellationToken cancellationToken) =>
        ReadClearanceAsync(expected, includeSalvage, ground, cancellationToken);

    Task<RimGovernor.Brain.GameFacts.Observations.BuildingsSnapshot> IRefrigerationSource.ReadConstructionBuildingsAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, IReadOnlyList<string> ids, CancellationToken cancellationToken) =>
        ReadConstructionBuildingsAsync(identity, ids, cancellationToken);

    Task<RimGovernor.Brain.GameFacts.Observations.ZonesSnapshot> IZonesNative.ReadZoneSectionAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, CancellationToken cancellationToken) =>
        ReadZoneSectionAsync(identity, cancellationToken);

    // ---- Clock ----------------------------------------------------------------------------------------------

    Task<RimGovernor.Brain.GameFacts.Lifecycle.LoadedIdentity> IClockNative.IdentityAsync(CancellationToken ct) =>
        IdentityAsync(ct);

    Task<EntityRows<RimGovernor.Brain.GameFacts.Observations.BuildingState>> IEntityNative.ReadBuildingsAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, CancellationToken ct) =>
        ReadBuildingsAsync(identity, ct);

    Task<EntityRows<RimGovernor.Brain.GameFacts.Observations.BillStackRow>> IEntityNative.ReadBillStacksAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, CancellationToken ct) =>
        ReadBillStacksAsync(identity, ct);

    Task<RimGovernor.Brain.GameFacts.Observations.BundleSnapshot?> IColonyEventNative.ReadStepAsync(StepRequest request, CancellationToken ct) =>
        ReadStepAsync(request, ct);

    Task<RimGovernor.Brain.GameFacts.Observations.BundleSnapshot?> IPlannerStepNative.ReadStepAsync(StepRequest request, CancellationToken ct) =>
        ReadStepAsync(request, ct);

    Task<RimGovernor.Brain.GameFacts.Observations.WorldProgressionSnapshot> IPlannerStepNative.ReadWorldProgressionAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, CancellationToken ct) =>
        ReadWorldProgressionAsync(identity, ct);

    Task<System.Threading.Channels.ChannelReader<ColonyEvent>> IColonyEventNative.OpenColonyEventsAsync(CancellationToken ct) =>
        OpenColonyEventsAsync(ct);

    long IColonyEventNative.ColonyEventsLost => ColonyEventsLost;

    Task<GameAccess.PlanningWindow> IPlanningWindowNative.ReadPlanningWindowAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, Policy.Rectangle region, CancellationToken ct) =>
        ReadPlanningWindowAsync(identity, region, ct);

    Task<EmergencyObservation> IClockDriverNative.ReadEmergencyAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, CancellationToken ct) =>
        ReadEmergencyAsync(identity, ct);

    // ---- Colony and presentation ----------------------------------------------------------------------------

    Task<RimGovernor.Brain.GameFacts.Lifecycle.LoadedIdentity> IColonyStatusNative.IdentityAsync(CancellationToken ct) =>
        IdentityAsync(ct);

    Task<RimGovernor.Brain.GameFacts.Observations.ColonyFactsSnapshot> IColonyStatusNative.ReadColonyFactsAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, CancellationToken ct) =>
        ReadColonyFactsAsync(identity, ct);

    Task<RimGovernor.Brain.GameFacts.Observations.PawnSnapshot> IColonyStatusNative.ReadHomeColonistsAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, CancellationToken ct) =>
        ReadHomeColonistsAsync(identity, ct);

    // ---- Pawn reads of the boundary, haul and break paths ---------------------------------------------------

    Task<RimGovernor.Brain.GameFacts.Observations.PawnSnapshot> IArrivalPawns.ReadPawnsAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, IReadOnlyList<string> pawnIds, CancellationToken ct) =>
        ReadPawnsAsync(identity, pawnIds, ct);


    Task<EmergencyObservation> IBoundaryNative.ReadEmergencyAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, CancellationToken ct) =>
        ReadEmergencyAsync(identity, ct);

    Task<BuildingPreview> IBoundaryNative.PreviewBuildingAsync(DomainAction action, GenerationSnapshot snapshot, CancellationToken ct) =>
        PreviewBuildingAsync(action, snapshot, ct);

    Task<MapBounds> IBoundaryNative.ReadMapBoundsAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, Cell anchor, CancellationToken ct) =>
        ReadMapBoundsAsync(identity, anchor, ct);

}
