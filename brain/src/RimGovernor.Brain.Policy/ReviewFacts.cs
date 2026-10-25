namespace RimGovernor.Brain.Policy;

public sealed record PlannerSettings
{
    public MedicalReservePolicy MedicalReserve { get; init; } = MedicalReservePolicy.Default();

    public FoodStoragePolicy FoodStorage { get; init; } = new();

    public LightingPolicy Lighting { get; init; } = new();

    public FlooringPolicy Flooring { get; init; } = new();

    public RoutesPolicy Routes { get; init; } = new();

    public double FoodMinDays { get; init; }

    public double FoodTargetDays { get; init; }

    public double FootholdFoodDays { get; init; }

    public double FoodReserveDays { get; init; }

    public double ColdEnter { get; init; }

    public double ColdExit { get; init; }

    public double HotExit { get; init; }

    public double HotEnter { get; init; }

    /// <summary>The growth fraction a plantation tree must reach before chop selection offers it; 0 offers every harvestable one. Wild trees ignore it.</summary>
    public double ChopMinGrowth { get; init; }

    /// <summary>
    /// Bounds how long a dispatched Hunt-kind acquisition action may sit unresolved before
    /// AcquisitionPlanner abandons it and lets a fresh SelectAcquisition pass propose something else.
    /// Without this grace a stuck action reads as open work forever and blocks the planner from ever trying
    /// a different prey or a non-hunt source. It counts from the last observation, and a live hunt (travel, chase, the hunter's
    /// sleep and meals, a clock run between observations) outlasts 6000 ticks: that bound withdrew
    /// working hunts as "cancelled" and cooled their prey, so it is half a day.
    /// </summary>
    public long HuntStallTicks { get; init; }

    /// <summary>
    /// Bounds how long a dispatched plant-harvest acquisition may stay designated with its effect pending (no
    /// colonist has taken the designation) before the acquisition and medical planners cancel it so the goal
    /// re-plans from another source instead of waiting on one plant (wild healroot pending 120k ticks).
    /// </summary>
    public long AcquisitionStallTicks { get; init; }

    /// <summary>
    /// How long prisoners/recruit feeds a prisoner it has no use for (not worth recruiting, not enslavable)
    /// while the food runway holds FoodTargetDays before releasing it (default 15 days); below the target it
    /// releases at once. See PrisonerPolicy.
    /// </summary>
    public double PrisonerReleaseAfterDays { get; init; }

    /// <summary>Is the prisoners family's slice of this policy.</summary>
    public PrisonerPolicy Prisoners() => new() { ReleaseAfterDays = PrisonerReleaseAfterDays, FoodTargetDays = FoodTargetDays };

    /// <summary>Throws DomainException when the policy is invalid.</summary>
    public void Validate()
    {
        if (MedicalReserve.MinimumPerColonist < 0 || MedicalReserve.TargetPerColonist <= MedicalReserve.MinimumPerColonist)
        {
            throw new DomainException("invalid medicine reserve thresholds");
        }
        if (!FoodStorage.Valid)
        {
            throw new DomainException("invalid food storage thresholds");
        }
        foreach (var n in new[] { FoodMinDays, FoodTargetDays, FootholdFoodDays, FoodReserveDays, ColdEnter, ColdExit, HotExit, HotEnter, PrisonerReleaseAfterDays })
        {
            if (double.IsNaN(n) || double.IsInfinity(n))
            {
                throw new DomainException("nonfinite routine threshold");
            }
        }
        if (FoodReserveDays < 0 || FoodReserveDays > 60 || FoodMinDays <= 0 || FoodTargetDays <= FoodMinDays || FootholdFoodDays <= 0
            || ColdEnter >= ColdExit || ColdExit >= HotExit || HotExit >= HotEnter)
        {
            throw new DomainException("unordered routine thresholds");
        }
        if (!(ChopMinGrowth >= 0 && ChopMinGrowth <= 1))
        {
            throw new DomainException("invalid chop minimum growth");
        }
        if (PrisonerReleaseAfterDays < 0 || PrisonerReleaseAfterDays > 120)
        {
            throw new DomainException("invalid prisoner release threshold");
        }
        if (HuntStallTicks <= 0)
        {
            throw new DomainException("invalid hunt stall grace");
        }
        if (AcquisitionStallTicks <= 0)
        {
            throw new DomainException("invalid acquisition stall grace");
        }
    }
}

/// <summary>
/// Holds derived native facts, not forecasts masquerading as output. FoodDays is the accessible
/// diet/rot-aware stock runway. FieldCoverage is the separate native crop-capacity forecast; it never
/// increases FoodDays.
/// </summary>
public sealed partial record ReviewFacts
{
    /// <summary>The player's standing preferences (the goals record's); owning policies read them and never propose what they forbid. The default is the behavior without any.</summary>
    public PreferencesRecord Preferences { get; init; } = new();

    /// <summary>The catalog's item numbers (market value, nutrition, medical potency, stuff factors); the zero value without a catalog.</summary>
    public ItemFacts Items { get; init; } = new();

    /// <summary>The recipe rows' derived facts.</summary>
    public RecipeFacts Recipes { get; init; } = new();

    /// <summary>
    /// The catalog's craft recipes: every recipe a player-buildable bench hosts, as a bench recipe row with no bench,
    /// available when its research and some host's are finished. A declarer orders one when no standing bench lists
    /// a recipe making what it needs (#3659); empty without a catalog or a research read.
    /// </summary>
    public EquatableArray<GearRecipe> Crafts { get; init; }

    /// <summary>The game's capacity penalty tables of malnutrition and food poisoning (#3519), priced by the capability-days ledger (#3520); unknown without a catalog.</summary>
    public CapacityPenaltyTables CapacityPenalties { get; init; } = new();

    /// <summary>The catalog's mood-break curve: the mean days each tier's break takes its colonist out of work, which prices break risk in capability-days (#3520); unknown (empty tiers) without a catalog.</summary>
    public MoodBreakModel MoodBreak { get; init; } = new();

    /// <summary>The defs' map from a coupled task to the work it is and the speed stat it runs at (DefinitionCatalog.WorkSpeedFacts, #3825), which the work shortfall reads; empty without a catalog.</summary>
    public WorkSpeedFacts WorkSpeeds { get; init; } = new();

    /// <summary>The work a unit of nutrition costs over the research finished (DefinitionCatalog.FoodWork, #3825), a pawn's food upkeep; no crop and no meal without a catalog.</summary>
    public FoodWorkFacts FoodWork { get; init; } = new();

    /// <summary>When a part, an organ or a medicine a rule waits on arrives (#3868, StockArrivalReads.Of); built empty without the read.</summary>
    public StockArrival StockArrival { get; init; } = new();

    /// <summary>The colonist beds no one owns (SleepingRules.VacantColonistBed) by id, which make a pawn's bed free (#3825, V6).</summary>
    public IReadOnlyList<string> VacantColonistBeds { get; init; } = [];

    /// <summary>Per research bench def, its ResearchSpeedFactor as the def data states it (DefinitionCatalog.ResearchBenchSpeeds, #3800); empty without a catalog.</summary>
    public IReadOnlyDictionary<string, BenchSpeedModel> ResearchBenchSpeeds { get; init; } = new Dictionary<string, BenchSpeedModel>();

    /// <summary>The bench a planned laboratory is furnished with (RoomFurniture.BenchFor of Laboratory, #3800); "" without a catalog.</summary>
    public string LaboratoryBench { get; init; } = "";

    /// <summary>Per sleeping bed def and stuff, what lying in a built one gives (DefinitionCatalog.SleepingBeds, #3840); empty without a catalog.</summary>
    public IReadOnlyList<SleepingBedFacts> SleepingBeds { get; init; } = [];

    /// <summary>The comfort need's NeedDef curve (StationCatalog.Comfort.Comfort, #3840) the bedroom credit's comfort term runs; unknown without a catalog.</summary>
    public NeedCurve ComfortCurve { get; init; }

    /// <summary>The bed a planned hospital is furnished with (RoomFurniture.HospitalBed, #3804); no def without a catalog.</summary>
    public FurnitureBed HospitalBed { get; init; } = new();

    /// <summary>The catalog's filth model (StationCatalog.Filth) a planned room's predicted cleanliness reads (#3797, #3800), and the kitchen and butchery credits its poisoning (#3802); the zero model without a catalog.</summary>
    public FilthModel Filth { get; init; } = new();

    /// <summary>The hospital's animal ward; unread (the zero value) until the layout exposes it, which keeps sterilize off.</summary>
    public AnimalWard AnimalWard { get; init; } = new();

    /// <summary>
    /// The food plan every full review plans from the census (#3929). Null while no plan is held: the immediate pass
    /// before the first full review for its colony, which opens no hunt.
    /// </summary>
    public FoodPlan? FoodPlan { get; init; }

    /// <summary>The food plan; a pass that holds none raises <see cref="RequiredFactException"/>.</summary>
    public FoodPlan RequireFoodPlan() => FoodPlan ?? throw new RequiredFactException("food plan");

    /// <summary>The travel reserve's review; Disabled until the review reads it.</summary>
    public FoodReserveState FoodReserve { get; init; } = new FoodReserveState.Disabled();

    /// <summary>The babies' food review; the empty review (nothing owed) without Biotech baby care.</summary>
    public BabyFeeding BabyFeeding { get; init; } = new();

    public EquatableArray<FoodIngredientSlot> TradeMealIngredients { get; init; }

    public EquatableArray<PenGrazing> PenGrazing { get; init; }

    /// <summary>What a cell of each terrain grows for grazing in the worst quadrum, the per-cell term of the game's pen food sum (PastureTable).</summary>
    public PastureTable Pasture { get; init; } = new(new Dictionary<string, double>());

    public RecoverySafety RecoverySafety { get; init; } = new();


    public EquatableArray<DisasterCondition> DisasterConditions { get; init; }

    /// <summary>The colony biome's permanent darkness: its map conditions include a no-sunlight class.</summary>
    public bool OutdoorsDark { get; init; }

    public EquatableArray<RimGovernor.Brain.GameFacts.Observations.BuildingState> RecoveryBuildings { get; init; }

    public DisasterHistory? Disaster { get; init; }

    public Tick DisasterTick { get; init; }

    /// <summary>The game tick the facts were assembled at, which Dash is judged against; zero where no dash is planned.</summary>
    public Tick Tick { get; init; }

    /// <summary>The planned outdoor dash window through extreme cold (#2765); unknown or zero is closed.</summary>
    public DashWindow Dash { get; init; } = new();

    public EquatableArray<MoodPawn> MoodPawns { get; init; }

    /// <summary>Where the colony loses mood, built each review from the mood census and the catalog thought facts; unknown with the census.</summary>
    public MoodLedger MoodLedger { get; init; } = new();

    /// <summary>The Home census (#3771); null only on a projection that no colony facts read built.</summary>
    public RimGovernor.Brain.GameFacts.Observations.HomeCoverageFacts? HomeCoverage { get; init; }

    public EquatableArray<ConstructionClaim> ConstructionClaims { get; init; }

    private CurrentConstruction? _currentConstruction;

    /// <summary>The total construction census. A synthetic projection must supply it before planning construction.</summary>
    public CurrentConstruction CurrentConstruction
    {
        get => _currentConstruction ?? throw new RequiredFactException("construction census");
        init => _currentConstruction = value;
    }

    /// <summary>The material the whole layout plan still owes its walls, doors, floors and stations, per room id (PlannedShellDemandByRoom and kin); null while there is no plan or no construction census to owe against: nothing planned is then owed.</summary>
    public IReadOnlyDictionary<int, IReadOnlyDictionary<Resource, long>>? PlannedConstruction { get; init; }

    /// <summary>
    /// The same owed operations as hypothetical sites, one per op x stuff, plus the unplaced shelf candidates, each joined to
    /// the native planned-site read (PlannedSitesOf, JoinPlannedSites); unknown while the plan or the construction census is.
    /// </summary>
    public PlannedSiteFacts? PlannedSites { get; init; }

    /// <summary>
    /// The stock each planned storage room, yard and meal closet would keep from deteriorating, by room id
    /// (StoreRules.PlannedStoreStock over the stockpile review's request, #3798); null when the review did not run or its
    /// request was not whole, and a room whose rows are unread has no entry.
    /// </summary>
    public IReadOnlyDictionary<int, PlannedStock>? PlannedStoreStock { get; init; }

    /// <summary>
    /// The native thermal read of the unplaced planned shelters (PlannedThermalRequestOf, #3323): their cells and planned
    /// heaters, joined into the coupled thermal read (WithPlannedThermal); null when nothing was asked or native did not answer.
    /// </summary>
    public RimGovernor.Brain.GameFacts.Observations.CoupledThermalRead? PlannedThermal { get; init; }

    /// <summary>
    /// The native closed-room predictions of the planned shelters (PlannedRoomPredictionRequestOf, #3339): per room and ring
    /// stuff whose ring closes a room, the game's role and impressiveness stage once its walls and doors stand; none when
    /// no planned room was asked about (#3834).
    /// </summary>
    public EquatableArray<RimGovernor.Brain.GameFacts.Observations.PlannedRoomPrediction> PlannedRoomPredictions { get; init; }

    /// <summary>Live bill jobs' promised ingredients, per working pawn (MaterialBudget).</summary>
    public EquatableArray<IngredientReservation> BillReservations { get; init; }

    /// <summary>Committed fixed batches reconciled with current native remaining counts.</summary>
    public ProductionPromiseSet ProductionPromises { get; init; } = new ProductionPromiseSet.Blocked(ProductionPromiseBlock.AwaitingReview);

    public WorkAdmissionObservation WorkAdmission { get; init; } = new();
    public WorkResourceClaims? WorkClaims { get; init; }
    public WorkResourceView? WorkView { get; init; }

    /// <summary>The sleeping census (people, slaves, guests, beds, room quality); read every tick, so always present.</summary>
    public SleepingObservation Sleeping { get; init; } = new();

    /// <summary>Every colonist owns, and was seen using, a suitable bed and no bedroom step is due.</summary>
    public bool SleepingRecovered { get; init; }

    /// <summary>A planned individual bedroom step is due; it keeps the shelter/sleeping Need unmet once everyone owns a shelter bed.</summary>
    public bool BedroomsOwed { get; init; }

    /// <summary>The stock covers the butcher table's build cost (its cheapest stuff choice).</summary>
    public bool ButcherTableAffordable { get; init; }

    /// <summary>The warm standing tombs, morgues and meal closets (WarmCoolingRooms); they join the food-stores/refrigeration Need's rooms.</summary>
    public EquatableArray<string> WarmRooms { get; init; }

    /// <summary>The planned meal closet waits to be shelled while its dining room stands; it keeps the food-stores/refrigeration Need unmet.</summary>
    public bool MealClosetOwed { get; init; }

    /// <summary>A stove kitchen supersedes a cooking campfire; it keeps the food/cooking Need unmet.</summary>
    public bool CampfireRetireOwed { get; init; }

    /// <summary>A heat campfire's auto-refuel should switch, or a sleeping room sits unheated below its sleepers' comfort minimum (TemperatureOwed); it keeps the shelter/temperature Need unmet.</summary>
    public bool TemperatureOwed { get; init; }

    /// <summary>A bedroom below target, weakest in beauty, has a free cell for a sculpture (SculptureRoomsOwed); the work ledger's art bills read it. Unknown unless the rooms/art Need is composed.</summary>
    public bool SculptureRoomsOwed { get; init; }

    /// <summary>The Safe allowed area differs from the enclosed roofed rooms (SafeAreaCells); it keeps the shelter/safe-area Need unmet. Unknown unless that Need is composed.</summary>
    public SafeAreaStatus SafeAreaOwed { get; init; }

    /// <summary>The finished buildings the held rituals' patterns require (observation, from the frame's building census).</summary>
    public EquatableArray<RimGovernor.Brain.GameFacts.Observations.BuildingState> RitualSites { get; init; }

    /// <summary>The Safe allowed area's native load id, "" when the map has none (PlanSheltering).</summary>
    public string ShelterArea { get; init; } = "";

    /// <summary>The squad's draft set during a threat: the colonists a raid or manhunter pack does not shelter. Unknown shelters no colonist for a threat.</summary>
    public EquatableArray<PawnId> ShelterCombatants { get; init; }

    /// <summary>The NoDanger allowed area's native load id, "" when the map has none.</summary>
    public string NoDangerArea { get; init; } = "";

    /// <summary>The threat census's danger seeds (DangerSeeds), unknown with the hostile count.</summary>
    public EquatableArray<Cell> DangerSeeds { get; init; }

    /// <summary>Whether haulers are kept out of the danger cells now (DangerWindowOf).</summary>
    public bool DangerWindow { get; init; }

    /// <summary>The pawns with Hauling enabled.</summary>
    public EquatableArray<PawnId> DangerHaulers { get; init; }

    /// <summary>The Isolation allowed area's native load id, "" when the map has none (the admin/creep-joiners Need).</summary>
    public string IsolationArea { get; init; } = "";

    /// <summary>Counts the packed art no owed room reserves (SaleSculptures); read only while the wealth headroom is negative, it opens a trade as the shed_art need.</summary>
    public long SaleArt { get; init; }

    /// <summary>The generic goods the economy/trade Need's last declaration could rank (ExportSaleProducts), held stacks of which sell as surplus while the silver gap is open (ExportSaleSurplus). Set from the ColonyReview's memory; empty before the first declaration of a world.</summary>
    public EquatableArray<Resource> ExportProducts { get; init; }

    /// <summary>The part items a usable gear bench has a researched recipe for, read only while a medical pawn wants a part; the caravan assessment counts only parts no bench can make.</summary>
    public IReadOnlySet<Resource>? FabricableParts { get; init; }

    public AnimalUpkeepObservation AnimalUpkeep { get; init; } = new();

    public FoodStorageObservation FoodStorageUpkeep { get; init; } = new();

    public MedicalReserveObservation MedicalReserve { get; init; } = new();

    /// <summary>
    /// The population census: unlike AnimalUpkeep, this has no generic per-tick colony read to piggyback on
    /// (recruitable/current-interaction facts live only on the dedicated observations_read_population
    /// census), so it is the frame's population section: the prisoners, the capture/rescue candidates (every observed
    /// humanlike), the colony side of a prisoner's use, the guests' care cap inputs and the owned-pawn names.
    /// </summary>
    public PrisonerCensus Population { get; init; } = PrisonerCensus.Nobody;

    /// <summary>The containment cell's inputs and the entity rows the capture rule decides, set by the routine reading.</summary>
    public ContainmentPlanning Containment { get; init; } = new();

    /// <summary>The colonist creepjoiners the harm term's downside part prices (#3844), set by the routine reading; none by default.</summary>
    public CreepJoinerHarmInputs CreepJoiners { get; init; } = new();

    /// <summary>
    /// Carries the joiners family's quest census: every visible quest row
    /// (rimgovernor/observations_read_world_progression), read per cycle by a Source offering
    /// QuestSource, for JoinerDeficit to detect and SelectJoinerMethod to answer a joiner offer from.
    /// </summary>
    public EquatableArray<QuestState> QuestOffers { get; init; } = [];

    /// <summary>The catalog's classification of every quest root the census holds (QuestRoots.RootOf); empty without a census.</summary>
    public EquatableArray<QuestRoot> QuestRoots { get; init; }

    /// <summary>The colony's map: a quest on it is a quest at home (QuestSelection's off-map check).</summary>
    public MapId ColonyMap { get; init; }

    public EquatableArray<WorldSite> QuestSites { get; init; } = [];

    /// <summary>The royalty read from the pawn rows, the def mirror and the colony section: Inactive without the expansion (the default before the read).</summary>
    public RoyaltyState Royalty { get; init; } = new RoyaltyState.Inactive();

    /// <summary>The worship room, believers, presence demands and rituals of the frame's ideoligion (#3808); empty without one.</summary>
    public WorshipFacts Worship { get; init; } = new();

    /// <summary>The frame's Ideology mode: the expansion is not installed, or it is and the primary ideoligion comes with the catalog's defs.</summary>
    public IdeologyMode Ideology { get; init; } = new IdeologyMode.NotInstalled();

    /// <summary>The colony read's Anomaly section: Inactive without the expansion (the default before the read), and then the anomaly family derives no Needs; Active carries the monolith's state.</summary>
    public AnomalyState Anomaly { get; init; } = new AnomalyState.Inactive();

    /// <summary>
    /// The colony read's Biotech section: Inactive without the expansion (the default before the read), and then the
    /// biotech family derives no Needs; Active carries the chargers, gene buildings and wastepacks.
    /// </summary>
    public RimGovernor.Brain.GameFacts.Observations.BiotechSection Biotech { get; init; } = new RimGovernor.Brain.GameFacts.Observations.BiotechSection.Inactive();


    /// <summary>The bestowing-ceremony quests the title claim gate allows to accept now (ClaimQuests); the review fills it once the plan, rooms and royalty read are known.</summary>
    public EquatableArray<QuestId> TitleClaimQuests { get; init; }

    public EquatableArray<JoinerLetter> JoinerLetters { get; init; } = [];

    private RimGovernor.Brain.GameFacts.Observations.ColonyFactsSnapshot? census;

    /// <summary>The canonical native census retained by this review; a missing census refuses planning.</summary>
    public RimGovernor.Brain.GameFacts.Observations.ColonyFactsSnapshot Census { get => census ?? throw new RequiredFactException("colony census"); init => census = value; }

    /// <summary>The storyteller defs' threat comps from the catalog (#3539), read by ThreatSchedule.Of; empty without a catalog.</summary>
    public ThreatScheduleTable ThreatTable { get; init; } = new();

    /// <summary>The catalog's raid factions, hit coverage and natural armor (#3546), read by the harm term; empty without a catalog.</summary>
    public HarmCatalog HarmCatalog { get; init; } = new();

    /// <summary>The catalog's storyteller disease comps, biomes and disease incidents (#3824), read by IllnessSchedule.Of; empty without a catalog.</summary>
    public DiseaseTable DiseaseTable { get; init; } = new();


    /// <summary>The built mortars' shell targets (ArmoryShellsRules.MortarShellTargets), read once per review: ShellsShort and the armory declarer's shell orders derive from them (#3566). Empty without a built mortar or a known threat.</summary>
    public EquatableArray<Amount> ShellTargets { get; init; }

    /// <summary>The armory shell review: a built mortar's shell stock below half its target keeps the military/equipment Need unmet.</summary>
    public bool ShellsShort { get; init; }

    /// <summary>The colonists' and powered turrets' observed combat strength in raid-point units (DefenseCapacity).</summary>
    public double DefenseCapacity { get; init; }

    /// <summary>The total native corpse census that MorgueWaiting and the tomb census read.</summary>
    public EquatableArray<RimGovernor.Brain.GameFacts.Observations.WasteItem> Waste { get; init; }

    /// <summary>The map trader census (bridge.ListTraders) the economy/caravan Need needs; a source without the read leaves it unknown and the goal off.</summary>
    public EquatableArray<RimGovernor.Brain.GameFacts.Observations.Trader> Traders { get; init; }

    /// <summary>The keys of the tradeable traders whose sheet the offer book has no fresh record of (TradeRecovered's always-browse session); empty until the food plan marks them.</summary>
    public EquatableArray<string> UnpricedTraders { get; init; } = [];

    /// <summary>The total visible blighted-plant census, for BlightDeficit to detect and SelectBlightCuts to designate from.</summary>
    public EquatableArray<BlightedPlant> Blight { get; init; }

    /// <summary>Counts the owned stockpile zones by role kind for the colony status census; empty without an owned zone.</summary>
    public EquatableArray<StockpileRoleCount> StockpileZones { get; init; }

    /// <summary>The migrated families' Needs the runtime composes; supplied by the configured runtime, never native facts.</summary>
    public EquatableArray<NeedId> AvailableNeeds { get; init; }

    public UpkeepObservation Upkeep { get; init; } = new();

    private GearObservation? _gear;

    public GearObservation Gear
    {
        get => _gear ?? throw new RequiredFactException("gear census");
        init => _gear = value;
    }

    /// <summary>The finished buildings counted by definition: what a built-count goal is judged by (#3496).</summary>
    public EquatableArray<Amount> BuiltBuildings { get; init; }

    /// <summary>The allowed garments covering a core group with their recipes (ClothingRunway); empty until the gear census and catalog are read.</summary>
    public EquatableArray<ClothingGarment> Garments { get; init; }

    /// <summary>The hosting-room view of the total comfort census; null only before a colony decode.</summary>
    public ComfortObservation? Comfort { get; init; }

    /// <summary>The hosting census; a projection no colony decode built raises a required fact.</summary>
    public ComfortObservation RequireComfort() => Comfort ?? throw new RequiredFactException("comfort census");

    /// <summary>
    /// The same census before the hosting-room filter: every indoor seat at an eating surface and every
    /// recreation source, whatever room (or none) hosts it. The rooms/comfort Need measures it; Comfort
    /// keeps only facilities in rooms whose native role the facility catalog hosts.
    /// </summary>
    public ComfortObservation? BasicComfort { get; init; }

    /// <summary>The unfiltered census; a projection no colony decode built raises a required fact.</summary>
    public ComfortObservation RequireBasicComfort() => BasicComfort ?? throw new RequiredFactException("basic comfort census");

    /// <summary>The loot census: every visible haulable stack with its forbid state, hauling verdict and storage headroom.</summary>
    public EquatableArray<LootItem> EventLoot { get; init; }

    public Bounds MapBounds { get; init; }

    public EquatableArray<CarePawn> MedicalPawns { get; init; }

    public bool MedicalCareRecovered { get; init; }

    /// <summary>The planner's per-work-type coverage (PlanWork): the owners each type wanted and found and the pawns capable of it, so a goal can name a missing capability instead of stalling.</summary>
    public WorkRosterRead WorkRoster { get; init; } = WorkRosterRead.NotRead;

    // Quest capacity is derived from the current roster, world census and emergency review.
    /// <summary>The roster split the work plan implies: spare pawns, the home floor and the intended priorities; Unplanned until the review plans work.</summary>
    public QuestRoster QuestRoster { get; init; } = new QuestRoster.Unplanned();

    /// <summary>The colony's pawns as quest capacity reads them; Unseen without a complete pawn census.</summary>
    public QuestPawns QuestPawns { get; init; } = new QuestPawns.Unseen();

    /// <summary>The caravans and forming assemblies the world census holds.</summary>
    public EquatableArray<ExpeditionTrip> QuestExpeditionTrips { get; init; } = [];

    /// <summary>The tick of the world census the quest rows were read at.</summary>
    public Tick QuestObservedTick { get; init; }

    /// <summary>Whether the colony is calm enough to send a quest party out.</summary>
    public QuestCalm QuestCalm { get; init; } = new QuestCalm.Unsettled();

    /// <summary>Every work pawn's typed profile (Profiles); a presentation fact the review records, never a planner input.</summary>
    public EquatableArray<PawnProfile> WorkProfiles { get; init; }

    public long GrowingCells { get; init; }

    public long Armed { get; init; }

    /// <summary>Counts living, conscious colonists able to fight who hold no weapon; military/defense stays unmet while it is positive.</summary>
    public long Unarmed { get; init; }

    /// <summary>
    /// The colonists' food runway (#3929): the shortest diet and rot aware runway over those who eat, or none eat.
    /// Null only on a projection no colony decode built.
    /// </summary>
    public FoodRunway? FoodDays { get; init; }

    /// <summary>The food runway; a projection no colony decode built raises <see cref="RequiredFactException"/>.</summary>
    public FoodRunway RequireFoodRunway() => FoodDays ?? throw new RequiredFactException("food runway");

    public double FieldCoverage { get; init; }

    /// <summary>The same plan's construction helper record; null when the plan ran without the helper input.</summary>
    public ConstructionHelpRecord? WorkHelp { get; init; }

    /// <summary>
    /// The tile's canonical native growing calendar. InspectReview widens the policy's food and wood
    /// targets by its harvest gap (PlannerSettings.Seasonal) before measuring any latch. Every colony read carries it.
    /// </summary>
    public RimGovernor.Brain.GameFacts.Observations.FoodClimate Calendar => Census.FoodClimate;

    public double PowerHeadroom { get; init; }

    /// <summary>Observed ColonyForecast diagnostics not carried by the facts above.</summary>
    public ColonyForecastObserved ForecastObserved { get; init; } = new();
    public ForecastNeedsReview? NeedsAlternatives { get; init; }
    public ForecastCoupledCandidates? CoupledAlternatives { get; init; }
    public EquatableArray<PlannedRoom> PlannedShelters { get; init; }
    /// <summary>The layout plan's door graph between the <see cref="PlannedShelters"/> (LayoutPlan.Links): the rooms a circulation corridor reaches (#3815).</summary>
    public EquatableArray<RoomLink> PlannedLinks { get; init; }
    /// <summary>The layout plan's pens by room id, each its herd's key (PlannedRoom.Herd: the race, "" the misc unit's), which the pen credit matches roamers to (#3812).</summary>
    public IReadOnlyDictionary<int, string> PlannedPens { get; init; } = new Dictionary<int, string>();
    /// <summary>
    /// The urgent build this review compares (#3537): the buildings of the sleeping shelter or bed placements whose blueprints
    /// stand since the last review, which the clock was held for. While it is set the coupled comparison leaves routine
    /// construction out (planned sites and every other placed site are vanilla's) and compares these sites and the planned
    /// shelters they ring. Empty in every other review.
    /// </summary>
    public EquatableArray<Building> UrgentSites { get; init; }
    /// <summary>Existing journal ownership, not forecast progress: a coupled activity is installed.</summary>
    public bool CoupledWorkActive { get; init; }
    /// <summary>The ids the latest coupled comparison selected (#3563): the warm start of this review's search; empty before the first selection after a restart.</summary>
    public EquatableArray<string> PreviousCoupledSelection { get; init; }
    /// <summary>The planned rooms (<see cref="PlannedShelters"/>) at the review that selected <see cref="PreviousCoupledSelection"/> (#3556): a room the layout re-tiled under a new id since is found again by its role and ground (<see cref="Rules.WarmStartOf"/>).</summary>
    public EquatableArray<PlannedRoom> PreviousPlannedShelters { get; init; }
    /// <summary>The pawn values of the latest review that valued pawns (#3825): the creepjoiner term's Leaves and Aggressive price (#3844); empty before the first layout plan after a restart.</summary>
    public EquatableArray<PawnValue> PreviousPawnValues { get; init; }
    public EssentialServiceInputs EssentialInputs { get; init; } = new();


    /// <summary>The refuelable buildings of the power census (FuelRunway).</summary>
    public EquatableArray<FuelConsumer> Fuel { get; init; }

    /// <summary>The open costs of admitted methods, from the last review's live records: an open shortfall raises an economy/resource floor for the bounded difference.</summary>
    public EquatableArray<AdmittedCost> Admitted { get; init; }

    /// <summary>The player's unfinished gear bills with their recipe slots (OpenBillDemand): ingredient demand ResourceDemandOf counts.</summary>
    public EquatableArray<OpenBill> OpenBills { get; init; }


    /// <summary>The safe surface ore per material the consumption ring names; a material without an entry (its source read failed, or none was read) has none.</summary>
    public IReadOnlyDictionary<Resource, long> ResourceSurfaceOre { get; init; } = new Dictionary<Resource, long>();

    /// <summary>The recurring spend over the rate window, from native's realized-consumption ring; null while the ring is unread (no page merged, or the read failed).</summary>
    public ResourceConsumption? ResourceConsumption { get; init; }

    public EquatableArray<ResourceRunway> ResourceRunways { get; init; }

    /// <summary>The colonists whose drug policy permits each social drug (DrugUsers); it sets the drug runway's reserve before any dose is observed.</summary>
    public IReadOnlyDictionary<Resource, long>? DrugUsers { get; init; }


    private ResearchFacts? research;

    /// <summary>The native research state read inside the same paused identity bracket as the other routine facts. A projection without a routine census refuses research planning.</summary>
    public ResearchFacts Research { get => research ?? throw new RequiredFactException("research census"); init => research = value; }

    /// <summary>The sorted ResearchProjectDefs other goals' recorded evidence is waiting on (the workshop ladder's research rung); the first unfinished one is the economy/research Need.s target.</summary>
    public EquatableArray<string> ResearchNeeds { get; init; }

    /// <summary>
    /// The derived stock floors other goals' recorded evidence asks for (the defensive layout's turret fuel
    /// the census found no stock of); ResourceGoalTargets merges them into the operator's the economy/resource Need
    /// targets, never lowering a configured floor.
    /// </summary>
    public IReadOnlyDictionary<Resource, long> ResourceNeeds { get; init; } = new Dictionary<Resource, long>();

    /// <summary>The colonist head count the player's goals ask for (#3494), 0 for none; the joiner and lance population target rises to it, never below the bot's own.</summary>
    public int ColonistGoal { get; init; }

    /// <summary>The head count per animal race the player's goals ask the herd to reach (#3494); the herd plan raises its race's band to it.</summary>
    public IReadOnlyDictionary<Resource, long> AnimalGoals { get; init; } = new Dictionary<Resource, long>();

    /// <summary>The open bills the player's worn-count goals owe (#3496): each missing garment priced like a replacement, merged into <see cref="ClothingDemandRules.ClothingRunway"/>.</summary>
    public IReadOnlyList<OpenBill> WornBills { get; init; } = [];

    /// <summary>
    /// The stuff tier 2 puts on each pawn's gear slot within the clothing horizon (<see cref="ColonyValue.ClothingStuff"/>, #3623), stamped
    /// by this review's colony model run before the rounds review: the clothing runway demands it. Empty when the model did not run.
    /// </summary>
    public IReadOnlyDictionary<(PawnId Pawn, GearSlot Slot), Resource> ClothingStuff { get; init; } = new Dictionary<(PawnId Pawn, GearSlot Slot), Resource>();

    /// <summary>The quests the player's goals name (#3499); the quest step considers them before any other offer.</summary>
    public IReadOnlyList<QuestId> NamedQuests { get; init; } = [];

    /// <summary>The rooms and hospital beds the player's goals ask the planned-room, hospital and station channels for (#3496).</summary>
    public GoalBuildDemand GoalBuild { get; init; } = GoalBuildDemand.None;

    /// <summary>The player's unmet goals as the forecast's capability-days ledger values them (#3503).</summary>
    public EquatableArray<ForecastGoalValue> GoalValues { get; init; }

    /// <summary>The pawns the player's unmet skill goals train and their skills (#3508): the work planner and the reading policy read them.</summary>
    public EquatableArray<SkillGoal> GoalTraining { get; init; }

    /// <summary>The operations the player's unmet health goals ask for (#3508): the surgery context carries them to the surgery planner.</summary>
    public EquatableArray<SurgeryGoal> GoalSurgeries { get; init; }

    public HostileCensus Hostiles { get; init; } = new HostileCensus.AwaitingEmergency();

    public long CriticalPatients { get; init; }

    /// <summary>
    /// Counts the critical patients who are bleeding or downed with a tend outstanding
    /// (policy.UrgentPatients). The remote hold and the resource harvest read it; the medical family's critical
    /// Needs count every critical patient (#3258).
    /// </summary>
    public long UrgentPatients { get; init; }

    /// <summary>A drafted colonist no live plan needs waits for the undraft sweep.</summary>
    public bool CleanupPawns { get; init; }

    /// <summary>A colonist whose hostility response differs from the one it should hold; the admin/work-assignments Need writes it.</summary>
    public bool HostilityOwed { get; init; }

    /// <summary>A pawn whose medical care differs from its cap.</summary>
    public bool MedicalCareOwed { get; init; }

    /// <summary>A colonist whose medicine carry count differs from the planned one; the admin/work-assignments Need writes it.</summary>
    public bool MedicineCarryOwed { get; init; }

    /// <summary>An owned pawn whose short name an older owned pawn holds; the admin/work-assignments Need renames it.</summary>
    public bool NamesOwed { get; init; }

    /// <summary>A stocked kitchen stands ready to cook.</summary>
    public bool Cooking { get; init; }

    public WorkCoverageState WorkCoverage { get; init; }

    public bool PowerRequired { get; init; }

    public bool DisabledConsumers { get; init; }

    public bool PowerWeatherSafe { get; init; }

    public DisasterShortCircuit ShortCircuit { get; init; } = new DisasterShortCircuit.NoneSeen();
}

public sealed record Latches
{
    public bool MedicalReserve { get; set; }

    /// <summary>The planned outdoor dash window (#2765), held until its bound; zero when none is open.</summary>
    public DashWindow Dash { get; set; } = new();
}

/// <summary>One review's findings: the latches, the disaster history and the derived facts the families read.</summary>
public sealed record Findings
{
    [System.Text.Json.Serialization.JsonIgnore]
    public ForecastCoupledSelection? CoupledSelection { get; set; }

    /// <summary>The wall time the policy inspection that produced these findings took (the store times it; the coupled search <see cref="CoupledSelection"/> dominates it), for the rounds_review row; derived, not saved.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public double InspectMs { get; set; }

    /// <summary>How many ids of the previous selection named a planned room re-tiled under a new id and were pointed at its successor (#3556); derived, not saved.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int CoupledWarmStartTranslated { get; set; }

    /// <summary>The work the selection left unscheduled, per work type and stat (#3825); null when tier 2 did not run; derived, not saved.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public WorkShortfall? WorkShortfall { get; set; }

    /// <summary>Every colonist's value in capability-days a day (#3825), in pawn order; empty when tier 2 did not run or the values
    /// await a plan (<see cref="PawnValuesAwaitingPlan"/>); derived, not saved.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public EquatableArray<PawnValue> PawnValues { get; set; }

    /// <summary>The plan the pawn values await though tier 2 ran (#3857, awaiting_plan): <see cref="PawnValueRules.AwaitingLayoutPlan"/>
    /// before the colony has a layout plan, when <see cref="PawnValues"/> is empty and <see cref="WorkShortfall"/> stands; empty
    /// otherwise; derived, not saved.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string PawnValuesAwaitingPlan { get; set; } = "";
    public DisasterHistory? Disaster { get; set; }

    public Latches Latches { get; set; } = new();

    /// <summary>The review's one resource-demand value (Needs are the effective the economy/resource Need stock targets the review held the census to; the stock overlay tints stockpiles by them).</summary>
    public DerivedDemand ResourceDemand { get; set; } = new();

    /// <summary>The review's trade facts (the silver gap, the caravans' standing) the economy family's trade Needs read; derived, not saved.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public TradeReview Trade { get; set; } = new TradeReview.AwaitingFoodPlan();

    /// <summary>Whether the review owes work assignments, settings or coupled activities (WorkAssignmentsOwed), which the admin/work-assignments Need reads; derived, not saved.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public WorkAssignmentsReview WorkAssignments { get; set; }
    public EssentialServiceReview? EssentialServices { get; set; }
}

public static partial class Rules
{
    /// <summary>Supplies the routine inspection targets and budgets. Individual planners bound their orders; vanilla priorities schedule pawn work.</summary>
    public static PlannerSettings DefaultPlannerSettings() => new()
    {
        MedicalReserve = MedicalReservePolicy.Default(),
        FoodStorage = FoodStorageUpkeepRules.DefaultFoodStoragePolicy(),
        Lighting = LightingPolicy.Default(),
        Flooring = FlooringRules.DefaultFlooringPolicy(),
        Routes = RoutesRules.DefaultRoutesPolicy(),
        FoodMinDays = 3,
        FoodTargetDays = 7,
        FootholdFoodDays = 3,
        FoodReserveDays = FoodReserveRules.DefaultFoodReserveDays,
        PrisonerReleaseAfterDays = 15,
        ColdEnter = 12,
        ColdExit = 16,
        HotExit = 28,
        HotEnter = 32,
        HuntStallTicks = GameTime.TicksPerDay / 2,
        AcquisitionStallTicks = GameTime.TicksPerDay,
    };

    // The foothold facts below are read live from one review's facts: the foothold goals open on them
    // (InspectReview), and the disaster services (DisasterServiceFacts)
    // and the food ladder (FoodProgress) read the ones they need.

    /// <summary>The colonist count the foothold sizes for.</summary>
    internal static long FootholdCount(ReviewFacts f) => f.Census.ColonistCount;

    internal static bool FootholdSleeping(ReviewFacts f) => FootholdCount(f) > 0 && f.Census.BedCapacity >= FootholdCount(f);

    internal static bool FootholdShelter(ReviewFacts f) => FootholdCount(f) > 0 && f.Census.IndoorSleepingCapacity >= FootholdCount(f);

    internal static bool FootholdProduction(ReviewFacts f) => f.Census.ColonistCount > 0 && f.GrowingCells >= f.Census.ColonistCount * 10;

    internal static bool FootholdFood(ReviewFacts f, PlannerSettings p) => Reached(f.FoodDays, p.FootholdFoodDays);

    /// <summary>Whether the required runway reaches <paramref name="days"/>.</summary>
    internal static bool Reached(FoodRunway? runway, double days) =>
        (runway ?? throw new RequiredFactException("food runway")).Reaches(days);

    internal static bool FootholdTemperature(ReviewFacts f, PlannerSettings p) =>
        f.Census.SleepingTemperature is RimGovernor.Brain.GameFacts.Observations.SleepingTemperature.Indoor indoor && indoor.MinC >= p.ColdEnter && indoor.MaxC <= p.HotEnter;

    internal static bool FootholdPower(ReviewFacts f)
    {
        var powered = !f.PowerRequired || f.PowerHeadroom >= 0;
        return f.PowerWeatherSafe && powered && !f.DisabledConsumers;
    }

    /// <summary>The armed fighters a colony of n colonists owes: two, every colonist when fewer, never below one. The only place the number lives.</summary>
    internal static long ArmedTarget(long n) => Math.Max(1, Math.Min(2, n));

    private static DisasterServiceStatus ServiceStatus(bool met) => met ? DisasterServiceStatus.Restored : DisasterServiceStatus.Deficit;

    /// <summary>The survival services ReviewDisaster tracks, read live from the review's facts (infrastructure is its own).</summary>
    public static Dictionary<DisasterService, DisasterServiceStatus> DisasterServiceFacts(ReviewFacts f, PlannerSettings p) => new()
    {
        [DisasterService.Food] = f.FoodDays is { } runway ? ServiceStatus(runway.Reaches(p.FootholdFoodDays)) : DisasterServiceStatus.AwaitingFoodPlan,
        [DisasterService.Production] = ServiceStatus(FootholdProduction(f)),
        [DisasterService.Sleeping] = ServiceStatus(FootholdSleeping(f)),
        [DisasterService.Shelter] = ServiceStatus(FootholdShelter(f)),
        [DisasterService.Temperature] = ServiceStatus(FootholdTemperature(f, p)),
        [DisasterService.Cooking] = ServiceStatus(f.Cooking),
        [DisasterService.Power] = ServiceStatus(FootholdPower(f)),
        [DisasterService.Storage] = ServiceStatus(StockpileRules.FoodStorageStanding(f)),

    };

    /// <summary>
    /// The combat situation (#3311): unclear while the hostile count is unknown; active while a hostile stands or
    /// the food plan opens a squad hunt (<paramref name="prey"/> is its prey count); clear otherwise. The review's
    /// combat standing and the <c>military/combat</c> Need both read it, so they cannot disagree.
    /// </summary>
    public static Situation CombatSituation(HostileCensus hostiles, int prey) => hostiles switch
    {
        HostileCensus.AwaitingEmergency => Situation.Unclear,
        HostileCensus.Quiet => prey > 0 ? Situation.Active : Situation.Clear,
        HostileCensus.Active { Count: > 0 } => Situation.Active,
        HostileCensus unexpected => throw new DomainException($"invalid hostile census state: {unexpected.GetType().Name}"),
    };


    /// <summary>
    /// The full review: it validates the facts and derives once what the families read (the latches, the resource
    /// demand, the coupled selection, the disaster history, the trade review, the work-assignments measure and the
    /// essential services). It judges no Need. Throws DomainException on invalid facts.
    /// </summary>
    /// <summary>
    /// The previous selection as this review's warm start (#3556). A tiled replan hands an unbuilt planned room a new id
    /// (ids are never reused), so an id of the previous selection that names a room no longer in the plan is pointed at
    /// the current room of the same role whose floor overlaps the remembered one, the lowest id when several do; an id
    /// naming a room still in the plan, no planned room, or a room with no such successor is kept as it was.
    /// </summary>
    public static EquatableArray<string> WarmStartOf(EquatableArray<string> previous, EquatableArray<PlannedRoom> previousRooms, EquatableArray<PlannedRoom> rooms)
    {
        if (previous.Count == 0 || previousRooms.Count == 0) return previous;
        var standing = rooms.Select(r => r.Id).ToHashSet();
        var successors = new Dictionary<int, int>();
        foreach (var old in previousRooms)
        {
            if (standing.Contains(old.Id)) continue;
            var successor = rooms.Where(r => r.Role == old.Role && r.Region.Overlaps(old.Region)).Select(r => r.Id).DefaultIfEmpty(0).Min();
            if (successor != 0) successors[old.Id] = successor;
        }
        if (successors.Count == 0) return previous;
        const string prefix = "planned/room";
        return EquatableArray.From(previous.Select(id =>
        {
            if (!id.StartsWith(prefix, StringComparison.Ordinal)) return id;
            var end = id.IndexOf('/', prefix.Length);
            if (end < 0) end = id.Length;
            return int.TryParse(id.AsSpan(prefix.Length, end - prefix.Length), out var room) && successors.TryGetValue(room, out var successor)
                ? prefix + successor + id[end..] : id;
        }));
    }

    public static Findings InspectReview(ReviewFacts f, Latches previous, PlannerSettings p, Func<double>? clockMs = null)
    {
        if (!f.Calendar.Valid())
        {
            throw new DomainException("invalid calendar fact");
        }
        p = p.Seasonal(f.Calendar, f.DisasterConditions);
        var medicine = MedicalRules.ReviewMedicalReserve(f.MedicalReserve with { Census = f.Census }, previous.MedicalReserve, p.MedicalReserve);
        p.Validate();
        // The colonist and capacity counts are native unsigned counts and need no check.
        if (f.GrowingCells < 0 || f.GrowingCells > long.MaxValue / 10) throw new DomainException("invalid growing cell count");
        if (f.Hostiles is not HostileCensus.AwaitingEmergency && f.Hostiles.ThreatCount > long.MaxValue / 10)
            throw new DomainException("invalid routine hostile count");
        if (f.CriticalPatients < 0 || f.UrgentPatients < 0) throw new DomainException("invalid patient count");
        if (f.WoodStock() < 0 || f.WoodStock() > long.MaxValue / 10)
        {
            throw new DomainException("invalid routine count");
        }
        if (double.IsNaN(f.PowerHeadroom) || double.IsInfinity(f.PowerHeadroom))
        {
            throw new DomainException("nonfinite routine fact");
        }
        if (!double.IsFinite(f.FieldCoverage))
            throw new DomainException("nonfinite routine fact");
        if (!double.IsFinite(f.Census.OutdoorTemperatureC))
        {
            throw new DomainException("nonfinite routine fact");
        }
        var coverage = f.FieldCoverage;
        if (coverage < 0)
        {
            throw new DomainException("negative food fact");
        }
        if (f.FoodDays is FoodRunway.Days { Value: var runway } && (double.IsNaN(runway) || double.IsInfinity(runway) || runway < 0))
        {
            throw new DomainException("nonfinite or negative food runway");
        }
        var latches = new Latches
        {
            MedicalReserve = medicine.Active,
            Dash = f.Dash,
        };
        var demand = ResourceRules.ResourceDemandOf(f, p, latches);
        var r = new Findings { Latches = latches, ResourceDemand = demand };
        var warm = WarmStartOf(f.PreviousCoupledSelection, f.PreviousPlannedShelters, f.PlannedShelters);
        r.CoupledWarmStartTranslated = warm.Zip(f.PreviousCoupledSelection).Count(pair => pair.First != pair.Second);
        if (f.CoupledAlternatives is { } coupled)
            r.CoupledSelection = ForecastCoupledSelection.CompareActivityOutcomes(ForecastCoupled.CoupledForecastRequestOf(new(f.Tick.Value),
                ForecastCoupled.TierOneHorizonDays, f, demand, []), coupled, warmStart: warm, clockMs: clockMs);
        // Each colonist's value against the work the selection left short (#3825), once tier 2 has given the pawns' free hours.
        if (r.CoupledSelection is { } selected && f.CoupledAlternatives is { Context.ModelBasis: { } basis } candidates)
            (r.WorkShortfall, r.PawnValues, r.PawnValuesAwaitingPlan) = PawnValueRules.Review(candidates, selected, f.ForecastObserved.Needs ?? throw new RequiredFactException("needs read"),
                f.WorkSpeeds, f.FoodWork, basis);
        // The disaster history the disaster family's Needs read (#3304).
        r.Disaster = DisasterRules.ReviewDisaster(f.DisasterConditions, f.RecoveryBuildings, DisasterServiceFacts(f, p), f.Disaster, f.DisasterTick, f.ShortCircuit);
        r.Trade = TradeReviewOf(f, p, medicine, demand);
        r.WorkAssignments = WorkAssignmentsOwed(f, r.CoupledSelection);
        r.EssentialServices = EssentialServicesRules.ReviewEssentialServices(f, p, demand, medicineActive: latches.MedicalReserve);
        return r;
    }

    /// <summary>
    /// The food/supply Need's butcher-facility check. A bench must stand apart from the kitchen: a butcher
    /// bench inside it keeps the colony fed but not clean. The butcher spot stands in the planned butchery
    /// until the table is built; while the stock cannot pay for the table the spot alone is enough, and a table standing beside
    /// its spot owes the spot's removal. It is wanted whatever the food runway, because hunts and hides are
    /// processed on it.
    /// </summary>
    internal static bool ButcherFacilityMet(ReviewFacts f)
    {
        var benches = f.EssentialInputs.Benches.Where(b => b.Butcher).ToArray();
        if (benches.Length == 0)
        {
            return false;
        }
        var shared = KitchenSeparationRules.KitchenSeparation(f.Upkeep.Rooms);
        var colocated = new HashSet<string>(StringComparer.Ordinal);
        foreach (var room in shared)
        {
            colocated.Add(room.Id);
        }
        bool apart = false, table = false, spot = false;
        foreach (var bench in benches)
        {
            if (bench.Room is not { } room || !colocated.Contains(room))
            {
                apart = true;
                table = table || bench.Definition != ButcherSpotDefinition;
                spot = spot || bench.Definition == ButcherSpotDefinition;
            }
        }
        if (!apart)
        {
            return false;
        }
        if (table)
        {
            return !spot;
        }
        // Only the spot stands apart: with the stock to build the table, the goal stays open for it.
        return !f.ButcherTableAffordable;
    }

    /// <summary>The free, instant butcher bench that stands in the planned butchery until the table is built.</summary>
    public const string ButcherSpotDefinition = "ButcherSpot";
}
