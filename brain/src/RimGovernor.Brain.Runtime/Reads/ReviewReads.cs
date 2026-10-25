using DefinitionCatalog = RimGovernor.Brain.Bridge.DefinitionCatalog;
using static RimGovernor.Brain.Bridge.FrameReads;
namespace RimGovernor.Brain.Runtime;

internal sealed class FrameColony(IReviewSource inner, ReviewFrame frame) : IColonySource, IDefinitionSource, IResearchSource
{
    public Task<RimGovernor.Brain.GameFacts.Lifecycle.LoadedTick> TickAsync(CancellationToken cancellationToken) => inner.TickAsync(cancellationToken);

    public Task<RimGovernor.Brain.GameFacts.Lifecycle.LoadedIdentity> IdentityAsync(CancellationToken cancellationToken) => inner.IdentityAsync(cancellationToken);

    public Task<RimGovernor.Brain.GameFacts.Observations.ColonyFactsSnapshot> ReadColonyFactsAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, CancellationToken cancellationToken) =>
        Task.FromResult(frame.Colony ?? throw new BridgeUnavailableException("The frame holds no colony facts."));

    public Task<Tables> FrameTablesAsync(RimGovernor.Brain.GameFacts.Common.Identity identity, CancellationToken cancellationToken) => Task.FromResult(frame.Tables with
    {
        Rooms = frame.Rooms ?? throw new BridgeContractException("missing total rooms census"), RoomCells = frame.RoomCells,
        Zones = frame.Zones ?? throw new BridgeContractException("missing total zones census"),
    });

    /// <summary>The load's catalog.</summary>
    public Task<DefinitionCatalog> DefinitionCatalogAsync(RimGovernor.Brain.GameFacts.Common.Identity id, CancellationToken cancellationToken) => inner.DefinitionCatalogAsync(id, cancellationToken);

    /// <summary>The frame's research section.</summary>
    public Task<RimGovernor.Brain.GameFacts.Observations.ResearchSnapshot> ReadResearchAsync(RimGovernor.Brain.GameFacts.Common.Identity id, CancellationToken cancellationToken) => Task.FromResult(FrameReads.ResearchSection(frame));
}

public static class ReviewReads
{
    public static Task<ReviewReading> ObserveReviewOwned(IReviewSource? source, IClock clock, Identity expected, TimeSpan maxAge, EquatableArray<ConstructionClaim> claims, IReadOnlyList<string> definitions, CancellationToken cancellationToken) =>
        ObserveReview(source, clock, expected, maxAge, claims, true, definitions, cancellationToken);

    /// <summary>Decodes the same native frame and room observations without fetching the development planning window or its definition reads.</summary>
    public static Task<ReviewReading> ObserveReviewProtection(IReviewSource? source, IClock clock, Identity expected, TimeSpan maxAge, EquatableArray<ConstructionClaim> claims, CancellationToken cancellationToken) =>
        ObserveReview(source, clock, expected, maxAge, claims, false, [], cancellationToken);

    /// <summary>
    /// Decodes one frame. ObserveColony checks the frame's colony context against expected; every other section of
    /// the frame shares that context and tick, so none is checked against another.
    /// </summary>
    private static async Task<ReviewReading> ObserveReview(
        IReviewSource? source, IClock clock, Identity expected, TimeSpan maxAge, EquatableArray<ConstructionClaim> claims,
        bool planning, IReadOnlyList<string> definitions, CancellationToken cancellationToken)
    {
        if (source is null)
        {
            throw new ObservationContractException("missing observation dependency");
        }
        var id = new RimGovernor.Brain.GameFacts.Common.Identity { ColonyId = expected.Colony.Value, LoadToken = expected.Load.Value, MapId = expected.Map.Value };
        var started = clock.Now();
        var frame = await source.ReadReviewFrameAsync(id, cancellationToken).ConfigureAwait(false);
        var catalog = await source.DefinitionCatalogAsync(id, cancellationToken).ConfigureAwait(false);
        if (frame.Colony is null || frame.Emergency.Context is null)
        {
            throw new ObservationContractException("rounds frame lacks colony or emergency context");
        }
        var colonySource = new FrameColony(source, frame);
        // Every colony projection uses the frame's own zone census, including protection and direct reads.
        var reading = await ColonyReads.ObserveColony(colonySource, clock, expected, maxAge, planning, cancellationToken).ConfigureAwait(false);
        var p = reading;
        var colony = frame.Colony;
        var emergency = frame.Emergency.Facts;
        // Room-role furniture rides every routine reading: the catalog names which definitions the planners
        // furnish rooms from.
        var roleRows = catalog.RoomRoleRows();
        p = p with { Facts = p.Facts with { Shapes = catalog.PieceShapes(), Stations = catalog.StationCatalog() } };
        var names = definitions.Concat(roleRows).Concat(p.Facts.Shapes.Furniture.Definitions());
        p = p with { Facts = p.Facts with { Definitions = FrameDefinitionFacts(frame, catalog).AppendDefinitions([.. p.Facts.Definitions], names) } };
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
                MoodBreak = p.Facts.Stations.Mood.Break,
                ResearchBenchSpeeds = catalog.ResearchBenchSpeeds(),
                LaboratoryBench = p.Facts.Shapes.Furniture.BenchFor(RoomRole.Laboratory),
                SleepingBeds = catalog.SleepingBeds(),
                ComfortCurve = p.Facts.Stations.Comfort.Comfort,
                HospitalBed = p.Facts.Shapes.Furniture.HospitalBed(),
                Filth = p.Facts.Stations.Filth,
                Crafts = catalog.CraftRecipes(finished),
                WorkSpeeds = catalog.WorkSpeedFacts(),
                FoodWork = catalog.FoodWork(finished),
            },
        };
        p = p with { Facts = p.Facts with { Containment = ContainmentReads.ContainmentPlanningOf(frame, catalog, p.Facts.Definitions) } };
        var buildable = ColonyPlanningRules.DefinitionAvailable(p.Facts, p.Facts.Shapes.Furniture.PrimaryBed());
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
        var quests = frame.Quests ?? throw new ObservationContractException("a routine frame carries no world progression");
        var questPawns = QuestReads.FrameQuestPawns(quests, expected.Map, emergency, MoodReads.Defenders(colony, emergency, pawns), p.Facts.ForecastObserved.WorkPawns);
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
                QuestPawns = questPawns,
                QuestObservedTick = new Tick(quests.Context.Tick),
                ForecastObserved = p.Facts.ForecastObserved with { WorkPawns = p.Facts.ForecastObserved.WorkPawns, Needs = colony.Forecast.Needs, Coupled = colony.Forecast.Coupled },
                EssentialInputs = p.Facts.EssentialInputs with { Power = p.Facts.EssentialInputs.Power },
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
                Royalty = ColonyPlanningReads.RoyaltyOf(p.Snapshot.Royalty, pawns, catalog),
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
        return new ReviewReading
        {
            Reading = p with { StartedAt = started, ObservedAt = observedAt },
            Emergency = emergency,
            Sections = MoodReads.BuildReviewSections(frame, p, roomCensus),
            Frame = frame,
            Catalog = catalog,
        };
    }

}
