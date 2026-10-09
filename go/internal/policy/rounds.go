package policy

import (
	"errors"
	"math"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

const (
	AnswerDialog            ConcernID = "AnswerDialog"
	ActiveCombat            ConcernID = "ActiveCombat"
	CriticalMedicine        ConcernID = "CriticalMedical"
	RestoreWorkers          ConcernID = "RestoreWorkers"
	ManageSupplySafety      ConcernID = "ManageSupplySafety"
	EnsureWorkAssignments   ConcernID = "EnsureWorkAssignments"
	EnsureFoodSupply        ConcernID = "EnsureFoodSupply"
	MaintainHousing         ConcernID = "MaintainHousing"
	EnsureTemperatureSafety ConcernID = "EnsureTemperatureSafety"
	EnsureCooking           ConcernID = "EnsureCooking"
	MaintainButcherSpot     ConcernID = "MaintainButcherSpot"
	EnsureBasicPower        ConcernID = "EnsureBasicPower"
	EnsureBasicDefense      ConcernID = "EnsureBasicDefense"
	MaintainMedicalReserves ConcernID = "MaintainMedicalReserves"
	MaintainFoodStorage     ConcernID = "MaintainFoodStorage"
	MaintainRefrigeration   ConcernID = "MaintainRefrigeration"
	EnsureComfort           ConcernID = "EnsureComfort"
	ClearPests              ConcernID = "ClearPests"
	MaintainEquipment       ConcernID = "MaintainEquipment"
	EnsureResearch          ConcernID = "EnsureResearch"
	MaintainResource        ConcernID = "MaintainResource"
	EnsureDefensiveLayout   ConcernID = "EnsureDefensiveLayout"
	TradeWithCaravan        ConcernID = "TradeWithCaravan"
)

// foodStorageUpkeepPriority is MaintainFoodStorage's entry development
// priority, the same priority medicalReservePriority (a local var, not a
// const, at its own point of use below) starts MaintainMedicalReserves at.
const foodStorageUpkeepPriority = 3

// refrigerationPriority keeps MaintainRefrigeration out of the ranked
// development queue (see DetectRounds's comment at its point of use).
const refrigerationPriority = 2

// lightingPriority ranks MaintainLighting with the other upkeep projects.
const lightingPriority = 3

// flooringPriority ranks MaintainFlooring while a clean workspace is short
// of floor; living-room flooring alone ranks one step lower and traffic
// flooring last, never below the lowest goal rank.
const flooringPriority = 3

// routesPriority ranks MaintainRoutes with the other upkeep projects: an
// unreachable facility idles whatever it serves, so it ranks with a dark
// bench, never as an emergency.
const routesPriority = 3

type RoundsPolicy struct {
	MedicalReserve                                MedicalReservePolicy
	FoodStorage                                   FoodStoragePolicy
	Cleanliness                                   CleanlinessPolicy
	Lighting                                      LightingPolicy
	Flooring                                      FlooringPolicy
	Routes                                        RoutesPolicy
	FoodMinDays, FoodTargetDays, FootholdFoodDays float64
	FoodReserveDays                               float64
	ColdEnter, ColdExit, HotExit, HotEnter        float64
	WoodMin, WoodTarget, WoodMax                  int64
	// ChopMinGrowth is the growth fraction a plantation tree must
	// reach before chop selection offers it; 0 offers every harvestable one.
	// Wild trees ignore it.
	ChopMinGrowth float64
	// HuntStallTicks bounds how long a dispatched Hunt-kind acquisition action
	// may sit unresolved before RoundsAcquisitionPlanner abandons it and lets
	// a fresh SelectAcquisition pass propose something else. Native's own
	// HuntingSafety.RouteSafe guard can repeatedly interrupt the shared game
	// clock while a hunter's route stays unsafe; that guard stays
	// fully authoritative, but without this grace the stuck action reads as
	// open work forever and blocks the planner from ever trying a different
	// prey or a non-hunt source. It counts from the last observation, and a
	// live hunt (travel, chase, the hunter's sleep and meals, a clock window
	// between observations) outlasts 6000 ticks: that bound withdrew working
	// hunts as "cancelled" and cooled their prey, so it is half a day.
	HuntStallTicks int64
	// AcquisitionStallTicks bounds how long a dispatched plant-harvest
	// acquisition may stay designated with its effect pending (no colonist
	// has taken the designation) before the acquisition and medical
	// planners cancel it so the goal re-plans from another source instead
	// of waiting on one plant (wild healroot pending 120k ticks).
	AcquisitionStallTicks int64
	// ConcernStallTicks bounds how long a goal's progress record may go
	// without native evidence advancing its expected observable before
	// ExpireGoalProgress rotates the method (or keys the failed situation
	// out with a cooldown): GoalProgressContract's default contract and the
	// food ladder's rungs (FoodProgress) both use it as their deadline.
	// StageRoundsPolicy scales it down during StageFoothold
	// (StageGoalStallScale) so a stuck method rotates in a fraction of a
	// day rather than the full day this defaults to, while starvation risk
	// is highest; it returns to this value once the colony reaches
	// Reserves.
	ConcernStallTicks int64
	// ResearchLadder is the ordered roadmap EnsureResearch walks when the workshop ladder records no need
	// (DefaultResearchLadder by default; empty disables the roadmap). Each
	// rung is reached through ResearchPrerequisiteQueue like a target, a
	// current native project is respected and recovers the goal, and the
	// planner lends the clock ticks while one is current so the rung
	// finishes on its own.
	ResearchLadder []string
	// PrisonerReleaseAfterDays is how long MaintainPopulation feeds a
	// prisoner it has no use for (not worth recruiting, not enslavable)
	// while the food runway holds FoodTargetDays before releasing it
	// (default 15 days); below the target it releases at once. See
	// PrisonerPolicy.
	PrisonerReleaseAfterDays float64
	// DefensiveLayout is an operator-declared opt-in for EnsureDefensiveLayout:
	// the staged chokepoint/firing-line/funnel/trap-corridor
	// construction RoundsDefenseLayoutPlanner proposes from a fresh native
	// defense-site census. It is config-only: the review does not derive layout completeness from a
	// census, the planner decides per tier from its own admitted plans.
	DefensiveLayout bool
	// Stage holds the colony stage thresholds; zero fields take the
	// defaults RoundsPolicy.Stages derives from the food thresholds.
	Stage ColonyStagePolicy
	// ColonyStage is the stage the last review left (StageRoundsPolicy):
	// DetectRounds raises only the goals the stage allows
	// (StageGoalAllowed). DefaultRoundsPolicy stands at Development, so a
	// policy nobody staged raises every goal.
	ColonyStage ColonyStage `json:",omitempty"`
}

// DefaultRoundsPolicy supplies the routine inspection targets and budgets.
// Individual planners bound their orders; vanilla priorities schedule pawn work.
func DefaultRoundsPolicy() RoundsPolicy {
	return RoundsPolicy{MedicalReserve: DefaultMedicalReservePolicy(), FoodStorage: DefaultFoodStoragePolicy(), Cleanliness: DefaultCleanlinessPolicy(), Lighting: DefaultLightingPolicy(), Flooring: DefaultFlooringPolicy(), Routes: DefaultRoutesPolicy(), FoodMinDays: 3, FoodTargetDays: 7, FootholdFoodDays: 3, FoodReserveDays: DefaultFoodReserveDays, PrisonerReleaseAfterDays: 15,
		ColdEnter: 12, ColdExit: 16, HotExit: 28, HotEnter: 32, WoodMin: 120, WoodTarget: 350, WoodMax: 500, HuntStallTicks: domain.TicksPerDay / 2, AcquisitionStallTicks: domain.TicksPerDay, ConcernStallTicks: int64(domain.TicksPerDay), ResearchLadder: DefaultResearchLadder(), ColonyStage: StageDevelopment}
}

func (p RoundsPolicy) Validate() error {
	if p.MedicalReserve.MinimumPerColonist < 0 || p.MedicalReserve.TargetPerColonist <= p.MedicalReserve.MinimumPerColonist {
		return errors.New("invalid medicine reserve thresholds")
	}
	if !p.FoodStorage.valid() {
		return errors.New("invalid food storage thresholds")
	}
	if !p.Cleanliness.valid() {
		return errors.New("invalid cleanliness thresholds")
	}
	for _, n := range []float64{p.FoodMinDays, p.FoodTargetDays, p.FootholdFoodDays, p.FoodReserveDays, p.ColdEnter, p.ColdExit, p.HotExit, p.HotEnter, p.PrisonerReleaseAfterDays} {
		if math.IsNaN(n) || math.IsInf(n, 0) {
			return errors.New("nonfinite routine threshold")
		}
	}
	if p.FoodReserveDays < 0 || p.FoodReserveDays > 60 || p.FoodMinDays <= 0 || p.FoodTargetDays <= p.FoodMinDays || p.FootholdFoodDays <= 0 ||
		p.ColdEnter >= p.ColdExit || p.ColdExit >= p.HotExit || p.HotExit >= p.HotEnter ||
		p.WoodMin < 0 || p.WoodTarget <= p.WoodMin || p.WoodMax < p.WoodTarget {
		return errors.New("unordered routine thresholds")
	}
	if !(p.ChopMinGrowth >= 0 && p.ChopMinGrowth <= 1) {
		return errors.New("invalid chop minimum growth")
	}
	if p.PrisonerReleaseAfterDays < 0 || p.PrisonerReleaseAfterDays > 120 {
		return errors.New("invalid prisoner release threshold")
	}
	if err := p.Stages().validate(); err != nil {
		return err
	}
	if p.HuntStallTicks <= 0 {
		return errors.New("invalid hunt stall grace")
	}
	if p.AcquisitionStallTicks <= 0 {
		return errors.New("invalid acquisition stall grace")
	}
	if p.ConcernStallTicks <= 0 {
		return errors.New("invalid standard stall grace")
	}
	for _, rung := range p.ResearchLadder {
		if !validResource(Resource(rung)) {
			return errors.New("invalid research ladder rung")
		}
	}
	return nil
}

// Prisoners is the MaintainPopulation slice of this policy.
func (p RoundsPolicy) Prisoners() PrisonerPolicy {
	return PrisonerPolicy{ReleaseAfterDays: p.PrisonerReleaseAfterDays, FoodTargetDays: p.FoodTargetDays}
}

// RoundsFacts holds derived native facts, not forecasts masquerading as output.
// FoodDays is the accessible diet/rot-aware stock runway. FieldCoverage is the
// separate native crop-capacity forecast; it never increases FoodDays.
type RoundsFacts struct {
	// PersonalShares are the colonists' personal wealth shares the
	// elective surgery gate reads; nil is ungated, a live reading
	// always sets it (an empty map gates every colonist).
	PersonalShares map[PawnID]PersonalShare `json:",omitzero"`
	// Items are the catalog's item numbers (market value, nutrition,
	// medical potency, stuff factors); the zero value without a catalog.
	Items ItemFacts `json:",omitzero"`
	// Recipes are the recipe rows' derived facts: the stuff-made part
	// installs peg-leg cycling plans around.
	Recipes RecipeFacts `json:",omitzero"`
	// VetRoom is the layout's vet room; unread (the zero value) until
	// the layout exposes it, which keeps sterilize off.
	VetRoom VetRoom
	// BarnArea is the id of the bot-owned Barn allowed area, "" until
	// a standing barn has created it.
	BarnArea domain.Fact[string]
	// CompanionArea and WildArea are the ids of the bot-owned Companion and
	// Wild allowed areas, "" until created.
	CompanionArea, WildArea domain.Fact[string]
	// PaddockClosed is the plan's ring closed around the yard (fed
	// from the runtime's paddockClosed); unknown leaves animal areas alone.
	PaddockClosed domain.Fact[bool]
	FoodPlan      domain.Fact[FoodPlan]
	FoodReserve   domain.Fact[FoodReserveReview]
	// BabyFeeding is the babies' food review; unknown without
	// Biotech baby care or consumer facts.
	BabyFeeding          domain.Fact[BabyFeeding]
	TradeMealIngredients domain.Fact[[]FoodIngredientSlot]
	PenGrazing           domain.Fact[[]PenGrazing]
	RecoverySafety       domain.Fact[RecoverySafety]
	RecoveryWorkers      domain.Fact[[]RecoveryWorker]
	DisasterConditions   domain.Fact[[]DisasterCondition]
	// OutdoorsDark is the colony biome's permanent darkness: its map
	// conditions include a no-sunlight class.
	OutdoorsDark      domain.Fact[bool]
	RecoveryBuildings domain.Fact[[]RecoveryBuilding]
	Disaster          *DisasterHistory
	DisasterTick      domain.Tick
	MoodPawns         domain.Fact[[]MoodPawn]
	// MoodLedger is where the colony loses mood, built each review from the
	// mood census and the catalog thought facts; unknown with the census.
	MoodLedger   domain.Fact[MoodLedger]
	Mood         MoodHistory
	HomeCoverage domain.Fact[HomeCoverageObservation]
	// RangeHold are the training range cells the home area leaves out
	// (RangeHomeHold); empty while the range is whole, absent or in its repair window.
	RangeHold           []domain.Cell
	StoneStructures     domain.Fact[[]StoneStructure]
	ConstructionClaims  domain.Fact[[]ConstructionClaim]
	CurrentConstruction domain.Fact[CurrentConstruction]
	// ConstructionDeficit is the material standing blueprints and frames
	// are still owed, per resource.
	ConstructionDeficit domain.Fact[map[Resource]int64]
	// BillReservations are live bill jobs' promised ingredients, per
	// working pawn (MaterialBudget).
	BillReservations  domain.Fact[[]IngredientReservation]
	Sleeping          domain.Fact[SleepingObservation]
	SleepingRecovered domain.Fact[bool]
	// BedroomsOwed: a planned individual bedroom step is due; it
	// keeps MaintainHousing open once everyone owns a shelter bed.
	BedroomsOwed domain.Fact[bool]
	// BurialOwed: a tomb, grave or morgue step is due; it keeps
	// MaintainBurial open while a corpse waits on one.
	BurialOwed domain.Fact[bool]
	// IncinerationOwed: the waste yard or incinerator shell, a due burn or
	// ash to clean waits; it keeps MaintainIncineration open.
	IncinerationOwed domain.Fact[bool]
	// WarmRooms: the warm standing tombs, morgues and meal closets (WarmCoolingRooms); they
	// join MaintainRefrigeration's rooms.
	WarmRooms domain.Fact[[]string]
	// MealClosetOwed: the planned meal closet waits to be shelled while its
	// dining room stands; it keeps MaintainRefrigeration open.
	MealClosetOwed domain.Fact[bool]
	// PartySpotOwed: the colony owes its PartySpot a placement or a
	// deconstruction (ReviewPartySpot); it holds EnsureComfort Unmet for the
	// pass or two the step takes. Unknown leaves comfort as it was.
	PartySpotOwed domain.Fact[bool]
	// CampfireRetireOwed: a stove kitchen supersedes a cooking campfire; it keeps EnsureCooking open.
	CampfireRetireOwed domain.Fact[bool]
	// TemperatureOwed: a heat campfire's auto-refuel should switch, or a
	// sleeping room sits unheated below its sleepers' comfort minimum
	// (TemperatureOwed); it holds EnsureTemperatureSafety open.
	TemperatureOwed domain.Fact[bool]
	// SculptureRoomsOwed: a bedroom below target, weakest in beauty, has a
	// free cell for a sculpture (SculptureRoomsOwed); with a
	// qualifying artist it holds MaintainArt open.
	SculptureRoomsOwed domain.Fact[bool]
	// LedgerOwed: the work ledger's reconcile diff is non-empty
	// (LedgerDiffOwed); it holds MaintainWorkLedger open. Unknown unless the
	// ledger has declarers and read every bench's bills.
	LedgerOwed domain.Fact[bool]
	// UnmetThroughput is the dispatcher's unmet throughput per bench kind
	// (units a day short and why), sorted by kind; the facilities ladder reads
	// it. Empty when nothing is short or the ledger has no declarers.
	UnmetThroughput []UnmetThroughput `json:",omitempty"`
	// SafeAreaOwed: the Safe allowed area differs from the enclosed roofed
	// rooms (PlanSafeArea); it holds MaintainShelter open. Unknown
	// unless the MaintainShelter method is composed.
	SafeAreaOwed domain.Fact[bool]
	// FirebreakOwed: the firebreak ring has a cut cell with a standing
	// plant or an undesignated wooden ruin (FirebreakOwed); it holds
	// MaintainFirebreak open. Unknown unless the method is composed.
	FirebreakOwed domain.Fact[bool]
	// MechGestationOwed: a mechanitor can afford the next mech, a gestator
	// is idle and no waste is uncleared (MechGestationOwed); it holds
	// MaintainMechs open. Unknown unless the method is composed.
	MechGestationOwed domain.Fact[bool]
	// HerdRoomsOwed: a barn or vet room step is due (NextHerdStep);
	// it holds MaintainAnimalContainment open after the pen stands.
	HerdRoomsOwed domain.Fact[bool]
	// PsylinkOwed: a willing colonist has no psylink and a psylink
	// neuroformer is held (PsylinkOwed); it holds MaintainPsylink
	// open. Unknown unless the method is composed.
	PsylinkOwed domain.Fact[bool]
	// CreepJoinerOwed: a creepjoiner whose downside has not shown holds a
	// weapon (CreepJoinerDownsides.WeaponDrops); it holds
	// ManageCreepJoiners open. Unknown unless the method is composed.
	CreepJoinerOwed domain.Fact[bool]
	// RolesOwed: an active ideoligion role has a free place and a fitting
	// believer (RolesOwed); it holds MaintainIdeoRoles open.
	RolesOwed domain.Fact[bool]
	// RitualSites are the finished buildings the held rituals' patterns
	// require (observation, from the frame's building census); RitualPlans
	// the rituals to begin now (PlanRituals), whose attendees the
	// schedule planners hold off Sleep (HeldOffSleep); RitualsOwed holds
	// MaintainRituals open. Unknown unless the method is composed.
	RitualSites domain.Fact[[]RitualSite]
	RitualPlans domain.Fact[[]RitualPlan]
	RitualsOwed domain.Fact[bool]
	// GatheringPlan is the party to start now (PlanGathering); GatheringOwed
	// holds HoldGatherings open. Unknown unless the method is composed.
	GatheringPlan domain.Fact[GatheringPlan]
	GatheringOwed domain.Fact[bool]
	ReformOwed    domain.Fact[bool]
	// ShelterArea is the Safe allowed area's native load id, "" when the
	// map has none (PlanSheltering).
	ShelterArea domain.Fact[string]
	// ShelterCombatants is the squad's draft set during a threat: the
	// colonists a raid or manhunter pack does not shelter. Unknown shelters
	// no colonist for a threat.
	ShelterCombatants domain.Fact[[]PawnID]
	// NoDangerArea is the NoDanger allowed area's native load id, ""
	// when the map has none; DangerWindow whether haulers are kept out of
	// the danger cells now (DangerWindowOf); DangerHaulers the pawns with
	// Hauling enabled; DangerSeeds the threat census's danger seeds
	// (DangerSeeds), unknown with the hostile count.
	NoDangerArea  domain.Fact[string]
	DangerSeeds   domain.Fact[[]domain.Cell]
	DangerWindow  domain.Fact[bool]
	DangerHaulers domain.Fact[[]PawnID]
	// IsolationArea is the Isolation allowed area's native load id, "" when
	// the map has none (ManageCreepJoiners).
	IsolationArea domain.Fact[string]
	// SaleArt counts the packed art no owed room reserves (SaleSculptures);
	// read only while the wealth headroom is negative, it opens a trade as
	// the shed_art need.
	SaleArt domain.Fact[int64]
	// FabricableParts are the part items a usable gear bench has a researched
	// recipe for, read only while a medical pawn wants a part; the
	// caravan assessment counts only parts no bench can make.
	FabricableParts   map[Resource]bool
	AnimalUpkeep      AnimalUpkeepObservation
	FoodStorageUpkeep FoodStorageObservation
	MedicalReserve    MedicalReserveObservation
	// Prisoners carries Population-*'s recruit/maintain census: unlike
	// AnimalUpkeep, this has no generic per-tick colony read to piggyback on
	// (recruitable/current-interaction facts live only on the dedicated
	// rimgovernor/observations_read_population census), so it is populated by
	// a dedicated per-cycle RoundsSource read instead of ObserveColony's
	// always-present projection.
	Prisoners domain.Fact[[]PrisonerFacts]
	// PrisonerColony is the same read's colony side of each prisoner's
	// use: the free colonists' best skills and the Ideology facts.
	PrisonerColony domain.Fact[PrisonerColony]
	// Custody carries Population-*'s capture/rescue candidate census: every
	// observed humanlike from the same dedicated population read Prisoners
	// uses, broadened past prisoners alone so RoundsPopulationCustodyPlanner
	// can detect and select a downed hostile or unadmitted guest to dispatch.
	Custody domain.Fact[[]CustodyFacts]
	// Containment is the containment cell's inputs and the entity
	// rows the capture rule decides, set by the routine reading.
	Containment ContainmentPlanning
	// Outlook is the same population read's storyteller outlook.
	Outlook PopulationOutlook
	// OwnedNames is the same read's owned-pawn short-name census.
	OwnedNames domain.Fact[[]OwnedName]
	// QuestOffers carries MaintainPopulation's joiner census: every visible
	// quest row (rimgovernor/observations_read_world_progression), read per
	// cycle by a RoundsSource offering RoundsQuestSource, for JoinerDeficit
	// to detect and SelectJoinerMethod to answer a joiner offer from.
	QuestOffers domain.Fact[[]JoinerOffer]
	QuestSites  domain.Fact[[]WorldSite]
	// Royalty is the royalty facts from the pawn rows, the def mirror
	// and the colony section; unknown when Royalty is not applicable or a read
	// failed.
	Royalty domain.Fact[RoyaltyFacts]
	// Ideology is the primary ideoligion from the frame's ideology
	// section with the catalog's defs; unknown when the frame carries no
	// section (no Ideology, or no primary ideoligion).
	Ideology domain.Fact[Ideoligion]
	// IdeologyInstalled is whether the Ideology expansion is active;
	// unknown when the frame does not say.
	IdeologyInstalled domain.Fact[bool]
	// TitleClaimQuests are the bestowing-ceremony quests the title claim
	// gate allows to accept now (ClaimQuests); the review fills it
	// once the plan, rooms and royalty read are known.
	TitleClaimQuests []domain.QuestID
	JoinerLetters    domain.Fact[[]JoinerLetterOffer]
	RaidPoints       domain.Fact[float64]
	// Monolith is the void monolith's state; unknown without Anomaly.
	Monolith domain.Fact[MonolithFacts]
	// ShellsShort is the armory shell review: a built mortar's shell
	// stock below half its target puts MaintainEquipment in deficit.
	ShellsShort domain.Fact[bool]
	// DefenseCapacity is the colonists' and powered turrets' observed
	// combat strength in raid-point units (DefenseCapacity).
	DefenseCapacity domain.Fact[float64]
	// Waste carries the exposed/eligible native item census (the same
	// WasteReply the generic per-tick colony read already carries) that
	// MorgueWaiting and RouteStranger read.
	Waste domain.Fact[[]WasteItem]
	// Traders is the map trader census (bridge.ListTraders) TradeWithCaravan
	// needs; a source without the read leaves it unknown and the goal off.
	Traders domain.Fact[[]TraderFacts]
	// Blight carries RemoveBlight's blighted-plant census (the colony read's
	// plant things of the planning window), for BlightDeficit to detect and
	// SelectBlightCuts to designate from.
	Blight domain.Fact[[]BlightedPlant]
	// Pollution carries ManagePollution's wastepack verdicts and the polluted
	// cells outside the clear area (the Biotech colony section); unknown
	// without Biotech or when the read failed, and then the need is unknown.
	Pollution domain.Fact[PollutionFacts]
	// MechChargerOwed is whether the colony owes one more mech charger
	// (MechChargerNeed); unknown without Biotech, mechs or chargers
	// read, and then the EnsureMechCharger need is unknown.
	MechChargerOwed domain.Fact[bool]
	// GeneBankOwed is whether more genepacks lie loose than the standing
	// gene banks have room for (GeneBankNeed); unknown without
	// Biotech or a complete gene-building read, and then MaintainGeneBank
	// has no assessment.
	GeneBankOwed domain.Fact[bool]
	// Stockpiles is the MaintainStockpiles review: this cycle's
	// stockpile edits and the planned rooms owed a shell, or why none stands.
	Stockpiles domain.Fact[StockpileReview]
	// StockpileZones counts the owned stockpile zones by role kind for the
	// colony status census; unknown until the zone claims are read.
	StockpileZones domain.Fact[[]StockpileRoleCount]
	// AvailableMethods is supplied by the configured runtime, never native facts.
	AvailableMethods domain.Fact[[]ConcernID]
	Upkeep           UpkeepObservation
	// ShrineHolds is each Upkeep.Shrines row's breach judgement as
	// the reviewer read it, journalled beside the review; empty while the
	// census is unknown.
	ShrineHolds  []ShrineHold
	UpkeepIssued map[ConcernID]bool
	Gear         domain.Fact[GearObservation]
	// Garments are the allowed garments covering a core group with their
	// recipes (ClothingRunway); empty until the gear census and catalog
	// are read.
	Garments []ClothingGarment `json:",omitzero"`
	Comfort  domain.Fact[ComfortObservation]
	// BasicComfort is the same census before the hosting-room filter: every
	// indoor seat at an eating surface and every recreation source, whatever
	// room (or none) hosts it. EnsureComfort's basic phase measures it; Comfort keeps
	// only facilities in rooms whose native role the facility catalog hosts.
	BasicComfort         domain.Fact[ComfortObservation]
	ComfortRecovered     domain.Fact[bool]
	ComfortDeficit       domain.Fact[float64]
	EventLoot            domain.Fact[[]LootItem]
	EventLootPending     domain.Fact[bool]
	LootReadiness        LootReadiness // the loot census's reach readiness
	MapBounds            domain.Fact[Bounds]
	MedicalPawns         domain.Fact[[]CarePawn]
	MedicalCareRecovered domain.Fact[bool]
	Workers              domain.Fact[int]
	// Labor is the per-work-type census of the same pawns Workers counts
	// (RoundsLabor); unknown labor remains unavailable evidence.
	Labor domain.Fact[map[WorkType]int]
	// WorkRoster is the planner's per-work-type coverage (PlanWork): the
	// owners each type wanted and found and the pawns capable of it, so a
	// goal can name a missing capability instead of stalling.
	WorkRoster domain.Fact[[]WorkCoverage]
	// Quest capacity is derived from the current roster, world census and emergency review.
	QuestSparePawns      domain.Fact[[]PawnID]
	QuestWorkers         domain.Fact[[]WorkPawn]
	QuestWorkCapacity    domain.Fact[[]QuestWorkCapacity]
	QuestDeparturePawns  domain.Fact[[]QuestDeparturePawn]
	QuestDepartureWork   domain.Fact[[]PawnWorkAssignment]
	QuestExpeditionTrips domain.Fact[[]ExpeditionTrip]
	QuestObservedTick    domain.Fact[domain.Tick]
	QuestColonistsAtHome domain.Fact[int]
	// QuestHomeFloor is the sole essential owners who stay home (QuestHomeFloor).
	QuestHomeFloor  domain.Fact[int]
	QuestColonyCalm domain.Fact[bool]
	// WorkDecaying is the same plan's skills above 10 that no assignment
	// exercises (WorkDecision.Decaying) and WorkProfiles every work pawn's
	// typed profile (Profiles); both are presentation facts the review
	// records for presentation, never planner inputs.
	WorkDecaying                                                               domain.Fact[[]DecayingSkill]
	WorkProfiles                                                               domain.Fact[[]PawnProfile]
	Colonists, HousingTarget, BedCapacity, IndoorCapacity, GrowingCells, Armed domain.Fact[int64]
	// Unarmed counts living, conscious colonists able to fight who hold no
	// weapon; EnsureBasicDefense stays owed while it is positive.
	Unarmed                 domain.Fact[int64]
	FoodDays, FieldCoverage domain.Fact[float64]
	// WorkHelp is the same plan's construction helper record;
	// nil when the plan ran without the helper input.
	WorkHelp *ConstructionHelpRecord
	// Calendar is the tile's native growing calendar (policy.Calendar).
	// DetectRounds widens the policy's food and wood targets by its
	// harvest gap (RoundsPolicy.Seasonal) before measuring any latch;
	// an unknown calendar keeps the configured flat targets.
	Calendar                                                    domain.Fact[Calendar]
	SleepingMin, SleepingMax, OutdoorTemperature, PowerHeadroom domain.Fact[float64]
	// Forward are the shadow projector inputs the facts above lack.
	Forward ForwardObserved `json:",omitzero"`
	Wood    domain.Fact[int64]
	// Fuel are the refuelable buildings of the power census (FuelRunway).
	Fuel domain.Fact[[]FuelConsumer] `json:",omitzero"`

	// Admitted are the open costs of admitted methods, from the last
	// review's live records: an open shortfall raises a MaintainResource
	// floor for the bounded difference.
	Admitted []AdmittedCost
	// OpenBills are the player's unfinished gear bills with their recipe
	// slots (OpenBillDemand): ingredient demand ResourceDemandOf counts.
	OpenBills []OpenBill
	// Resources is the generic reachable, unforbidden player item census
	// (the same colony facts rows Wood is taken from), so MaintainResource's
	// deficit is measured at review time instead of assumed from config.
	Resources          domain.Fact[[]Amount]
	ResourceSurfaceOre map[Resource]domain.Fact[int64]
	// ResourceConsumption is the recurring spend over the rate window, from
	// native's realized-consumption ring; unknown when the read failed.
	ResourceConsumption domain.Fact[ResourceConsumption]
	ResourceRunways     []ResourceRunway
	// DrugUsers is the colonists whose drug policy permits each social drug
	// (DrugUsers); it sets the drug runway's reserve before any dose is observed.
	DrugUsers map[Resource]int64
	// Wealth is the colony wealth split TradeWithCaravan's
	// wealth-driven surplus keys on; unknown leaves that surplus out.
	Wealth domain.Fact[WealthFacts]
	// Research is the native research state read inside the same paused
	// identity bracket as the other routine facts. Unknown when the source
	// cannot read research; missing facts never recover EnsureResearch.
	Research domain.Fact[ResearchFacts]
	// ResearchNeeds are the sorted ResearchProjectDefs other goals' recorded
	// evidence is waiting on (the workshop ladder's research rung); the
	// first unfinished one is EnsureResearch's target when none is
	// configured.
	ResearchNeeds []string
	// ResourceNeeds are derived stock floors other goals' recorded evidence
	// asks for (the defensive layout's turret fuel the census found no
	// stock of); ResourceGoalTargets merges them into the operator's
	// MaintainResource targets, never lowering a configured floor.
	ResourceNeeds map[Resource]int64
	// DefensiveLayoutStanding is journal evidence for EnsureDefensiveLayout:
	// known true while the stored layout was verified complete and every
	// tier still stood at the last census, so a standing layout no longer
	// outranks the upkeep goals (repairs, power) that keep it working.
	DefensiveLayoutStanding    domain.Fact[bool]
	Hostiles, CriticalPatients domain.Fact[int64]
	// UrgentPatients counts the critical patients who are bleeding or downed
	// with a tend outstanding (policy.UrgentPatients). CriticalMedicine is an
	// emergency only while one exists or the
	// count is unknown; a colonist who merely needs tending, or is downed
	// with nothing to tend, keeps the goal active at priority 2 so the
	// colony's other work and the clock go on around the tend or rescue.
	UrgentPatients                   domain.Fact[int64]
	AllPatientsResting, CleanupPawns domain.Fact[bool]
	// HostilityOwed is a colonist whose hostility response differs from
	// the one it should hold; EnsureWorkAssignments writes it.
	HostilityOwed domain.Fact[bool]
	// MedicalCareOwed is a pawn whose medical care differs from its cap;
	// Guests are the colony's guests' cap inputs.
	MedicalCareOwed domain.Fact[bool]
	Guests          domain.Fact[[]CarePatient]
	// MedicineCarryOwed is a colonist whose medicine carry count differs
	// from the planned one; EnsureWorkAssignments writes it.
	MedicineCarryOwed domain.Fact[bool]
	// SelfTendOwed is a colonist whose self-tend setting differs from the
	// one it should hold; EnsureWorkAssignments writes it.
	SelfTendOwed domain.Fact[bool]
	// NamesOwed is an owned pawn whose short name an older owned pawn
	// holds; EnsureWorkAssignments renames it.
	NamesOwed domain.Fact[bool]
	// ChoiceDialog is true while the game is force-paused by a choice dialog
	// it opened by itself; AnswerDialog is the goal that answers it.
	ChoiceDialog domain.Fact[bool]
	// ButcherBenches are the standing butcher benches and the room each stands
	// in; MaintainButcherSpot is recovered once one stands outside the kitchen.
	ButcherBenches                                          domain.Fact[[]ButcherBench]
	Cooking, WorkCoverage, PowerRequired, DisabledConsumers domain.Fact[bool]
	PowerWeatherSafe                                        domain.Fact[bool]
	ShortCircuitTick                                        domain.Fact[domain.Tick]
}

// The foothold facts below are read live from one review's facts: the
// foothold goals open on them (DetectRounds), and the colony stage
// (StageColonyFacts), the disaster services (DisasterServiceFacts) and the
// food ladder (FoodProgress) read the ones they need.

// footholdCount is the colonist count the foothold sizes for: the housing
// target when it is larger.
func footholdCount(f RoundsFacts) domain.Fact[int64] {
	count := f.Colonists
	if target, k := f.HousingTarget.Value(); k {
		if n, nk := count.Value(); nk {
			count = domain.Known(max(n, target))
		}
	}
	return count
}
func footholdSleeping(f RoundsFacts) domain.Fact[bool] {
	return countCapacity(f.BedCapacity, footholdCount(f), 1)
}
func footholdShelter(f RoundsFacts) domain.Fact[bool] {
	return countCapacity(f.IndoorCapacity, footholdCount(f), 1)
}
func footholdProduction(f RoundsFacts) domain.Fact[bool] {
	return countCapacity(f.GrowingCells, footholdCount(f), 10)
}
func footholdFood(f RoundsFacts, p RoundsPolicy) domain.Fact[bool] {
	return measured(f.FoodDays, func(v float64) bool { return v >= p.FootholdFoodDays })
}
func footholdTemperature(f RoundsFacts, p RoundsPolicy) domain.Fact[bool] {
	return allFacts(measured(f.SleepingMin, func(v float64) bool { return v >= p.ColdEnter }), measured(f.SleepingMax, func(v float64) bool { return v <= p.HotEnter }))
}
func footholdPower(f RoundsFacts) domain.Fact[bool] {
	if safe, known := f.PowerWeatherSafe.Value(); known && !safe {
		return domain.Known(false)
	}
	power := measured(f.PowerHeadroom, func(v float64) bool { return v >= 0 })
	if required, k := f.PowerRequired.Value(); k && !required {
		power = domain.Known(true)
	} else if !k {
		power = domain.Unknown[bool]()
	}
	return allFacts(power, measured(f.DisabledConsumers, func(v bool) bool { return !v }))
}

// footholdArmed is two armed fighters (every colonist when fewer).
func footholdArmed(f RoundsFacts) domain.Fact[bool] {
	if n, k := footholdCount(f).Value(); k {
		return measured(f.Armed, func(v int64) bool { return v >= min(2, n) })
	}
	return domain.Unknown[bool]()
}

// DisasterServiceFacts are the survival services ReviewDisaster tracks,
// read live from the review's facts (infrastructure is its own).
func DisasterServiceFacts(f RoundsFacts, p RoundsPolicy) map[DisasterService]domain.Fact[bool] {
	return map[DisasterService]domain.Fact[bool]{
		DisasterFood: footholdFood(f, p), DisasterProduction: footholdProduction(f), DisasterSleeping: footholdSleeping(f),
		DisasterShelter: footholdShelter(f), DisasterTemperature: footholdTemperature(f, p), DisasterCooking: f.Cooking,
		DisasterPower: footholdPower(f), DisasterStorage: FoodStorageStanding(f),
	}
}

type RoundsLatches struct {
	HomeCoverage, StoneShell bool
	Sleeping                 bool
	Animals                  AnimalUpkeepHistory
	MedicalReserve           bool
	FoodStorage              bool
	Refrigeration            bool
	// RefrigerationSince is the review tick the Refrigeration latch last
	// engaged, kept while it holds and zero when it is released: the
	// refrigeration planner lends native cooling time from it when the
	// goal's epoch has no cooler method of its own.
	RefrigerationSince domain.Tick `json:",omitempty"`
	// Lighting holds the bench IDs MaintainLighting last measured dark.
	Lighting []string
	// Flooring holds the room keys MaintainFlooring last measured short.
	Flooring []string
	// Routes holds the facility IDs MaintainRoutes last measured unreachable.
	Routes                []string
	Food, Cold, Hot, Wood bool
	Upkeep                UpkeepHistory
	// Soldiers latches once the gear census derives a soldier role: the
	// research roadmap then walks the armor ladder (ArmorResearchLadder ) and keeps walking it when the squad is later undrafted.
	Soldiers bool `json:",omitempty"`
	// Housing is the MaintainHousing phase this review left owed
	// (reviewHousing); empty once housing recovered. The housing planners
	// run only for their own phase.
	Housing Phase `json:",omitempty"`
	// Comfort is the EnsureComfort phase this review left owed: basic
	// (reachable table, seat and recreation) before ranked (hosting rooms
	// and proof of use, from StageDevelopment). Empty once recovered.
	Comfort Phase `json:",omitempty"`
	// Medical is the MaintainMedicalReserves phase this review left owed:
	// care (sick colonists resting, the hospital) before reserves (the
	// medicine stock). Empty once recovered.
	Medical Phase `json:",omitempty"`
}
type RoundsFindings struct {
	Disaster *DisasterHistory
	Latches  RoundsLatches
	Concerns []RoundsConcern
	// Assessments are the Standard and Project needs the review files rows for;
	// Incidents are the incident kinds' assessments.
	Assessments []RoundsAssessment
	Incidents   []RoundsAssessment
	// NoOps is every detector that raised nothing, with the typed reason,
	// in registry order.
	NoOps []NoOpRecord `json:",omitempty"`
	// ResourceDemand is the review's one resource-demand value (Needs are
	// the effective MaintainResource stock targets the review held the census
	// to; the stock overlay tints stockpiles by them).
	ResourceDemand DerivedDemand
	// GearBudgetPawns are the pawns whose gear loadout search spent
	// GearSearchBudget and kept the best ensemble found (journaled as gear_search).
	GearBudgetPawns []PawnID `json:",omitempty"`
}

// Assessments cover recovered and unknown needs as well as actionable deficits.
// Absence from the scheduling list is never evidence of recovery.
type RoundsAssessment struct {
	ID ConcernID
	// Subject is the pawn a per-pawn Response (EnsureMood) is assessed
	// for; empty otherwise. (ID, Subject) keys its incident.
	Subject  domain.PawnID `json:",omitempty"`
	Priority int
	Finding  domain.Finding
	// MethodUnavailable marks an emergency-tier upkeep need (a home fire)
	// whose serve family this runtime did not declare. The review still
	// records the need, but it must not suspend every other goal: with no
	// method to clear it and the clock held for it, nothing could ever
	// resume, which parked a power enclosure build behind an unfought
	// short-circuit fire.
	MethodUnavailable bool
	// Hunt is the squad prey of an ActiveCombat deficit raised by the food
	// plan with no hostile standing: the incident's hunt origin.
	Hunt []domain.PawnID `json:",omitempty"`
}

// combatCleared is whether ActiveCombat has nothing to answer: no hostile
// stands and the food plan opens no squad hunt.
func combatCleared(f RoundsFacts) domain.Fact[bool] {
	cleared := measured(f.Hostiles, func(n int64) bool { return n == 0 })
	if positive(cleared) && len(HuntRequest(f.FoodPlan)) > 0 {
		return domain.Known(false)
	}
	return cleared
}

func positive(v domain.Fact[bool]) bool { b, k := v.Value(); return k && b }
func negative(v domain.Fact[bool]) bool { b, k := v.Value(); return k && !b }
func measured[T any](f domain.Fact[T], predicate func(T) bool) domain.Fact[bool] {
	v, k := f.Value()
	if !k {
		return domain.Unknown[bool]()
	}
	return domain.Known(predicate(v))
}
func allFacts(facts ...domain.Fact[bool]) domain.Fact[bool] {
	unknown := false
	for _, f := range facts {
		v, k := f.Value()
		if k && !v {
			return domain.Known(false)
		}
		unknown = unknown || !k
	}
	if unknown {
		return domain.Unknown[bool]()
	}
	return domain.Known(true)
}
func latchValue(active bool, fact domain.Fact[float64], enter, exit float64, high bool) bool {
	v, k := fact.Value()
	if !k {
		return active
	}
	if high {
		if active {
			return v >= exit
		}
		return v > enter
	}
	if active {
		return v <= exit
	}
	return v < enter
}

// fallback is f when measured, else other: an unmeasured sleeping-room
// temperature reads as the outdoor temperature (sleeping outdoors).
func fallback[T any](f, other domain.Fact[T]) domain.Fact[T] {
	if _, k := f.Value(); k {
		return f
	}
	return other
}
func countCapacity(capacity, count domain.Fact[int64], multiplier int64) domain.Fact[bool] {
	c, k := count.Value()
	v, vk := capacity.Value()
	if !k || !vk {
		return domain.Unknown[bool]()
	}
	return domain.Known(c > 0 && v >= c*multiplier)
}

// DetectRounds ports colony_policy.criteria/priority_nodes for the common
// survival goals: it reviews the facts once, then runs every registered
// inspection in registry order (inspections). Family-specific needs
// join these same goals during review.
func InspectRounds(f RoundsFacts, previous RoundsLatches, p RoundsPolicy) (RoundsFindings, error) {
	if c, known := f.Calendar.Value(); known && !c.Valid() {
		return RoundsFindings{}, errors.New("invalid calendar fact")
	}
	p = p.Seasonal(f.Calendar, f.DisasterConditions)
	owned, err := OwnedConstructions(f.ConstructionClaims, f.CurrentConstruction)
	if err != nil {
		return RoundsFindings{}, err
	}
	c := &roundsRun{f: f, previous: previous, p: p}
	if c.home, err = PlanHomeArea(f.MapBounds, f.CurrentConstruction, f.ConstructionClaims, f.HomeCoverage, f.RangeHold); err != nil {
		return RoundsFindings{}, err
	}
	if c.stone, err = ReviewStoneShell(owned, f.StoneStructures); err != nil {
		return RoundsFindings{}, err
	}
	if c.animals, err = ReviewAnimalUpkeep(f.AnimalUpkeep, previous.Animals); err != nil {
		return RoundsFindings{}, err
	}
	medicineFacts := f.MedicalReserve
	medicineFacts.Colonists = f.Colonists
	if c.medicine, err = ReviewMedicalReserve(medicineFacts, previous.MedicalReserve, p.MedicalReserve); err != nil {
		return RoundsFindings{}, err
	}
	if c.foodStorage, err = ReviewFoodStorage(f.FoodStorageUpkeep, previous.FoodStorage, p.FoodStorage); err != nil {
		return RoundsFindings{}, err
	}
	if c.refrigeration, err = ReviewRefrigeration(f.FoodStorageUpkeep, previous.Refrigeration, p.FoodStorage); err != nil {
		return RoundsFindings{}, err
	}
	c.refrigeration = c.refrigeration.WithWarmRooms(f.WarmRooms)
	if c.upkeep, err = ReviewUpkeepWith(f.Upkeep, previous.Upkeep, f.UpkeepIssued, p.Cleanliness); err != nil {
		return RoundsFindings{}, err
	}
	if c.lighting, err = ReviewLighting(f.Upkeep.Lighting, previous.Lighting, p.Lighting, SkyDarkHold(f.DisasterConditions, f.OutdoorsDark)); err != nil {
		return RoundsFindings{}, err
	}
	if c.flooring, err = ReviewFlooring(f.Upkeep.Flooring, f.Upkeep.Rooms, previous.Flooring, p.Flooring); err != nil {
		return RoundsFindings{}, err
	}
	if c.routes, err = ReviewRoutes(f.Upkeep.Routes, previous.Routes, p.Routes); err != nil {
		return RoundsFindings{}, err
	}
	if v, known := f.ComfortDeficit.Value(); known && (math.IsNaN(v) || math.IsInf(v, 0) || v < 0 || v > 1) {
		return RoundsFindings{}, errors.New("invalid comfort deficit")
	}
	if err := p.Validate(); err != nil {
		return RoundsFindings{}, err
	}
	for _, fact := range []domain.Fact[int64]{f.Colonists, f.HousingTarget, f.BedCapacity, f.IndoorCapacity, f.GrowingCells, f.Armed, f.Unarmed, f.Wood, f.Hostiles, f.CriticalPatients, f.UrgentPatients} {
		if v, k := fact.Value(); k && (v < 0 || v > math.MaxInt64/10) {
			return RoundsFindings{}, errors.New("invalid routine count")
		}
	}
	for _, fact := range []domain.Fact[float64]{f.FoodDays, f.FieldCoverage, f.SleepingMin, f.SleepingMax, f.OutdoorTemperature, f.PowerHeadroom} {
		if v, k := fact.Value(); k && (math.IsNaN(v) || math.IsInf(v, 0)) {
			return RoundsFindings{}, errors.New("nonfinite routine fact")
		}
	}
	for _, fact := range []domain.Fact[float64]{f.FoodDays, f.FieldCoverage} {
		if v, k := fact.Value(); k && v < 0 {
			return RoundsFindings{}, errors.New("negative food fact")
		}
	}
	wood := domain.Unknown[float64]()
	if n, k := f.Wood.Value(); k {
		wood = domain.Known(float64(n))
	}
	c.sleepingActive = previous.Sleeping
	if recovered, known := f.SleepingRecovered.Value(); known {
		c.sleepingActive = !recovered
	}
	c.homeActive, c.stoneActive = previous.HomeCoverage, previous.StoneShell
	if diff, known := c.home.Value(); known {
		c.homeActive = !diff.Empty()
	}
	if rows, known := c.stone.Value(); known {
		c.stoneActive = len(rows) > 0
	}
	c.l = RoundsLatches{
		HomeCoverage: c.homeActive, StoneShell: c.stoneActive,
		Sleeping:       c.sleepingActive,
		Animals:        c.animals.History,
		MedicalReserve: c.medicine.Active,
		FoodStorage:    c.foodStorage.Active,
		Refrigeration:  c.refrigeration.Active,
		Lighting:       c.lighting.Dark,
		Flooring:       c.flooring.Latched,
		Routes:         c.routes.Latched,
		Upkeep:         c.upkeep.History,
		Food:           latchValue(previous.Food, f.FoodDays, p.FoodMinDays, p.FoodTargetDays, false),
		Cold:           latchValue(previous.Cold, fallback(f.SleepingMin, f.OutdoorTemperature), p.ColdEnter, p.ColdExit, false),
		Hot:            latchValue(previous.Hot, fallback(f.SleepingMax, f.OutdoorTemperature), p.HotEnter, p.HotExit, true),
		Wood:           latchValue(previous.Wood, wood, float64(p.WoodMin), float64(p.WoodTarget), false),
		Soldiers:       previous.Soldiers || GearSoldierPresent(f.Gear),
	}
	c.r = RoundsFindings{Latches: c.l}
	c.demand = ResourceDemandOf(f, p, c.l)
	c.r.ResourceDemand = c.demand
	for _, d := range inspections {
		goals, assessments := len(c.r.Concerns), len(c.r.Assessments)
		if err := d.Inspect(c); err != nil {
			return RoundsFindings{}, err
		}
		if n, noOp := noOpOf(d.Concern, c.r.Concerns[goals:], c.r.Assessments[assessments:]); noOp {
			c.r.NoOps = append(c.r.NoOps, n)
		}
	}
	r := c.r
	// Thought pressure raises the owning upkeep goal's deficit to at least the
	// ledger-weighted share of pawns under the entry margin: the goal's own
	// census still decides whether it is active and what it builds, so a
	// recovered owner is not re-raised, and the pawn's EnsureMood incident
	// defers to it (MoodProvision) instead of dispatching need relief.
	var provision map[ConcernID]float64
	if census, ck := f.MoodPawns.Value(); ck {
		if ledger, lk := f.MoodLedger.Value(); lk {
			provision = MoodProvisionDeficits(census, ledger)
		}
	}
	for i := range r.Concerns {
		pressure, ok := provision[r.Concerns[i].ID]
		if !ok {
			continue
		}
		if current, known := r.Concerns[i].Deficit.Value(); !known || current < pressure {
			r.Concerns[i].Deficit = domain.Known(pressure)
		}
	}
	if methods, known := f.AvailableMethods.Value(); known {
		available := map[ConcernID]bool{}
		recognized := map[ConcernID]bool{}
		for _, assessment := range r.Assessments {
			recognized[assessment.ID] = true
		}
		// Disaster recovery is only assessed once a disaster history exists,
		// but its method capability is declared at composition time, before
		// any facts are read; it must validate against empty facts too.
		recognized[RecoverDisasterServices] = true
		for _, id := range methods {
			if !recognized[id] || available[id] {
				return RoundsFindings{}, errors.New("invalid routine method capability " + string(id))
			}
			available[id] = true
		}
		for i := range r.Concerns {
			if r.Concerns[i].Priority >= 3 && !available[r.Concerns[i].ID] {
				r.Concerns[i].MethodUnavailable = true
			}
		}
		// Each upkeep need's method is its own serve family; an undeclared
		// one at emergency priority is recorded without the suspension.
		declarable := map[ConcernID]bool{}
		for _, n := range c.upkeep.Needs {
			declarable[n.Concern] = true
		}
		for i := range r.Assessments {
			if a := r.Assessments[i]; a.Priority < 2 && declarable[a.ID] && !available[a.ID] {
				r.Assessments[i].MethodUnavailable = true
			}
		}
	}
	all := r.Assessments
	r.Assessments = nil
	for _, a := range all {
		if IsIncidentKind(a.ID) {
			r.Incidents = append(r.Incidents, a)
		} else {
			r.Assessments = append(r.Assessments, a)
		}
	}
	return r, nil
}

// All is every assessment, goal needs then incident kinds.
func (r RoundsFindings) All() []RoundsAssessment {
	return slices.Concat(r.Assessments, r.Incidents)
}

// criticalMedicinePriority is 1 (an emergency finding)
// while any critical patient is urgent (policy.UrgentPatients) or the urgent
// count is unknown, and 2 while every patient is stable: resting under care,
// only needing a tend, or downed with nothing to tend. A stable patient is
// served by the same tend and rescue methods without classifying their
// condition as an emergency.
func criticalMedicinePriority(f RoundsFacts) int {
	patients, pk := f.CriticalPatients.Value()
	if !pk || patients == 0 {
		return 1
	}
	if positive(f.AllPatientsResting) {
		return 2
	}
	if urgent, known := f.UrgentPatients.Value(); known && urgent == 0 {
		return 2
	}
	return 1
}

// cookingMet is EnsureCooking's recovery: cooking ready and no cooking
// campfire owed its retirement.
func cookingMet(f RoundsFacts) domain.Fact[bool] {
	if positive(f.CampfireRetireOwed) {
		return domain.Known(false)
	}
	return f.Cooking
}

// ButcherBench is a standing butcher bench and the room it stands in
// (unknown outdoors).
type ButcherBench struct {
	ID         string
	Definition string
	Room       domain.Fact[string]
}

// ButcherTableWood is the WoodLog stock at which a stand-in butcher spot is
// owed its table: the table is stuff-built, so the goal stays open below it
// only to wait, never to starve the colony of wood.
const ButcherTableWood int64 = 120

// butcherSpotMet is MaintainButcherSpot's recovery: a butcher bench stands
// that does not share a room with a cooking bench. A butcher bench inside
// the kitchen keeps the colony fed but not clean, so the goal stays open for
// a separate table, wanted whatever the food runway because hunts and
// hides are processed on it.
func butcherSpotMet(f RoundsFacts) domain.Fact[bool] {
	benches, known := f.ButcherBenches.Value()
	if !known {
		return domain.Unknown[bool]()
	}
	if len(benches) == 0 {
		return domain.Known(false)
	}
	shared, known := KitchenSeparation(f.Upkeep.Rooms).Value()
	if !known {
		return domain.Known(true)
	}
	colocated := map[string]bool{}
	for _, room := range shared {
		colocated[room.ID] = true
	}
	for _, bench := range benches {
		if room, known := bench.Room.Value(); !known || !colocated[room] {
			return domain.Known(true)
		}
	}
	return domain.Known(false)
}
