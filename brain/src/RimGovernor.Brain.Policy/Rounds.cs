namespace RimGovernor.Brain.Policy;

public sealed record RoundsPolicy
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
    /// RoundsAcquisitionPlanner abandons it and lets a fresh SelectAcquisition pass propose something else.
    /// Native's own HuntingSafety.RouteSafe guard can repeatedly interrupt the shared game clock while a
    /// hunter's route stays unsafe; that guard stays fully authoritative, but without this grace the stuck
    /// action reads as open work forever and blocks the planner from ever trying a different prey or a
    /// non-hunt source. It counts from the last observation, and a live hunt (travel, chase, the hunter's
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
    /// Bounds how long a goal's progress record may go without native evidence advancing its expected
    /// observable before ExpireConcernProgress rotates the method (or keys the failed situation out with a
    /// cooldown): ConcernProgressContract's default contract and the food ladder's rungs (FoodProgress) both
    /// use it as their deadline.
    /// </summary>
    public long ConcernStallTicks { get; init; }

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
        if (ConcernStallTicks <= 0)
        {
            throw new DomainException("invalid standard stall grace");
        }
    }
}

/// <summary>
/// Holds derived native facts, not forecasts masquerading as output. FoodDays is the accessible
/// diet/rot-aware stock runway. FieldCoverage is the separate native crop-capacity forecast; it never
/// increases FoodDays.
/// </summary>
public sealed record RoundsFacts
{
    /// <summary>The catalog's item numbers (market value, nutrition, medical potency, stuff factors); the zero value without a catalog.</summary>
    public ItemFacts Items { get; init; } = new();

    /// <summary>The recipe rows' derived facts: the stuff-made part installs peg-leg cycling plans around.</summary>
    public RecipeFacts Recipes { get; init; } = new();

    /// <summary>The hospital's animal ward; unread (the zero value) until the layout exposes it, which keeps sterilize off.</summary>
    public AnimalWard AnimalWard { get; init; } = new();

    public Fact<FoodPlan> FoodPlan { get; init; }

    public Fact<FoodReserveReview> FoodReserve { get; init; }

    /// <summary>The babies' food review; unknown without Biotech baby care or consumer facts.</summary>
    public Fact<BabyFeeding> BabyFeeding { get; init; }

    public Fact<EquatableArray<FoodIngredientSlot>> TradeMealIngredients { get; init; }

    public Fact<EquatableArray<PenGrazing>> PenGrazing { get; init; }

    /// <summary>What a cell of each terrain grows for grazing in the worst quadrum, the per-cell term of the game's pen food sum (PastureTable).</summary>
    public Fact<PastureTable> Pasture { get; init; }

    public Fact<RecoverySafety> RecoverySafety { get; init; }

    public Fact<EquatableArray<RecoveryWorker>> RecoveryWorkers { get; init; }

    public Fact<EquatableArray<DisasterCondition>> DisasterConditions { get; init; }

    /// <summary>The colony biome's permanent darkness: its map conditions include a no-sunlight class.</summary>
    public Fact<bool> OutdoorsDark { get; init; }

    public Fact<EquatableArray<RecoveryBuilding>> RecoveryBuildings { get; init; }

    public DisasterHistory? Disaster { get; init; }

    public Tick DisasterTick { get; init; }

    /// <summary>The game tick the facts were assembled at, which Dash is judged against; zero where no dash is planned.</summary>
    public Tick Tick { get; init; }

    /// <summary>The planned outdoor dash window through extreme cold (#2765); unknown or zero is closed.</summary>
    public Fact<DashWindow> Dash { get; init; }

    public Fact<EquatableArray<MoodPawn>> MoodPawns { get; init; }

    /// <summary>Where the colony loses mood, built each review from the mood census and the catalog thought facts; unknown with the census.</summary>
    public Fact<MoodLedger> MoodLedger { get; init; }

    public MoodHistory Mood { get; init; } = new();

    public Fact<HomeCoverageObservation> HomeCoverage { get; init; }

    public Fact<EquatableArray<StoneStructure>> StoneStructures { get; init; }

    public Fact<EquatableArray<ConstructionClaim>> ConstructionClaims { get; init; }

    public Fact<CurrentConstruction> CurrentConstruction { get; init; }

    /// <summary>The material standing blueprints and frames are still owed, per resource.</summary>
    public Fact<IReadOnlyDictionary<Resource, long>> ConstructionDeficit { get; init; }

    /// <summary>The material the whole layout plan still owes its walls, doors, floors and stations, per room id (PlannedShellDemandByRoom and kin); unknown while the plan or the construction census is.</summary>
    public Fact<IReadOnlyDictionary<int, IReadOnlyDictionary<Resource, long>>> PlannedConstruction { get; init; }

    /// <summary>
    /// The same owed operations as hypothetical sites, one per op x stuff, plus the unplaced shelf candidates, each joined to
    /// the native planned-site read (PlannedSitesOf, JoinPlannedSites); unknown while the plan, the construction census or the colonists are.
    /// </summary>
    public Fact<PlannedSiteFacts> PlannedSites { get; init; }

    /// <summary>
    /// The native thermal read of the unplaced planned shelters (PlannedThermalRequestOf, #3323): their cells and planned
    /// heaters, joined into the coupled thermal read (WithPlannedThermal); null when nothing was asked or native did not answer.
    /// </summary>
    public RimGovernor.Contracts.Wire.Observations.CoupledThermalRead? PlannedThermal { get; init; }

    /// <summary>Live bill jobs' promised ingredients, per working pawn (MaterialBudget).</summary>
    public Fact<EquatableArray<IngredientReservation>> BillReservations { get; init; }

    /// <summary>Committed fixed batches reconciled with current native remaining counts.</summary>
    public Fact<EquatableArray<ForecastCommitment>> ProductionPromises { get; init; }

    public WorkAdmissionObservation WorkAdmission { get; init; } = new();
    public Fact<EquatableArray<RimGovernor.Contracts.Wire.Observations.WorkResourcePawn>> WorkResources { get; init; }
    public WorkResourceClaims? WorkClaims { get; init; }
    public WorkResourceView? WorkView { get; init; }

    public Fact<SleepingObservation> Sleeping { get; init; }

    public Fact<bool> SleepingRecovered { get; init; }

    /// <summary>A planned individual bedroom step is due; it keeps the shelter/sleeping Need unmet once everyone owns a shelter bed.</summary>
    public Fact<bool> BedroomsOwed { get; init; }

    /// <summary>The stock covers the butcher table's build cost (its cheapest stuff choice).</summary>
    public Fact<bool> ButcherTableAffordable { get; init; }

    /// <summary>The warm standing tombs, morgues and meal closets (WarmCoolingRooms); they join the food-stores/refrigeration Need's rooms.</summary>
    public Fact<EquatableArray<string>> WarmRooms { get; init; }

    /// <summary>The planned meal closet waits to be shelled while its dining room stands; it keeps the food-stores/refrigeration Need unmet.</summary>
    public Fact<bool> MealClosetOwed { get; init; }

    /// <summary>A stove kitchen supersedes a cooking campfire; it keeps the food/cooking Need unmet.</summary>
    public Fact<bool> CampfireRetireOwed { get; init; }

    /// <summary>A weapon bench stands beside the crafting spot that stood in for it; it keeps the military/equipment Need unmet for the deconstruction.</summary>
    public Fact<bool> CraftingSpotRetireOwed { get; init; }

    /// <summary>A heat campfire's auto-refuel should switch, or a sleeping room sits unheated below its sleepers' comfort minimum (TemperatureOwed); it keeps the shelter/temperature Need unmet.</summary>
    public Fact<bool> TemperatureOwed { get; init; }

    /// <summary>A bedroom below target, weakest in beauty, has a free cell for a sculpture (SculptureRoomsOwed); the work ledger's art bills read it. Unknown unless the rooms/art Need is composed.</summary>
    public Fact<bool> SculptureRoomsOwed { get; init; }

    /// <summary>The dispatcher's unmet throughput per bench kind (units a day short and why), sorted by kind; the facilities ladder reads it. Empty when nothing is short or the ledger has no declarers.</summary>
    public EquatableArray<UnmetThroughput> UnmetThroughput { get; init; }

    /// <summary>The Safe allowed area differs from the enclosed roofed rooms (PlanSafeArea); it keeps the shelter/safe-area Need unmet. Unknown unless that Need is composed.</summary>
    public Fact<bool> SafeAreaOwed { get; init; }



    /// <summary>The finished buildings the held rituals' patterns require (observation, from the frame's building census).</summary>
    public Fact<EquatableArray<RitualSite>> RitualSites { get; init; }

    /// <summary>The Safe allowed area's native load id, "" when the map has none (PlanSheltering).</summary>
    public Fact<string> ShelterArea { get; init; }

    /// <summary>The squad's draft set during a threat: the colonists a raid or manhunter pack does not shelter. Unknown shelters no colonist for a threat.</summary>
    public Fact<EquatableArray<PawnId>> ShelterCombatants { get; init; }

    /// <summary>The NoDanger allowed area's native load id, "" when the map has none.</summary>
    public Fact<string> NoDangerArea { get; init; }

    /// <summary>The threat census's danger seeds (DangerSeeds), unknown with the hostile count.</summary>
    public Fact<EquatableArray<Cell>> DangerSeeds { get; init; }

    /// <summary>Whether haulers are kept out of the danger cells now (DangerWindowOf).</summary>
    public Fact<bool> DangerWindow { get; init; }

    /// <summary>The pawns with Hauling enabled.</summary>
    public Fact<EquatableArray<PawnId>> DangerHaulers { get; init; }

    /// <summary>The Isolation allowed area's native load id, "" when the map has none (the admin/creep-joiners Need).</summary>
    public Fact<string> IsolationArea { get; init; }

    /// <summary>Counts the packed art no owed room reserves (SaleSculptures); read only while the wealth headroom is negative, it opens a trade as the shed_art need.</summary>
    public Fact<long> SaleArt { get; init; }

    /// <summary>The generic goods the economy/trade Need's last declaration could rank (ExportSaleProducts), held stacks of which sell as surplus while the silver gap is open (ExportSaleSurplus). Set from the Rounder's memory; empty before the first declaration of a world.</summary>
    public EquatableArray<Resource> ExportProducts { get; init; }

    /// <summary>The part items a usable gear bench has a researched recipe for, read only while a medical pawn wants a part; the caravan assessment counts only parts no bench can make.</summary>
    public IReadOnlySet<Resource>? FabricableParts { get; init; }

    public AnimalUpkeepObservation AnimalUpkeep { get; init; } = new();

    public FoodStorageObservation FoodStorageUpkeep { get; init; } = new();

    public MedicalReserveObservation MedicalReserve { get; init; } = new();

    /// <summary>
    /// Carries Population-*'s recruit/maintain census: unlike AnimalUpkeep, this has no generic per-tick
    /// colony read to piggyback on (recruitable/current-interaction facts live only on the dedicated
    /// rimgovernor/observations_read_population census), so it is populated by a dedicated per-cycle
    /// RoundsSource read instead of ObserveColony's always-present projection.
    /// </summary>
    public Fact<EquatableArray<PrisonerFacts>> Prisoners { get; init; }

    /// <summary>The same read's colony side of each prisoner's use: the free colonists' best skills and the Ideology facts.</summary>
    public Fact<PrisonerColony> PrisonerColony { get; init; }

    /// <summary>
    /// Carries Population-*'s capture/rescue candidate census: every observed humanlike from the same
    /// dedicated population read Prisoners uses, broadened past prisoners alone so
    /// the prisoners family can detect and select a downed hostile or unadmitted guest to dispatch.
    /// </summary>
    public Fact<EquatableArray<CustodyFacts>> Custody { get; init; }

    /// <summary>The containment cell's inputs and the entity rows the capture rule decides, set by the routine reading.</summary>
    public ContainmentPlanning Containment { get; init; } = new();

    /// <summary>The same population read's storyteller outlook.</summary>
    public PopulationOutlook Outlook { get; init; } = new();

    /// <summary>The same read's owned-pawn short-name census.</summary>
    public Fact<EquatableArray<OwnedName>> OwnedNames { get; init; }

    /// <summary>
    /// Carries the joiners family's quest census: every visible quest row
    /// (rimgovernor/observations_read_world_progression), read per cycle by a RoundsSource offering
    /// RoundsQuestSource, for JoinerDeficit to detect and SelectJoinerMethod to answer a joiner offer from.
    /// </summary>
    public Fact<EquatableArray<JoinerOffer>> QuestOffers { get; init; }

    public Fact<EquatableArray<WorldSite>> QuestSites { get; init; }

    /// <summary>The royalty facts from the pawn rows, the def mirror and the colony section; unknown when Royalty is not applicable or a read failed.</summary>
    public Fact<RoyaltyFacts> Royalty { get; init; }

    /// <summary>The primary ideoligion from the frame's ideology section with the catalog's defs; unknown when the frame carries no section (no Ideology, or no primary ideoligion).</summary>
    public Fact<Ideoligion> Ideology { get; init; }

    /// <summary>Whether the Ideology expansion is active; unknown when the frame does not say.</summary>
    public Fact<bool> IdeologyInstalled { get; init; }

    /// <summary>The bestowing-ceremony quests the title claim gate allows to accept now (ClaimQuests); the review fills it once the plan, rooms and royalty read are known.</summary>
    public EquatableArray<QuestId> TitleClaimQuests { get; init; }

    public Fact<EquatableArray<JoinerLetterOffer>> JoinerLetters { get; init; }

    public Fact<double> RaidPoints { get; init; }

    /// <summary>The void monolith's state; unknown without Anomaly.</summary>
    public Fact<MonolithFacts> Monolith { get; init; }

    /// <summary>The armory shell review: a built mortar's shell stock below half its target keeps the military/equipment Need unmet.</summary>
    public Fact<bool> ShellsShort { get; init; }

    /// <summary>The colonists' and powered turrets' observed combat strength in raid-point units (DefenseCapacity).</summary>
    public Fact<double> DefenseCapacity { get; init; }

    /// <summary>The exposed/eligible native item census (the same WasteReply the generic per-tick colony read already carries) that MorgueWaiting and RouteStranger read.</summary>
    public Fact<EquatableArray<WasteItem>> Waste { get; init; }

    /// <summary>The map trader census (bridge.ListTraders) the economy/caravan Need needs; a source without the read leaves it unknown and the goal off.</summary>
    public Fact<EquatableArray<TraderFacts>> Traders { get; init; }

    /// <summary>The food/blight Need's blighted-plant census (the colony read's plant things of the planning window), for BlightDeficit to detect and SelectBlightCuts to designate from.</summary>
    public Fact<EquatableArray<BlightedPlant>> Blight { get; init; }

    /// <summary>The biotech/pollution Need's wastepack verdicts and the polluted cells outside the clear area (the Biotech colony section); unknown without Biotech or when the read failed, and then the need is unknown.</summary>
    public Fact<PollutionFacts> Pollution { get; init; }

    /// <summary>Whether the colony owes one more mech charger (MechChargerNeed); unknown without Biotech, mechs or chargers read, and then the biotech/mech-charger Need is unknown.</summary>
    public Fact<bool> MechChargerOwed { get; init; }

    /// <summary>Whether more genepacks lie loose than the standing gene banks have room for (GeneBankNeed); unknown without Biotech or a complete gene-building read, and then the biotech/gene-bank Need is unknown.</summary>
    public Fact<bool> GeneBankOwed { get; init; }

    /// <summary>The stockpile review (the admin/stockpiles Need): this cycle's stockpile edits and the planned rooms owed a shell, or why none stands.</summary>
    public Fact<StockpileReview> Stockpiles { get; init; }

    /// <summary>Counts the owned stockpile zones by role kind for the colony status census; unknown until the zone claims are read.</summary>
    public Fact<EquatableArray<StockpileRoleCount>> StockpileZones { get; init; }

    /// <summary>The migrated families' Needs the runtime composes; supplied by the configured runtime, never native facts.</summary>
    public Fact<EquatableArray<NeedId>> AvailableNeeds { get; init; }

    public UpkeepObservation Upkeep { get; init; } = new();

    public Fact<GearObservation> Gear { get; init; }

    /// <summary>The allowed garments covering a core group with their recipes (ClothingRunway); empty until the gear census and catalog are read.</summary>
    public EquatableArray<ClothingGarment> Garments { get; init; }

    public Fact<ComfortObservation> Comfort { get; init; }

    /// <summary>
    /// The same census before the hosting-room filter: every indoor seat at an eating surface and every
    /// recreation source, whatever room (or none) hosts it. The rooms/comfort Need measures it; Comfort
    /// keeps only facilities in rooms whose native role the facility catalog hosts.
    /// </summary>
    public Fact<ComfortObservation> BasicComfort { get; init; }

    public Fact<EquatableArray<LootItem>> EventLoot { get; init; }

    public Fact<Bounds> MapBounds { get; init; }

    public Fact<EquatableArray<CarePawn>> MedicalPawns { get; init; }

    public Fact<bool> MedicalCareRecovered { get; init; }

    public Fact<int> Workers { get; init; }

    /// <summary>The per-work-type census of the same pawns Workers counts (RoundsLabor); unknown labor remains unavailable evidence.</summary>
    public Fact<IReadOnlyDictionary<WorkType, int>> Labor { get; init; }

    /// <summary>The planner's per-work-type coverage (PlanWork): the owners each type wanted and found and the pawns capable of it, so a goal can name a missing capability instead of stalling.</summary>
    public Fact<EquatableArray<WorkCoverage>> WorkRoster { get; init; }

    // Quest capacity is derived from the current roster, world census and emergency review.
    public Fact<EquatableArray<PawnId>> QuestSparePawns { get; init; }

    public Fact<EquatableArray<WorkPawn>> QuestWorkers { get; init; }

    public Fact<EquatableArray<QuestWorkCapacity>> QuestWorkCapacity { get; init; }

    public Fact<EquatableArray<QuestDeparturePawn>> QuestDeparturePawns { get; init; }

    public Fact<EquatableArray<PawnWorkAssignment>> QuestDepartureWork { get; init; }

    public Fact<EquatableArray<ExpeditionTrip>> QuestExpeditionTrips { get; init; }

    public Fact<Tick> QuestObservedTick { get; init; }

    public Fact<int> QuestColonistsAtHome { get; init; }

    /// <summary>The sole essential owners who stay home (QuestHomeFloor).</summary>
    public Fact<int> QuestHomeFloor { get; init; }

    public Fact<bool> QuestColonyCalm { get; init; }

    /// <summary>The same plan's skills above 10 that no assignment exercises (WorkDecision.Decaying); a presentation fact the review records, never a planner input.</summary>
    public Fact<EquatableArray<DecayingSkill>> WorkDecaying { get; init; }

    /// <summary>Every work pawn's typed profile (Profiles); a presentation fact the review records, never a planner input.</summary>
    public Fact<EquatableArray<PawnProfile>> WorkProfiles { get; init; }

    public Fact<long> Colonists { get; init; }

    public Fact<long> HousingTarget { get; init; }

    public Fact<long> BedCapacity { get; init; }

    public Fact<long> IndoorCapacity { get; init; }

    public Fact<long> GrowingCells { get; init; }

    public Fact<long> Armed { get; init; }

    /// <summary>Counts living, conscious colonists able to fight who hold no weapon; military/defense stays unmet while it is positive.</summary>
    public Fact<long> Unarmed { get; init; }

    public Fact<double> FoodDays { get; init; }

    public Fact<double> FieldCoverage { get; init; }

    /// <summary>The same plan's construction helper record; null when the plan ran without the helper input.</summary>
    public ConstructionHelpRecord? WorkHelp { get; init; }

    /// <summary>
    /// The tile's native growing calendar (policy.Calendar). InspectRounds widens the policy's food and wood
    /// targets by its harvest gap (RoundsPolicy.Seasonal) before measuring any latch; an unknown calendar
    /// keeps the configured flat targets.
    /// </summary>
    public Fact<Calendar> Calendar { get; init; }

    public Fact<double> SleepingMin { get; init; }

    public Fact<double> SleepingMax { get; init; }

    public Fact<double> OutdoorTemperature { get; init; }

    public Fact<double> PowerHeadroom { get; init; }

    /// <summary>Observed ColonyForecast diagnostics not carried by the facts above.</summary>
    public ColonyForecastObserved ForecastObserved { get; init; } = new();
    public ForecastNeedsReview? NeedsAlternatives { get; init; }
    public ForecastCoupledCandidates? CoupledAlternatives { get; init; }
    public EquatableArray<PlannedRoom> PlannedShelters { get; init; }
    /// <summary>Existing journal ownership, not forecast progress.</summary>
    public bool CoupledWorkActive { get; init; }
    public EssentialServiceInputs EssentialInputs { get; init; } = new();

    public Fact<long> Wood { get; init; }

    /// <summary>The refuelable buildings of the power census (FuelRunway).</summary>
    public Fact<EquatableArray<FuelConsumer>> Fuel { get; init; }

    /// <summary>The open costs of admitted methods, from the last review's live records: an open shortfall raises an economy/resource floor for the bounded difference.</summary>
    public EquatableArray<AdmittedCost> Admitted { get; init; }

    /// <summary>The player's unfinished gear bills with their recipe slots (OpenBillDemand): ingredient demand ResourceDemandOf counts.</summary>
    public EquatableArray<OpenBill> OpenBills { get; init; }

    /// <summary>The generic reachable, unforbidden player item census (the same colony facts rows Wood is taken from), so the economy/resource Need's deficit is measured at review time instead of assumed from config.</summary>
    public Fact<EquatableArray<Amount>> Resources { get; init; }

    public IReadOnlyDictionary<Resource, Fact<long>>? ResourceSurfaceOre { get; init; }

    /// <summary>The recurring spend over the rate window, from native's realized-consumption ring; unknown when the read failed.</summary>
    public Fact<ResourceConsumption> ResourceConsumption { get; init; }

    public EquatableArray<ResourceRunway> ResourceRunways { get; init; }

    /// <summary>The colonists whose drug policy permits each social drug (DrugUsers); it sets the drug runway's reserve before any dose is observed.</summary>
    public IReadOnlyDictionary<Resource, long>? DrugUsers { get; init; }

    /// <summary>The colony wealth split the economy/caravan Need's wealth-driven surplus keys on; unknown leaves that surplus out.</summary>
    public Fact<WealthFacts> Wealth { get; init; }

    /// <summary>The native research state read inside the same paused identity bracket as the other routine facts. Unknown when the source cannot read research; missing facts never meet the economy/research Need.</summary>
    public Fact<ResearchFacts> Research { get; init; }

    /// <summary>The sorted ResearchProjectDefs other goals' recorded evidence is waiting on (the workshop ladder's research rung); the first unfinished one is the economy/research Need.s target.</summary>
    public EquatableArray<string> ResearchNeeds { get; init; }

    /// <summary>
    /// The derived stock floors other goals' recorded evidence asks for (the defensive layout's turret fuel
    /// the census found no stock of); ResourceGoalTargets merges them into the operator's the economy/resource Need
    /// targets, never lowering a configured floor.
    /// </summary>
    public IReadOnlyDictionary<Resource, long> ResourceNeeds { get; init; } = new Dictionary<Resource, long>();

    public Fact<long> Hostiles { get; init; }

    public Fact<long> CriticalPatients { get; init; }

    /// <summary>
    /// Counts the critical patients who are bleeding or downed with a tend outstanding
    /// (policy.UrgentPatients). The remote hold and the resource harvest read it; the medical family's critical
    /// Needs count every critical patient (#3258).
    /// </summary>
    public Fact<long> UrgentPatients { get; init; }

    public Fact<bool> AllPatientsResting { get; init; }

    public Fact<bool> CleanupPawns { get; init; }

    /// <summary>A colonist whose hostility response differs from the one it should hold; the admin/work-assignments Need writes it.</summary>
    public Fact<bool> HostilityOwed { get; init; }

    /// <summary>A pawn whose medical care differs from its cap.</summary>
    public Fact<bool> MedicalCareOwed { get; init; }

    /// <summary>The colony's guests' cap inputs.</summary>
    public Fact<EquatableArray<CarePatient>> Guests { get; init; }

    /// <summary>A colonist whose medicine carry count differs from the planned one; the admin/work-assignments Need writes it.</summary>
    public Fact<bool> MedicineCarryOwed { get; init; }

    /// <summary>An owned pawn whose short name an older owned pawn holds; the admin/work-assignments Need renames it.</summary>
    public Fact<bool> NamesOwed { get; init; }

    /// <summary>The standing butcher benches and the room each stands in; the food/supply Need's butcher facility is met once a table stands outside the kitchen.</summary>
    public Fact<EquatableArray<ButcherBench>> ButcherBenches { get; init; }

    public Fact<bool> Cooking { get; init; }

    public Fact<bool> WorkCoverage { get; init; }

    public Fact<bool> PowerRequired { get; init; }

    public Fact<bool> DisabledConsumers { get; init; }

    public Fact<bool> PowerWeatherSafe { get; init; }

    public Fact<Tick> ShortCircuitTick { get; init; }
}

public sealed record RoundsLatches
{
    public bool MedicalReserve { get; set; }

    /// <summary>The planned outdoor dash window (#2765), held until its bound; zero when none is open.</summary>
    public DashWindow Dash { get; set; } = new();

    /// <summary>
    /// <see cref="Phase.HousingShelter"/> while this review finds the starter shelter not standing (a sleeping or an
    /// indoor place short for everyone, or unread), else empty: the starter shelter then holds the colony's
    /// Construction labor (<see cref="PolicyFunctions.ConstructionLaborHeld"/>). A per-review value, not a latch.
    /// </summary>
    public Phase Housing { get; set; }

}

/// <summary>One review's findings: the latches, the disaster history and the derived facts the families read.</summary>
public sealed record RoundsFindings
{
    [System.Text.Json.Serialization.JsonIgnore]
    public ForecastCoupledSelection? CoupledSelection { get; set; }
    public DisasterHistory? Disaster { get; set; }

    public RoundsLatches Latches { get; set; } = new();

    /// <summary>The review's one resource-demand value (Needs are the effective the economy/resource Need stock targets the review held the census to; the stock overlay tints stockpiles by them).</summary>
    public DerivedDemand ResourceDemand { get; set; } = new();

    /// <summary>The review's trade facts (the silver gap, the caravans' standing) the economy family's trade Needs read; derived, not saved.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public TradeReview Trade { get; set; } = new();

    /// <summary>Whether the review owes work assignments, settings or coupled activities (WorkAssignmentsOwed), which the admin/work-assignments Need reads; derived, not saved.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Fact<bool> WorkAssignments { get; set; }
    public EssentialServiceReview? EssentialServices { get; set; }
}

public static partial class PolicyFunctions
{
    /// <summary>Supplies the routine inspection targets and budgets. Individual planners bound their orders; vanilla priorities schedule pawn work.</summary>
    public static RoundsPolicy DefaultRoundsPolicy() => new()
    {
        MedicalReserve = MedicalReservePolicy.Default(),
        FoodStorage = DefaultFoodStoragePolicy(),
        Lighting = LightingPolicy.Default(),
        Flooring = DefaultFlooringPolicy(),
        Routes = DefaultRoutesPolicy(),
        FoodMinDays = 3,
        FoodTargetDays = 7,
        FootholdFoodDays = 3,
        FoodReserveDays = DefaultFoodReserveDays,
        PrisonerReleaseAfterDays = 15,
        ColdEnter = 12,
        ColdExit = 16,
        HotExit = 28,
        HotEnter = 32,
        HuntStallTicks = GameTime.TicksPerDay / 2,
        AcquisitionStallTicks = GameTime.TicksPerDay,
        ConcernStallTicks = GameTime.TicksPerDay,
    };

    // The foothold facts below are read live from one review's facts: the foothold goals open on them
    // (InspectRounds), and the disaster services (DisasterServiceFacts)
    // and the food ladder (FoodProgress) read the ones they need.

    /// <summary>The colonist count the foothold sizes for: the housing target when it is larger.</summary>
    internal static Fact<long> FootholdCount(RoundsFacts f)
    {
        var count = f.Colonists;
        if (f.HousingTarget.TryGetValue(out var target))
        {
            if (count.TryGetValue(out var n))
            {
                count = Known(Math.Max(n, target));
            }
        }
        return count;
    }

    internal static Fact<bool> FootholdSleeping(RoundsFacts f) => CountCapacity(f.BedCapacity, FootholdCount(f), 1);

    internal static Fact<bool> FootholdShelter(RoundsFacts f) => CountCapacity(f.IndoorCapacity, FootholdCount(f), 1);

    internal static Fact<bool> FootholdProduction(RoundsFacts f) => CountCapacity(f.GrowingCells, FootholdCount(f), 10);

    internal static Fact<bool> FootholdFood(RoundsFacts f, RoundsPolicy p) => Measured(f.FoodDays, v => v >= p.FootholdFoodDays);

    internal static Fact<bool> FootholdTemperature(RoundsFacts f, RoundsPolicy p) =>
        AllFacts(Measured(f.SleepingMin, v => v >= p.ColdEnter), Measured(f.SleepingMax, v => v <= p.HotEnter));

    internal static Fact<bool> FootholdPower(RoundsFacts f)
    {
        if (f.PowerWeatherSafe.TryGetValue(out var safe) && !safe)
        {
            return Known(false);
        }
        var power = Measured(f.PowerHeadroom, v => v >= 0);
        if (f.PowerRequired.TryGetValue(out var required))
        {
            if (!required)
            {
                power = Known(true);
            }
        }
        else
        {
            power = Unknown<bool>();
        }
        return AllFacts(power, Measured(f.DisabledConsumers, v => !v));
    }

    /// <summary>The armed fighters a colony of n colonists owes: two, every colonist when fewer, never below one. The only place the number lives.</summary>
    internal static long ArmedTarget(long n) => Math.Max(1, Math.Min(2, n));

    /// <summary>The survival services ReviewDisaster tracks, read live from the review's facts (infrastructure is its own).</summary>
    public static Dictionary<DisasterService, Fact<bool>> DisasterServiceFacts(RoundsFacts f, RoundsPolicy p) => new()
    {
        [DisasterService.Food] = FootholdFood(f, p),
        [DisasterService.Production] = FootholdProduction(f),
        [DisasterService.Sleeping] = FootholdSleeping(f),
        [DisasterService.Shelter] = FootholdShelter(f),
        [DisasterService.Temperature] = FootholdTemperature(f, p),
        [DisasterService.Cooking] = f.Cooking,
        [DisasterService.Power] = FootholdPower(f),
        [DisasterService.Storage] = FoodStorageStanding(f),
    };

    /// <summary>
    /// The combat situation (#3311): unclear while the hostile count is unknown; active while a hostile stands or
    /// the food plan opens a squad hunt (<paramref name="prey"/> is its prey count); clear otherwise. The review's
    /// combat standing and the <c>military/combat</c> Need both read it, so they cannot disagree.
    /// </summary>
    public static Situation CombatSituation(Fact<long> hostiles, int prey)
    {
        if (!hostiles.TryGetValue(out var n))
        {
            return Situation.Unclear;
        }
        return n > 0 || prey > 0 ? Situation.Active : Situation.Clear;
    }

    /// <summary>Whether combat has nothing to answer: <see cref="CombatSituation"/> is clear.</summary>
    internal static Fact<bool> CombatCleared(RoundsFacts f) =>
        CombatSituation(f.Hostiles, HuntRequest(f.FoodPlan).Count) switch
        {
            Situation.Clear => Known(true),
            Situation.Active => Known(false),
            _ => Unknown<bool>(),
        };

    internal static bool Positive(Fact<bool> v) => v.TryGetValue(out var b) && b;

    internal static bool Negative(Fact<bool> v) => v.TryGetValue(out var b) && !b;

    internal static Fact<bool> Measured<T>(Fact<T> f, Func<T, bool> predicate)
    {
        if (!f.TryGetValue(out var v))
        {
            return Unknown<bool>();
        }
        return Known(predicate(v));
    }

    internal static Fact<bool> AllFacts(params Fact<bool>[] facts)
    {
        var unknown = false;
        foreach (var f in facts)
        {
            var k = f.TryGetValue(out var v);
            if (k && !v)
            {
                return Known(false);
            }
            unknown = unknown || !k;
        }
        if (unknown)
        {
            return Unknown<bool>();
        }
        return Known(true);
    }

    /// <summary>f when measured, else other: an unmeasured sleeping-room temperature reads as the outdoor temperature (sleeping outdoors).</summary>
    internal static Fact<T> Fallback<T>(Fact<T> f, Fact<T> other) => f.TryGetValue(out _) ? f : other;

    internal static Fact<bool> CountCapacity(Fact<long> capacity, Fact<long> count, long multiplier)
    {
        var k = count.TryGetValue(out var c);
        var vk = capacity.TryGetValue(out var v);
        if (!k || !vk)
        {
            return Unknown<bool>();
        }
        return Known(c > 0 && v >= c * multiplier);
    }

    /// <summary>
    /// The full review: it validates the facts and derives once what the families read (the latches, the resource
    /// demand, the coupled selection, the disaster history, the trade review, the work-assignments measure and the
    /// essential services). It judges no Need. Throws DomainException on invalid facts.
    /// </summary>
    public static RoundsFindings InspectRounds(RoundsFacts f, RoundsLatches previous, RoundsPolicy p)
    {
        if (f.Calendar.TryGetValue(out var calendar) && !calendar.Valid())
        {
            throw new DomainException("invalid calendar fact");
        }
        p = p.Seasonal(f.Calendar, f.DisasterConditions);
        var medicine = ReviewMedicalReserve(f.MedicalReserve with { Colonists = f.Colonists }, previous.MedicalReserve, p.MedicalReserve);
        p.Validate();
        foreach (var fact in new[] { f.Colonists, f.HousingTarget, f.BedCapacity, f.IndoorCapacity, f.GrowingCells, f.Armed, f.Unarmed, f.Wood, f.Hostiles, f.CriticalPatients, f.UrgentPatients })
        {
            if (fact.TryGetValue(out var v) && (v < 0 || v > long.MaxValue / 10))
            {
                throw new DomainException("invalid routine count");
            }
        }
        foreach (var fact in new[] { f.FoodDays, f.FieldCoverage, f.SleepingMin, f.SleepingMax, f.OutdoorTemperature, f.PowerHeadroom })
        {
            if (fact.TryGetValue(out var v) && (double.IsNaN(v) || double.IsInfinity(v)))
            {
                throw new DomainException("nonfinite routine fact");
            }
        }
        foreach (var fact in new[] { f.FoodDays, f.FieldCoverage })
        {
            if (fact.TryGetValue(out var v) && v < 0)
            {
                throw new DomainException("negative food fact");
            }
        }
        var latches = new RoundsLatches
        {
            MedicalReserve = medicine.Active,
            Dash = DashLatch(f.Dash),
            Housing = StarterShelterOwed(f) ? Phase.HousingShelter : Phase.None,
        };
        var demand = ResourceDemandOf(f, p, latches);
        var r = new RoundsFindings { Latches = latches, ResourceDemand = demand };
        if (f.CoupledAlternatives is { } coupled)
            r.CoupledSelection = CompareActivityOutcomes(CoupledForecastRequestOf(new("observed-review", f.Tick.Value),
                1, f, p, demand, [], new()), coupled);
        // The disaster history the disaster family's Needs read (#3304).
        r.Disaster = ReviewDisaster(f.DisasterConditions, f.RecoveryBuildings, DisasterServiceFacts(f, p), f.Disaster, f.DisasterTick, f.ShortCircuitTick);
        r.Trade = TradeReviewOf(f, p, medicine, demand);
        r.WorkAssignments = WorkAssignmentsOwed(f, r.CoupledSelection);
        r.EssentialServices = ReviewEssentialServices(f, p, demand, medicineActive: latches.MedicalReserve);
        return r;
    }

    /// <summary>
    /// The food/supply Need's butcher-facility check. A bench must stand apart from the kitchen: a butcher
    /// bench inside it keeps the colony fed but not clean. The butcher spot stands in the planned butchery
    /// until the table is built; while the stock cannot pay for the table the spot alone is enough, and a table standing beside
    /// its spot owes the spot's removal. It is wanted whatever the food runway, because hunts and hides are
    /// processed on it.
    /// </summary>
    internal static Fact<bool> ButcherFacilityMet(RoundsFacts f)
    {
        if (!f.ButcherBenches.TryGetValue(out var benches))
        {
            return Unknown<bool>();
        }
        if (benches.Count == 0)
        {
            return Known(false);
        }
        if (!KitchenSeparation(f.Upkeep.Rooms).TryGetValue(out var shared))
        {
            return Known(true);
        }
        var colocated = new HashSet<string>(StringComparer.Ordinal);
        foreach (var room in shared)
        {
            colocated.Add(room.Id);
        }
        bool apart = false, table = false, spot = false;
        foreach (var bench in benches)
        {
            if (!bench.Room.TryGetValue(out var room) || !colocated.Contains(room))
            {
                apart = true;
                table = table || bench.Definition != ButcherSpotDefinition;
                spot = spot || bench.Definition == ButcherSpotDefinition;
            }
        }
        if (!apart)
        {
            return Known(false);
        }
        if (table)
        {
            return Known(!spot);
        }
        // Only the spot stands apart: with the stock to build the table, the goal stays open for it.
        return Known(!(f.ButcherTableAffordable.TryGetValue(out var affordable) && affordable));
    }

    /// <summary>The free, instant bench that hosts the club and short bow recipes; the military/equipment Need stands one in the planned workshop until a real weapon bench is built.</summary>
    public const string CraftingSpotDefinition = "CraftingSpot";

    /// <summary>The free, instant butcher bench that stands in the planned butchery until the table is built.</summary>
    public const string ButcherSpotDefinition = "ButcherSpot";
}

/// <summary>A standing butcher bench and the room it stands in (unknown outdoors).</summary>
public sealed record ButcherBench
{
    public string Id { get; init; } = "";

    public string Definition { get; init; } = "";

    public Fact<string> Room { get; init; }
}

public static partial class PolicyConstants
{
    /// <summary>The crafting spot def, as the constants class names it for the building runtime (the value lives with the rounds that stand it).</summary>
    public const string CraftingSpotDefinition = PolicyFunctions.CraftingSpotDefinition;
}
