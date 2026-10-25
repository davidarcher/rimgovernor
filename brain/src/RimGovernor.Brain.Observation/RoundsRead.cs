using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.Observation;

/// <summary>
/// The routine census: one decoded frame per reading (the bridge client's ReadRoundsFrame) with the load's
/// definition catalog, whose race rows are the herd plan's race catalog (DefinitionCatalog.AnimalRaces).
/// </summary>
public interface IRoundsSource : IColonySource, IDefinitionSource
{
    Task<RoundsFrame> ReadRoundsFrameAsync(C.Identity identity, CancellationToken cancellationToken);
}

public sealed record RoundsReading
{
    /// <summary>The colony reading the frame decoded to.</summary>
    public required ColonyReading Reading { get; init; }

    /// <summary>The embedded reading's projection.</summary>
    public ColonyProjection Projection => Reading.Projection;

    /// <summary>When the embedded reading began.</summary>
    public DateTimeOffset StartedAt => Reading.StartedAt;

    /// <summary>When the embedded reading finished.</summary>
    public DateTimeOffset ObservedAt => Reading.ObservedAt;


    public required EmergencyFacts Emergency { get; init; }

    /// <summary>The reading's decoded census, per section with the tick each reply described, for the step's fact store.</summary>
    public RoundsSections Sections { get; init; } = new();

    /// <summary>The frame the reading decoded.</summary>
    public required RoundsFrame Frame { get; init; }

    /// <summary>The load's definition catalog the reading decoded against, null for a source without one.</summary>
    public DefinitionCatalog? Catalog { get; init; }
}

/// <summary>Answers ObserveColony's reads from the coherent frame.</summary>
internal sealed class FrameColony(IRoundsSource inner, RoundsFrame frame) : IColonySource, IDefinitionSource, IResearchSource
{
    public Task<L.LoadedTick> TickAsync(CancellationToken cancellationToken) => inner.TickAsync(cancellationToken);

    public Task<L.LoadedIdentity> IdentityAsync(CancellationToken cancellationToken) => inner.IdentityAsync(cancellationToken);

    public Task<O.ColonyFactsSnapshot> ReadColonyFactsAsync(C.Identity identity, CancellationToken cancellationToken) =>
        Task.FromResult(frame.Colony ?? throw new BridgeUnavailableException("The frame holds no colony facts."));

    public Task<Tables> FrameTablesAsync(C.Identity identity, CancellationToken cancellationToken) => Task.FromResult(frame.Tables with
    {
        Rooms = frame.Rooms ?? throw new BridgeContractException("missing total rooms census"), RoomCells = frame.RoomCells,
        Zones = frame.Zones ?? throw new BridgeContractException("missing total zones census"),
    });

    /// <summary>The load's catalog.</summary>
    public Task<DefinitionCatalog> DefinitionCatalogAsync(C.Identity id, CancellationToken cancellationToken) => inner.DefinitionCatalogAsync(id, cancellationToken);

    /// <summary>The frame's research section.</summary>
    public Task<O.ResearchSnapshot> ReadResearchAsync(C.Identity id, CancellationToken cancellationToken) => Task.FromResult(RoundsReads.ResearchSection(frame));
}

public static class RoundsReads
{
    public static Task<RoundsReading> ObserveRoundsOwned(IRoundsSource? source, IClock clock, Identity expected, TimeSpan maxAge, EquatableArray<ConstructionClaim> claims, IReadOnlyList<string> definitions, CancellationToken cancellationToken) =>
        ObserveRounds(source, clock, expected, maxAge, claims, true, definitions, cancellationToken);

    /// <summary>Decodes the same native frame and room observations without fetching the development planning window or its definition reads.</summary>
    public static Task<RoundsReading> ObserveRoundsProtection(IRoundsSource? source, IClock clock, Identity expected, TimeSpan maxAge, EquatableArray<ConstructionClaim> claims, CancellationToken cancellationToken) =>
        ObserveRounds(source, clock, expected, maxAge, claims, false, [], cancellationToken);

    /// <summary>
    /// Decodes one frame. ObserveColony checks the frame's colony context against expected; every other section of
    /// the frame shares that context and tick, so none is checked against another.
    /// </summary>
    private static async Task<RoundsReading> ObserveRounds(
        IRoundsSource? source, IClock clock, Identity expected, TimeSpan maxAge, EquatableArray<ConstructionClaim> claims,
        bool planning, IReadOnlyList<string> definitions, CancellationToken cancellationToken)
    {
        if (source is null)
        {
            throw new ObservationContractException("missing observation dependency");
        }
        var id = new C.Identity { ColonyId = expected.Colony.Value, LoadToken = expected.Load.Value, MapId = expected.Map.Value };
        var started = clock.Now();
        var frame = await source.ReadRoundsFrameAsync(id, cancellationToken).ConfigureAwait(false);
        var catalog = await source.DefinitionCatalogAsync(id, cancellationToken).ConfigureAwait(false);
        if (frame.Colony is null || frame.Emergency.Context is null)
        {
            throw new ObservationContractException("rounds frame lacks colony or emergency context");
        }
        var colonySource = new FrameColony(source, frame);
        // Every colony projection uses the frame's own zone census, including protection and direct reads.
        var reading = await ColonyReads.ObserveColony(colonySource, clock, expected, maxAge, planning, cancellationToken).ConfigureAwait(false);
        var p = reading.Projection;
        var colony = frame.Colony;
        var emergency = frame.Emergency.Facts;
        // Room-role furniture rides every routine reading: the catalog names which definitions the planners
        // furnish rooms from.
        var roleRows = catalog.RoomRoleRows();
        p = p with { Shapes = catalog.PieceShapes(), Stations = catalog.StationCatalog() };
        var names = definitions.Concat(roleRows).Concat(p.Shapes.Furniture.Definitions());
        p = p with { Definitions = FrameDefinitionFacts(frame, catalog).AppendDefinitions([.. p.Definitions], names) };
        p = p with
        {
            Facts = p.Facts with { Items = catalog.ItemFacts(), ConstructionClaims = claims },
        };
        var finished = new HashSet<string>(ResearchSection(frame).Finished, StringComparer.Ordinal);
        p = p with
        {
            Facts = p.Facts with
            {
                MedicalReserve = p.Facts.MedicalReserve with { Catalog = p.Facts.Items },
                Recipes = catalog.RecipeFacts(),
                CapacityPenalties = catalog.CapacityPenalties(),
                ThreatTable = catalog.ThreatSchedule(),
                DiseaseTable = catalog.DiseaseTable(),
                HarmCatalog = new()
                {
                    Raid = catalog.RaidFactions(), Coverage = catalog.BodyCoverage(), Natural = catalog.NaturalArmor(),
                    Parts = new() { Rules = catalog.PartHitRules(), Recipes = catalog.PartReplacementRecipes() },
                },
                MoodBreak = p.Stations.Mood.Break,
                ResearchBenchSpeeds = catalog.ResearchBenchSpeeds(),
                LaboratoryBench = p.Shapes.Furniture.BenchFor(RoomRole.Laboratory),
                SleepingBeds = catalog.SleepingBeds(),
                ComfortCurve = p.Stations.Comfort.Comfort,
                HospitalBed = p.Shapes.Furniture.HospitalBed(),
                Filth = p.Stations.Filth,
                Crafts = catalog.CraftRecipes(finished),
                WorkSpeeds = catalog.WorkSpeedFacts(),
                FoodWork = catalog.FoodWork(finished),
            },
        };
        p = p with { Facts = p.Facts with { Containment = RoundsContainmentReads.ContainmentPlanningOf(frame, catalog, p.Definitions) } };
        var buildable = p.DefinitionAvailable(p.Shapes.Furniture.PrimaryBed());
        var sleeping = p.Facts.Sleeping with { BedBuildable = buildable };
        p = p with { Facts = p.Facts with { Sleeping = sleeping, VacantColonistBeds = PawnValueRules.VacantColonistBeds(sleeping) } };
        var census = frame.Buildings ?? throw new ObservationContractException("a routine frame carries no building census");
        p = p with { Facts = p.Facts with { BillReservations = ConstructionReads.BillReservations(frame.Bills) } };
        var pawns = frame.Pawns ?? throw new BridgeContractException("routine frame without its pawn census");
        p = p with { Facts = p.Facts with { StockArrival = StockArrivalReads.Of(catalog, finished, frame.Bills, pawns, colony, p.Facts.Items.MedicineTiers()) } };
        p = p with
        {
            Facts = p.Facts with
            {
                Research = FrameResearch(ResearchSection(frame), catalog),
                BuiltBuildings = BuiltBuildingsOf(census),
            },
        };
        p = p with { TechTier = TechTierRules.SelectTechTier(FinishedResearch(p.Facts.Research), p.PlayerTechLevel) };
        var quests = frame.Quests ?? throw new ObservationContractException("a routine frame carries no world progression");
        var (questCapacity, questPawns) = QuestReads.FrameQuestPawns(quests, expected.Map, emergency, RoundsMoodReads.RoundsDefenders(colony, emergency, pawns), p.WorkPawns);
        p = p with
        {
            Facts = p.Facts with
            {
                Traders = FrameTraders(frame.Traders),
                QuestOffers = EquatableArray.From(quests.Quests),
                QuestRoots = FrameQuestRoots(quests, catalog),
                ColonyMap = expected.Map,
                QuestSites = EquatableArray.From(quests.Sites),
                QuestExpeditionTrips = QuestReads.FrameExpeditionTrips(quests, expected.Map),
                QuestWorkCapacity = questCapacity,
                QuestPawns = questPawns,
                QuestObservedTick = new Tick(quests.Context.Tick),
                ForecastObserved = p.Facts.ForecastObserved with { WorkPawns = p.WorkPawns, Needs = colony.Forecast.Needs, Coupled = colony.Forecast.Coupled },
                EssentialInputs = new() { Benches = p.ProductionBenches, Power = p.PowerPlanning },
            },
        };
        p = p with
        {
            Facts = p.Facts with
            {
                Ideology = frame.Ideology,
                Worship = WorshipRules.FactsOf(frame.Ideology),
            },
        };
        p = p with { Facts = DlcSectionReads.Installed(p.Facts, colony) };
        p = p with { Facts = p.Facts with { RitualSites = RitualSitesReads.RitualSitesOf(census, p.Facts.Ideology) } };
        p = p with
        {
            Facts = p.Facts with
            {
                AnimalUpkeep = p.Facts.AnimalUpkeep with { Animals = HerdPreceptsRules.ApplyHerdPrecepts(p.Facts.AnimalUpkeep.Animals, p.Facts.Ideology) },
            },
        };
        var population = frame.Population ?? throw new ObservationContractException("a routine frame carries no population census");
        p = p with
        {
            Facts = p.Facts with
            {
                Royalty = ColonyProjection.RoyaltyOf(p.RoyaltySection, pawns, catalog),
                Population = population with { Colony = population.Colony with { Ideoligion = p.Facts.Ideology } },
            },
        };
        p = WithFrameRooms(p, frame, catalog.DiningFurniture());
        var roomCensus = frame.Rooms;
        // The reading is observed once the frame lands, under the age bound.
        var observedAt = clock.Now();
        if (observedAt - started > maxAge)
        {
            throw new ObservationStaleException();
        }
        cancellationToken.ThrowIfCancellationRequested();
        p = p with { Facts = QuestReads.StampQuestGuestProtection(p.Facts) };
        p = p with { Facts = QuestReads.StampQuestRefugees(p.Facts) };
        return new RoundsReading
        {
            Reading = reading with { Projection = p, StartedAt = started, ObservedAt = observedAt },
            Emergency = emergency,
            Sections = RoundsMoodReads.BuildRoundsSections(frame, p, roomCensus),
            Frame = frame,
            Catalog = catalog,
        };
    }

    /// <summary>Joins every routine frame's required room table to comfort; detailed room consumers are opt-in.</summary>
    internal static ColonyProjection WithFrameRooms(ColonyProjection p, RoundsFrame frame, DiningFurniture dining)
    {
        // The frame's rooms census is the one room table: room quality reads it on every reading, the room census
        // sections only when asked.
        var frameRooms = frame.Rooms ?? throw new ObservationContractException("a routine frame carries no room census");
        var bedded = p.Facts.Sleeping with { Rooms = SleepingUpkeepReads.UpkeepRooms(frameRooms, p.Facts.Sleeping.Beds) };
        var comfortRooms = new RoomObservation
        {
            Rooms = frameRooms.Rooms,
            Cells = frame.RoomCells,
            EligibleBeds = EquatableArray.From(p.Facts.Sleeping.Beds.Where(bed => bed.Humanlike && !bed.Medical && !bed.Prisoners).Select(bed => bed.Id)),
            Dining = dining,
            Shapes = p.Shapes,
        };
        p = p with { Facts = p.Facts with { Sleeping = bedded, Comfort = ComfortRules.HostedComfort(p.Facts.RequireBasicComfort(), comfortRooms) } };
        p = p with { Rooms = comfortRooms, Facts = p.Facts with { SleepingTemperature = TemperatureMethodRules.TemperatureRange(comfortRooms) } };
        return p;
    }

    /// <summary>Resolves definitions against the frame's catalog, research and crop rows.</summary>
    internal static DefinitionFacts FrameDefinitionFacts(RoundsFrame frame, DefinitionCatalog catalog) =>
        new()
        {
            Catalog = catalog,
            Crops = DefinitionsReads.CropRows((frame.Colony ?? throw new RequiredFactException("colony")).Crops),
            Finished = DefinitionsReads.FinishedSet((frame.Research ?? throw new RequiredFactException("finished research")).Finished),
        };

    /// <summary>
    /// The catalog's classification of every quest root the census holds, once per root: its profile and, for a
    /// ship-only Odyssey root, the space layer (QuestClass). A root the catalog cannot classify carries why the class
    /// failed ("" when only the profile did).
    /// </summary>
    internal static EquatableArray<QuestRoot> FrameQuestRoots(WorldProgressionRead read, DefinitionCatalog catalog)
    {
        var roots = new List<QuestRoot>();
        foreach (var def in read.Quests.Select(q => q.ScriptDef).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            string layer = "", classError = "";
            try
            {
                layer = catalog.QuestClass(def).SpaceLayer;
            }
            catch (DomainException e)
            {
                classError = e.Message;
            }
            try
            {
                roots.Add(new QuestRoot.Classified { ScriptDef = def, Profile = catalog.QuestProfile(def), SpaceLayer = layer });
            }
            catch (DomainException)
            {
                roots.Add(new QuestRoot.Unclassified { ScriptDef = def, Error = classError });
            }
        }
        return EquatableArray.From(roots);
    }

    /// <summary>The research census; unknown when the frame carries none.</summary>
    /// <summary>The finished buildings of the frame's building table counted by definition (what a built-count goal is judged by).</summary>
    internal static EquatableArray<Amount> BuiltBuildingsOf(BuildingCensus census)
    {
        var counts = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var row in census.Rows.Table.Values())
        {
            if (row.Status == O.BuildingStatus.Built)
            {
                counts[row.Building.DefName] = counts.GetValueOrDefault(row.Building.DefName) + 1;
            }
        }
        return EquatableArray.From(counts.Select(c => new Amount(new Resource(c.Key), c.Value)));
    }

    /// <summary>The research section of a frame read from the source; the empty carrier frame has none.</summary>
    internal static O.ResearchSnapshot ResearchSection(RoundsFrame frame) =>
        frame.Research ?? throw new BridgeUnavailableException("The frame holds no research section.");

    internal static ResearchFacts FrameResearch(O.ResearchSnapshot read, DefinitionCatalog catalog) =>
        EconomyRules.ResearchSummary(EconomyRules.ResearchCensusOf(read, catalog.Research, catalog.KnowledgeOverflow));

    /// <summary>The trader census; unknown when the frame carries none.</summary>
    internal static EquatableArray<RimGovernor.Contracts.Wire.Observations.Trader> FrameTraders(RimGovernor.Contracts.Wire.Observations.TradersSnapshot? read) =>
        read?.Traders ?? throw new BridgeUnavailableException("The frame holds no trader census.");

    /// <summary>The finished project list of a known research census, the input TechTier is selected from.</summary>
    public static Fact<EquatableArray<ResearchProjectId>> FinishedResearch(ResearchFacts? research) =>
        research is { } facts ? Known(facts.Finished) : Unknown<EquatableArray<ResearchProjectId>>();
}

public sealed partial record ColonyProjection
{
    /// <summary>
    /// Appends the planning rows of names the frame's catalog describes and the projection does not hold yet (a
    /// definition only a later read names, such as a title's throne).
    /// </summary>
    public ColonyProjection AddDefinitions(RoundsFrame frame, DefinitionCatalog? catalog, IEnumerable<string> names) =>
        catalog is null
            ? this
            : this with { Definitions = RoundsReads.FrameDefinitionFacts(frame, catalog).AppendDefinitions([.. Definitions], names) };
}
