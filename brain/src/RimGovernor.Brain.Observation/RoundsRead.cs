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

/// <summary>Answers ObserveColony's colony facts and zone reads from the frame.</summary>
internal sealed class FrameColony(IRoundsSource inner, RoundsFrame frame) : IColonySource, IZonesNative, IZonesSource, IDefinitionSource, IResearchSource
{
    public Task<L.LoadedTick> TickAsync(CancellationToken cancellationToken) => inner.TickAsync(cancellationToken);

    public Task<L.LoadedIdentity> IdentityAsync(CancellationToken cancellationToken) => inner.IdentityAsync(cancellationToken);

    public Task<O.ColonyFactsSnapshot> ReadColonyFactsAsync(C.Identity identity, bool planning, CancellationToken cancellationToken) =>
        Task.FromResult(frame.Colony ?? throw new BridgeUnavailableException("The frame holds no colony facts."));

    public Task<Tables> FrameTablesAsync(C.Identity identity, CancellationToken cancellationToken) => Task.FromResult(frame.Tables);

    public Task<O.ZonesSnapshot> ReadZoneSectionAsync(C.Identity identity, CancellationToken cancellationToken)
    {
        if (frame.Zones is not { } zones)
        {
            throw new BridgeUnavailableException();
        }
        return Task.FromResult(zones);
    }

    public async Task<Held<O.ZonesSnapshot>> ZonesAsync(C.Identity identity, CancellationToken cancellationToken)
    {
        var read = await ReadZoneSectionAsync(identity, cancellationToken).ConfigureAwait(false);
        return new Held<O.ZonesSnapshot> { Value = read, AsOf = read.Context.Tick, Complete = true, Source = "native_frame" };
    }

    /// <summary>The load's catalog.</summary>
    public Task<DefinitionCatalog> DefinitionCatalogAsync(C.Identity id, CancellationToken cancellationToken) => inner.DefinitionCatalogAsync(id, cancellationToken);

    /// <summary>The frame's research section.</summary>
    public Task<O.ResearchSnapshot> ReadResearchAsync(C.Identity id, CancellationToken cancellationToken) => Task.FromResult(RoundsReads.ResearchSection(frame));
}

public static class RoundsReads
{
    public static Task<RoundsReading> ObserveRoundsOwned(IRoundsSource? source, IClock clock, Identity expected, TimeSpan maxAge, Fact<EquatableArray<ConstructionClaim>> claims, IReadOnlyList<string> definitions, CancellationToken cancellationToken) =>
        ObserveRounds(source, clock, expected, maxAge, claims, false, true, definitions, cancellationToken);

    /// <summary>
    /// Additionally takes the frame's room census. Temperature planning takes room heat from it and comfort takes
    /// each facility's hosting room role: without the census no comfort facility can be certified as hosted, so
    /// comfort becomes unknown.
    /// </summary>
    public static Task<RoundsReading> ObserveRoundsRooms(IRoundsSource? source, IClock clock, Identity expected, TimeSpan maxAge, Fact<EquatableArray<ConstructionClaim>> claims, IReadOnlyList<string> definitions, CancellationToken cancellationToken) =>
        ObserveRounds(source, clock, expected, maxAge, claims, true, true, definitions, cancellationToken);

    /// <summary>Decodes the same native frame and room observations without fetching the development planning window or its definition reads.</summary>
    public static Task<RoundsReading> ObserveRoundsProtection(IRoundsSource? source, IClock clock, Identity expected, TimeSpan maxAge, CancellationToken cancellationToken) =>
        ObserveRounds(source, clock, expected, maxAge, Unknown<EquatableArray<ConstructionClaim>>(), true, false, [], cancellationToken);

    /// <summary>
    /// Decodes one frame. ObserveColony checks the frame's colony context against expected; every other section of
    /// the frame shares that context and tick, so none is checked against another.
    /// </summary>
    private static async Task<RoundsReading> ObserveRounds(
        IRoundsSource? source, IClock clock, Identity expected, TimeSpan maxAge, Fact<EquatableArray<ConstructionClaim>> claims,
        bool rooms, bool planning, IReadOnlyList<string> definitions, CancellationToken cancellationToken)
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
        using var zoneScope = planning ? null : ZonesReads.WithZones(colonySource);
        // Protection uses only this coherent frame, never the ordinary scheduler's zone refresher and its
        // potentially blocked census.
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
            Facts = p.Facts with { Items = catalog.ItemFacts() },
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
        if (p.Facts.Sleeping.TryGetValue(out var sleeping))
        {
            sleeping = sleeping with { BedBuildable = p.DefinitionAvailable(p.Shapes.Furniture.PrimaryBed()) };
            p = p with { Facts = p.Facts with { Sleeping = Known(sleeping), VacantColonistBeds = PawnValueRules.VacantColonistBeds(sleeping) } };
        }
        var census = frame.Buildings ?? throw new ObservationContractException("a routine frame carries no building census");
        p = p with { Facts = p.Facts with { CurrentConstruction = ConstructionReads.ConstructionFromCensus(census) } };
        p = p with { Facts = p.Facts with { BillReservations = ConstructionReads.BillReservations(frame.Bills) } };
        var pawns = RoundsPawnsOf(frame, id);
        p = p with
        {
            WorkPawns = default,
            Rest = null,
            Facts = p.Facts with { Armed = default, Unarmed = default, DefenseCapacity = default, MedicalPawns = default, MoodPawns = default },
        };
        if (pawns is not null)
        {
            var (armed, unarmed) = RoundsMoodReads.RoundsArmed(colony, emergency, pawns, catalog.CreepJoinerDownsides());
            p = p with
            {
                Facts = p.Facts with
                {
                    Armed = armed,
                    Unarmed = unarmed,
                    DefenseCapacity = DefenseCapacityPolicy.DefenseCapacity(RoundsMoodReads.RoundsDefenders(colony, emergency, pawns), p.DefenseTurrets),
                },
                MechCatalog = catalog.Biotech?.MechCatalog() ?? new(),
                WorkPawns = RoundsWorkReads.RoundsWork(colony, emergency, pawns, catalog, frame.Tables.Things),
                Rest = RoundsWorkReads.RoundsRest(pawns),
            };
            p = p with { Facts = p.Facts with { MedicalPawns = RoundsContainmentReads.RoundsMedical(colony, emergency, pawns, catalog) } };
            p = p with { Facts = p.Facts with { MoodPawns = RoundsMoodReads.RoundsMood(colony, emergency, pawns) } };
            var thoughtFacts = catalog.AllThoughtFacts();
            p = p with { Facts = p.Facts with { MoodLedger = RoundsMoodReads.RoundsMoodLedger(p.Facts.MoodPawns, pawns, thoughtFacts) } };
        }
        else if (emergency.AllSeen && emergency.Colonists.Count == 0 && colony.ColonistCount == 0)
        {
            p = p with
            {
                Facts = p.Facts with
                {
                    MoodPawns = Known<EquatableArray<MoodPawn>>([]),
                    MoodLedger = Known(MoodRules.BuildMoodLedger([], new Dictionary<string, ThoughtFacts>())),
                },
            };
        }
        p = p with { Facts = p.Facts with { StockArrival = StockArrivalReads.Of(catalog, finished, frame.Bills, pawns, colony, p.Facts.Items.MedicineTiers()) } };
        p = p with { Mechs = frame.Tables.Pawns.Count > 0 ? Known(RoundsContainmentReads.MechFleetOf(frame.Tables.Pawns.Table.Values())) : Unknown<MechFleet>() };
        p = p with
        {
            Facts = p.Facts with
            {
                RecoveryWorkers = ColonyFramesReads.RecoveryWorkers(p.Facts.MoodPawns),
                Gear = ColonyGearReads.RoundsGear(p.Facts.Gear, emergency),
                Research = FrameResearch(ResearchSection(frame), catalog),
                BuiltBuildings = BuiltBuildingsOf(frame.Buildings),
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
        p = p with { Facts = p.Facts with { RitualSites = RitualSitesReads.RitualSitesOf(frame.Buildings, p.Facts.Ideology) } };
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
        // The frame's rooms census is the one room table: room quality reads it on every reading, the room census
        // sections only when asked.
        if (frame.Rooms is { } frameRooms)
        {
            if (p.Facts.Sleeping.TryGetValue(out var bedded))
            {
                bedded = bedded with { Rooms = SleepingUpkeepReads.UpkeepRooms(frameRooms, bedded.Beds) };
                p = p with { Facts = p.Facts with { Sleeping = Known(bedded) } };
            }
        }
        O.RoomsSnapshot? roomCensus = null;
        if (rooms)
        {
            roomCensus = frame.Rooms;
            var temperature = Unknown<RoomObservation>();
            if (roomCensus is not null)
            {
                temperature = Known(new RoomObservation
                {
                    Rooms = roomCensus.Rooms,
                    Cells = frame.RoomCells,
                    EligibleBeds = p.Facts.Sleeping.TryGetValue(out var sleepers)
                        ? Known(EquatableArray.From(sleepers.Beds.Where(bed => bed.Humanlike && !bed.Medical && !bed.Prisoners).Select(bed => bed.Id)))
                        : Unknown<EquatableArray<string>>(),
                    Dining = catalog.DiningFurniture(),
                    Shapes = p.Shapes,
                });
            }
            var (sleepingMin, sleepingMax) = TemperatureMethodRules.TemperatureRange(temperature);
            p = p with
            {
                Rooms = temperature,
                Facts = p.Facts with { SleepingMin = sleepingMin, SleepingMax = sleepingMax, Comfort = HostedComfort(p.Facts.Comfort, temperature) },
            };
        }
        // The reading is observed once the frame lands, under the age bound.
        var observedAt = clock.Now();
        if (observedAt - started > maxAge)
        {
            throw new ObservationStaleException();
        }
        cancellationToken.ThrowIfCancellationRequested();
        p = ColonyGearReads.StampGearCreepjoinersOn(p, pawns, catalog.CreepJoinerDownsides());
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

    /// <summary>
    /// The frame's colonist pawn detail, validated against the frame's complete emergency roster; null when the
    /// roster is incomplete or empty, or the frame carries no detail.
    /// </summary>
    private static O.PawnSnapshot? RoundsPawnsOf(RoundsFrame frame, C.Identity id)
    {
        var facts = frame.Emergency.Facts;
        if (!facts.AllSeen || facts.Colonists.Count == 0 || frame.Pawns is null)
        {
            return null;
        }
        var ids = new List<string>(facts.Colonists.Count);
        foreach (var pawn in facts.Colonists)
        {
            ids.Add(pawn.Id.Value);
        }
        return frame.Pawns;
    }

    /// <summary>Resolves definitions against the frame's catalog, research and crop rows.</summary>
    internal static DefinitionFacts FrameDefinitionFacts(RoundsFrame frame, DefinitionCatalog catalog) =>
        new()
        {
            Catalog = catalog,
            Crops = DefinitionsReads.CropRows(frame.Colony?.Planning is O.Planning.Requested requested ? requested.Facts : null),
            Finished = frame.Research is { } research ? DefinitionsReads.FinishedSet(research.Finished) : default,
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
    internal static Fact<EquatableArray<RimGovernor.Contracts.Wire.Observations.Trader>> FrameTraders(RimGovernor.Contracts.Wire.Observations.TradersSnapshot? read) =>
        read is null ? Unknown<EquatableArray<RimGovernor.Contracts.Wire.Observations.Trader>>() : Known(read.Traders);

    private static Fact<ComfortObservation> HostedComfort(Fact<ComfortObservation> comfort, Fact<RoomObservation> rooms)
    {
        if (!comfort.TryGetValue(out var v) || !rooms.TryGetValue(out var census))
        {
            return Unknown<ComfortObservation>();
        }
        try
        {
            return Known(ComfortRules.HostedComfort(v, census));
        }
        catch (DomainException)
        {
            return Unknown<ComfortObservation>();
        }
    }

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
