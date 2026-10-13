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

    public long WoodMin { get; init; }

    public long WoodTarget { get; init; }

    public long WoodMax { get; init; }

    /// <summary>The growth fraction a plantation tree must reach before chop selection offers it; 0 offers every harvestable one. Wild trees ignore it.</summary>
    public double ChopMinGrowth { get; init; }

    /// <summary>
    /// Bounds how long a dispatched Hunt-kind acquisition action may sit unresolved before
    /// RoundsAcquisitionPlanner abandons it and lets a fresh SelectAcquisition pass propose something else.
    /// Native's own HuntingSafety.RouteSafe guard can repeatedly interrupt the shared game clock while a
    /// hunter's route stays unsafe; that guard stays fully authoritative, but without this grace the stuck
    /// action reads as open work forever and blocks the planner from ever trying a different prey or a
    /// non-hunt source. It counts from the last observation, and a live hunt (travel, chase, the hunter's
    /// sleep and meals, a clock window between observations) outlasts 6000 ticks: that bound withdrew
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
    /// use it as their deadline. StageRoundsPolicy scales it down during Foothold (StageGoalStallScale) so a
    /// stuck method rotates in a fraction of a day rather than the full day this defaults to, while
    /// starvation risk is highest; it returns to this value once the colony reaches Reserves.
    /// </summary>
    public long ConcernStallTicks { get; init; }

    /// <summary>
    /// The ordered roadmap EnsureResearch walks when the workshop ladder records no need
    /// (DefaultResearchLadder by default; empty disables the roadmap). Each rung is reached through
    /// ResearchPrerequisiteQueue like a target, a current native project is respected and recovers the goal,
    /// and the planner lends the clock ticks while one is current so the rung finishes on its own.
    /// </summary>
    public EquatableArray<string> ResearchLadder { get; init; }

    /// <summary>
    /// How long MaintainPopulation feeds a prisoner it has no use for (not worth recruiting, not enslavable)
    /// while the food runway holds FoodTargetDays before releasing it (default 15 days); below the target it
    /// releases at once. See PrisonerPolicy.
    /// </summary>
    public double PrisonerReleaseAfterDays { get; init; }

    /// <summary>
    /// An operator-declared opt-in for EnsureDefensiveLayout: the staged chokepoint/firing-line/funnel/
    /// trap-corridor construction RoundsDefenseLayoutPlanner proposes from a fresh native defense-site
    /// census. It is config-only: the review does not derive layout completeness from a census, the planner
    /// decides per tier from its own admitted plans.
    /// </summary>
    public bool DefensiveLayout { get; init; }

    /// <summary>Holds the colony stage thresholds; zero fields take the defaults RoundsPolicy.Stages derives from the food thresholds.</summary>
    public ColonyStagePolicy Stage { get; init; } = new();

    /// <summary>
    /// The stage the last review left (StageRoundsPolicy): InspectRounds raises only the goals the stage
    /// allows (StageGoalAllowed). DefaultRoundsPolicy stands at Development, so a policy nobody staged raises
    /// every goal.
    /// </summary>
    public ColonyStage ColonyStage { get; init; }

    /// <summary>Is the MaintainPopulation slice of this policy.</summary>
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
            || ColdEnter >= ColdExit || ColdExit >= HotExit || HotExit >= HotEnter
            || WoodMin < 0 || WoodTarget <= WoodMin || WoodMax < WoodTarget)
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
        this.Stages().Validate();
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
        foreach (var rung in ResearchLadder)
        {
            if (!PolicyFunctions.ValidResource(new Resource(rung)))
            {
                throw new DomainException("invalid research ladder rung");
            }
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
    /// <summary>The colonists' personal wealth shares the elective surgery gate reads; null is ungated, a live reading always sets it (an empty map gates every colonist).</summary>
    public IReadOnlyDictionary<PawnId, PersonalShare>? PersonalShares { get; init; }

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

    /// <summary>The built, powered rooms the layout plan lights with a planned lamp unbuilt (PlanLitRooms); they keep MaintainLighting open beside the dark benches.</summary>
    public Fact<EquatableArray<int>> PlanLit { get; init; }

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

    /// <summary>Live bill jobs' promised ingredients, per working pawn (MaterialBudget).</summary>
    public Fact<EquatableArray<IngredientReservation>> BillReservations { get; init; }

    public Fact<SleepingObservation> Sleeping { get; init; }

    public Fact<bool> SleepingRecovered { get; init; }

    /// <summary>A planned individual bedroom step is due; it keeps MaintainHousing open once everyone owns a shelter bed.</summary>
    public Fact<bool> BedroomsOwed { get; init; }

    /// <summary>A tomb, grave or morgue step is due; it keeps MaintainBurial open while a corpse waits on one.</summary>
    public Fact<bool> BurialOwed { get; init; }

    /// <summary>The waste yard or incinerator shell, a due burn or ash to clean waits; it keeps MaintainIncineration open.</summary>
    public Fact<bool> IncinerationOwed { get; init; }

    /// <summary>The warm standing tombs, morgues and meal closets (WarmCoolingRooms); they join MaintainRefrigeration's rooms.</summary>
    public Fact<EquatableArray<string>> WarmRooms { get; init; }

    /// <summary>The planned meal closet waits to be shelled while its dining room stands; it keeps MaintainRefrigeration open.</summary>
    public Fact<bool> MealClosetOwed { get; init; }

    /// <summary>
    /// The colony owes its PartySpot a placement or a deconstruction (ReviewPartySpot); it holds
    /// EnsureComfort Unmet for the pass or two the step takes. Unknown leaves comfort as it was.
    /// </summary>
    public Fact<bool> PartySpotOwed { get; init; }

    /// <summary>A stove kitchen supersedes a cooking campfire; it keeps EnsureCooking open.</summary>
    public Fact<bool> CampfireRetireOwed { get; init; }

    /// <summary>A weapon bench stands beside the crafting spot that stood in for it; it keeps MaintainEquipment open for the deconstruction.</summary>
    public Fact<bool> CraftingSpotRetireOwed { get; init; }

    /// <summary>A heat campfire's auto-refuel should switch, or a sleeping room sits unheated below its sleepers' comfort minimum (TemperatureOwed); it holds EnsureTemperatureSafety open.</summary>
    public Fact<bool> TemperatureOwed { get; init; }

    /// <summary>A bedroom below target, weakest in beauty, has a free cell for a sculpture (SculptureRoomsOwed); with a qualifying artist it holds MaintainArt open.</summary>
    public Fact<bool> SculptureRoomsOwed { get; init; }

    /// <summary>The work ledger's reconcile diff is non-empty (LedgerDiffOwed); it holds MaintainWorkLedger open. Unknown unless the ledger has declarers and read every bench's bills.</summary>
    public Fact<bool> LedgerOwed { get; init; }

    /// <summary>The dispatcher's unmet throughput per bench kind (units a day short and why), sorted by kind; the facilities ladder reads it. Empty when nothing is short or the ledger has no declarers.</summary>
    public EquatableArray<UnmetThroughput> UnmetThroughput { get; init; }

    /// <summary>The Safe allowed area differs from the enclosed roofed rooms (PlanSafeArea); it holds MaintainShelter open. Unknown unless the MaintainShelter method is composed.</summary>
    public Fact<bool> SafeAreaOwed { get; init; }

    /// <summary>The firebreak ring has a cut cell with a standing plant or an undesignated wooden ruin (FirebreakOwed); it holds MaintainFirebreak open. Unknown unless the method is composed.</summary>
    public Fact<bool> FirebreakOwed { get; init; }

    /// <summary>A planned room is owed a vent or a roof, or the roofed hallway is open to the sky (#2962); it holds MaintainClimateEnvelope open. Unknown unless the method is composed.</summary>
    public Fact<bool> EnvelopeOwed { get; init; }

    /// <summary>A mechanitor can afford the next mech, a gestator is idle and no waste is uncleared (MechGestationOwed); it holds MaintainMechs open. Unknown unless the method is composed.</summary>
    public Fact<bool> MechGestationOwed { get; init; }

    /// <summary>A barn or vet room step is due (NextHerdStep); it holds MaintainAnimalContainment open after the pen stands.</summary>
    public Fact<bool> HerdRoomsOwed { get; init; }

    /// <summary>A willing colonist has no psylink and a psylink neuroformer is held (PsylinkOwed); it holds MaintainPsylink open. Unknown unless the method is composed.</summary>
    public Fact<bool> PsylinkOwed { get; init; }

    /// <summary>A creepjoiner whose downside has not shown holds a weapon (CreepJoinerDownsides.WeaponDrops); it holds ManageCreepJoiners open. Unknown unless the method is composed.</summary>
    public Fact<bool> CreepJoinerOwed { get; init; }

    /// <summary>An active ideoligion role has a free place and a fitting believer (RolesOwed); it holds MaintainIdeoRoles open.</summary>
    public Fact<bool> RolesOwed { get; init; }

    /// <summary>The finished buildings the held rituals' patterns require (observation, from the frame's building census).</summary>
    public Fact<EquatableArray<RitualSite>> RitualSites { get; init; }

    /// <summary>The rituals to begin now (PlanRituals), whose attendees the schedule planners hold off Sleep (HeldOffSleep).</summary>
    public Fact<EquatableArray<RitualPlan>> RitualPlans { get; init; }

    /// <summary>Holds MaintainRituals open. Unknown unless the method is composed.</summary>
    public Fact<bool> RitualsOwed { get; init; }

    /// <summary>The party to start now (PlanGathering).</summary>
    public Fact<GatheringPlan> GatheringPlan { get; init; }

    /// <summary>Holds HoldGatherings open. Unknown unless the method is composed.</summary>
    public Fact<bool> GatheringOwed { get; init; }

    public Fact<bool> ReformOwed { get; init; }

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

    /// <summary>The Isolation allowed area's native load id, "" when the map has none (ManageCreepJoiners).</summary>
    public Fact<string> IsolationArea { get; init; }

    /// <summary>Counts the packed art no owed room reserves (SaleSculptures); read only while the wealth headroom is negative, it opens a trade as the shed_art need.</summary>
    public Fact<long> SaleArt { get; init; }

    /// <summary>The generic goods MaintainTrade's last declaration could rank (ExportSaleProducts), held stacks of which sell as surplus while the silver gap is open (ExportSaleSurplus). Set from the Rounder's memory; empty before the first declaration of a world.</summary>
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
    /// RoundsPopulationCustodyPlanner can detect and select a downed hostile or unadmitted guest to dispatch.
    /// </summary>
    public Fact<EquatableArray<CustodyFacts>> Custody { get; init; }

    /// <summary>The containment cell's inputs and the entity rows the capture rule decides, set by the routine reading.</summary>
    public ContainmentPlanning Containment { get; init; } = new();

    /// <summary>The same population read's storyteller outlook.</summary>
    public PopulationOutlook Outlook { get; init; } = new();

    /// <summary>The same read's owned-pawn short-name census.</summary>
    public Fact<EquatableArray<OwnedName>> OwnedNames { get; init; }

    /// <summary>
    /// Carries MaintainPopulation's joiner census: every visible quest row
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

    /// <summary>The armory shell review: a built mortar's shell stock below half its target puts MaintainEquipment in deficit.</summary>
    public Fact<bool> ShellsShort { get; init; }

    /// <summary>The colonists' and powered turrets' observed combat strength in raid-point units (DefenseCapacity).</summary>
    public Fact<double> DefenseCapacity { get; init; }

    /// <summary>The exposed/eligible native item census (the same WasteReply the generic per-tick colony read already carries) that MorgueWaiting and RouteStranger read.</summary>
    public Fact<EquatableArray<WasteItem>> Waste { get; init; }

    /// <summary>The map trader census (bridge.ListTraders) TradeWithCaravan needs; a source without the read leaves it unknown and the goal off.</summary>
    public Fact<EquatableArray<TraderFacts>> Traders { get; init; }

    /// <summary>RemoveBlight's blighted-plant census (the colony read's plant things of the planning window), for BlightDeficit to detect and SelectBlightCuts to designate from.</summary>
    public Fact<EquatableArray<BlightedPlant>> Blight { get; init; }

    /// <summary>ManagePollution's wastepack verdicts and the polluted cells outside the clear area (the Biotech colony section); unknown without Biotech or when the read failed, and then the need is unknown.</summary>
    public Fact<PollutionFacts> Pollution { get; init; }

    /// <summary>Whether the colony owes one more mech charger (MechChargerNeed); unknown without Biotech, mechs or chargers read, and then the EnsureMechCharger need is unknown.</summary>
    public Fact<bool> MechChargerOwed { get; init; }

    /// <summary>Whether more genepacks lie loose than the standing gene banks have room for (GeneBankNeed); unknown without Biotech or a complete gene-building read, and then MaintainGeneBank has no assessment.</summary>
    public Fact<bool> GeneBankOwed { get; init; }

    /// <summary>The MaintainStockpiles review: this cycle's stockpile edits and the planned rooms owed a shell, or why none stands.</summary>
    public Fact<StockpileReview> Stockpiles { get; init; }

    /// <summary>Counts the owned stockpile zones by role kind for the colony status census; unknown until the zone claims are read.</summary>
    public Fact<EquatableArray<StockpileRoleCount>> StockpileZones { get; init; }

    /// <summary>Supplied by the configured runtime, never native facts.</summary>
    public Fact<EquatableArray<ConcernId>> AvailableMethods { get; init; }

    public UpkeepObservation Upkeep { get; init; } = new();

    /// <summary>Each Upkeep.Shrines row's breach judgement as the reviewer read it, journalled beside the review; empty while the census is unknown.</summary>
    public EquatableArray<ShrineHold> ShrineHolds { get; init; }

    public IReadOnlyDictionary<ConcernId, bool>? UpkeepIssued { get; init; }

    public Fact<GearObservation> Gear { get; init; }

    /// <summary>The allowed garments covering a core group with their recipes (ClothingRunway); empty until the gear census and catalog are read.</summary>
    public EquatableArray<ClothingGarment> Garments { get; init; }

    public Fact<ComfortObservation> Comfort { get; init; }

    /// <summary>
    /// The same census before the hosting-room filter: every indoor seat at an eating surface and every
    /// recreation source, whatever room (or none) hosts it. EnsureComfort's basic phase measures it; Comfort
    /// keeps only facilities in rooms whose native role the facility catalog hosts.
    /// </summary>
    public Fact<ComfortObservation> BasicComfort { get; init; }

    public Fact<bool> ComfortRecovered { get; init; }

    public Fact<double> ComfortDeficit { get; init; }

    public Fact<EquatableArray<LootItem>> EventLoot { get; init; }

    public Fact<bool> EventLootPending { get; init; }

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

    /// <summary>Counts living, conscious colonists able to fight who hold no weapon; EnsureBasicDefense stays owed while it is positive.</summary>
    public Fact<long> Unarmed { get; init; }

    public Fact<double> FoodDays { get; init; }

    public Fact<double> FieldCoverage { get; init; }

    /// <summary>Whether the observed planning window holds no soil (NoSoil); with a short FoodDays it latches RoundsLatches.FoodFirst.</summary>
    public Fact<bool> NoSoil { get; init; }

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

    /// <summary>The shadow projector inputs the facts above lack.</summary>
    public ForwardObserved Forward { get; init; } = new();

    public Fact<long> Wood { get; init; }

    /// <summary>The refuelable buildings of the power census (FuelRunway).</summary>
    public Fact<EquatableArray<FuelConsumer>> Fuel { get; init; }

    /// <summary>The open costs of admitted methods, from the last review's live records: an open shortfall raises a MaintainResource floor for the bounded difference.</summary>
    public EquatableArray<AdmittedCost> Admitted { get; init; }

    /// <summary>The player's unfinished gear bills with their recipe slots (OpenBillDemand): ingredient demand ResourceDemandOf counts.</summary>
    public EquatableArray<OpenBill> OpenBills { get; init; }

    /// <summary>The generic reachable, unforbidden player item census (the same colony facts rows Wood is taken from), so MaintainResource's deficit is measured at review time instead of assumed from config.</summary>
    public Fact<EquatableArray<Amount>> Resources { get; init; }

    public IReadOnlyDictionary<Resource, Fact<long>>? ResourceSurfaceOre { get; init; }

    /// <summary>The recurring spend over the rate window, from native's realized-consumption ring; unknown when the read failed.</summary>
    public Fact<ResourceConsumption> ResourceConsumption { get; init; }

    public EquatableArray<ResourceRunway> ResourceRunways { get; init; }

    /// <summary>The colonists whose drug policy permits each social drug (DrugUsers); it sets the drug runway's reserve before any dose is observed.</summary>
    public IReadOnlyDictionary<Resource, long>? DrugUsers { get; init; }

    /// <summary>The colony wealth split TradeWithCaravan's wealth-driven surplus keys on; unknown leaves that surplus out.</summary>
    public Fact<WealthFacts> Wealth { get; init; }

    /// <summary>The native research state read inside the same paused identity bracket as the other routine facts. Unknown when the source cannot read research; missing facts never recover EnsureResearch.</summary>
    public Fact<ResearchFacts> Research { get; init; }

    /// <summary>The sorted ResearchProjectDefs other goals' recorded evidence is waiting on (the workshop ladder's research rung); the first unfinished one is EnsureResearch's target when none is configured.</summary>
    public EquatableArray<string> ResearchNeeds { get; init; }

    /// <summary>
    /// The derived stock floors other goals' recorded evidence asks for (the defensive layout's turret fuel
    /// the census found no stock of); ResourceGoalTargets merges them into the operator's MaintainResource
    /// targets, never lowering a configured floor.
    /// </summary>
    public IReadOnlyDictionary<Resource, long>? ResourceNeeds { get; init; }

    /// <summary>Journal evidence for EnsureDefensiveLayout: known true while the stored layout was verified complete and every tier still stood at the last census, so a standing layout no longer outranks the upkeep goals (repairs, power) that keep it working.</summary>
    public Fact<bool> DefensiveLayoutStanding { get; init; }

    public Fact<long> Hostiles { get; init; }

    public Fact<long> CriticalPatients { get; init; }

    /// <summary>
    /// Counts the critical patients who are bleeding or downed with a tend outstanding
    /// (policy.UrgentPatients). CriticalMedicine is an emergency only while one exists or the count is
    /// unknown; a colonist who merely needs tending, or is downed with nothing to tend, keeps the goal active
    /// at priority 2 so the colony's other work and the clock go on around the tend or rescue.
    /// </summary>
    public Fact<long> UrgentPatients { get; init; }

    public Fact<bool> AllPatientsResting { get; init; }

    public Fact<bool> CleanupPawns { get; init; }

    /// <summary>A colonist whose hostility response differs from the one it should hold; EnsureWorkAssignments writes it.</summary>
    public Fact<bool> HostilityOwed { get; init; }

    /// <summary>A pawn whose medical care differs from its cap.</summary>
    public Fact<bool> MedicalCareOwed { get; init; }

    /// <summary>The colony's guests' cap inputs.</summary>
    public Fact<EquatableArray<CarePatient>> Guests { get; init; }

    /// <summary>A colonist whose medicine carry count differs from the planned one; EnsureWorkAssignments writes it.</summary>
    public Fact<bool> MedicineCarryOwed { get; init; }

    /// <summary>A colonist whose eat trigger differs from the one the food runway calls for; EnsureWorkAssignments writes it.</summary>
    public Fact<bool> EatTriggerOwed { get; init; }

    /// <summary>A colonist whose self-tend setting differs from the one it should hold; EnsureWorkAssignments writes it.</summary>
    public Fact<bool> SelfTendOwed { get; init; }

    /// <summary>An owned pawn whose short name an older owned pawn holds; EnsureWorkAssignments renames it.</summary>
    public Fact<bool> NamesOwed { get; init; }

    /// <summary>True while the game is force-paused by a choice dialog it opened by itself; AnswerDialog is the goal that answers it.</summary>
    public Fact<bool> ChoiceDialog { get; init; }

    /// <summary>The standing butcher benches and the room each stands in; EnsureFoodSupply's butcher facility is met once a table stands outside the kitchen.</summary>
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
    public bool HomeCoverage { get; set; }

    public bool StoneShell { get; set; }

    public bool Sleeping { get; set; }

    public AnimalUpkeepHistory Animals { get; set; } = new();

    public bool MedicalReserve { get; set; }

    public bool FoodStorage { get; set; }

    public bool Refrigeration { get; set; }

    /// <summary>
    /// The review tick the Refrigeration latch last engaged, kept while it holds and zero when it is
    /// released: the refrigeration planner lends native cooling time from it when the goal's epoch has no
    /// cooler method of its own.
    /// </summary>
    public Tick RefrigerationSince { get; set; }

    /// <summary>Holds the bench IDs MaintainLighting last measured dark.</summary>
    public EquatableArray<string> Lighting { get; set; }

    /// <summary>Holds the room keys MaintainFlooring last measured short.</summary>
    public EquatableArray<string> Flooring { get; set; }

    /// <summary>Holds the facility IDs MaintainRoutes last measured unreachable.</summary>
    public EquatableArray<string> Routes { get; set; }

    public bool Food { get; set; }

    public bool Cold { get; set; }

    public bool Hot { get; set; }

    public bool Wood { get; set; }

    public UpkeepHistory Upkeep { get; set; } = new();

    /// <summary>The planned outdoor dash window (#2765), held until its bound; zero when none is open.</summary>
    public DashWindow Dash { get; set; } = new();

    /// <summary>Latches once the gear census derives a soldier role: the research roadmap then walks the armor ladder (ArmorResearchLadder) and keeps walking it when the squad is later undrafted.</summary>
    public bool Soldiers { get; set; }

    /// <summary>Latches once a map with no soil runs under FoodFirstRunwayDays of stored food: the research roadmap then leads with the food chain (FoodFirstLadder).</summary>
    public bool FoodFirst { get; set; }

    /// <summary>The MaintainHousing phase this review left owed (reviewHousing); empty once housing recovered. The housing planners run only for their own phase.</summary>
    public Phase Housing { get; set; }

    /// <summary>The EnsureComfort phase this review left owed: basic (reachable table, seat and recreation) before ranked (hosting rooms and proof of use, from Development). Empty once recovered.</summary>
    public Phase Comfort { get; set; }

    /// <summary>The MaintainMedicalReserves phase this review left owed: care (sick colonists resting, the hospital) before reserves (the medicine stock). Empty once recovered.</summary>
    public Phase Medical { get; set; }
}

/// <summary>
/// One review's findings. Concerns, Assessments, Incidents, NoOps and GearBudgetPawns are accumulators the
/// inspections append to, so they stay lists; the Go slices share their backing arrays the same way.
/// </summary>
public sealed record RoundsFindings
{
    public DisasterHistory? Disaster { get; set; }

    public RoundsLatches Latches { get; set; } = new();

    public List<RoundsConcern> Concerns { get; set; } = [];

    /// <summary>The Standard needs the review files rows for.</summary>
    public List<RoundsAssessment> Assessments { get; set; } = [];

    /// <summary>The incident kinds' assessments.</summary>
    public List<RoundsAssessment> Incidents { get; set; } = [];

    /// <summary>Every detector that raised nothing, with the typed reason, in registry order.</summary>
    public List<NoOpRecord> NoOps { get; set; } = [];

    /// <summary>The review's one resource-demand value (Needs are the effective MaintainResource stock targets the review held the census to; the stock overlay tints stockpiles by them).</summary>
    public DerivedDemand ResourceDemand { get; set; } = new();

    /// <summary>The pawns whose gear loadout search spent GearSearchBudget and kept the best ensemble found (journaled as gear_search).</summary>
    public List<PawnId> GearBudgetPawns { get; set; } = [];

    /// <summary>Every assessment, goal needs then incident kinds.</summary>
    public List<RoundsAssessment> All() => [.. Assessments, .. Incidents];
}

/// <summary>
/// An Assessment covers recovered and unknown needs as well as actionable deficits. Absence from the
/// scheduling list is never evidence of recovery.
/// </summary>
public sealed record RoundsAssessment
{
    public ConcernId Id { get; set; }

    /// <summary>The pawn a per-pawn Response (EnsureMood) is assessed for; empty otherwise. (Id, Subject) keys its incident.</summary>
    public PawnId Subject { get; set; }

    public int Priority { get; set; }

    public Finding Finding { get; set; }

    /// <summary>
    /// Marks an emergency-tier upkeep need (a home fire) whose serve family this runtime did not declare. The
    /// review still records the need, but it must not suspend every other goal: with no method to clear it
    /// and the clock held for it, nothing could ever resume, which parked a power enclosure build behind an
    /// unfought short-circuit fire.
    /// </summary>
    public bool MethodUnavailable { get; set; }

    /// <summary>The squad prey of an ActiveCombat deficit raised by the food plan with no hostile standing: the incident's hunt origin.</summary>
    public EquatableArray<PawnId> Hunt { get; set; }
}

public static partial class PolicyFunctions
{
    public static readonly ConcernId AnswerDialog = new("AnswerDialog");
    public static readonly ConcernId ActiveCombat = new("ActiveCombat");
    public static readonly ConcernId CriticalMedicine = new("CriticalMedical");
    public static readonly ConcernId RestoreWorkers = new("RestoreWorkers");
    public static readonly ConcernId ManageSupplySafety = new("ManageSupplySafety");
    public static readonly ConcernId EnsureWorkAssignments = new("EnsureWorkAssignments");
    public static readonly ConcernId EnsureFoodSupply = new("EnsureFoodSupply");
    public static readonly ConcernId MaintainHousing = new("MaintainHousing");
    public static readonly ConcernId EnsureTemperatureSafety = new("EnsureTemperatureSafety");
    public static readonly ConcernId EnsureCooking = new("EnsureCooking");
    public static readonly ConcernId EnsureBasicPower = new("EnsureBasicPower");
    public static readonly ConcernId EnsureBasicDefense = new("EnsureBasicDefense");
    public static readonly ConcernId MaintainMedicalReserves = new("MaintainMedicalReserves");
    public static readonly ConcernId MaintainFoodStorage = new("MaintainFoodStorage");
    public static readonly ConcernId MaintainRefrigeration = new("MaintainRefrigeration");
    public static readonly ConcernId EnsureComfort = new("EnsureComfort");
    public static readonly ConcernId ClearPests = new("ClearPests");
    public static readonly ConcernId MaintainEquipment = new("MaintainEquipment");
    public static readonly ConcernId EnsureResearch = new("EnsureResearch");
    public static readonly ConcernId MaintainResource = new("MaintainResource");
    public static readonly ConcernId EnsureDefensiveLayout = new("EnsureDefensiveLayout");
    public static readonly ConcernId TradeWithCaravan = new("TradeWithCaravan");

    /// <summary>MaintainFoodStorage's entry development priority, the same priority the medical reserve starts MaintainMedicalReserves at.</summary>
    internal const int FoodStorageUpkeepPriority = 3;

    /// <summary>Keeps MaintainRefrigeration out of the ranked development queue (see InspectRefrigeration's comment).</summary>
    internal const int RefrigerationPriority = 2;

    /// <summary>Ranks MaintainLighting with the other upkeep projects.</summary>
    internal const int LightingPriority = 3;

    /// <summary>Ranks MaintainFlooring at the lowest goal rank in every tier: floors add no speed or dirt benefit early, and must not take labor or starting wood from rooms still unbuilt.</summary>
    internal const int FlooringPriority = DomainConstants.MaxPriority;

    /// <summary>
    /// Ranks MaintainRoutes with the other upkeep projects: an unreachable facility idles whatever it serves,
    /// so it ranks with a dark bench, never as an emergency.
    /// </summary>
    internal const int RoutesPriority = 3;

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
        WoodMin = 120,
        WoodTarget = 350,
        WoodMax = 500,
        HuntStallTicks = GameTime.TicksPerDay / 2,
        AcquisitionStallTicks = GameTime.TicksPerDay,
        ConcernStallTicks = GameTime.TicksPerDay,
        ResearchLadder = EquatableArray.From(DefaultResearchLadder()),
        ColonyStage = ColonyStage.Development,
    };

    /// <summary>Reads a Go-nil-map lookup of UpkeepIssued: absent is false.</summary>
    public static bool IsIssued(this RoundsFacts f, ConcernId id) => f.UpkeepIssued is not null && f.UpkeepIssued.TryGetValue(id, out var issued) && issued;

    // The foothold facts below are read live from one review's facts: the foothold goals open on them
    // (InspectRounds), and the colony stage (StageColonyFacts), the disaster services (DisasterServiceFacts)
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

    /// <summary>ArmedTarget fighters.</summary>
    internal static Fact<bool> FootholdArmed(RoundsFacts f)
    {
        if (FootholdCount(f).TryGetValue(out var n))
        {
            return Measured(f.Armed, v => v >= Math.Min(2, n));
        }
        return Unknown<bool>();
    }

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

    /// <summary>Whether ActiveCombat has nothing to answer: no hostile stands and the food plan opens no squad hunt.</summary>
    internal static Fact<bool> CombatCleared(RoundsFacts f)
    {
        var cleared = Measured(f.Hostiles, n => n == 0);
        if (Positive(cleared) && HuntRequest(f.FoodPlan).Count > 0)
        {
            return Known(false);
        }
        return cleared;
    }

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

    internal static bool LatchValue(bool active, Fact<double> fact, double enter, double exit, bool high)
    {
        if (!fact.TryGetValue(out var v))
        {
            return active;
        }
        if (high)
        {
            if (active)
            {
                return v >= exit;
            }
            return v > enter;
        }
        if (active)
        {
            return v <= exit;
        }
        return v < enter;
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
    /// Ports colony_policy.criteria/priority_nodes for the common survival goals: it reviews the facts once,
    /// then runs every registered inspection in registry order (Inspections). Family-specific needs join
    /// these same goals during review. Throws DomainException on invalid facts.
    /// </summary>
    public static RoundsFindings InspectRounds(RoundsFacts f, RoundsLatches previous, RoundsPolicy p)
    {
        if (f.Calendar.TryGetValue(out var calendar) && !calendar.Valid())
        {
            throw new DomainException("invalid calendar fact");
        }
        p = p.Seasonal(f.Calendar, f.DisasterConditions);
        var owned = OwnedConstructions(f.ConstructionClaims, f.CurrentConstruction);
        var c = new RoundsRun(f, previous, p)
        {
            Home = PlanHomeArea(f.MapBounds, f.CurrentConstruction, f.ConstructionClaims, f.HomeCoverage),
            Stone = ReviewStoneShell(owned, f.StoneStructures),
            Animals = ReviewAnimalUpkeep(f.AnimalUpkeep, previous.Animals),
        };
        var medicineFacts = f.MedicalReserve with { Colonists = f.Colonists };
        c.Medicine = ReviewMedicalReserve(medicineFacts, previous.MedicalReserve, p.MedicalReserve);
        c.FoodStorage = ReviewFoodStorage(f.FoodStorageUpkeep, previous.FoodStorage, p.FoodStorage);
        c.Refrigeration = ReviewRefrigeration(f.FoodStorageUpkeep, previous.Refrigeration, p.FoodStorage);
        c.Refrigeration = c.Refrigeration.WithWarmRooms(f.WarmRooms);
        c.Upkeep = ReviewUpkeep(f.Upkeep, previous.Upkeep, f.UpkeepIssued);
        c.Lighting = ReviewLighting(f.Upkeep.Lighting, previous.Lighting, p.Lighting, SkyDarkHold(f.DisasterConditions, f.OutdoorsDark));
        c.Lighting = c.Lighting.WithPlanned(f.PlanLit);
        c.Flooring = ReviewFlooring(f.Upkeep.Flooring, f.Upkeep.Rooms, previous.Flooring, p.Flooring);
        c.Routes = ReviewRoutes(f.Upkeep.Routes, previous.Routes, p.Routes);
        if (f.ComfortDeficit.TryGetValue(out var comfortDeficit) && (double.IsNaN(comfortDeficit) || double.IsInfinity(comfortDeficit) || comfortDeficit < 0 || comfortDeficit > 1))
        {
            throw new DomainException("invalid comfort deficit");
        }
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
        var wood = Unknown<double>();
        if (f.Wood.TryGetValue(out var woodCount))
        {
            wood = Known((double)woodCount);
        }
        c.SleepingActive = previous.Sleeping;
        if (f.SleepingRecovered.TryGetValue(out var recovered))
        {
            c.SleepingActive = !recovered;
        }
        c.HomeActive = previous.HomeCoverage;
        c.StoneActive = previous.StoneShell;
        if (c.Home.TryGetValue(out var diff))
        {
            c.HomeActive = !diff.Empty();
        }
        if (c.Stone.TryGetValue(out var rows))
        {
            c.StoneActive = rows.Count > 0;
        }
        c.L = new RoundsLatches
        {
            HomeCoverage = c.HomeActive,
            StoneShell = c.StoneActive,
            Sleeping = c.SleepingActive,
            Animals = c.Animals.History,
            MedicalReserve = c.Medicine.Active,
            FoodStorage = c.FoodStorage.Active,
            Refrigeration = c.Refrigeration.Active,
            Lighting = EquatableArray.From(c.Lighting.Dark),
            Flooring = EquatableArray.From(c.Flooring.Latched),
            Routes = c.Routes.Latched,
            Upkeep = c.Upkeep.History,
            Dash = DashLatch(f.Dash),
            Food = LatchValue(previous.Food, f.FoodDays, p.FoodMinDays, p.FoodTargetDays, false),
            Cold = LatchValue(previous.Cold, Fallback(f.SleepingMin, f.OutdoorTemperature), p.ColdEnter, p.ColdExit, false),
            Hot = LatchValue(previous.Hot, Fallback(f.SleepingMax, f.OutdoorTemperature), p.HotEnter, p.HotExit, true),
            Wood = LatchValue(previous.Wood, wood, p.WoodMin, p.WoodTarget, false),
            Soldiers = previous.Soldiers || GearSoldierPresent(f.Gear),
            FoodFirst = previous.FoodFirst || FoodFirstOwed(f),
        };
        c.R = new RoundsFindings { Latches = c.L with { } };
        c.Demand = ResourceDemandOf(f, p, c.L);
        c.R.ResourceDemand = c.Demand;
        foreach (var d in InspectionRegistry.Inspections)
        {
            int goals = c.R.Concerns.Count, assessments = c.R.Assessments.Count;
            d.Inspect(c);
            if (NoOpOf(d.Concern, c.R.Concerns.Skip(goals).ToList(), c.R.Assessments.Skip(assessments).ToList()) is { } n)
            {
                c.R.NoOps.Add(n);
            }
        }
        var r = c.R;
        // Thought pressure raises the owning upkeep goal's deficit to at least the ledger-weighted share of
        // pawns under the entry margin: the goal's own census still decides whether it is active and what it
        // builds, so a recovered owner is not re-raised, and the pawn's EnsureMood incident defers to it
        // (MoodProvision) instead of dispatching need relief.
        IReadOnlyDictionary<ConcernId, double>? provision = null;
        if (f.MoodPawns.TryGetValue(out var census) && f.MoodLedger.TryGetValue(out var ledger))
        {
            provision = MoodProvisionDeficits(census, ledger);
        }
        foreach (var concern in r.Concerns)
        {
            if (provision is null || !provision.TryGetValue(concern.Id, out var pressure))
            {
                continue;
            }
            if (!concern.Deficit.TryGetValue(out var current) || current < pressure)
            {
                concern.Deficit = Known(pressure);
            }
        }
        if (f.AvailableMethods.TryGetValue(out var methods))
        {
            var available = new HashSet<ConcernId>();
            var recognizedIds = new HashSet<ConcernId>();
            foreach (var assessment in r.Assessments)
            {
                recognizedIds.Add(assessment.Id);
            }
            // Disaster recovery is only assessed once a disaster history exists, but its method capability is
            // declared at composition time, before any facts are read; it must validate against empty facts too.
            recognizedIds.Add(RecoverDisasterServices);
            foreach (var id in methods)
            {
                if (!recognizedIds.Contains(id) || available.Contains(id))
                {
                    throw new DomainException("invalid routine method capability " + id.Value);
                }
                available.Add(id);
            }
            foreach (var concern in r.Concerns)
            {
                if (concern.Priority >= 3 && !available.Contains(concern.Id))
                {
                    concern.MethodUnavailable = true;
                }
            }
            // Each upkeep need's method is its own serve family; an undeclared one at emergency priority is
            // recorded without the suspension.
            var declarable = new HashSet<ConcernId>();
            foreach (var n in c.Upkeep.Needs)
            {
                declarable.Add(n.Concern);
            }
            foreach (var a in r.Assessments)
            {
                if (a.Priority < 2 && declarable.Contains(a.Id) && !available.Contains(a.Id))
                {
                    a.MethodUnavailable = true;
                }
            }
        }
        var all = r.Assessments;
        r.Assessments = [];
        foreach (var a in all)
        {
            if (IsIncidentKind(a.Id))
            {
                r.Incidents.Add(a);
            }
            else
            {
                r.Assessments.Add(a);
            }
        }
        return r;
    }

    /// <summary>
    /// 1 (an emergency finding) while any critical patient is urgent (policy.UrgentPatients) or the urgent
    /// count is unknown, and 2 while every patient is stable: resting under care, only needing a tend, or
    /// downed with nothing to tend. A stable patient is served by the same tend and rescue methods without
    /// classifying their condition as an emergency.
    /// </summary>
    internal static int CriticalMedicinePriority(RoundsFacts f)
    {
        if (!f.CriticalPatients.TryGetValue(out var patients) || patients == 0)
        {
            return 1;
        }
        if (Positive(f.AllPatientsResting))
        {
            return 2;
        }
        if (f.UrgentPatients.TryGetValue(out var urgent) && urgent == 0)
        {
            return 2;
        }
        return 1;
    }

    /// <summary>EnsureCooking's recovery: cooking ready and no cooking campfire owed its retirement.</summary>
    internal static Fact<bool> CookingMet(RoundsFacts f)
    {
        if (Positive(f.CampfireRetireOwed))
        {
            return Known(false);
        }
        return f.Cooking;
    }

    /// <summary>
    /// The WoodLog stock at which the butcher table is owed: the table is stuff-built, so the butcher spot
    /// stands in for it below the stock and the goal never starves the colony of wood.
    /// </summary>
    public const long ButcherTableWood = 120;

    /// <summary>
    /// EnsureFoodSupply's butcher-facility recovery. A bench must stand apart from the kitchen: a butcher
    /// bench inside it keeps the colony fed but not clean. The butcher spot stands in the planned butchery
    /// until the table is built; below ButcherTableWood the spot alone is enough, and a table standing beside
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
        // Only the spot stands apart: with the wood to build the table, the goal stays open for it.
        var ok = f.Wood.TryGetValue(out var wood);
        return Known(!ok || wood < ButcherTableWood);
    }

    /// <summary>The free, instant bench that hosts the club and short bow recipes; MaintainEquipment stands one in the planned workshop until a real weapon bench is built.</summary>
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
