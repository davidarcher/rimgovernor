package policy

import (
	"errors"
	"math"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

const (
	ConfirmColonyNames      GoalID = "ConfirmColonyNames"
	AnswerDialog            GoalID = "AnswerDialog"
	ActiveCombat            GoalID = "ActiveCombat"
	CriticalMedicine        GoalID = "CriticalMedical"
	RestoreWorkers          GoalID = "RestoreWorkers"
	AllowStartingSupplies   GoalID = "AllowStartingSupplies"
	ManageSupplySafety      GoalID = "ManageSupplySafety"
	EnsureWorkAssignments   GoalID = "EnsureWorkAssignments"
	EnsureFoodSupply        GoalID = "EnsureFoodSupply"
	MaintainHousing         GoalID = "MaintainHousing"
	EnsureTemperatureSafety GoalID = "EnsureTemperatureSafety"
	EnsureCooking           GoalID = "EnsureCooking"
	MaintainButcherSpot     GoalID = "MaintainButcherSpot"
	EnsureBasicPower        GoalID = "EnsureBasicPower"
	EnsureBasicDefense      GoalID = "EnsureBasicDefense"
	MaintainMedicalReserves GoalID = "MaintainMedicalReserves"
	MaintainFoodStorage     GoalID = "MaintainFoodStorage"
	MaintainRefrigeration   GoalID = "MaintainRefrigeration"
	EnsureComfort           GoalID = "EnsureComfort"
	ClearPests              GoalID = "ClearPests"
	MaintainEquipment       GoalID = "MaintainEquipment"
	EnsureResearch          GoalID = "EnsureResearch"
	MaintainResource        GoalID = "MaintainResource"
	EnsureDefensiveLayout   GoalID = "EnsureDefensiveLayout"
	TradeWithCaravan        GoalID = "TradeWithCaravan"
)

// foodStorageUpkeepPriority is MaintainFoodStorage's entry development
// priority, the same priority medicalReservePriority (a local var, not a
// const, at its own point of use below) starts MaintainMedicalReserves at.
const foodStorageUpkeepPriority = 3

// refrigerationPriority keeps MaintainRefrigeration out of the ranked
// development queue (see DetectRoutine's comment at its point of use).
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

type RoutinePolicy struct {
	AnimalUpkeep                                  AnimalUpkeepPolicy
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
	// HuntStallTicks bounds how long a dispatched Hunt-kind acquisition action
	// may sit unresolved before RoutineAcquisitionPlanner abandons it and lets
	// a fresh SelectAcquisition pass propose something else. Native's own
	// HuntingSafety.RouteSafe guard can repeatedly interrupt the shared game
	// clock while a hunter's route stays unsafe (issue #1); that guard stays
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
	// of waiting on one plant (#291: wild healroot pending 120k ticks).
	AcquisitionStallTicks int64
	// GoalStallTicks bounds how long a goal's progress record (#629) may go
	// without native evidence advancing its expected observable before
	// ExpireGoalProgress rotates the method (or keys the failed situation
	// out with a cooldown): GoalProgressContract's default contract and the
	// food ladder's rungs (FoodProgress) both use it as their deadline.
	// StageRoutinePolicy scales it down during StageFoothold
	// (StageGoalStallScale) so a stuck method rotates in a fraction of a
	// day rather than the full day this defaults to, while starvation risk
	// is highest; it returns to this value once the colony reaches
	// Reserves.
	GoalStallTicks int64
	// ResearchLadder is the ordered roadmap EnsureResearch walks when the workshop ladder records no need
	// (DefaultResearchLadder by default; empty disables the roadmap). Each
	// rung is reached through ResearchPrerequisiteQueue like a target, a
	// current native project is respected and recovers the goal, and the
	// planner lends the clock ticks while one is current so the rung
	// finishes on its own (issue #230).
	ResearchLadder []string
	// ResourceTargets is an operator-declared map of native resource
	// definition name to the native stock floor MaintainResource should keep
	// it above; an empty map disables the goal entirely. The deficit is
	// measured against RoutineFacts.Resources each review as the worst-covered
	// target's shortfall fraction. This only supports
	// policy.SelectResourceTarget's own single-goal dynamic-target selection
	// across these targets.
	ResourceTargets map[Resource]int64
	// GearSpareTargets optionally maintains unworn replacements by definition.
	// MaintainResource owns both its stockpile zone and standing production bill.
	GearSpareTargets map[Resource]int64
	// StoneBlockTarget is an operator-declared native stock floor for stone
	// blocks of whichever Core stone the map's chunk census counts most
	// (StoneBlockTarget): it joins ResourceTargets through
	// EffectiveResourceTargets each review and planner step, so the
	// MaintainResource ladder stages a stonecutter's table and keeps a
	// do-until bill fed from map chunks without the operator naming the
	// stone. Zero disables it.
	StoneBlockTarget int64
	// Trade is TradeWithCaravan's configuration (policy/trade_routine.go).
	Trade RoutineTradePolicy
	// PrisonerReleaseAfterDays is how long MaintainPopulation feeds a
	// prisoner it has no use for (not worth recruiting, not enslavable)
	// while the food runway holds FoodTargetDays before releasing it
	// (default 15 days); below the target it releases at once. See
	// PrisonerPolicy.
	PrisonerReleaseAfterDays float64
	// DefensiveLayout is an operator-declared opt-in for EnsureDefensiveLayout
	// (issue #5): the staged chokepoint/firing-line/funnel/trap-corridor
	// construction RoutineDefenseLayoutPlanner proposes from a fresh native
	// defense-site census. It is config-only: the review does not derive layout completeness from a
	// census, the planner decides per tier from its own admitted plans.
	DefensiveLayout bool
	// Stage holds the colony stage thresholds (#630); zero fields take the
	// defaults RoutinePolicy.Stages derives from the food thresholds.
	Stage ColonyStagePolicy
	// ColonyStage is the stage the last review left (StageRoutinePolicy):
	// DetectRoutine raises only the goals the stage allows
	// (StageGoalAllowed). DefaultRoutinePolicy stands at Development, so a
	// policy nobody staged raises every goal.
	ColonyStage ColonyStage `json:",omitempty"`
}

// DefaultRoutinePolicy admits development automatically (#655): slots
// bound planner cost only and distinct observed workers decide admission.
func DefaultRoutinePolicy() RoutinePolicy {
	return RoutinePolicy{AnimalUpkeep: DefaultAnimalUpkeepPolicy(), MedicalReserve: DefaultMedicalReservePolicy(), FoodStorage: DefaultFoodStoragePolicy(), Cleanliness: DefaultCleanlinessPolicy(), Lighting: DefaultLightingPolicy(), Flooring: DefaultFlooringPolicy(), Routes: DefaultRoutesPolicy(), FoodMinDays: 3, FoodTargetDays: 7, FootholdFoodDays: 3, FoodReserveDays: DefaultFoodReserveDays, PrisonerReleaseAfterDays: 15,
		ColdEnter: 12, ColdExit: 16, HotExit: 28, HotEnter: 32, WoodMin: 120, WoodTarget: 350, WoodMax: 500, HuntStallTicks: 30000, AcquisitionStallTicks: 60000, GoalStallTicks: int64(DevelopmentStallTicks), ResearchLadder: DefaultResearchLadder(), ColonyStage: StageDevelopment}
}

func (p RoutinePolicy) Validate() error {
	if !foodNumber(p.AnimalUpkeep.FeedMinimumDays) || !foodNumber(p.AnimalUpkeep.FeedTargetDays) || p.AnimalUpkeep.FeedTargetDays <= p.AnimalUpkeep.FeedMinimumDays {
		return errors.New("invalid animal feed thresholds")
	}
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
	if p.GoalStallTicks <= 0 {
		return errors.New("invalid goal stall grace")
	}
	if err := p.Trade.Validate(); err != nil {
		return err
	}
	for _, rung := range p.ResearchLadder {
		if !validResource(Resource(rung)) {
			return errors.New("invalid research ladder rung")
		}
	}
	if err := ValidateResourceTargets(p.ResourceTargets); err != nil {
		return err
	}
	if err := ValidateResourceTargets(p.GearSpareTargets); err != nil {
		return err
	}
	if p.StoneBlockTarget < 0 || p.StoneBlockTarget > 10000 {
		return errors.New("invalid stone block target")
	}
	return nil
}

// Prisoners is the MaintainPopulation slice of this policy.
func (p RoutinePolicy) Prisoners() PrisonerPolicy {
	return PrisonerPolicy{ReleaseAfterDays: p.PrisonerReleaseAfterDays, FoodTargetDays: p.FoodTargetDays}
}

// ValidateResourceTargets checks every configured MaintainResource target:
// a valid native resource definition name with a positive target within the
// same StockTarget production bill bound (see domain.NewProductionBill).
func ValidateResourceTargets(targets map[Resource]int64) error {
	for resource, target := range targets {
		if !validResource(resource) || target <= 0 || target > 10000 {
			return errors.New("invalid resource target")
		}
	}
	return nil
}

// RoutineFacts holds derived native facts, not forecasts masquerading as output.
// FoodDays is the accessible diet/rot-aware stock runway. FieldCoverage is the
// separate native crop-capacity forecast; it never increases FoodDays.
type RoutineFacts struct {
	// VetRoom is the layout's vet room; unread (the zero value) until
	// the layout exposes it, which keeps sterilize off.
	VetRoom     VetRoom
	FoodPlan    domain.Fact[FoodPlan]
	FoodReserve domain.Fact[FoodReserveReview]
	// BabyFeeding is the babies' food review (#1681); unknown without
	// Biotech baby care or consumer facts.
	BabyFeeding          domain.Fact[BabyFeeding]
	TradeMealIngredients domain.Fact[[]FoodIngredientSlot]
	PenGrazing           domain.Fact[[]PenGrazing]
	RecoverySafety       domain.Fact[RecoverySafety]
	RecoveryWorkers      domain.Fact[[]RecoveryWorker]
	DisasterConditions   domain.Fact[[]DisasterCondition]
	RecoveryBuildings    domain.Fact[[]RecoveryBuilding]
	Disaster             *DisasterHistory
	DisasterTick         domain.Tick
	MoodPawns            domain.Fact[[]MoodPawn]
	Mood                 MoodHistory
	HomeCoverage         domain.Fact[HomeCoverageObservation]
	StoneStructures      domain.Fact[[]StoneStructure]
	ConstructionClaims   domain.Fact[[]ConstructionClaim]
	CurrentConstruction  domain.Fact[CurrentConstruction]
	// ConstructionDeficit is the material standing blueprints and frames
	// are still owed, per resource.
	ConstructionDeficit domain.Fact[map[Resource]int64]
	// BillReservations are live bill jobs' promised ingredients, per
	// working pawn (MaterialBudget).
	BillReservations  domain.Fact[[]IngredientReservation]
	Sleeping          domain.Fact[SleepingObservation]
	SleepingRecovered domain.Fact[bool]
	// BedroomsOwed: a planned individual bedroom step is due (#786); it
	// keeps MaintainHousing open once everyone owns a barracks bed.
	BedroomsOwed domain.Fact[bool]
	// CorpsesOwed: a tomb (#832) or cremation (#833) step is due; it keeps
	// MaintainWaste open while a corpse waits on either.
	CorpsesOwed domain.Fact[bool]
	// TombsWarm: the warm tombs holding a colonist (#840, WarmTombs); they
	// join MaintainRefrigeration's rooms.
	TombsWarm domain.Fact[[]string]
	// MealClosetOwed: the planned meal closet waits to be shelled while its
	// dining room stands (#936); it keeps MaintainRefrigeration open.
	MealClosetOwed domain.Fact[bool]
	// CampfireRetireOwed: a cooking campfire stands in a sleeping room or a
	// stove kitchen supersedes it (#1179); it keeps EnsureCooking open.
	CampfireRetireOwed domain.Fact[bool]
	// TemperatureOwed: a heat campfire's auto-refuel should switch, or a
	// sleeping room sits unheated below its sleepers' comfort minimum
	// (TemperatureOwed, #1180/#1199); it holds EnsureTemperatureSafety open.
	TemperatureOwed domain.Fact[bool]
	// SculptureRoomsOwed: a bedroom below target, weakest in beauty, has a
	// free cell for a sculpture (SculptureRoomsOwed, #1190); with a
	// qualifying artist it holds MaintainArt open.
	SculptureRoomsOwed domain.Fact[bool]
	// SafeAreaOwed: the Safe allowed area differs from the enclosed roofed
	// rooms (PlanSafeArea, #1325); it holds MaintainShelter open. Unknown
	// unless the MaintainShelter method is composed.
	SafeAreaOwed domain.Fact[bool]
	// FirebreakOwed: the firebreak ring has a cut cell with a standing
	// plant or an undesignated wooden ruin (FirebreakOwed, #1548); it holds
	// MaintainFirebreak open. Unknown unless the method is composed.
	FirebreakOwed domain.Fact[bool]
	// MechGestationOwed: a mechanitor can afford the next mech, a gestator
	// is idle and no waste is uncleared (MechGestationOwed, #1686); it holds
	// MaintainMechs open. Unknown unless the method is composed.
	MechGestationOwed domain.Fact[bool]
	// HerdRoomsOwed: a barn or vet room step is due (NextHerdStep, #1633);
	// it holds MaintainAnimalContainment open after the pen stands.
	HerdRoomsOwed domain.Fact[bool]
	// PsylinkOwed: a willing colonist has no psylink and a psylink
	// neuroformer is held (PsylinkOwed, #1609); it holds MaintainPsylink
	// open. Unknown unless the method is composed.
	PsylinkOwed domain.Fact[bool]
	// RolesOwed: an active ideoligion role has a free place and a fitting
	// believer (RolesOwed, #1661); it holds MaintainIdeoRoles open.
	RolesOwed domain.Fact[bool]
	// RitualSites are the finished buildings the held rituals' patterns
	// require (observation, from the frame's building census); RitualPlans
	// the rituals to begin now (PlanRituals, #1660), whose attendees the
	// schedule planners hold off Sleep (HeldOffSleep); RitualsOwed holds
	// MaintainRituals open. Unknown unless the method is composed.
	RitualSites domain.Fact[[]RitualSite]
	RitualPlans domain.Fact[[]RitualPlan]
	RitualsOwed domain.Fact[bool]
	// ShelterArea is the Safe allowed area's native load id, "" when the
	// map has none (PlanSheltering, #1326).
	ShelterArea domain.Fact[string]
	// ShelterCombatants is the squad's draft set during a threat: the
	// colonists a raid or manhunter pack does not shelter. Unknown shelters
	// no colonist for a threat.
	ShelterCombatants domain.Fact[[]PawnID]
	// NoKillboxArea is the NoKillbox allowed area's native load id, ""
	// when the map has none; KillboxWindow whether haulers are kept out of
	// the killbox now (KillboxWindowOf); KillboxHaulers the pawns with
	// Hauling enabled (#1327).
	NoKillboxArea  domain.Fact[string]
	KillboxWindow  domain.Fact[bool]
	KillboxHaulers domain.Fact[[]PawnID]
	// SaleArt counts the packed art no owed room reserves (SaleSculptures);
	// read only while the wealth headroom is negative, it opens a trade as
	// the shed_art need (#1247).
	SaleArt domain.Fact[int64]
	// FabricableParts are the part items a usable gear bench has a researched
	// recipe for, read only while a medical pawn wants a part (#1255); the
	// caravan assessment counts only parts no bench can make.
	FabricableParts   map[Resource]bool
	AnimalUpkeep      AnimalUpkeepObservation
	FoodStorageUpkeep FoodStorageObservation
	MedicalReserve    MedicalReserveObservation
	// Prisoners carries Population-*'s recruit/maintain census: unlike
	// AnimalUpkeep, this has no generic per-tick colony read to piggyback on
	// (recruitable/current-interaction facts live only on the dedicated
	// rimgovernor/observations_read_population census), so it is populated by
	// a dedicated per-cycle RoutineSource read instead of ObserveColony's
	// always-present projection.
	Prisoners domain.Fact[[]PrisonerFacts]
	// PrisonerColony is the same read's colony side of each prisoner's
	// use: the free colonists' best skills and the Ideology facts.
	PrisonerColony domain.Fact[PrisonerColony]
	// Custody carries Population-*'s capture/rescue candidate census: every
	// observed humanlike from the same dedicated population read Prisoners
	// uses, broadened past prisoners alone so RoutinePopulationCustodyPlanner
	// can detect and select a downed hostile or unadmitted guest to dispatch.
	Custody domain.Fact[[]CustodyFacts]
	// Outlook is the same population read's storyteller outlook (#1031).
	Outlook PopulationOutlook
	// OwnedNames is the same read's owned-pawn short-name census (#1310).
	OwnedNames domain.Fact[[]OwnedName]
	// QuestOffers carries MaintainPopulation's joiner census: every visible
	// quest row (rimgovernor/observations_read_world_progression), read per
	// cycle by a RoutineSource offering RoutineQuestSource, for JoinerDeficit
	// to detect and SelectJoinerMethod to answer a joiner offer from.
	QuestOffers domain.Fact[[]JoinerOffer]
	// Royalty is the slow-refresh royalty read (#1599), read by a
	// RoutineSource offering RoutineRoyaltySource; unknown when the source
	// has none, the read failed or Royalty is not applicable.
	Royalty domain.Fact[RoyaltyFacts]
	// Ideology is the primary ideoligion (#1654) from the frame's ideology
	// section with the catalog's defs; unknown when the frame carries no
	// section (no Ideology, or no primary ideoligion).
	Ideology domain.Fact[Ideoligion]
	// TitleClaimQuests are the bestowing-ceremony quests the title claim
	// gate allows to accept now (ClaimQuests, #1605); the review fills it
	// once the plan, rooms and royalty read are known.
	TitleClaimQuests []domain.QuestID
	JoinerLetters    domain.Fact[[]JoinerLetterOffer]
	RaidPoints       domain.Fact[float64]
	// ShellsShort is the armory shell review (#1207): a built mortar's shell
	// stock below half its target puts MaintainEquipment in deficit.
	ShellsShort domain.Fact[bool]
	// DefenseCapacity is the colonists' and powered turrets' observed
	// combat strength in raid-point units (#1188, DefenseCapacity).
	DefenseCapacity domain.Fact[float64]
	// Waste carries MaintainWaste's exposed/eligible native item census (the
	// same WasteReply the generic per-tick colony read already carries), for
	// pendingWaste/WasteDeficit to detect and, eventually, SelectWasteMethod
	// to dispatch containment/burial candidates from.
	Waste domain.Fact[[]WasteItem]
	// Traders is the map trader census (bridge.ListTraders) TradeWithCaravan
	// needs; a source without the read leaves it unknown and the goal off.
	Traders domain.Fact[[]TraderFacts]
	// Blight carries RemoveBlight's blighted-plant census (the colony read's
	// blighted_plants section), for BlightDeficit to detect and
	// SelectBlightCuts to designate from.
	Blight domain.Fact[[]BlightedPlant]
	// Pollution carries ManagePollution's wastepack verdicts and the polluted
	// cells outside the clear area (the Biotech colony section, #1679); unknown
	// without Biotech or when the read failed, and then no assessment exists.
	Pollution domain.Fact[PollutionFacts]
	// MechChargerOwed is whether the colony owes one more mech charger
	// (MechChargerNeed, #1688); unknown without Biotech, mechs or chargers
	// read, and then EnsureMechCharger has no assessment.
	MechChargerOwed domain.Fact[bool]
	// LayoutTidy is the layout tidying review (#611) the reviewer measures
	// from the room census against each room's derived interior plan;
	// unknown without a tier.
	LayoutTidy domain.Fact[TidyReview]
	// Stockpiles is the MaintainStockpiles review (#725): this cycle's
	// stockpile edits within the haul budget, or why none stands.
	Stockpiles domain.Fact[StockpileReview]
	// AvailableMethods is supplied by the configured runtime, never native facts.
	AvailableMethods domain.Fact[[]GoalID]
	Upkeep           UpkeepObservation
	// ShrineHolds is each Upkeep.Shrines row's breach judgement (#458) as
	// the reviewer read it, journalled beside the review; empty while the
	// census is unknown.
	ShrineHolds  []ShrineHold
	UpkeepIssued map[GoalID]bool
	Gear         domain.Fact[GearObservation]
	Comfort      domain.Fact[ComfortObservation]
	// BasicComfort is the same census before the hosting-room filter: every
	// indoor seat at an eating surface and every recreation source, whatever
	// room (or none) hosts it. EnsureComfort's basic phase measures it; Comfort keeps
	// only facilities in rooms whose native role the facility catalog hosts.
	BasicComfort         domain.Fact[ComfortObservation]
	ComfortRecovered     domain.Fact[bool]
	ComfortDeficit       domain.Fact[float64]
	StartingSupplies     domain.Fact[[]StartingSupply]
	EventLoot            domain.Fact[[]LootItem]
	EventLootPending     domain.Fact[bool]
	LootReadiness        LootReadiness // the loot census's reach readiness (#522)
	MapBounds            domain.Fact[Bounds]
	MedicalPawns         domain.Fact[[]CarePawn]
	MedicalCareRecovered domain.Fact[bool]
	Workers              domain.Fact[int]
	// Labor is the per-work-type census of the same pawns Workers counts
	// (RoutineLabor); unknown labor leaves only the coarse worker bound.
	Labor domain.Fact[map[WorkType]int]
	// LaborUse is what those pawns are doing (RoutineLaborUse): the evidence
	// RankDevelopment releases an idle commitment's slot on.
	LaborUse domain.Fact[LaborUse]
	// WorkerCensus is the distinct-worker census of the same pawns
	// (DevelopmentCensus), matched in automatic development mode.
	WorkerCensus domain.Fact[[]DevelopmentWorker]
	// WorkRoster is the planner's per-work-type coverage (PlanWork): the
	// owners each type wanted and found and the pawns capable of it, so a
	// goal can name a missing capability instead of stalling.
	WorkRoster domain.Fact[[]WorkCoverage]
	// WorkDecaying is the same plan's skills above 10 that no assignment
	// exercises (WorkDecision.Decaying) and WorkProfiles every work pawn's
	// typed profile (Profiles); both are presentation facts the review
	// records for the dashboard dossier (#448), never planner inputs.
	WorkDecaying                                                               domain.Fact[[]DecayingSkill]
	WorkProfiles                                                               domain.Fact[[]PawnProfile]
	Colonists, HousingTarget, BedCapacity, IndoorCapacity, GrowingCells, Armed domain.Fact[int64]
	// Unarmed counts living, conscious colonists able to fight who hold no
	// weapon; EnsureBasicDefense stays owed while it is positive.
	Unarmed                 domain.Fact[int64]
	FoodDays, FieldCoverage domain.Fact[float64]
	// WorkHelp is the same plan's construction helper record (#653);
	// nil when the plan ran without the helper input.
	WorkHelp *ConstructionHelpRecord
	// Calendar is the tile's native growing calendar (policy.Calendar).
	// DetectRoutine widens the policy's food and wood targets by its
	// harvest gap (RoutinePolicy.Seasonal) before measuring any latch;
	// an unknown calendar keeps the configured flat targets.
	Calendar                                                    domain.Fact[Calendar]
	SleepingMin, SleepingMax, OutdoorTemperature, PowerHeadroom domain.Fact[float64]
	Wood                                                        domain.Fact[int64]
	// Dependencies are the live typed shortfall edges (#651) carried from the
	// last review: an open shortfall raises a MaintainResource floor for
	// the bounded difference (#711, #728).
	Dependencies []DevelopmentDependency
	// Resources is the generic reachable, unforbidden player item census
	// (the same colony facts rows Wood is taken from), so MaintainResource's
	// deficit is measured at review time instead of assumed from config.
	Resources          domain.Fact[[]Amount]
	ResourceSurfaceOre map[Resource]domain.Fact[int64]
	ResourceRunways    []ResourceRunway
	// Wealth is the colony wealth split (#395) TradeWithCaravan's
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
	// stock of, #205); ResourceGoalTargets merges them into the operator's
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
	// emergency, suspending every other goal, only while one exists or the
	// count is unknown; a colonist who merely needs tending, or is downed
	// with nothing to tend, keeps the goal active at priority 2 so the
	// colony's other work and the clock go on around the tend or rescue.
	UrgentPatients                                                    domain.Fact[int64]
	AllPatientsResting, ColonyNaming, CleanupPawns, ForbiddenSupplies domain.Fact[bool]
	// HostilityOwed is a colonist whose hostility response differs from
	// the one it should hold (#1299); EnsureWorkAssignments writes it.
	HostilityOwed domain.Fact[bool]
	// MedicalCareOwed is a pawn whose medical care differs from its cap
	// (#1301); Guests are the colony's guests' cap inputs.
	MedicalCareOwed domain.Fact[bool]
	Guests          domain.Fact[[]CarePatient]
	// MedicineCarryOwed is a colonist whose medicine carry count differs
	// from the planned one (#1307); EnsureWorkAssignments writes it.
	MedicineCarryOwed domain.Fact[bool]
	// SelfTendOwed is a colonist whose self-tend setting differs from the
	// one it should hold (#1305); EnsureWorkAssignments writes it.
	SelfTendOwed domain.Fact[bool]
	// NamesOwed is an owned pawn whose short name an older owned pawn
	// holds (#1310); EnsureWorkAssignments renames it.
	NamesOwed domain.Fact[bool]
	// ChoiceDialog is true while the game is force-paused by a choice dialog
	// it opened by itself (#156); AnswerDialog is the goal that answers it.
	ChoiceDialog domain.Fact[bool]
	// ButcherBenches are the standing butcher benches and the room each stands
	// in; MaintainButcherSpot is recovered once one stands outside the kitchen.
	ButcherBenches                                                       domain.Fact[[]ButcherBench]
	FoodStorage, Cooking, WorkCoverage, PowerRequired, DisabledConsumers domain.Fact[bool]
	PowerWeatherSafe                                                     domain.Fact[bool]
	ShortCircuitTick                                                     domain.Fact[domain.Tick]
}

// The foothold facts below are read live from one review's facts: the
// foothold goals open on them (DetectRoutine), and the colony stage
// (StageColonyFacts), the disaster services (DisasterServiceFacts) and the
// food ladder (FoodProgress) read the ones they need.

// footholdCount is the colonist count the foothold sizes for: the housing
// target when it is larger.
func footholdCount(f RoutineFacts) domain.Fact[int64] {
	count := f.Colonists
	if target, k := f.HousingTarget.Value(); k {
		if n, nk := count.Value(); nk {
			count = domain.Known(max(n, target))
		}
	}
	return count
}
func footholdSleeping(f RoutineFacts) domain.Fact[bool] {
	return countCapacity(f.BedCapacity, footholdCount(f), 1)
}
func footholdShelter(f RoutineFacts) domain.Fact[bool] {
	return countCapacity(f.IndoorCapacity, footholdCount(f), 1)
}
func footholdProduction(f RoutineFacts) domain.Fact[bool] {
	return countCapacity(f.GrowingCells, footholdCount(f), 10)
}
func footholdFood(f RoutineFacts, p RoutinePolicy) domain.Fact[bool] {
	return measured(f.FoodDays, func(v float64) bool { return v >= p.FootholdFoodDays })
}
func footholdTemperature(f RoutineFacts, p RoutinePolicy) domain.Fact[bool] {
	return allFacts(measured(f.SleepingMin, func(v float64) bool { return v >= p.ColdEnter }), measured(f.SleepingMax, func(v float64) bool { return v <= p.HotEnter }))
}
func footholdPower(f RoutineFacts) domain.Fact[bool] {
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
func footholdArmed(f RoutineFacts) domain.Fact[bool] {
	if n, k := footholdCount(f).Value(); k {
		return measured(f.Armed, func(v int64) bool { return v >= min(2, n) })
	}
	return domain.Unknown[bool]()
}

// DisasterServiceFacts are the survival services ReviewDisaster tracks,
// read live from the review's facts (infrastructure is its own).
func DisasterServiceFacts(f RoutineFacts, p RoutinePolicy) map[DisasterService]domain.Fact[bool] {
	return map[DisasterService]domain.Fact[bool]{
		DisasterFood: footholdFood(f, p), DisasterProduction: footholdProduction(f), DisasterSleeping: footholdSleeping(f),
		DisasterShelter: footholdShelter(f), DisasterTemperature: footholdTemperature(f, p), DisasterCooking: f.Cooking,
		DisasterPower: footholdPower(f), DisasterStorage: f.FoodStorage,
	}
}

type RoutineLatches struct {
	HomeCoverage, StoneShell bool
	Sleeping                 bool
	Animals                  AnimalUpkeepHistory
	MedicalReserve           bool
	FoodStorage              bool
	Refrigeration            bool
	// RefrigerationSince is the review tick the Refrigeration latch last
	// engaged, kept while it holds and zero when it is released: the
	// refrigeration planner lends native cooling time from it when the
	// goal's epoch has no cooler method of its own (#202).
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
	// research roadmap then walks the armor ladder (ArmorResearchLadder,
	// #470) and keeps walking it when the squad is later undrafted.
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
type RoutineNeeds struct {
	Disaster *DisasterHistory
	Latches  RoutineLatches
	Goals    []DevelopmentGoal
	// Assessments are the goal needs the review files goal rows for;
	// Incidents are the incident kinds' assessments (#1020, #1121).
	Assessments []RoutineAssessment
	Incidents   []RoutineAssessment
	// WoodFloor is the WoodLog stock floor the wood latch asks of
	// MaintainResource, 0 while the latch is off (#728).
	WoodFloor int64
	// ResourceTargets are the effective MaintainResource stock targets the
	// review held the census to (configured, derived and the wood floor);
	// the stock overlay (#825) tints stockpiles by them.
	ResourceTargets map[Resource]int64 `json:",omitempty"`
}

// Assessments cover recovered and unknown needs as well as actionable deficits.
// Absence from the scheduling list is never evidence of recovery.
type RoutineAssessment struct {
	ID GoalID
	// Subject is the pawn a per-pawn Response (EnsureMood) is assessed
	// for; empty otherwise. (ID, Subject) keys its incident (#1019).
	Subject  domain.PawnID `json:",omitempty"`
	Priority int
	Need     domain.NeedState
	// MethodUnavailable marks an emergency-tier upkeep need (a home fire)
	// whose serve family this runtime did not declare. The review still
	// records the need, but it must not suspend every other goal: with no
	// method to clear it and the clock held for it, nothing could ever
	// resume, which parked a power enclosure build behind an unfought
	// short-circuit fire (#435).
	MethodUnavailable bool
	// Hunt is the squad prey of an ActiveCombat deficit raised by the food
	// plan with no hostile standing (#1617): the incident's hunt origin.
	Hunt []domain.PawnID `json:",omitempty"`
}

// combatCleared is whether ActiveCombat has nothing to answer: no hostile
// stands and the food plan opens no squad hunt.
func combatCleared(f RoutineFacts) domain.Fact[bool] {
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

// DetectRoutine ports colony_policy.criteria/priority_nodes for the common
// survival goals. Family-specific needs join these same goals during review.
func DetectRoutine(f RoutineFacts, previous RoutineLatches, p RoutinePolicy) (RoutineNeeds, error) {
	if c, known := f.Calendar.Value(); known && !c.Valid() {
		return RoutineNeeds{}, errors.New("invalid calendar fact")
	}
	p = p.Seasonal(f.Calendar, f.DisasterConditions)
	owned, err := OwnedConstructions(f.ConstructionClaims, f.CurrentConstruction)
	if err != nil {
		return RoutineNeeds{}, err
	}
	home, err := PlanHomeArea(f.MapBounds, f.CurrentConstruction, f.ConstructionClaims, f.HomeCoverage)
	if err != nil {
		return RoutineNeeds{}, err
	}
	stone, err := ReviewStoneShell(owned, f.StoneStructures)
	if err != nil {
		return RoutineNeeds{}, err
	}
	animals, err := ReviewAnimalUpkeep(f.AnimalUpkeep, previous.Animals, p.AnimalUpkeep)
	if err != nil {
		return RoutineNeeds{}, err
	}
	medicineFacts := f.MedicalReserve
	medicineFacts.Colonists = f.Colonists
	medicine, err := ReviewMedicalReserve(medicineFacts, previous.MedicalReserve, p.MedicalReserve)
	if err != nil {
		return RoutineNeeds{}, err
	}
	foodStorage, err := ReviewFoodStorage(f.FoodStorageUpkeep, previous.FoodStorage, p.FoodStorage)
	if err != nil {
		return RoutineNeeds{}, err
	}
	refrigeration, err := ReviewRefrigeration(f.FoodStorageUpkeep, previous.Refrigeration, p.FoodStorage)
	if err != nil {
		return RoutineNeeds{}, err
	}
	refrigeration = refrigeration.WithTombs(f.TombsWarm)
	upkeep, err := ReviewUpkeepWith(f.Upkeep, previous.Upkeep, f.UpkeepIssued, p.Cleanliness)
	if err != nil {
		return RoutineNeeds{}, err
	}
	lighting, err := ReviewLighting(f.Upkeep.Lighting, previous.Lighting, p.Lighting, EclipseHold(f.DisasterConditions))
	if err != nil {
		return RoutineNeeds{}, err
	}
	flooring, err := ReviewFlooring(f.Upkeep.Flooring, f.Upkeep.Rooms, previous.Flooring, p.Flooring)
	if err != nil {
		return RoutineNeeds{}, err
	}
	routes, err := ReviewRoutes(f.Upkeep.Routes, previous.Routes, p.Routes)
	if err != nil {
		return RoutineNeeds{}, err
	}
	gear, err := ReviewGear(f.Gear)
	if err != nil {
		return RoutineNeeds{}, err
	}
	basicComfort, err := ReviewBasicComfort(f.BasicComfort)
	if err != nil {
		return RoutineNeeds{}, err
	}
	if v, known := f.ComfortDeficit.Value(); known && (math.IsNaN(v) || math.IsInf(v, 0) || v < 0 || v > 1) {
		return RoutineNeeds{}, errors.New("invalid comfort deficit")
	}
	if err := p.Validate(); err != nil {
		return RoutineNeeds{}, err
	}
	for _, fact := range []domain.Fact[int64]{f.Colonists, f.HousingTarget, f.BedCapacity, f.IndoorCapacity, f.GrowingCells, f.Armed, f.Unarmed, f.Wood, f.Hostiles, f.CriticalPatients, f.UrgentPatients} {
		if v, k := fact.Value(); k && (v < 0 || v > math.MaxInt64/10) {
			return RoutineNeeds{}, errors.New("invalid routine count")
		}
	}
	for _, fact := range []domain.Fact[float64]{f.FoodDays, f.FieldCoverage, f.SleepingMin, f.SleepingMax, f.OutdoorTemperature, f.PowerHeadroom} {
		if v, k := fact.Value(); k && (math.IsNaN(v) || math.IsInf(v, 0)) {
			return RoutineNeeds{}, errors.New("nonfinite routine fact")
		}
	}
	for _, fact := range []domain.Fact[float64]{f.FoodDays, f.FieldCoverage} {
		if v, k := fact.Value(); k && v < 0 {
			return RoutineNeeds{}, errors.New("negative food fact")
		}
	}
	count := footholdCount(f)
	sleepingMet, shelterMet, productionMet := footholdSleeping(f), footholdShelter(f), footholdProduction(f)
	foodMet, temperatureMet, powerMet := footholdFood(f, p), footholdTemperature(f, p), footholdPower(f)
	if positive(f.TemperatureOwed) {
		// A refuel switch or a room below its sleepers' band holds the
		// goal open (#1180, #1199).
		temperatureMet = domain.Known(false)
	}
	medicalMet := measured(f.CriticalPatients, func(v int64) bool { return v == 0 })
	workMet := allFacts(f.WorkCoverage, measured(f.CleanupPawns, func(v bool) bool { return !v }), measured(f.ColonyNaming, func(v bool) bool { return !v }))
	if positive(f.HostilityOwed) || positive(f.SelfTendOwed) || positive(f.NamesOwed) || positive(f.MedicineCarryOwed) || positive(f.MedicalCareOwed) {
		workMet = domain.Known(false)
	}
	defenseMet := allFacts(footholdArmed(f), measured(f.Hostiles, func(v int64) bool { return v == 0 }))
	wood := domain.Unknown[float64]()
	if n, k := f.Wood.Value(); k {
		wood = domain.Known(float64(n))
	}
	sleepingActive := previous.Sleeping
	if recovered, known := f.SleepingRecovered.Value(); known {
		sleepingActive = !recovered
	}
	homeActive, stoneActive := previous.HomeCoverage, previous.StoneShell
	if diff, known := home.Value(); known {
		homeActive = !diff.Empty()
	}
	if rows, known := stone.Value(); known {
		stoneActive = len(rows) > 0
	}
	l := RoutineLatches{
		HomeCoverage: homeActive, StoneShell: stoneActive,
		Sleeping:       sleepingActive,
		Animals:        animals.History,
		MedicalReserve: medicine.Active,
		FoodStorage:    foodStorage.Active,
		Refrigeration:  refrigeration.Active,
		Lighting:       lighting.Dark,
		Flooring:       flooring.Latched,
		Routes:         routes.Latched,
		Upkeep:         upkeep.History,
		Food:           latchValue(previous.Food, f.FoodDays, p.FoodMinDays, p.FoodTargetDays, false),
		Cold:           latchValue(previous.Cold, fallback(f.SleepingMin, f.OutdoorTemperature), p.ColdEnter, p.ColdExit, false),
		Hot:            latchValue(previous.Hot, fallback(f.SleepingMax, f.OutdoorTemperature), p.HotEnter, p.HotExit, true),
		Wood:           latchValue(previous.Wood, wood, float64(p.WoodMin), float64(p.WoodTarget), false),
		Soldiers:       previous.Soldiers || GearSoldierPresent(f.Gear),
	}
	r := RoutineNeeds{Latches: l}
	addGoal := func(id GoalID, priority int) {
		r.Goals = append(r.Goals, DevelopmentGoal{ID: id, Source: AutopilotGoal, Priority: priority, Deficit: RoutineDevelopmentDeficit(id, f, p), Labor: GoalLabor(id), Risk: RoutineDevelopmentRisk(id, f, l)})
	}
	if positive(f.ColonyNaming) {
		addGoal(ConfirmColonyNames, 0)
	}
	if positive(f.ChoiceDialog) {
		addGoal(AnswerDialog, 0)
	}
	if !positive(combatCleared(f)) {
		addGoal(ActiveCombat, 0)
	}
	medicalPriority := criticalMedicinePriority(f)
	if !positive(medicalMet) {
		addGoal(CriticalMedicine, medicalPriority)
	}
	if positive(measured(f.Hostiles, func(n int64) bool { return n == 0 })) && positive(f.CleanupPawns) {
		addGoal(RestoreWorkers, 1)
	}
	if positive(f.EventLootPending) {
		addGoal(ManageSupplySafety, supplySafetyPriority(f))
	}
	if positive(f.ForbiddenSupplies) {
		addGoal(AllowStartingSupplies, 2)
	}
	if !positive(workMet) {
		addGoal(EnsureWorkAssignments, 2)
	}
	if HumanFoodPending(f.FoodPlan) || l.Food || !positive(foodMet) || !positive(productionMet) || !positive(measured(f.FieldCoverage, func(v float64) bool { return v >= 1-1e-9 })) {
		addGoal(EnsureFoodSupply, 2)
	}
	housing := reviewHousing(f, previous, p, sleepingActive)
	r.Latches.Housing = housing.Phase
	if housing.Phase != "" {
		addGoal(MaintainHousing, housing.Priority)
		r.Goals[len(r.Goals)-1].Deficit = housing.Deficit
		r.Goals[len(r.Goals)-1].Blocked = housing.Blocked
		if housing.Phase == HousingShelter {
			// The starter shelter is a foothold goal: no ranked labor, as
			// before the housing goals merged.
			r.Goals[len(r.Goals)-1].Labor, r.Goals[len(r.Goals)-1].Risk = nil, domain.Known(0.0)
		}
	}
	if l.Cold || l.Hot || !positive(temperatureMet) {
		addGoal(EnsureTemperatureSafety, 2)
	}
	if !positive(cookingMet(f)) {
		addGoal(EnsureCooking, 2)
	}
	if !positive(butcherSpotMet(f)) {
		addGoal(MaintainButcherSpot, 2)
	}
	// A solar flare with a known remaining duration switches every powered
	// building off for hours: the power deficit it measures is real but
	// answering it with a generator is not, so the goal stays open with no
	// method (it neither extends the startup hold nor is cancelled) until
	// the flare ends and the planner can tell an outage from a shortfall.
	flare := SolarFlareHold(f.DisasterConditions)
	if !positive(powerMet) {
		addGoal(EnsureBasicPower, 2)
		r.Goals[len(r.Goals)-1].MethodUnavailable = flare
	}
	defense := basicDefenseRecovered(defenseMet, f.Unarmed)
	if !positive(defense) {
		addGoal(EnsureBasicDefense, 3)
		n, k := count.Value()
		stock, sk := f.Armed.Value()
		if k && sk && n > 0 {
			deficit := max(0, float64(min(2, n)-stock)/float64(min(2, n)))
			if unarmed, uk := f.Unarmed.Value(); uk && unarmed > 0 && deficit == 0 {
				deficit = float64(unarmed) / float64(unarmed+stock)
			}
			r.Goals[len(r.Goals)-1].Deficit = domain.Known(deficit)
		}
	}
	// A table with a seat and a recreation source are provided with the
	// starter hut, at foothold priority: the two cheapest mood debuffs to
	// remove should not wait for the ranked comfort project (#232). While the
	// initial shelter is still owed there is no room to furnish, so the goal
	// holds no method and neither extends the startup hold nor competes.
	comfortRanked := p.ColonyStage >= StageDevelopment
	if !positive(basicComfort.Recovered()) {
		r.Latches.Comfort = ComfortBasic
		addGoal(EnsureComfort, basicComfort.Priority())
		r.Goals[len(r.Goals)-1].Deficit = basicComfort.Deficit()
		r.Goals[len(r.Goals)-1].MethodUnavailable = !positive(shelterMet) || !positive(sleepingMet) || basicComfort.Priority() == 3 && !basicComfort.VarietyKnown
	} else if comfortRanked && !positive(f.ComfortRecovered) {
		// The ranked phase: hosting rooms and proof of use, once the basic
		// facilities stand and the colony reached StageDevelopment.
		r.Latches.Comfort = ComfortRanked
		addGoal(EnsureComfort, 4)
		r.Goals[len(r.Goals)-1].Comfort = true
		r.Goals[len(r.Goals)-1].Deficit = f.ComfortDeficit
	}
	comfortPriority, comfortRecovered := 4, basicComfort.Recovered()
	if r.Latches.Comfort == ComfortBasic {
		comfortPriority = basicComfort.Priority()
	} else {
		// The ranked phase is assessed at every stage, as before the merge;
		// only its goal waits for StageDevelopment.
		comfortRecovered = allFacts(comfortRecovered, f.ComfortRecovered)
	}
	// A recognised pest on the map (an alphabeaver pack eating the trees,
	// #247) is a foothold deficit answered by hunting, priority 2: it is
	// not an emergency (the census never holds the clock for a docile
	// animal) but it outranks every ranked project while it lasts. Only a
	// known census with a pest opens the goal: an unknown wild census
	// (beyond the native bound) has nothing to hunt and must not hold the
	// startup ladder for good.
	pests := PestCensus(f.AnimalUpkeep.WildAnimals)
	pestsClear := measured(pests, func(n int) bool { return n == 0 })
	if n, known := pests.Value(); known && n > 0 {
		addGoal(ClearPests, 2)
		r.Goals[len(r.Goals)-1].Deficit = domain.Known(1.0)
	}
	if !positive(gear.Recovered) {
		addGoal(MaintainEquipment, 3)
		r.Goals[len(r.Goals)-1].Deficit = gear.Deficit
	}
	addAssessment := func(id GoalID, priority int, recovered domain.Fact[bool]) {
		need := domain.NeedUnknown
		if value, known := recovered.Value(); known {
			need = domain.NeedDeficit
			if value {
				need = domain.NeedRecovered
			}
		}
		r.Assessments = append(r.Assessments, RoutineAssessment{ID: id, Priority: priority, Need: need})
	}
	not := func(f domain.Fact[bool]) domain.Fact[bool] { return measured(f, func(v bool) bool { return !v }) }
	// A retained latch with missing input preserves history, not fresh evidence.
	latchRecovered := func(active bool, observed domain.Fact[float64]) domain.Fact[bool] {
		return measured(observed, func(float64) bool { return !active })
	}
	addAssessment(ConfirmColonyNames, 0, not(f.ColonyNaming))
	addAssessment(AnswerDialog, 0, not(f.ChoiceDialog))
	addAssessment(ActiveCombat, 0, combatCleared(f))
	if cleared := measured(f.Hostiles, func(n int64) bool { return n == 0 }); positive(cleared) {
		r.Assessments[len(r.Assessments)-1].Hunt = HuntRequest(f.FoodPlan)
	}
	addAssessment(CriticalMedicine, medicalPriority, medicalMet)
	addAssessment(RestoreWorkers, 1, not(f.CleanupPawns))
	addAssessment(AllowStartingSupplies, 2, not(f.ForbiddenSupplies))
	addAssessment(ManageSupplySafety, supplySafetyPriority(f), not(f.EventLootPending))
	addAssessment(EnsureWorkAssignments, 2, workMet)
	addAssessment(EnsureFoodSupply, 2, allFacts(domain.Known(!HumanFoodPending(f.FoodPlan)), foodMet, productionMet, measured(f.FieldCoverage, func(v float64) bool { return v >= 1-1e-9 }), latchRecovered(l.Food, f.FoodDays)))
	addAssessment(MaintainHousing, housing.Priority, housing.Recovered)
	addAssessment(EnsureTemperatureSafety, 2, allFacts(temperatureMet, latchRecovered(l.Cold, fallback(f.SleepingMin, f.OutdoorTemperature)), latchRecovered(l.Hot, fallback(f.SleepingMax, f.OutdoorTemperature))))
	addAssessment(EnsureCooking, 2, cookingMet(f))
	addAssessment(MaintainButcherSpot, 2, butcherSpotMet(f))
	addAssessment(EnsureBasicPower, 2, powerMet)
	addAssessment(EnsureBasicDefense, 3, defense)
	addAssessment(EnsureComfort, comfortPriority, comfortRecovered)
	addAssessment(ClearPests, 2, pestsClear)
	equipped := gear.Recovered
	if short, _ := f.ShellsShort.Value(); short {
		equipped = domain.Known(false)
	}
	addAssessment(MaintainEquipment, 3, equipped)
	// EnsureResearch and MaintainResource are operator-configured targets whose
	// deficit is measured against native facts read in this review: no target
	// configured is certain recovery, a configured target with missing facts is
	// unknown, and RoutineResearchPlanner/RoutineResourcePlanner still re-read
	// native state immediately before proposing a method.
	researchNeeds := DeepDrillingResearch(f.ResearchNeeds, f.ResourceRunways)
	researchTarget, researchDerived := ResearchGoal(ArmorResearchPolicy(p, l.Soldiers), researchNeeds, f.Research)
	researchRecovered, researchDeficit := ResearchTargetNeed(researchTarget, researchDerived, f.Research)
	if !positive(researchRecovered) {
		addGoal(EnsureResearch, 4)
		r.Goals[len(r.Goals)-1].Deficit = researchDeficit
	}
	addAssessment(EnsureResearch, 4, researchRecovered)
	// The wood latch is a WoodLog floor on MaintainResource (#728): below
	// WoodMin it asks for WoodTarget until the latch recovers.
	r.WoodFloor = WoodFloor(l.Wood, p)
	resourceTargets, err := p.EffectiveResourceTargets(f.Resources, ResourceGoalTargets(ResourceGoalTargets(MedicineResourceNeeds(ResourceGoalTargets(f.ResourceNeeds, SocialDrugTargets(f.Research)), p.MedicineReserveTarget(f.Colonists, medicine.Active)), DependencyResourceNeeds(f.Dependencies)), WoodFloorNeeds(r.WoodFloor)))
	if err != nil {
		return RoutineNeeds{}, err
	}
	r.ResourceTargets = resourceTargets
	resourceRecovered, resourceDeficit := ResourceTargetNeed(resourceTargets, WoodStock(f.Resources, f.Wood, resourceTargets))
	for _, runway := range f.ResourceRunways {
		if _, known := runway.Deficit.Value(); !known && runway.WindowDays >= 1 && positive(resourceRecovered) {
			resourceRecovered = domain.Unknown[bool]()
			resourceDeficit = domain.Unknown[float64]()
		}
		if deficit, known := runway.Deficit.Value(); known && deficit {
			resourceRecovered = domain.Known(false)
			if days, known := runway.DaysLeft.Value(); known {
				old, _ := resourceDeficit.Value()
				resourceDeficit = domain.Known(max(old, 1-days/ResourceRunwayDays))
			}
		}
	}
	resourcePriority := 4
	if l.Wood {
		// Low wood keeps the old wood goal's standing (#728).
		resourcePriority = 3
	}
	if !positive(resourceRecovered) {
		addGoal(MaintainResource, resourcePriority)
		r.Goals[len(r.Goals)-1].Deficit = resourceDeficit
		// The ladder's research rung: while a project the workshop recorded
		// as gating the bench is unfinished, the goal has no method of its
		// own and holds no slot, so EnsureResearch can take one (#4 M4).
		// Wood and dependency floors are chopped or mined meanwhile.
		r.Goals[len(r.Goals)-1].MethodUnavailable = ResearchGoalTarget("", f.ResearchNeeds, f.Research) != "" && r.WoodFloor == 0 && len(DependencyResourceNeeds(f.Dependencies)) == 0
	}
	addAssessment(MaintainResource, resourcePriority, resourceRecovered)
	// EnsureDefensiveLayout is config-only like EnsureResearch above: opt-in
	// activates the goal at priority 3 (after the storage gate) and the
	// planner reports no work once every tier stands.
	// TradeWithCaravan is a Response (#1078): an incident per caravan
	// visit, never a development goal. It needs a negotiator's
	// conversation, not a development slot, and recovers by itself when
	// the caravan leaves or nothing is left worth trading.
	// Restore parts a bench could make do not stand the goal (#1255).
	tradeNeed := AnimalSaleNeed(ShedArtNeed(SurgeryTradeNeed(ReserveSurgeryStock(OrganSaleSurplus(ReviewTradeNeed(medicine, f.Resources, p.ResourceTargets, RoutineTradeFloors(p, nil), f.Wealth, p.Trade, RoutineTradeFood(f, p)), f.Resources, f.Colonists), f.MedicalPawns), TradeSurgeryParts(SurgeryParts(SelectSurgery(f.MedicalPawns, nil, SurgeryContext{}).Wants), f.FabricableParts)), f.WealthBudget(), f.SaleArt), f.SaleAnimals(), f.Silver(), f.Colonists)
	tradeRecovered := TradeRecovered(f.Traders, PopulationTradeNeed(tradeNeed, JoinerCapacity(f.JoinerCapacity())))
	addAssessment(TradeWithCaravan, 3, tradeRecovered)
	defensiveLayoutRecovered := domain.Known(!p.DefensiveLayout)
	if !positive(defensiveLayoutRecovered) {
		addGoal(EnsureDefensiveLayout, 3)
	}
	addAssessment(EnsureDefensiveLayout, 3, defensiveLayoutRecovered)
	for _, n := range upkeep.Needs {
		recovered := domain.Unknown[bool]()
		targetsKnown := false
		if _, known := n.Targets.Value(); known {
			recovered = domain.Known(!n.Active)
			targetsKnown = true
		}
		addAssessment(n.Goal, n.Priority, recovered)
		if !positive(recovered) {
			addGoal(n.Goal, n.Priority)
			// addGoal's Deficit defaults to RoutineDevelopmentDeficit(n.Goal, ...),
			// which only covers a few measured goals
			// and otherwise reports Unknown -- leaving every upkeep.Needs-sourced
			// goal (Fire/Supplies/Repairs/Cleaning/Storage) permanently
			// DevelopmentUnknown in RankDevelopment, so it could never win a
			// capacity slot. These needs are binary (recovered/deficit, not a
			// partial fraction -- see UpkeepNeed.Active/Targets above), so a
			// confirmed active deficit reports the full Known(1.0), matching
			// development_test.go's own fixture for this exact shape.
			if targetsKnown {
				r.Goals[len(r.Goals)-1].Deficit = domain.Known(1.0)
			}
			// SecureSupplies, MaintainEssentialRepairs, MaintainCleanFacilities
			// and MaintainStorage now each have a composed dispatch method
			// (G01.07c 05.4, G01.07b 05.2); the rest of the direct upkeep
			// orders remain visible-only until their own dispatch verticals
			// land.
			if n.Goal != SecureSupplies && n.Goal != MaintainEssentialRepairs && n.Goal != MaintainCleanFacilities && n.Goal != MaintainStorage && n.Goal != ClearHomeObstructions && n.Goal != ClearAncientShrine {
				r.Goals[len(r.Goals)-1].MethodUnavailable = true
			}
		}
	}
	// Clearance ranks below repairs and above direct cleaning. Safety goals
	// already suspend all development work through the shared emergency gate.
	// The shrine breach (#458) ranks with clearance below repairs; while its
	// breach is issued, obstruction clearance waits so the construction hand
	// is the breacher, not a wanderer past the trap line.
	// Repairs hold the shrine only when their method is served: a repair
	// deficit nothing can serve must not hold the breach forever.
	repairsHold := upkeep.History.Repairs
	if methods, known := f.AvailableMethods.Value(); known && repairsHold {
		repairsHold = slices.Contains(methods, MaintainEssentialRepairs)
	}
	for i := range r.Goals {
		switch r.Goals[i].ID {
		case ClearAncientShrine:
			r.Goals[i].MethodUnavailable = r.Goals[i].MethodUnavailable || repairsHold
		case ClearHomeObstructions:
			r.Goals[i].MethodUnavailable = r.Goals[i].MethodUnavailable || upkeep.History.Repairs || f.UpkeepIssued[ClearAncientShrine]
		case MaintainCleanFacilities:
			r.Goals[i].MethodUnavailable = r.Goals[i].MethodUnavailable || upkeep.History.Clearance
		}
	}
	homeRecovered, stoneRecovered := domain.Unknown[bool](), domain.Unknown[bool]()
	if diff, known := home.Value(); known {
		homeRecovered = domain.Known(diff.Empty())
	}
	if rows, known := stone.Value(); known {
		stoneRecovered = domain.Known(len(rows) == 0)
	}
	for _, facility := range []struct {
		id        GoalID
		recovered domain.Fact[bool]
		active    bool
		priority  int
	}{{MaintainHomeCoverage, homeRecovered, homeActive, 3}, {MaintainStoneShell, stoneRecovered, stoneActive, 4}} {
		recovered, priority := facility.recovered, facility.priority
		if _, known := recovered.Value(); !known && !facility.active && !f.UpkeepIssued[facility.id] {
			priority = 4
		}
		if f.UpkeepIssued[facility.id] {
			recovered = domain.Known(false)
		}
		addAssessment(facility.id, priority, recovered)
		if !positive(recovered) {
			addGoal(facility.id, priority)
			// Binary need, like the upkeep.Needs goals above: a confirmed
			// deficit ranks at Known(1.0); an unknown census stays
			// DevelopmentUnknown. Method availability follows the composed
			// capability list (AvailableMethods below), since both the
			// MaintainHomeCoverage and MaintainStoneShell verticals dispatch.
			if _, known := recovered.Value(); known {
				r.Goals[len(r.Goals)-1].Deficit = domain.Known(1.0)
			}
		}
	}
	// A plan issued for the care phase is not a medicine bill.
	medicalReserveActive := medicine.Active || f.UpkeepIssued[MaintainMedicalReserves] && previous.Medical != MedicalCare
	medicalReserveRecovered := domain.Unknown[bool]()
	medicalReservePriority := 3
	if _, known := medicine.Stock.Value(); known {
		medicalReserveRecovered = domain.Known(!medicalReserveActive)
	} else if !medicalReserveActive {
		medicalReservePriority = 4
	}
	// MaintainMedicalReserves is the one medical upkeep goal: resting and
	// hospital care for the sick first (from StageStable), then the medicine
	// stock. The care phase keeps its pre-merge shape: priority 2, no ranked
	// method and no labor, since work assignments own disease rest and
	// monitoring recovery must not reserve execution capacity while the pawn
	// rests.
	careRaised := p.ColonyStage >= StageStable
	medicalUpkeepPriority, medicalRecovered := medicalReservePriority, medicalReserveRecovered
	if careRaised {
		medicalRecovered = allFacts(f.MedicalCareRecovered, medicalReserveRecovered)
	}
	if careRaised && !positive(f.MedicalCareRecovered) {
		r.Latches.Medical, medicalUpkeepPriority = MedicalCare, 2
		addGoal(MaintainMedicalReserves, 2)
		r.Goals[len(r.Goals)-1].MethodUnavailable = true
		r.Goals[len(r.Goals)-1].Labor = nil
	} else if !positive(medicalReserveRecovered) {
		r.Latches.Medical = MedicalReserves
		addGoal(MaintainMedicalReserves, medicalReservePriority)
	}
	addAssessment(MaintainMedicalReserves, medicalUpkeepPriority, medicalRecovered)
	// MaintainSurgery (#1164): an operation the planner serves stands on a
	// living colonist until the health change removes it.
	// An actionable elective upgrade (#1167) keeps it open too.
	surgeryRecovered := allFacts(SurgeryRecovered(f.MedicalPawns), measured(ElectiveSurgeryOwed(f.MedicalPawns, HospitalBedReady(f.Sleeping)), func(owed bool) bool { return !owed }))
	// A sale organ harvest (#1169) holds it open while the silver runway
	// is short and a prisoner's organ clears its cost; a prisoner's
	// recoverable artificial part (#1232) too, and a peg-leg step: doctor
	// training below the Medicine floor, prisoner control or a reinstall
	// before release (#1236).
	// A prisoner whose care allows better than herbal (#1239) too.
	if SaleHarvestWanted(f, reviewSilverShort(f, p, medicine)) || PartRecoveryWanted(f) || PegCycleWanted(f, p.Prisoners()) {
		surgeryRecovered = domain.Known(false)
	}
	addAssessment(MaintainSurgery, surgeryPriority, surgeryRecovered)
	if !positive(surgeryRecovered) {
		addGoal(MaintainSurgery, surgeryPriority)
	}
	// MaintainBabyFeeding (#1681): owed while babies have no breastfeeder and
	// too little baby-edible food; unknown raises nothing.
	babyRecovered := measured(f.BabyFeeding, func(b BabyFeeding) bool { return !b.Short })
	addAssessment(MaintainBabyFeeding, babyFeedingPriority, babyRecovered)
	if recovered, known := babyRecovered.Value(); known && !recovered {
		addGoal(MaintainBabyFeeding, babyFeedingPriority)
	}
	reserve, reserveKnown := f.FoodReserve.Value()
	reserveAccess := reserveKnown && (len(reserve.Hold) > 0 || len(reserve.Release) > 0)
	reserveRefill := reserveKnown && !reserve.Emergency && reserve.DeficitNutrition > 0
	// MaintainFoodStorage is the one food storage goal: the foothold food
	// stockpile (the storage gate) first, then the larder, the reserve and
	// the stored-food upkeep.
	stockpileOwed := !positive(f.FoodStorage)
	foodStorageActive := foodStorage.Active || f.UpkeepIssued[MaintainFoodStorage] || reserveAccess || reserveRefill
	foodStorageRecovered := domain.Unknown[bool]()
	foodStoragePriority := foodStorageUpkeepPriority
	larder, _ := SelectCorpseLarder(f.FoodStorageUpkeep)
	// The stockpile, releasing cooking inputs and preserving fresh corpses
	// must not wait behind development projects, just as refrigeration
	// must not.
	if larder.Kind != "" || reserveAccess || stockpileOwed {
		foodStoragePriority = 2
	}
	if _, known := foodStorage.StoredNutrition.Value(); known {
		foodStorageRecovered = domain.Known(!foodStorageActive)
	} else if !foodStorageActive && !stockpileOwed {
		foodStoragePriority = 4
	}
	if reserveAccess || reserveRefill {
		foodStorageRecovered = domain.Known(false)
	}
	foodStorageRecovered = allFacts(f.FoodStorage, foodStorageRecovered)
	addAssessment(MaintainFoodStorage, foodStoragePriority, foodStorageRecovered)
	if !positive(foodStorageRecovered) {
		addGoal(MaintainFoodStorage, foodStoragePriority)
		r.Goals[len(r.Goals)-1].MethodUnavailable = !stockpileOwed && larder.Kind == "" && !reserveAccess && !reserveRefill
	}
	// Refrigeration answers the same at-risk perishable nutrition as
	// MaintainFoodStorage by cooling the room the food already sits in. It
	// runs at foothold priority like EnsureTemperatureSafety rather than as a
	// ranked development project: the review only latches on food inside
	// SafeRotDays of spoiling, and a cooler queued behind the project limit
	// arrives after the food is gone.
	refrigerationRecovered := domain.Unknown[bool]()
	if _, known := refrigeration.WarmNutrition.Value(); known {
		refrigerationRecovered = domain.Known(!refrigeration.Active)
	}
	closetOwed, _ := f.MealClosetOwed.Value()
	if closetOwed {
		refrigerationRecovered = domain.Known(false)
	}
	addAssessment(MaintainRefrigeration, refrigerationPriority, refrigerationRecovered)
	if !positive(refrigerationRecovered) {
		addGoal(MaintainRefrigeration, refrigerationPriority)
		// A cooler cannot run under a solar flare either (the cooler
		// planner reports solar_flare), but the goal keeps a method: the
		// warm stock is cooked ahead on a bench that still works (#408).
		if nutrition, known := refrigeration.WarmNutrition.Value(); known && p.FoodStorage.AtRiskNutritionThreshold > 0 {
			r.Goals[len(r.Goals)-1].Deficit = domain.Known(min(1, nutrition/p.FoodStorage.AtRiskNutritionThreshold))
		}
		if len(refrigeration.Tombs) > 0 {
			r.Goals[len(r.Goals)-1].Deficit = domain.Known(1.0)
		}
		if d, _ := r.Goals[len(r.Goals)-1].Deficit.Value(); closetOwed && d < 0.5 {
			r.Goals[len(r.Goals)-1].Deficit = domain.Known(0.5)
		}
	}
	// Lighting is a ranked development project: a dark bench costs work
	// speed and mood, not lives, so it competes for a project slot like the
	// other upkeep needs. The deficit is the measured dark fraction.
	lightingRecovered := domain.Unknown[bool]()
	lightingPriority := lightingPriority
	if lighting.Known {
		lightingRecovered = domain.Known(!lighting.Active)
	} else if !lighting.Active {
		lightingPriority = 4
	}
	addAssessment(MaintainLighting, lightingPriority, lightingRecovered)
	if !positive(lightingRecovered) {
		addGoal(MaintainLighting, lightingPriority)
		if lighting.Known {
			r.Goals[len(r.Goals)-1].Deficit = domain.Known(1.0)
		}
	}
	// Flooring is likewise a ranked project. A clean workspace on bare
	// ground ranks with lighting; living rooms alone rank one step lower.
	flooringRecovered := domain.Unknown[bool]()
	flooringPriority := flooringPriority
	if flooring.Known {
		flooringRecovered = domain.Known(!flooring.Active)
		if flooring.Active && flooring.Deficits[0].Tier != FloorTierClean {
			flooringPriority = min(4, flooringPriority+floorTierOrder[flooring.Deficits[0].Tier])
		}
	} else if !flooring.Active {
		flooringPriority = 4
	}
	addAssessment(MaintainFlooring, flooringPriority, flooringRecovered)
	if !positive(flooringRecovered) {
		addGoal(MaintainFlooring, flooringPriority)
		if flooring.Known {
			r.Goals[len(r.Goals)-1].Deficit = domain.Known(1.0)
		}
	}
	// Routes is likewise a ranked project: the deficit is the measured
	// fraction of facilities no colonist reaches.
	routesRecovered := domain.Unknown[bool]()
	routesPriority := routesPriority
	if routes.Known {
		routesRecovered = domain.Known(!routes.Active)
	} else if !routes.Active {
		routesPriority = 4
	}
	addAssessment(MaintainRoutes, routesPriority, routesRecovered)
	if !positive(routesRecovered) {
		addGoal(MaintainRoutes, routesPriority)
		if routes.Known {
			r.Goals[len(r.Goals)-1].Deficit = domain.Known(1.0)
		}
	}
	// Art is a ranked upkeep project too (#1190): owed only while a room
	// needs a sculpture and a qualifying artist exists; unknown raises
	// nothing.
	// An inspired artist holds it open without a room (#1192).
	artRecovered := domain.Unknown[bool]()
	// Sale demand (#1193) holds it open the same way while an artist exists.
	if profiles, pk := f.WorkProfiles.Value(); pk && (len(InspiredArtists(profiles)) > 0 || len(Artists(profiles)) > 0 && artForSale(f, p, medicine)) {
		artRecovered = domain.Known(false)
	} else if owed, known := f.SculptureRoomsOwed.Value(); known && !owed {
		artRecovered = domain.Known(true)
	} else if profiles, pk := f.WorkProfiles.Value(); known && pk {
		artRecovered = domain.Known(len(Artists(profiles)) == 0)
	}
	addAssessment(MaintainArt, artPriority, artRecovered)
	if recovered, known := artRecovered.Value(); known && !recovered {
		addGoal(MaintainArt, artPriority)
		r.Goals[len(r.Goals)-1].Deficit = domain.Known(1.0)
	}
	// MaintainShelter (#1325): a Safe area edit is owed. A settings write,
	// ranked with the upkeep projects; unknown raises nothing. While a
	// sheltering trigger holds it is ShelterPriority, so a threat's
	// emergency cannot veto the Safe area that PlanSheltering moves pawns
	// into.
	shelterPriority := 3
	if trigger, _ := ShelterTriggerOf(f); trigger != ShelterNone {
		shelterPriority = ShelterPriority(trigger)
	}
	addAssessment(MaintainShelter, shelterPriority, measured(f.SafeAreaOwed, func(owed bool) bool { return !owed }))
	if owed, known := f.SafeAreaOwed.Value(); known && owed {
		addGoal(MaintainShelter, shelterPriority)
		r.Goals[len(r.Goals)-1].Deficit = domain.Known(1.0)
	}
	// MaintainFirebreak (#1548): ring work is owed. Ranked with the upkeep
	// projects; unknown raises nothing.
	addAssessment(MaintainFirebreak, 3, measured(f.FirebreakOwed, func(owed bool) bool { return !owed }))
	if owed, known := f.FirebreakOwed.Value(); known && owed {
		addGoal(MaintainFirebreak, 3)
		r.Goals[len(r.Goals)-1].Deficit = domain.Known(1.0)
	}
	// MaintainMechs (#1686): a mech is owed inside the bandwidth. Ranked with
	// the upkeep projects; unknown raises nothing.
	addAssessment(MaintainMechs, mechPriority, measured(f.MechGestationOwed, func(owed bool) bool { return !owed }))
	if owed, known := f.MechGestationOwed.Value(); known && owed {
		addGoal(MaintainMechs, mechPriority)
		r.Goals[len(r.Goals)-1].Deficit = domain.Known(1.0)
	}
	// MaintainPsylink (#1609): a held neuroformer waits for a willing
	// colonist. Ranked with the upkeep projects; unknown raises nothing.
	addAssessment(MaintainPsylink, 3, measured(f.PsylinkOwed, func(owed bool) bool { return !owed }))
	if owed, known := f.PsylinkOwed.Value(); known && owed {
		addGoal(MaintainPsylink, 3)
		r.Goals[len(r.Goals)-1].Deficit = domain.Known(1.0)
	}
	// MaintainIdeoRoles (#1661): a role place and a fitting believer.
	addAssessment(MaintainIdeoRoles, 3, measured(f.RolesOwed, func(owed bool) bool { return !owed }))
	if owed, known := f.RolesOwed.Value(); known && owed {
		addGoal(MaintainIdeoRoles, 3)
		r.Goals[len(r.Goals)-1].Deficit = domain.Known(1.0)
	}
	// MaintainRituals (#1660): a ritual is due, calm and ready to begin.
	addAssessment(MaintainRituals, 3, measured(f.RitualsOwed, func(owed bool) bool { return !owed }))
	if owed, known := f.RitualsOwed.Value(); known && owed {
		addGoal(MaintainRituals, 3)
		r.Goals[len(r.Goals)-1].Deficit = domain.Known(1.0)
	}
	// MaintainPermits (#1606): a colonist holds permit points for a permit
	// worth taking. Unknown without the royalty read raises nothing.
	addAssessment(MaintainPermits, 3, PermitsSpent(f.Royalty))
	if _, owed := NextPermitOf(f.Royalty); owed {
		addGoal(MaintainPermits, 3)
		r.Goals[len(r.Goals)-1].Deficit = domain.Known(1.0)
	}
	animalContainment := domain.Unknown[bool]()
	if targets, known := animals.Containment.Value(); known {
		animalContainment = domain.Known(len(targets) == 0)
	}
	if owed, known := f.HerdRoomsOwed.Value(); known && owed {
		animalContainment = domain.Known(false)
	}
	animalFeed := domain.Unknown[bool]()
	if targets, known := animals.Feed.Value(); known {
		animalFeed = domain.Known(len(targets) == 0)
	}
	if need, known := HayNutritionNeed(f.PenGrazing, HarvestGapDays(f.Calendar, f.DisasterConditions)).Value(); known && need > 0 {
		animalFeed = domain.Known(false)
	}
	herd := f.HerdPolicy()
	herdRecovered := domain.Unknown[bool]()
	if deficit, known := AnimalHerdDeficit(f.AnimalUpkeep.Animals, f.AnimalUpkeep.WildAnimals, HerdFeedShort(animals), herd).Value(); known {
		herdRecovered = domain.Known(!deficit)
	}
	if choice := FoodSlaughterChoice(f.FoodPlan, f.AnimalUpkeep.Animals, herd); choice.Method == domain.HusbandrySlaughter {
		herdRecovered = domain.Known(false)
	}
	if choice := ReconcileHerdRemoval(f.AnimalUpkeep.Animals, herd, f.FoodPlan); choice.Method != "" {
		herdRecovered = domain.Known(false)
	} else if choice.Reason == HusbandryUnknown {
		herdRecovered = domain.Unknown[bool]()
	}
	// A standing designation with a capable handler is ordered to completion.
	if PrioritizeSlaughterChoice(f.AnimalUpkeep.Animals, f.WorkProfiles).Method != "" {
		herdRecovered = domain.Known(false)
	}
	if HerdMasterChoice(f.AnimalUpkeep.Animals, herd, f.WorkProfiles).Method != "" {
		herdRecovered = domain.Known(false)
	}
	if SterilizeChoice(f.AnimalUpkeep.Animals, herd, f.VetRoom).Method != "" {
		herdRecovered = domain.Known(false)
	}
	addAssessment(MaintainHerd, 3, herdRecovered)
	if !positive(herdRecovered) {
		addGoal(MaintainHerd, 3)
		r.Goals[len(r.Goals)-1].MethodUnavailable = true
	}
	// custodyDeficit is only known once a deployment reads the population
	// census broadened for custody (RoutinePopulationCustodyPlanner); an
	// unknown custody status is not held against recovery, matching every
	// other optional sub-step fact in this function -- only a known deficit,
	// in either prisoner recruitment or custody, blocks recovery.
	// joinerDeficit likewise needs the quest census (RoutinePopulationJoinerPlanner).
	populationRecovered := domain.Unknown[bool]()
	prisonerDeficit, prisonerDeficitKnown := PrisonerRecruitDeficit(f.Prisoners, f.PrisonerColony, f.FoodDays, p.Prisoners()).Value()
	custodyDeficit, custodyDeficitKnown := CustodyDeficit(f.Custody).Value()
	joinerDeficit, joinerDeficitKnown := JoinerDeficit(f.QuestOffers, JoinerCapacity(f.JoinerCapacity())).Value()
	empireDeficit, empireKnown := EmpireDeficit(f.QuestOffers, f.TitleClaimQuests...).Value()
	letterDeficit, letterKnown := JoinerLetterDeficit(f.JoinerLetters, JoinerCapacity(f.JoinerCapacity())).Value()
	_, ceremonyStarts := CeremonyStartOf(f.Royalty)
	switch {
	case ShrineArrestTarget(f) != "", LanceTarget(f) != "", prisonerDeficitKnown && prisonerDeficit, custodyDeficitKnown && custodyDeficit, joinerDeficitKnown && joinerDeficit, empireKnown && empireDeficit, letterKnown && letterDeficit, ceremonyStarts:
		populationRecovered = domain.Known(false)
	case prisonerDeficitKnown:
		populationRecovered = domain.Known(!prisonerDeficit)
	case custodyDeficitKnown:
		populationRecovered = domain.Known(!custodyDeficit)
	case joinerDeficitKnown:
		populationRecovered = domain.Known(!joinerDeficit)
	}
	addAssessment(MaintainPopulation, 3, populationRecovered)
	if !positive(populationRecovered) {
		addGoal(MaintainPopulation, 3)
		r.Goals[len(r.Goals)-1].MethodUnavailable = true
	}
	for _, animalNeed := range []struct {
		id        GoalID
		recovered domain.Fact[bool]
		active    bool
	}{
		{MaintainAnimalContainment, animalContainment, animals.History.Containment},
		{MaintainAnimalFeed, animalFeed, len(animals.History.Feed) > 0},
	} {
		recovered := animalNeed.recovered
		priority := 3
		if _, known := recovered.Value(); !known && !animalNeed.active && !f.UpkeepIssued[animalNeed.id] {
			priority = 4
		}
		if f.UpkeepIssued[animalNeed.id] {
			recovered = domain.Known(false)
		}
		addAssessment(animalNeed.id, priority, recovered)
		if !positive(recovered) {
			// Both animal needs have composed planners (RoutineAnimalContainment
			// Planner, RoutineAnimalFeedPlanner); availability is gated below
			// through AvailableMethods like MaintainWaste. Like waste, the
			// deficit is census-driven: any uncontained or unfed target is a
			// full deficit, so a known need ranks for a development slot.
			addGoal(animalNeed.id, priority)
			if _, known := recovered.Value(); known {
				r.Goals[len(r.Goals)-1].Deficit = domain.Known(1.0)
			}
		}
	}
	wasteRecovered := domain.Unknown[bool]()
	if items, known := f.Waste.Value(); known {
		wasteRecovered = domain.Known(len(pendingWaste(items)) == 0)
	}
	if owed, known := f.CorpsesOwed.Value(); known && owed {
		wasteRecovered = domain.Known(false)
	}
	addAssessment(MaintainWaste, 3, wasteRecovered)
	if !positive(wasteRecovered) {
		addGoal(MaintainWaste, 3)
		// MaintainWaste dispatches a GiveJobIntent HaulWaste (RoutineWastePlanner);
		// availability
		// is config-only, gated below through AvailableMethods like
		// MaintainResource/EnsureResearch.
	}
	blightRecovered := domain.Unknown[bool]()
	if deficit, known := BlightDeficit(f.Blight).Value(); known {
		blightRecovered = domain.Known(!deficit)
	}
	addAssessment(RemoveBlight, 3, blightRecovered)
	if !positive(blightRecovered) {
		// Census-driven like waste: any standing blighted plant is a full
		// deficit; availability is gated below through AvailableMethods.
		addGoal(RemoveBlight, 3)
	}
	// ManagePollution (#1683) exists only where the Biotech read is known, so
	// colonies without it keep their goal list unchanged.
	if _, biotech := f.Pollution.Value(); biotech {
		pollutionCleared := domain.Unknown[bool]()
		if deficit, known := PollutionDeficit(f.Pollution).Value(); known {
			pollutionCleared = domain.Known(!deficit)
		}
		addAssessment(ManagePollution, 3, pollutionCleared)
		if !positive(pollutionCleared) {
			addGoal(ManagePollution, 3)
		}
	}
	// EnsureMechCharger (#1688) exists only where the charger need is known.
	if owed, known := f.MechChargerOwed.Value(); known {
		addAssessment(EnsureMechCharger, 3, domain.Known(!owed))
		if owed {
			addGoal(EnsureMechCharger, 3)
		}
	}
	// TidyLayout (#611) is census-driven too: the layout review measures
	// off-plan furniture against each room's interior plan and stands a
	// proposal only while the colony is idle; a standing proposal is the
	// deficit. It ranks last (tidyPriority, tidyDeficit), and its
	// availability is gated below through AvailableMethods.
	tidyRecovered := domain.Unknown[bool]()
	if tidy, known := f.LayoutTidy.Value(); known && tidy.Known {
		tidyRecovered = domain.Known(!tidy.Active)
	}
	addAssessment(TidyLayout, tidyPriority, tidyRecovered)
	if !positive(tidyRecovered) {
		addGoal(TidyLayout, tidyPriority)
	}
	// MaintainStockpiles (#725): a standing stockpile edit is the deficit;
	// availability is gated below through AvailableMethods.
	stockpilesRecovered := domain.Unknown[bool]()
	if review, known := f.Stockpiles.Value(); known && review.Known {
		stockpilesRecovered = domain.Known(!review.Active)
	}
	addAssessment(MaintainStockpiles, stockpilePriority, stockpilesRecovered)
	if !positive(stockpilesRecovered) {
		addGoal(MaintainStockpiles, stockpilePriority)
	}
	if err := f.Mood.Validate(); err != nil {
		return RoutineNeeds{}, err
	}
	for _, state := range f.Mood.States {
		// A pawn's mood is an EnsureMood incident keyed by the pawn (#1078),
		// never a development goal. Its relief is optional and a mental
		// break ends only as ticks pass, so it never suspends other work.
		r.Assessments = append(r.Assessments, RoutineAssessment{ID: EnsureMood, Subject: domain.PawnID(state.Pawn.ID), Priority: state.Priority(), Need: state.Need(), MethodUnavailable: true})
	}
	// Dominant environment thought pressure raises the owning upkeep goal's
	// deficit to at least the fraction of pawns under it (#255): the goal's
	// own census still decides whether it is active and what it builds, so a
	// recovered owner is not re-raised, and the pawn's EnsureMood incident
	// defers to it (MoodProvision) instead of dispatching need relief.
	for i := range r.Goals {
		pressure, ok := MoodProvisionDeficits(f.Mood)[r.Goals[i].ID]
		if !ok {
			continue
		}
		if current, known := r.Goals[i].Deficit.Value(); !known || current < pressure {
			r.Goals[i].Deficit = domain.Known(pressure)
		}
	}
	r.Disaster, err = ReviewDisaster(f.DisasterConditions, f.RecoveryBuildings, DisasterServiceFacts(f, p), f.Disaster, f.DisasterTick, f.ShortCircuitTick)
	if err != nil {
		return RoutineNeeds{}, err
	}
	areaChanges := PlanSheltering(f)
	if _, safetyKnown := f.RecoverySafety.Value(); r.Disaster != nil || safetyKnown {
		need := RecoveryNeed(r.Disaster)
		if len(areaChanges) > 0 {
			need = domain.NeedDeficit
		}
		priority := r.Disaster.Promote(RecoverDisasterServices, 3)
		if len(areaChanges) > 0 {
			trigger, _ := ShelterTriggerOf(f)
			priority = ShelterPriority(trigger)
		}
		r.Assessments = append(r.Assessments, RoutineAssessment{ID: RecoverDisasterServices, Priority: priority, Need: need})
		for i := range r.Goals {
			r.Goals[i].Priority = r.Disaster.Promote(r.Goals[i].ID, r.Goals[i].Priority)
		}
		for i := range r.Assessments {
			r.Assessments[i].Priority = r.Disaster.Promote(r.Assessments[i].ID, r.Assessments[i].Priority)
		}
	}
	r.Goals = raisedAtStage(r.Goals, f, p, l)
	if methods, known := f.AvailableMethods.Value(); known {
		available := map[GoalID]bool{}
		recognized := map[GoalID]bool{}
		for _, assessment := range r.Assessments {
			recognized[assessment.ID] = true
		}
		// Disaster recovery is only assessed once a disaster history exists,
		// but its method capability is declared at composition time, before
		// any facts are read; it must validate against empty facts too.
		recognized[RecoverDisasterServices] = true
		// ManagePollution is assessed only on a colony whose Biotech read is
		// known (#1683); its capability is declared the same way.
		recognized[ManagePollution] = true
		recognized[EnsureMechCharger] = true
		for _, id := range methods {
			if !recognized[id] || available[id] {
				return RoutineNeeds{}, errors.New("invalid routine method capability " + string(id))
			}
			available[id] = true
		}
		for i := range r.Goals {
			if r.Goals[i].Priority >= 3 && !available[r.Goals[i].ID] {
				r.Goals[i].MethodUnavailable = true
			}
		}
		// Each upkeep need's method is its own serve family; an undeclared
		// one at emergency priority is recorded without the suspension.
		declarable := map[GoalID]bool{}
		for _, n := range upkeep.Needs {
			declarable[n.Goal] = true
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
func (r RoutineNeeds) All() []RoutineAssessment {
	return slices.Concat(r.Assessments, r.Incidents)
}

// criticalMedicinePriority is 1 (an emergency that suspends every other goal)
// while any critical patient is urgent (policy.UrgentPatients) or the urgent
// count is unknown, and 2 while every patient is stable: resting under care,
// only needing a tend, or downed with nothing to tend. A stable patient is
// served by the same tend and rescue methods; what the lower priority drops
// is the suspension that otherwise parked the colony and its clock behind a
// condition nobody could clear (#66, #304).
func criticalMedicinePriority(f RoutineFacts) int {
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

// raisedAtStage marks the goals the colony stage does not raise yet
// (StageGoalAllowed) as Staged: they stay in the ranking with a waiting
// reason instead of vanishing, and take no slot. The exceptions: the stage's exceptions: MaintainResource opens
// early for the wood floor or a construction dependency, MaintainRefrigeration
// for a full spoiling emergency, the stone shell waits for stone blocks
// (a known unfinished Stonecutting) and the animal goals for a tame animal (a census that knows of none
// raises none).
func raisedAtStage(goals []DevelopmentGoal, f RoutineFacts, p RoutinePolicy, l RoutineLatches) []DevelopmentGoal {
	stage := p.ColonyStage
	for i := range goals {
		g := goals[i]
		allowed := StageGoalAllowed(g.ID, stage)
		switch g.ID {
		case MaintainResource:
			allowed = allowed || l.Wood || len(DependencyResourceNeeds(f.Dependencies)) > 0
		case MaintainRefrigeration:
			d, known := g.Deficit.Value()
			allowed = allowed || known && d >= 1
		case MaintainStoneShell:
			allowed = allowed && !stonecuttingUnfinished(f.Research)
		case MaintainAnimalContainment, MaintainAnimalFeed, MaintainHerd:
			animals, known := f.AnimalUpkeep.Animals.Value()
			allowed = allowed && !(known && len(animals) == 0)
		}
		goals[i].Staged = !allowed
	}
	return goals
}

// stonecuttingUnfinished: the research census is known and has not
// finished Stonecutting, so no stone block can be cut for a shell.
func stonecuttingUnfinished(research domain.Fact[ResearchFacts]) bool {
	r, known := research.Value()
	return known && !slices.Contains(r.Finished, "Stonecutting")
}

// cookingMet is EnsureCooking's recovery: cooking ready and no cooking
// campfire owed its retirement (#1179).
func cookingMet(f RoutineFacts) domain.Fact[bool] {
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
// a separate spot; the free, instant spot is always wanted, whatever the
// food runway, because hunts and hides are processed on it.
func butcherSpotMet(f RoutineFacts) domain.Fact[bool] {
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
	apart, table, spot := false, false, false
	for _, bench := range benches {
		if room, known := bench.Room.Value(); !known || !colocated[room] {
			apart = true
			table = table || bench.Definition != "ButcherSpot"
			spot = spot || bench.Definition == "ButcherSpot"
		}
	}
	if !apart {
		return domain.Known(false)
	}
	// A table standing beside its stand-in spot owes the spot's removal.
	if table && spot {
		return domain.Known(false)
	}
	// Only a stand-in spot stands apart: with the wood to build one, the
	// goal stays open for the butcher table.
	if wood, ok := f.Wood.Value(); !table && ok && wood >= ButcherTableWood {
		return domain.Known(false)
	}
	return domain.Known(true)
}
