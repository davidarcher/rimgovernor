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
    /// <summary>The colony reading the frame decoded to (Go embeds it).</summary>
    public ColonyReading Reading { get; init; } = new();

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
    public Task<(L.TickReply Reply, Result Receipt)> TickAsync(CancellationToken cancellationToken) => inner.TickAsync(cancellationToken);

    public Task<(L.IdentityReply Reply, Result Receipt)> IdentityAsync(CancellationToken cancellationToken) => inner.IdentityAsync(cancellationToken);

    public Task<(O.ColonyFactsReply Reply, Result Receipt)> ReadColonyFactsAsync(C.Identity identity, bool planning, CancellationToken cancellationToken) =>
        Task.FromResult((new O.ColonyFactsReply { Observed = frame.Colony }, new Result()));

    public Task<Tables> FrameTablesAsync(C.Identity identity, CancellationToken cancellationToken)
    {
        var tables = frame.Tables;
        // A power row's wattage includes the upgrades the frame's research census says are finished.
        if (frame.Research is { } research)
        {
            tables = tables with { FinishedResearch = Known<EquatableArray<string>>([.. research.Finished]) };
        }
        return Task.FromResult(tables);
    }

    public Task<(ZonesRead Read, Result Receipt)> ReadZoneSectionAsync(C.Identity identity, CancellationToken cancellationToken)
    {
        if (frame.Zones is not { } zones)
        {
            throw new BridgeUnavailableException();
        }
        return Task.FromResult((zones, new Result()));
    }

    public async Task<Held<ZonesRead>> ZonesAsync(C.Identity identity, CancellationToken cancellationToken)
    {
        var (read, _) = await ReadZoneSectionAsync(identity, cancellationToken).ConfigureAwait(false);
        return new Held<ZonesRead> { Value = read, AsOf = read.AsOf, Complete = true, Source = "native_frame" };
    }

    /// <summary>The load's catalog, null for a source without one.</summary>
    public Task<DefinitionCatalog?> DefinitionCatalogAsync(C.Identity id, CancellationToken cancellationToken) => inner.DefinitionCatalogAsync(id, cancellationToken);

    /// <summary>The frame's research section.</summary>
    public Task<(ResearchRead Read, Result Receipt)> ReadResearchAsync(C.Identity id, CancellationToken cancellationToken)
    {
        if (frame.Research is not { } research)
        {
            throw new BridgeUnavailableException();
        }
        return Task.FromResult((research, new Result()));
    }
}

public static partial class ObservationPackage
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
        using var zoneScope = planning ? null : WithZones(colonySource);
        // Protection uses only this coherent frame, never the ordinary scheduler's zone refresher and its
        // potentially blocked census.
        var reading = await ObserveColony(colonySource, clock, expected, maxAge, planning, cancellationToken).ConfigureAwait(false);
        var p = reading.Projection;
        var colony = frame.Colony;
        var emergency = frame.Emergency.Facts;
        if (catalog is not null)
        {
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
            p = p with
            {
                Facts = p.Facts with
                {
                    MedicalReserve = p.Facts.MedicalReserve with { Catalog = p.Facts.Items },
                    Recipes = catalog.RecipeFacts(),
                },
            };
        }
        p = p with { Facts = p.Facts with { Containment = ContainmentPlanningOf(frame, catalog, p.Definitions) } };
        if (p.Facts.Sleeping.TryGetValue(out var sleeping))
        {
            sleeping = sleeping with { BedBuildable = p.DefinitionAvailable(p.Shapes.Furniture.PrimaryBed()) };
            p = p with { Facts = p.Facts with { Sleeping = Known(sleeping) } };
        }
        p = p with { Facts = p.Facts with { CurrentConstruction = Unknown<CurrentConstruction>(), ConstructionDeficit = Unknown<IReadOnlyDictionary<Resource, long>>() } };
        if (frame.Buildings is { } census)
        {
            if (census.Invalid is { } invalid)
            {
                throw invalid;
            }
            var (current, deficit) = ConstructionFromCensus(census);
            p = p with { Facts = p.Facts with { CurrentConstruction = current, ConstructionDeficit = deficit } };
        }
        p = p with { Facts = p.Facts with { BillReservations = BillReservations(frame.Bills) } };
        if (p.PowerPlanning.TryGetValue(out var topology))
        {
            p = p with { Facts = p.Facts with { Forward = p.Facts.Forward with { Power = topology.Networks } } };
        }
        p = p with { Facts = p.Facts with { Forward = p.Facts.Forward with { Turrets = p.DefenseTurrets } } };
        var pawns = RoundsPawnsOf(frame, id);
        p = p with
        {
            WorkPawns = default,
            Facts = p.Facts with { Armed = default, Unarmed = default, DefenseCapacity = default, MedicalPawns = default, MoodPawns = default },
        };
        if (pawns is not null)
        {
            var (armed, unarmed) = RoundsArmed(colony, emergency, pawns, RequireCatalog(catalog).CreepJoinerDownsides());
            p = p with
            {
                Facts = p.Facts with
                {
                    Armed = armed,
                    Unarmed = unarmed,
                    DefenseCapacity = PolicyFunctions.DefenseCapacity(RoundsDefenders(colony, emergency, pawns), p.DefenseTurrets),
                },
                MechCatalog = catalog?.Biotech?.MechCatalog() ?? new(),
                WorkPawns = RoundsWork(colony, emergency, pawns, catalog, frame.Tables.Things),
                MeditateAvailable = Optional(pawns.HasMeditateAssignmentAvailable, pawns.MeditateAssignmentAvailable),
            };
            p = p with { Facts = p.Facts with { MedicalPawns = RoundsMedical(colony, emergency, pawns, RequireCatalog(catalog)) } };
            p = p with { Facts = p.Facts with { MoodPawns = RoundsMood(colony, emergency, pawns) } };
            var thoughtFacts = catalog?.AllThoughtFacts() ?? new Dictionary<string, ThoughtFacts>();
            p = p with { Facts = p.Facts with { MoodLedger = RoundsMoodLedger(p.Facts.MoodPawns, pawns, thoughtFacts) } };
        }
        else if (emergency.ColonistsComplete.TryGetValue(out var complete) && complete && emergency.Colonists.Count == 0 && colony.HasColonistCount && colony.ColonistCount == 0)
        {
            p = p with
            {
                Facts = p.Facts with
                {
                    MoodPawns = Known<EquatableArray<MoodPawn>>([]),
                    MoodLedger = Known(PolicyFunctions.BuildMoodLedger([], new Dictionary<string, ThoughtFacts>())),
                },
            };
        }
        p = p with { Mechs = frame.Tables.Pawns.Count > 0 ? Known(MechFleetOf(frame.Tables.Pawns.Table.Values())) : Unknown<MechFleet>() };
        p = p with
        {
            Facts = p.Facts with
            {
                MechChargerOwed = PolicyFunctions.MechChargerNeed(p.Mechs, MechChargerFact(p.Biotech)),
                GeneBankOwed = PolicyFunctions.GeneBankNeed(GeneBankFact(p.Biotech)),
                RecoveryWorkers = RecoveryWorkers(p.Facts.MoodPawns),
                Gear = RoundsGear(p.Facts.Gear, emergency),
                Research = FrameResearch(frame.Research),
            },
        };
        p = p with { TechTier = PolicyFunctions.SelectTechTier(FinishedResearch(p.Facts.Research), p.PlayerTechLevel) };
        p = p with
        {
            Facts = p.Facts with
            {
                Traders = FrameTraders(frame.Traders),
                QuestOffers = FrameQuests(frame.Quests, expected.Map, catalog),
                QuestSites = FrameWorldSites(frame.Quests),
                QuestExpeditionTrips = FrameExpeditionTrips(frame.Quests, expected.Map),
                QuestWorkers = p.WorkPawns,
            },
        };
        var (capacity, departures) = FrameQuestWorkerCapacity(frame.Quests, expected.Map);
        p = p with { Facts = p.Facts with { QuestWorkCapacity = capacity, QuestDeparturePawns = departures } };
        if (pawns is not null)
        {
            p = p with { Facts = p.Facts with { QuestDeparturePawns = ProjectDepartureStrength(p.Facts.QuestDeparturePawns, RoundsDefenders(colony, emergency, pawns)) } };
        }
        if (frame.Quests?.Context is { HasTick: true } questContext)
        {
            p = p with { Facts = p.Facts with { QuestObservedTick = Known(new Tick(questContext.Tick)) } };
        }
        p = p with
        {
            Facts = p.Facts with
            {
                QuestColonistsAtHome = FrameQuestColonistsAtHome(frame.Quests, expected.Map, emergency),
                Ideology = FrameIdeology(frame.Ideology),
                IdeologyInstalled = frame.IdeologyActive,
            },
        };
        p = p with { Facts = p.Facts with { RitualSites = RitualSitesOf(frame.Buildings, p.Facts.Ideology) } };
        var ideologyDefs = RequireCatalog(catalog).IdeologyDefs();
        p = p with
        {
            Facts = p.Facts with
            {
                AnimalUpkeep = p.Facts.AnimalUpkeep with { Animals = PolicyFunctions.ApplyHerdPrecepts(p.Facts.AnimalUpkeep.Animals, p.Facts.Ideology, ideologyDefs is not null) },
            },
        };
        // A pawn row or def the royalty read cannot use leaves the fact unknown; it must not fail the routine
        // reading the whole review stands on.
        Fact<RoyaltyFacts> royalty;
        try
        {
            royalty = p.RoyaltyOf(pawns, RequireCatalog(catalog));
        }
        catch (DomainException)
        {
            royalty = Unknown<RoyaltyFacts>();
        }
        p = p with
        {
            Facts = p.Facts with
            {
                Royalty = royalty,
                Prisoners = default,
                Custody = default,
                PrisonerColony = default,
                Outlook = new(),
                OwnedNames = default,
                Guests = default,
            },
        };
        if (frame.Population is { } population)
        {
            p = p with
            {
                Facts = p.Facts with
                {
                    Prisoners = population.Prisoners,
                    Custody = population.Custody,
                    PrisonerColony = population.Colony,
                    Outlook = population.Outlook,
                    OwnedNames = population.Names,
                    Guests = population.Guests,
                },
            };
            if (p.Facts.PrisonerColony.TryGetValue(out var prisonerColony))
            {
                p = p with { Facts = p.Facts with { PrisonerColony = Known(prisonerColony with { Ideology = p.Facts.Ideology }) } };
            }
        }
        // The frame's rooms census is the one room table: room quality reads it on every reading, the room census
        // sections only when asked.
        if (frame.Rooms is { } frameRooms)
        {
            if (p.Facts.Sleeping.TryGetValue(out var bedded))
            {
                bedded = bedded with { Rooms = UpkeepRooms(frameRooms, bedded.Beds) };
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
                temperature = TemperatureRooms(roomCensus, frame.RoomCells, p.Facts.Sleeping);
                if (temperature.TryGetValue(out var planned) && catalog is not null)
                {
                    planned = planned with { Dining = catalog.DiningFurniture(), Shapes = p.Shapes };
                    temperature = Known(planned);
                }
            }
            var (sleepingMin, sleepingMax) = PolicyFunctions.TemperatureRange(temperature);
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
        p = PersonalShares(p, frame, catalog, pawns);
        p = StampGearShares(p);
        p = StampGearCreepjoinersOn(p, pawns, RequireCatalog(catalog).CreepJoinerDownsides());
        p = p with { Facts = StampQuestGuestProtection(p.Facts) };
        p = p with { Facts = StampQuestRefugees(p.Facts) };
        return new RoundsReading
        {
            Reading = reading with { Projection = p, StartedAt = started, ObservedAt = observedAt },
            Emergency = emergency,
            Sections = BuildRoundsSections(frame, p, roomCensus),
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
        if (!facts.ColonistsComplete.TryGetValue(out var complete) || !complete || facts.Colonists.Count == 0 || frame.Pawns is null)
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
            Crops = CropRows(frame.Colony?.Planning?.Observed),
            Finished = frame.Research is { } research ? FinishedSet(research.Finished) : default,
        };

    /// <summary>
    /// The visible quest census; unknown when the frame carries none. Each offer carries the catalog's ground or
    /// ship-only class of its quest root (QuestClass), unknown without a catalog or when the catalog cannot
    /// classify the root.
    /// </summary>
    internal static Fact<EquatableArray<JoinerOffer>> FrameQuests(WorldProgressionRead? read, MapId home, DefinitionCatalog? catalog)
    {
        if (read is null)
        {
            return Unknown<EquatableArray<JoinerOffer>>();
        }
        var hostile = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var faction in read.Factions)
        {
            hostile[faction.Id] = faction.Hostile;
        }
        var offers = new List<JoinerOffer>(read.Quests.Count);
        foreach (var quest in read.Quests)
        {
            var offer = new JoinerOffer
            {
                Quest = new QuestId(quest.Id),
                ScriptDef = quest.ScriptDef,
                State = quest.State,
                CanAccept = quest.CanAccept,
                RequiresAccepter = quest.RequiresAccepter,
                ChoiceCount = quest.ChoiceCount,
                FactionId = quest.FactionId,
                FactionHostile = Unknown<bool>(),
                OnMap = quest.MapKnown && new MapId(quest.MapId) == home,
                Class = Unknown<QuestClass>(),
            };
            if (catalog is not null)
            {
                try
                {
                    offer = offer with { Profile = Known(catalog.QuestProfile(quest.ScriptDef)) };
                }
                catch (DomainException)
                {
                    // The profile stays unknown.
                }
                try
                {
                    offer = offer with { Class = Known(catalog.QuestClass(quest.ScriptDef)) };
                }
                catch (DomainException e)
                {
                    offer = offer with { ClassError = e.Message };
                }
            }
            if (quest.FactionId != "" && hostile.TryGetValue(quest.FactionId, out var value))
            {
                offer = offer with { FactionHostile = Known(value) };
            }
            offer = offer with
            {
                Favor = [.. quest.Favor.Select(f => new QuestFavor(f.Choice, f.Favor))],
                RewardChoiceParts = quest.RewardChoiceParts is { } parts ? Known(parts) : default,
                Asker = new PawnId(quest.Asker),
                DeparturePawnIds = [.. quest.DeparturePawnIds.Select(i => new PawnId(i))],
                AskerFactionPlayer = quest.AskerFactionPlayer is { } player ? Known(player) : default,
                ViolentQuestsAllowed = quest.ViolentQuestsAllowed is { } violent ? Known(violent) : default,
                ThreatPoints = Optional(quest.ThreatPoints.HasValue, quest.ThreatPoints.GetValueOrDefault()),
            };
            var rewards = new List<QuestReward>();
            foreach (var reward in quest.Rewards)
            {
                rewards.Add(new QuestReward
                {
                    Choice = reward.Choice,
                    Goodwill = reward.Goodwill,
                    Psylink = reward.Psylink,
                    PermitPoints = reward.PermitPoints,
                    Permits = [.. reward.Permits],
                    TitleDef = reward.TitleDef,
                    FactionId = reward.FactionId,
                    Items = [.. reward.Items.Select(item => new Amount(new Resource(item.Def), item.Count))],
                });
            }
            offer = offer with
            {
                Rewards = rewards,
                EligiblePawnIds = [.. quest.EligiblePawnIds.Select(i => new PawnId(i))],
                ExpiresInTicks = quest.ExpiresInTicks is { } expires ? Known(expires) : default,
            };
            var objectives = new List<QuestObjective>();
            foreach (var objective in quest.Objectives)
            {
                var row = new QuestObjective
                {
                    Kind = objective.Kind,
                    Def = objective.Def,
                    Stuff = objective.Stuff,
                    UnmetRequirement = objective.UnmetRequirement,
                    MinimumMood = Optional(objective.MinimumMood.HasValue, objective.MinimumMood.GetValueOrDefault()),
                    Monument = QuestMonumentOf(objective.Monument, catalog),
                    GravEngine = QuestGravEngineOf(objective.GravEngine),
                    SurveyScanner = QuestSurveyScannerOf(objective.SurveyScanner),
                    DurationTicks = Optional(objective.DurationTicks.HasValue, objective.DurationTicks.GetValueOrDefault()),
                    Workload = QuestWorkloadOf(objective.Workload, objective.Kind, catalog),
                    LodgerMoods = [.. objective.LodgerMoods.Select(m => new QuestLodgerMood(new PawnId(m.PawnId), Optional(m.Mood.HasValue, m.Mood.GetValueOrDefault())))],
                    Count = objective.Count is { } count ? Known(count) : default,
                    Active = objective.Active is { } active ? Known(active) : default,
                    Produced = objective.Produced is { } produced ? Known(produced) : default,
                    DeadlineTicks = objective.DeadlineTicks is { } deadline ? Known(deadline) : default,
                    PawnIds = [.. objective.PawnIds.Select(i => new PawnId(i))],
                };
                objectives.Add(QuestHackGift(objective, row));
            }
            var shuttles = new List<QuestShuttleState>();
            foreach (var shuttle in quest.Shuttles)
            {
                shuttles.Add(new QuestShuttleState
                {
                    Id = shuttle.Id,
                    AutoloadAvailable = Optional(shuttle.AutoloadAvailable.HasValue, shuttle.AutoloadAvailable.GetValueOrDefault()),
                    Autoload = Optional(shuttle.Autoload.HasValue, shuttle.Autoload.GetValueOrDefault()),
                    Loading = Optional(shuttle.Loading.HasValue, shuttle.Loading.GetValueOrDefault()),
                    AllRequiredLoaded = Optional(shuttle.AllRequiredLoaded.HasValue, shuttle.AllRequiredLoaded.GetValueOrDefault()),
                    ManualLaunchAvailable = Optional(shuttle.ManualLaunchAvailable.HasValue, shuttle.ManualLaunchAvailable.GetValueOrDefault()),
                    RequiredColonistCount = Optional(shuttle.RequiredColonistCount.HasValue, shuttle.RequiredColonistCount.GetValueOrDefault()),
                    PawnIds = [.. shuttle.PawnIds.Select(i => new PawnId(i))],
                    LoadedPawnIds = [.. shuttle.LoadedPawnIds.Select(i => new PawnId(i))],
                    PendingPawnIds = [.. shuttle.PendingPawnIds.Select(i => new PawnId(i))],
                });
            }
            offers.Add(offer with { Objectives = objectives, Shuttles = shuttles });
        }
        return Known(EquatableArray.From(offers));
    }

    /// <summary>The research census; unknown when the frame carries none.</summary>
    internal static Fact<ResearchFacts> FrameResearch(ResearchRead? read)
    {
        if (read is null)
        {
            return Unknown<ResearchFacts>();
        }
        var facts = new ResearchFacts { Current = new ResearchProjectId(read.CurrentProject) };
        if (read.CurrentProject != "")
        {
            facts = facts with { CurrentBenchMissing = PolicyFunctions.ResearchBenchNeeded(read.Projects.GetValueOrDefault(read.CurrentProject) ?? new ResearchProjectFacts()) };
        }
        var projects = read.Projects.Keys.Select(name => new ResearchProjectId(name)).ToList();
        projects.Sort((a, b) => string.CompareOrdinal(a.Value, b.Value));
        facts = facts with
        {
            Projects = EquatableArray.From(projects),
            Finished = [.. read.Finished.Select(name => new ResearchProjectId(name))],
            KnowledgePick = new ResearchProjectId(PolicyFunctions.KnowledgePick(read.Projects, read.Finished, read.Knowledge)),
        };
        return Known(facts);
    }

    /// <summary>The primary ideoligion; unknown when the frame carries no ideology section.</summary>
    internal static Fact<Ideoligion> FrameIdeology(Ideoligion? read) => read is null ? Unknown<Ideoligion>() : Known(read);

    /// <summary>The trader census; unknown when the frame carries none.</summary>
    internal static Fact<EquatableArray<TraderFacts>> FrameTraders(TradersRead? read)
    {
        if (read is null)
        {
            return Unknown<EquatableArray<TraderFacts>>();
        }
        var rows = new List<TraderFacts>(read.Traders.Count);
        foreach (var row in read.Traders)
        {
            rows.Add(new TraderFacts
            {
                Participant = row.Participant,
                ID = row.Id,
                Kind = row.Kind,
                Faction = row.Faction,
                CanTrade = row.CanTrade,
                Travelling = row.Travelling,
                GoodsStacks = row.GoodsStacks,
            });
        }
        return Known(EquatableArray.From(rows));
    }

    private static Fact<ComfortObservation> HostedComfort(Fact<ComfortObservation> comfort, Fact<RoomObservation> rooms)
    {
        if (!comfort.TryGetValue(out var v) || !rooms.TryGetValue(out var census))
        {
            return Unknown<ComfortObservation>();
        }
        try
        {
            return Known(PolicyFunctions.HostedComfort(v, census));
        }
        catch (DomainException)
        {
            return Unknown<ComfortObservation>();
        }
    }

    /// <summary>The finished project list of a known research census, the input TechTier is selected from.</summary>
    public static Fact<EquatableArray<ResearchProjectId>> FinishedResearch(Fact<ResearchFacts> research) =>
        research.TryGetValue(out var facts) ? Known(facts.Finished) : Unknown<EquatableArray<ResearchProjectId>>();
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
            : this with { Definitions = ObservationPackage.FrameDefinitionFacts(frame, catalog).AppendDefinitions([.. Definitions], names) };
}
