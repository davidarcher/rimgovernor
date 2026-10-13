using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Domain;
using BridgeCatalog = RimGovernor.Brain.Bridge.DefinitionCatalog;
using P = RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Host;

/// <summary>
/// The decoders of the other client groups, bound for the frame path (<see cref="IFrameSections"/>): each member calls the
/// decoder Go's <c>frames.go</c> called by the same name. It holds the client through a delegate because the client is built
/// with this adapter already assigned to <see cref="FacadeNativeClient.FrameSections"/>.
/// </summary>
internal sealed class FrameSectionsAdapter(Func<FacadeNativeClient> client) : IFrameSections
{
    /// <summary>Go <c>tableDetails</c>: every detail family a pawn table row may carry; the table varies detail by pawn kind, so a row is checked against the union.</summary>
    private static readonly FacadeNativeClient.PawnDetailSelection TableDetails = new(Combat: true, Work: true, Care: true, Schedule: true, Social: true);

    /// <inheritdoc />
    public void CheckPawnRow(PbObservations.PawnState row, PbCommon.ObservationContext context)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(context);
        var snapshot = new PbObservations.PawnSnapshot { Context = context, Pawns = EquatableArray.From([row]) };
        FacadeNativeClient.PawnReadChecks.PawnsSnapshotSelected(snapshot, context.Identity, new HashSet<string>(StringComparer.Ordinal) { row.Pawn?.Id ?? "" }, TableDetails);
    }

    /// <inheritdoc />
    public void CheckBuiltRow(PbObservations.BuildingState row, PbCommon.ObservationContext context)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.Status != PbObservations.BuildingStatus.Built)
        {
            return;
        }
        FacadeNativeClient.PlacementReads.ValidateConstructionRow(row, context);
    }

    /// <inheritdoc />
    public void CheckBuildingListRow(PbObservations.BuildingState row)
    {
        BridgeFunctions.BuildingUnknown(row);
        if (row is null || row.Building is null || !BridgeFunctions.IsValidId(row.Building.Id ?? ""))
        {
            throw BridgeFunctions.Contract("invalid entity identity");
        }
        FacadeNativeClient.PlacementReads.ValidateBuildingOdyssey(row.Odyssey);
        FacadeNativeClient.PlacementReads.ValidateBuildingAnomaly(row.Anomaly);
    }

    /// <inheritdoc />
    public void CheckEntity(PbObservations.EntityRef? entity, PbCommon.ObservationContext context) =>
        FacadeNativeClient.PawnReadChecks.PawnsEntity(entity, context);

    /// <inheritdoc />
    public Pawns PawnTable(PbObservations.PawnSnapshot? snapshot, PbCommon.Identity identity)
    {
        if (snapshot is null)
        {
            return default;
        }
        var requested = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in snapshot.Pawns)
        {
            requested.Add(row.Pawn?.Id ?? "");
        }
        FacadeNativeClient.PawnReadChecks.PawnsSnapshotSelected(snapshot, identity, requested, TableDetails);
        return Pawns.Of(snapshot.Pawns);
    }

    /// <inheritdoc />
    public EmergencyObservation DecodeEmergencyStatus(PbObservations.StatusSnapshot snapshot, Pawns pawns, PbCommon.Identity identity) =>
        FacadeNativeClient.EmergencyDecode.DecodeEmergencyStatus(snapshot, pawns, identity);

    /// <inheritdoc />
    public PrisonerCensus DecodePopulation(PbObservations.PopulationSnapshot snapshot, Pawns pawns, BridgeCatalog catalog) =>
        PopulationDecoder.Decode(snapshot, pawns, catalog);

    /// <inheritdoc />
    public ResearchRead DecodeResearch(PbObservations.ResearchSnapshot snapshot, PbCommon.Identity identity, BridgeCatalog catalog) =>
        FacadeNativeClient.ResearchReadSnapshot(snapshot, identity, catalog);

    /// <inheritdoc />
    public TradersRead DecodeTraders(PbObservations.TradersSnapshot snapshot, PbCommon.Identity identity) =>
        FacadeNativeClient.TradeDecodeTraders(snapshot, identity);

    /// <inheritdoc />
    public WorldProgressionRead DecodeWorldProgression(PbObservations.WorldProgressionSnapshot snapshot, PbCommon.Identity identity) =>
        BridgeFunctions.DecodeWorldProgressionSnapshot(snapshot, identity);

    /// <inheritdoc />
    public P.Ideoligion? DecodeIdeology(PbObservations.IdeologySnapshot snapshot, PbCommon.Identity identity, BridgeCatalog catalog) =>
        IdeologyDecoder.Decode(snapshot, identity, catalog);

    /// <inheritdoc />
    public ZonesRead DecodeZones(PbObservations.ZonesSnapshot snapshot, PbCommon.Identity identity) =>
        FacadeNativeClient.PlacementReads.DecodeZones(snapshot, identity);

    /// <inheritdoc />
    public IReadOnlyList<LineOfFire> ValidateLinesOfFire(PbObservations.LinesOfFireSnapshot snapshot, PbCommon.Identity identity, IReadOnlyList<Cell> firing, IReadOnlyList<Cell> approach) =>
        FacadeNativeClient.DefenseValidateLinesOfFire(snapshot, identity, firing, approach);

    /// <inheritdoc />
    public async Task<BridgeCatalog> DefinitionCatalogAsync(PbCommon.Identity identity, CancellationToken cancellationToken) =>
        await client().DefinitionCatalogAsync(identity, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public PbObservations.StatusRequest EmergencyRequest(PbCommon.Identity identity) =>
        FacadeNativeClient.EmergencyRequest(identity);

    /// <inheritdoc />
    public async Task<PbClock.Status> ReadClockStatusAsync(PbCommon.Identity identity, CancellationToken cancellationToken)
    {
        var (reply, _) = await client().ReadClockStatusAsync(identity, cancellationToken).ConfigureAwait(false);
        return FacadeWriteChecks.ClockStatusReply(reply, identity);
    }
}

/// <summary>
/// The recipe facts of the gear bench census (<see cref="IGearRecipeCatalog"/>) read from the definition catalog; the
/// catalog's <c>Recipe*</c> methods carry the same names and answer the same questions.
/// </summary>
internal sealed class GearRecipeCatalogAdapter(BridgeCatalog catalog) : IGearRecipeCatalog
{
    /// <inheritdoc />
    public RecipeRole RecipeRoleOf(string name) => catalog.RecipeRoleOf(name);

    /// <inheritdoc />
    public IReadOnlyList<P.Resource> RecipeProducts(string name) => catalog.RecipeProducts(name);

    /// <inheritdoc />
    public Fact<EquatableArray<EquatableArray<P.Amount>>> RecipeIngredients(string name) => catalog.RecipeIngredients(name);

    /// <inheritdoc />
    public Fact<EquatableArray<P.WorkRequirement>> RecipeWork(string name, string bench) => catalog.RecipeWork(name, bench);

    /// <inheritdoc />
    public Fact<double> RecipeWorkAmount(string name) => catalog.RecipeWorkAmount(name);

    /// <inheritdoc />
    public string RecipeMechKind(string name) => catalog.RecipeMechKind(name);

    /// <inheritdoc />
    public P.LedgerBillKind RecipeLedgerKind(string name, string mechKind) => catalog.RecipeLedgerKind(name, mechKind);

    /// <inheritdoc />
    public P.ArmoryTier RecipeArmoryTier(string name, string bench) => catalog.RecipeArmoryTier(name, bench);
}
