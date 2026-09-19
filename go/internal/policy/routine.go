package policy

import (
	"errors"
	"math"

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
	EnsureInitialShelter    GoalID = "EnsureInitialShelter"
	EnsureTemperatureSafety GoalID = "EnsureTemperatureSafety"
	EnsureCooking           GoalID = "EnsureCooking"
	EnsureBasicPower        GoalID = "EnsureBasicPower"
	EnsureFoodStorage       GoalID = "EnsureFoodStorage"
	EnsureBasicDefense      GoalID = "EnsureBasicDefense"
	MaintainWood            GoalID = "MaintainWood"
	MaintainMedicalCare     GoalID = "MaintainMedicalCare"
	MaintainMedicalReserves GoalID = "MaintainMedicalReserves"
	MaintainFoodStorage     GoalID = "MaintainFoodStorage"
	MaintainRefrigeration   GoalID = "MaintainRefrigeration"
	EnsureComfort           GoalID = "EnsureComfort"
	EnsureBasicComfort      GoalID = "EnsureBasicComfort"
	ClearPests              GoalID = "ClearPests"
	EnsureExpansion         GoalID = "EnsureExpansion"
	MaintainEquipment       GoalID = "MaintainEquipment"
	EnsureResearch          GoalID = "EnsureResearch"
	MaintainResource        GoalID = "MaintainResource"
	ProductionPolicy        GoalID = "ProductionPolicy"
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
	MaxDevelopmentProjects                        int
	FoodMinDays, FoodTargetDays, FootholdFoodDays float64
	ColdEnter, ColdExit, HotExit, HotEnter        float64
	WoodMin, WoodTarget, WoodMax                  int64
	// HuntStallTicks bounds how long a dispatched Hunt-kind acquisition action
	// may sit unresolved before RoutineAcquisitionPlanner abandons it and lets
	// a fresh SelectAcquisition pass propose something else. Native's own
	// HuntingSafety.RouteSafe guard can repeatedly interrupt the shared game
	// clock while a hunter's route stays unsafe (issue #1); that guard stays
	// fully authoritative, but without this grace the stuck action reads as
	// open work forever and blocks the planner from ever trying a different
	// prey or a non-hunt source.
	HuntStallTicks int64
	// HaulStallTicks bounds how long a proposed haul may stay held as
	// native_ineligible (no storage accepts the thing, no hauler can reach
	// it) before the planner cancels it so the attempt count advances toward
	// the covered-storage fallback instead of re-inspecting the same refusal
	// forever.
	HaulStallTicks int64
	// AcquisitionStallTicks bounds how long a dispatched plant-harvest
	// acquisition may stay designated with its effect pending (no colonist
	// has taken the designation) before the acquisition and medical
	// planners cancel it so the goal re-plans from another source instead
	// of waiting on one plant (#291: wild healroot pending 120k ticks).
	AcquisitionStallTicks int64
	// ResearchTarget is an operator-declared desired native ResearchProjectDef
	// name; empty disables EnsureResearch's routine dispatch. The need is
	// measured against RoutineFacts.Research each review (idle tab with the
	// target unfinished is a deficit; any current project or a finished
	// target is recovered). With no explicit target the review derives one
	// from RoutineFacts.ResearchNeeds, the projects the workshop ladder
	// recorded as gating a MaintainResource bench (issue #4 M4), else from
	// ResearchLadder.
	ResearchTarget string
	// ResearchLadder is the ordered roadmap EnsureResearch walks when no
	// ResearchTarget is set and the workshop ladder records no need
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
	// target's shortfall fraction. There is no plan-wide resource policy
	// (many simultaneously tracked floors driving both goal creation and the
	// native SetProductionPolicy push); this only supports
	// policy.SelectResourceTarget's own single-goal dynamic-target selection
	// across these targets and issues no SetProductionPolicy push at all.
	ResourceTargets map[Resource]int64
	// StoneBlockTarget is an operator-declared native stock floor for stone
	// blocks of whichever Core stone the map's chunk census counts most
	// (StoneBlockTarget): it joins ResourceTargets through
	// EffectiveResourceTargets each review and planner step, so the
	// MaintainResource ladder stages a stonecutter's table and keeps a
	// do-until bill fed from map chunks without the operator naming the
	// stone. Zero disables it.
	StoneBlockTarget int64
	// ResourceReserves and StoppedResources are operator-declared inputs to
	// ProductionFloors: the per-resource reserve/spending-stopped
	// configuration. Unlike
	// ResourceTargets (which drives MaintainResource's own goal/method
	// selection), these drive the ProductionPolicy goal's config-only
	// posture: RoutineProductionPolicyPlanner dispatches ProductionFloors's
	// computed floors/stopped rows through the native SetProductionPolicy
	// write whenever they diverge from a fresh ReadProductionPolicy. The push
	// is not development work and holds no development slot (DevelopmentExempt).
	ResourceReserves map[Resource]int64
	StoppedResources []Resource
	// AllowSlaughter is an operator-declared, explicit opt-in for
	// MaintainHerd to ever propose a slaughter write for a surplus animal;
	// it defaults to false (see DefaultRoutinePolicy), and slaughter is
	// never proposed unless an operator sets this true. Slaughter is a
	// destructive, irreversible in-game action, unlike every other
	// RoutinePolicy field -- this default must never change to true. See
	// MaintainHerd's doc comment for the full disclosed narrowing.
	AllowSlaughter bool
	// HerdPopulationMax is an operator-declared map of native animal
	// definition name (the same Resource-typed def name ResourceTargets
	// uses) to the population maximum MaintainHerd should keep that race at
	// or under; an empty map (the default) tracks no race at all, so
	// AllowSlaughter alone is not enough to dispatch a slaughter write --
	// both must be set. Only the maximum half of a per-race target is
	// supported (no minimum, protected-id set or breeding-reserve count),
	// narrowed the same way ResearchTarget's doc comment discloses its own gap.
	HerdPopulationMax map[Resource]int64
	// Trade is TradeWithCaravan's configuration (policy/trade_routine.go).
	Trade RoutineTradePolicy
	// AllowRelease is the operator opt-in for MaintainHerd to remove a
	// surplus animal by release-to-wild instead of slaughter. It defaults to
	// false; when both AllowRelease and AllowSlaughter are set, release is
	// preferred because it is non-lethal. Like slaughter it needs a
	// HerdPopulationMax entry for the race.
	AllowRelease bool
	// HerdPopulationMin is an operator-declared map of native animal
	// definition name to the population minimum MaintainHerd should keep
	// that race at or above by designating tameable wild animals of that
	// race for taming. Taming is otherwise never proposed. A race declared
	// in both maps must have minimum <= maximum.
	HerdPopulationMin map[Resource]int64
	// PrisonerReleaseAfterDays is the operator opt-in for MaintainPopulation
	// to release a prisoner the colony cannot turn (recruit resistance
	// unbroken, or never recruitable) once held that many days while the
	// food runway is below FoodTargetDays. Zero, the default, keeps the
	// recruit-only behaviour; see PrisonerPolicy.
	PrisonerReleaseAfterDays float64
	// DefensiveLayout is an operator-declared opt-in for EnsureDefensiveLayout
	// (issue #5): the staged chokepoint/firing-line/funnel/trap-corridor
	// construction RoutineDefenseLayoutPlanner proposes from a fresh native
	// defense-site census. It keeps the same config-only posture as
	// ResearchTarget: the review does not derive layout completeness from a
	// census, the planner decides per tier from its own admitted plans.
	DefensiveLayout bool
}

func DefaultRoutinePolicy() RoutinePolicy {
	return RoutinePolicy{AnimalUpkeep: DefaultAnimalUpkeepPolicy(), MedicalReserve: DefaultMedicalReservePolicy(), FoodStorage: DefaultFoodStoragePolicy(), Cleanliness: DefaultCleanlinessPolicy(), Lighting: DefaultLightingPolicy(), Flooring: DefaultFlooringPolicy(), Routes: DefaultRoutesPolicy(), MaxDevelopmentProjects: 2, FoodMinDays: 3, FoodTargetDays: 7, FootholdFoodDays: 3,
		ColdEnter: 12, ColdExit: 16, HotExit: 28, HotEnter: 32, WoodMin: 120, WoodTarget: 350, WoodMax: 500, HuntStallTicks: 6000, HaulStallTicks: 2500, AcquisitionStallTicks: 60000, ResearchLadder: DefaultResearchLadder()}
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
	if p.MaxDevelopmentProjects < 1 || p.MaxDevelopmentProjects > 8 {
		return errors.New("invalid development project limit")
	}
	for _, n := range []float64{p.FoodMinDays, p.FoodTargetDays, p.FootholdFoodDays, p.ColdEnter, p.ColdExit, p.HotExit, p.HotEnter, p.PrisonerReleaseAfterDays} {
		if math.IsNaN(n) || math.IsInf(n, 0) {
			return errors.New("nonfinite routine threshold")
		}
	}
	if p.FoodMinDays <= 0 || p.FoodTargetDays <= p.FoodMinDays || p.FootholdFoodDays <= 0 ||
		p.ColdEnter >= p.ColdExit || p.ColdExit >= p.HotExit || p.HotExit >= p.HotEnter ||
		p.WoodMin < 0 || p.WoodTarget <= p.WoodMin || p.WoodMax < p.WoodTarget {
		return errors.New("unordered routine thresholds")
	}
	if p.PrisonerReleaseAfterDays < 0 || p.PrisonerReleaseAfterDays > 120 {
		return errors.New("invalid prisoner release threshold")
	}
	if p.HuntStallTicks <= 0 {
		return errors.New("invalid hunt stall grace")
	}
	if p.HaulStallTicks <= 0 {
		return errors.New("invalid haul stall grace")
	}
	if p.AcquisitionStallTicks <= 0 {
		return errors.New("invalid acquisition stall grace")
	}
	if p.ResearchTarget != "" && !validResource(Resource(p.ResearchTarget)) {
		return errors.New("invalid research target")
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
	if p.StoneBlockTarget < 0 || p.StoneBlockTarget > 10000 {
		return errors.New("invalid stone block target")
	}
	if _, _, err := ProductionFloors(p.ResourceReserves, p.StoppedResources); err != nil {
		return err
	}
	if err := ValidateHerdPopulationMax(p.HerdPopulationMax); err != nil {
		return err
	}
	if err := ValidateHerdPopulationMax(p.HerdPopulationMin); err != nil {
		return err
	}
	for race, minimum := range p.HerdPopulationMin {
		if max, ok := p.HerdPopulationMax[race]; ok && minimum > max {
			return errors.New("herd population minimum exceeds maximum")
		}
	}
	return nil
}

// Herd is the MaintainHerd slice of this policy.
func (p RoutinePolicy) Herd() HerdPolicy {
	return HerdPolicy{AllowSlaughter: p.AllowSlaughter, AllowRelease: p.AllowRelease, PopulationMin: p.HerdPopulationMin, PopulationMax: p.HerdPopulationMax}
}

// Prisoners is the MaintainPopulation slice of this policy.
func (p RoutinePolicy) Prisoners() PrisonerPolicy {
	return PrisonerPolicy{ReleaseAfterDays: p.PrisonerReleaseAfterDays, FoodTargetDays: p.FoodTargetDays}
}

// ValidateHerdPopulationMax checks every configured MaintainHerd population
// maximum, the same shape and bound ValidateResourceTargets uses for
// MaintainResource's targets.
func ValidateHerdPopulationMax(populationMax map[Resource]int64) error {
	if len(populationMax) > 4096 {
		return errors.New("too many configured herd population maximums")
	}
	for race, max := range populationMax {
		if !validResource(race) || max <= 0 || max > 10000 {
			return errors.New("invalid herd population maximum")
		}
	}
	return nil
}

// ValidateResourceTargets checks every configured MaintainResource target:
// a valid native resource definition name with a positive target within the
// same StockTarget production bill bound (see domain.NewProductionBill).
func ValidateResourceTargets(targets map[Resource]int64) error {
	if len(targets) > 4096 {
		return errors.New("too many configured resource targets")
	}
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
	FoodPlan             domain.Fact[FoodPlan]
	TradeMealIngredients domain.Fact[[]FoodIngredientSlot]
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
	OwnedStockpiles      domain.Fact[[]OwnedStockpile]
	ConstructionClaims   domain.Fact[[]ConstructionClaim]
	CurrentConstruction  domain.Fact[CurrentConstruction]
	Sleeping             domain.Fact[SleepingObservation]
	SleepingRecovered    domain.Fact[bool]
	AnimalUpkeep         AnimalUpkeepObservation
	FoodStorageUpkeep    FoodStorageObservation
	MedicalReserve       MedicalReserveObservation
	// Prisoners carries Population-*'s recruit/maintain census: unlike
	// AnimalUpkeep, this has no generic per-tick colony read to piggyback on
	// (recruitable/current-interaction facts live only on the dedicated
	// rimgovernor/observations_read_population census), so it is populated by
	// a dedicated per-cycle RoutineSource read instead of ObserveColony's
	// always-present projection.
	Prisoners domain.Fact[[]PrisonerFacts]
	// Custody carries Population-*'s capture/rescue candidate census: every
	// observed humanlike from the same dedicated population read Prisoners
	// uses, broadened past prisoners alone so RoutinePopulationCustodyPlanner
	// can detect and select a downed hostile or unadmitted guest to dispatch.
	Custody domain.Fact[[]CustodyFacts]
	// QuestOffers carries MaintainPopulation's joiner census: every visible
	// quest row (rimgovernor/observations_read_world_progression), read per
	// cycle by a RoutineSource offering RoutineQuestSource, for JoinerDeficit
	// to detect and SelectJoinerMethod to answer a joiner offer from.
	QuestOffers   domain.Fact[[]JoinerOffer]
	JoinerLetters domain.Fact[[]JoinerLetterOffer]
	// PopulationCapacity is the player's declared PopulationPolicy (journal
	// evidence, not a native read): the maximum and food reserve a joiner
	// offer is admitted against. Unknown, or unset, answers no offer.
	PopulationCapacity domain.Fact[domain.PopulationPolicy]
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
	// AvailableMethods is supplied by the configured runtime, never native facts.
	AvailableMethods domain.Fact[[]GoalID]
	Upkeep           UpkeepObservation
	UpkeepIssued     map[GoalID]bool
	Gear             domain.Fact[GearObservation]
	Comfort          domain.Fact[ComfortObservation]
	// BasicComfort is the same census before the hosting-room filter: every
	// indoor seat at an eating surface and every recreation source, whatever
	// room (or none) hosts it. EnsureBasicComfort measures it; Comfort keeps
	// only facilities in rooms whose native role the facility catalog hosts.
	BasicComfort         domain.Fact[ComfortObservation]
	ComfortRecovered     domain.Fact[bool]
	ComfortDeficit       domain.Fact[float64]
	StartingSupplies     domain.Fact[[]StartingSupply]
	EventLoot            domain.Fact[[]LootItem]
	EventLootPending     domain.Fact[bool]
	MedicalPawns         domain.Fact[[]CarePawn]
	MedicalCareRecovered domain.Fact[bool]
	Workers              domain.Fact[int]
	// Labor is the per-work-type census of the same pawns Workers counts
	// (RoutineLabor); unknown labor leaves only the coarse worker bound.
	Labor domain.Fact[map[WorkType]int]
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
	FoodDays, PopulationFoodDays, FieldCoverage                                domain.Fact[float64]
	// Calendar is the tile's native growing calendar (policy.Calendar).
	// DetectRoutine widens the policy's food and wood targets by its
	// harvest gap (RoutinePolicy.Seasonal) before measuring any latch;
	// an unknown calendar keeps the configured flat targets.
	Calendar                                                    domain.Fact[Calendar]
	SleepingMin, SleepingMax, OutdoorTemperature, PowerHeadroom domain.Fact[float64]
	Wood                                                        domain.Fact[int64]
	// Resources is the generic reachable, unforbidden player item census
	// (the same colony facts rows Wood is taken from), so MaintainResource's
	// deficit is measured at review time instead of assumed from config.
	Resources domain.Fact[[]Amount]
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
	// ChoiceDialog is true while the game is force-paused by a choice dialog
	// it opened by itself (#156); AnswerDialog is the goal that answers it.
	ChoiceDialog                                                         domain.Fact[bool]
	FoodStorage, Cooking, WorkCoverage, PowerRequired, DisabledConsumers domain.Fact[bool]
	PowerWeatherSafe                                                     domain.Fact[bool]
	ShortCircuitTick                                                     domain.Fact[domain.Tick]
}

type FootholdGates struct {
	Sleeping, Shelter, Food, Production, Storage, Cooking, Temperature, Power, Medical, Defense, Work domain.Fact[bool]
}

func (g FootholdGates) Stable() bool {
	for _, v := range []domain.Fact[bool]{g.Sleeping, g.Shelter, g.Food, g.Production, g.Storage, g.Cooking, g.Temperature, g.Power, g.Medical, g.Defense, g.Work} {
		if !positive(v) {
			return false
		}
	}
	return true
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
}
type RoutineNeeds struct {
	Disaster    *DisasterHistory
	Gates       FootholdGates
	Latches     RoutineLatches
	Goals       []DevelopmentGoal
	Assessments []RoutineAssessment
}

// Assessments cover recovered and unknown needs as well as actionable deficits.
// Absence from the scheduling list is never evidence of recovery.
type RoutineAssessment struct {
	ID       GoalID
	Priority int
	Need     domain.NeedState
	// MethodUnavailable marks an emergency-tier upkeep need (a home fire)
	// whose serve family this runtime did not declare. The review still
	// records the need, but it must not suspend every other goal: with no
	// method to clear it and the clock held for it, nothing could ever
	// resume, which parked a power enclosure build behind an unfought
	// short-circuit fire (#435).
	MethodUnavailable bool
}

func positive(v domain.Fact[bool]) bool { b, k := v.Value(); return k && b }
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
	home, err := ReviewHomeCoverage(owned, f.OwnedStockpiles, f.HomeCoverage)
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
	for _, fact := range []domain.Fact[int64]{f.Colonists, f.HousingTarget, f.BedCapacity, f.IndoorCapacity, f.GrowingCells, f.Armed, f.Wood, f.Hostiles, f.CriticalPatients, f.UrgentPatients} {
		if v, k := fact.Value(); k && (v < 0 || v > math.MaxInt64/10) {
			return RoutineNeeds{}, errors.New("invalid routine count")
		}
	}
	for _, fact := range []domain.Fact[float64]{f.FoodDays, f.PopulationFoodDays, f.FieldCoverage, f.SleepingMin, f.SleepingMax, f.OutdoorTemperature, f.PowerHeadroom} {
		if v, k := fact.Value(); k && (math.IsNaN(v) || math.IsInf(v, 0)) {
			return RoutineNeeds{}, errors.New("nonfinite routine fact")
		}
	}
	for _, fact := range []domain.Fact[float64]{f.FoodDays, f.PopulationFoodDays, f.FieldCoverage} {
		if v, k := fact.Value(); k && v < 0 {
			return RoutineNeeds{}, errors.New("negative food fact")
		}
	}
	count := f.Colonists
	if target, k := f.HousingTarget.Value(); k {
		if n, nk := count.Value(); nk {
			count = domain.Known(max(n, target))
		}
	}
	g := FootholdGates{
		Sleeping: countCapacity(f.BedCapacity, count, 1), Shelter: countCapacity(f.IndoorCapacity, count, 1),
		Production: countCapacity(f.GrowingCells, count, 10), Storage: f.FoodStorage, Cooking: f.Cooking,
		Food:        measured(fallback(f.PopulationFoodDays, f.FoodDays), func(v float64) bool { return v >= p.FootholdFoodDays }),
		Temperature: allFacts(measured(f.SleepingMin, func(v float64) bool { return v >= p.ColdEnter }), measured(f.SleepingMax, func(v float64) bool { return v <= p.HotEnter })),
		Medical:     measured(f.CriticalPatients, func(v int64) bool { return v == 0 }),
		Work:        allFacts(f.WorkCoverage, measured(f.CleanupPawns, func(v bool) bool { return !v }), measured(f.ColonyNaming, func(v bool) bool { return !v })),
	}
	power := measured(f.PowerHeadroom, func(v float64) bool { return v >= 0 })
	if required, k := f.PowerRequired.Value(); k && !required {
		power = domain.Known(true)
	} else if !k {
		power = domain.Unknown[bool]()
	}
	g.Power = allFacts(power, measured(f.DisabledConsumers, func(v bool) bool { return !v }))
	if safe, known := f.PowerWeatherSafe.Value(); known && !safe {
		g.Power = domain.Known(false)
	}
	armed := domain.Unknown[bool]()
	if n, k := count.Value(); k {
		armed = measured(f.Armed, func(v int64) bool { return v >= min(2, n) })
	}
	g.Defense = allFacts(armed, measured(f.Hostiles, func(v int64) bool { return v == 0 }))
	wood := domain.Unknown[float64]()
	if n, k := f.Wood.Value(); k {
		wood = domain.Known(float64(n))
	}
	sleepingActive := previous.Sleeping
	if recovered, known := f.SleepingRecovered.Value(); known {
		sleepingActive = !recovered
	}
	homeActive, stoneActive := previous.HomeCoverage, previous.StoneShell
	if rows, known := home.Value(); known {
		homeActive = len(rows) > 0
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
	}
	r := RoutineNeeds{Gates: g, Latches: l}
	addGoal := func(id GoalID, priority int) {
		r.Goals = append(r.Goals, DevelopmentGoal{ID: id, Source: AutopilotGoal, Priority: priority, Deficit: RoutineDevelopmentDeficit(id, f, p), Labor: GoalLabor(id), Risk: RoutineDevelopmentRisk(id, f, l)})
	}
	if positive(f.ColonyNaming) {
		addGoal(ConfirmColonyNames, 0)
	}
	if positive(f.ChoiceDialog) {
		addGoal(AnswerDialog, 0)
	}
	if !positive(measured(f.Hostiles, func(n int64) bool { return n == 0 })) {
		addGoal(ActiveCombat, 0)
	}
	medicalPriority := criticalMedicinePriority(f)
	if !positive(g.Medical) {
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
	if !positive(g.Work) {
		addGoal(EnsureWorkAssignments, 2)
	}
	if l.Food || !positive(g.Food) || !positive(g.Production) || !positive(measured(f.FieldCoverage, func(v float64) bool { return v >= 1-1e-9 })) {
		addGoal(EnsureFoodSupply, 2)
	}
	if !positive(g.Shelter) || !positive(g.Sleeping) {
		addGoal(EnsureInitialShelter, 2)
	}
	if l.Cold || l.Hot || !positive(g.Temperature) {
		addGoal(EnsureTemperatureSafety, 2)
	}
	if !positive(g.Cooking) {
		addGoal(EnsureCooking, 2)
	}
	// A solar flare with a known remaining duration switches every powered
	// building off for hours: the power deficit it measures is real but
	// answering it with a generator is not, so the goal stays open with no
	// method (it neither extends the startup hold nor is cancelled) until
	// the flare ends and the planner can tell an outage from a shortfall.
	flare := SolarFlareHold(f.DisasterConditions)
	if !positive(g.Power) {
		addGoal(EnsureBasicPower, 2)
		r.Goals[len(r.Goals)-1].MethodUnavailable = flare
	}
	if !positive(g.Storage) {
		addGoal(EnsureFoodStorage, 2)
	}
	if !positive(g.Defense) {
		addGoal(EnsureBasicDefense, 3)
		n, k := count.Value()
		stock, sk := f.Armed.Value()
		if k && sk && n > 0 {
			r.Goals[len(r.Goals)-1].Deficit = domain.Known(max(0, float64(min(2, n)-stock)/float64(min(2, n))))
		}
	}
	if l.Wood {
		addGoal(MaintainWood, 3)
		if n, k := f.Wood.Value(); k {
			r.Goals[len(r.Goals)-1].Deficit = domain.Known(max(0, float64(p.WoodTarget-n)/float64(p.WoodTarget)))
		}
	}
	if !positive(f.MedicalCareRecovered) {
		addGoal(MaintainMedicalCare, 2)
		// This goal is monitoring only: its response (enabling Patient/
		// PatientBedRest priorities for resting patients) is already the
		// generic AssignWork default, and native AI rests/self-tends without
		// a dispatched order. Do not reserve execution capacity awaiting a
		// method this goal will never produce.
		r.Goals[len(r.Goals)-1].MethodUnavailable = true
	}
	// A table with a seat and a recreation source are provided with the
	// starter hut, at foothold priority: the two cheapest mood debuffs to
	// remove should not wait for the ranked comfort project (#232). While the
	// initial shelter is still owed there is no room to furnish, so the goal
	// holds no method and neither extends the startup hold nor competes.
	if !positive(basicComfort.Recovered()) {
		addGoal(EnsureBasicComfort, 2)
		r.Goals[len(r.Goals)-1].Deficit = basicComfort.Deficit()
		r.Goals[len(r.Goals)-1].MethodUnavailable = !positive(g.Shelter) || !positive(g.Sleeping)
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
	if !positive(f.ComfortRecovered) {
		addGoal(EnsureComfort, 4)
		r.Goals[len(r.Goals)-1].Comfort = true
		r.Goals[len(r.Goals)-1].Deficit = f.ComfortDeficit
	}
	expansion := domain.Unknown[bool]()
	if n, known := f.Colonists.Value(); known && n > 0 {
		expansion = measured(f.IndoorCapacity, func(capacity int64) bool { return capacity > n })
	}
	if !positive(expansion) {
		addGoal(EnsureExpansion, 4)
		r.Goals[len(r.Goals)-1].Deficit = RoutineDevelopmentDeficit(EnsureExpansion, f, p)
		r.Goals[len(r.Goals)-1].Blocked = !positive(g.Shelter) || !positive(g.Sleeping)
		if plan, known := f.FoodPlan.Value(); known && plan.GapPerDay > 0 {
			r.Goals[len(r.Goals)-1].Blocked = true
		}
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
	addAssessment(ActiveCombat, 0, measured(f.Hostiles, func(n int64) bool { return n == 0 }))
	addAssessment(CriticalMedicine, medicalPriority, g.Medical)
	addAssessment(RestoreWorkers, 1, not(f.CleanupPawns))
	addAssessment(AllowStartingSupplies, 2, not(f.ForbiddenSupplies))
	addAssessment(ManageSupplySafety, supplySafetyPriority(f), not(f.EventLootPending))
	addAssessment(EnsureWorkAssignments, 2, g.Work)
	addAssessment(EnsureFoodSupply, 2, allFacts(g.Food, g.Production, measured(f.FieldCoverage, func(v float64) bool { return v >= 1-1e-9 }), latchRecovered(l.Food, f.FoodDays)))
	addAssessment(EnsureInitialShelter, 2, allFacts(g.Shelter, g.Sleeping))
	addAssessment(EnsureTemperatureSafety, 2, allFacts(g.Temperature, latchRecovered(l.Cold, fallback(f.SleepingMin, f.OutdoorTemperature)), latchRecovered(l.Hot, fallback(f.SleepingMax, f.OutdoorTemperature))))
	addAssessment(EnsureCooking, 2, g.Cooking)
	addAssessment(EnsureBasicPower, 2, g.Power)
	addAssessment(EnsureFoodStorage, 2, g.Storage)
	addAssessment(EnsureBasicDefense, 3, g.Defense)
	addAssessment(MaintainWood, 3, latchRecovered(l.Wood, wood))
	addAssessment(MaintainMedicalCare, 2, f.MedicalCareRecovered)
	addAssessment(EnsureBasicComfort, 2, basicComfort.Recovered())
	addAssessment(ClearPests, 2, pestsClear)
	addAssessment(EnsureComfort, 4, f.ComfortRecovered)
	addAssessment(EnsureExpansion, 4, expansion)
	addAssessment(MaintainEquipment, 3, gear.Recovered)
	// EnsureResearch and MaintainResource are operator-configured targets whose
	// deficit is measured against native facts read in this review: no target
	// configured is certain recovery, a configured target with missing facts is
	// unknown, and RoutineResearchPlanner/RoutineResourcePlanner still re-read
	// native state immediately before proposing a method.
	researchTarget, researchDerived := ResearchGoal(p, f.ResearchNeeds, f.Research)
	researchRecovered, researchDeficit := ResearchTargetNeed(researchTarget, researchDerived, f.Research)
	if !positive(researchRecovered) {
		addGoal(EnsureResearch, 4)
		r.Goals[len(r.Goals)-1].Deficit = researchDeficit
	}
	addAssessment(EnsureResearch, 4, researchRecovered)
	resourceTargets, err := p.EffectiveResourceTargets(f.Resources, f.ResourceNeeds)
	if err != nil {
		return RoutineNeeds{}, err
	}
	resourceRecovered, resourceDeficit := ResourceTargetNeed(resourceTargets, f.Resources)
	if !positive(resourceRecovered) {
		addGoal(MaintainResource, 4)
		r.Goals[len(r.Goals)-1].Deficit = resourceDeficit
		// The ladder's research rung: while a project the workshop recorded
		// as gating the bench is unfinished, the goal has no method of its
		// own and holds no slot, so EnsureResearch can take one (#4 M4).
		r.Goals[len(r.Goals)-1].MethodUnavailable = ResearchGoalTarget("", f.ResearchNeeds, f.Research) != ""
	}
	addAssessment(MaintainResource, 4, resourceRecovered)
	// ProductionPolicy is a configuration push, not development work: it needs
	// no pawn labor and holds no optional capacity slot, so it is assessed (and
	// admitted) outside the development ranking. Its recovered state is
	// config-only: RoutineProductionPolicyPlanner performs its own fresh
	// ReadProductionPolicy comparison before proposing a method.
	productionPolicyRecovered := domain.Known(len(p.ResourceReserves) == 0 && len(p.StoppedResources) == 0)
	addAssessment(ProductionPolicy, 4, productionPolicyRecovered)
	// EnsureDefensiveLayout is config-only like EnsureResearch above: opt-in
	// activates the goal at priority 3 (after the storage gate) and the
	// planner reports no work once every tier stands.
	// TradeWithCaravan is config-only like ProductionPolicy: it needs a
	// negotiator's conversation, not a development slot, and recovers by
	// itself when the caravan leaves or nothing is left worth trading.
	tradeRecovered := TradeRecovered(f.Traders, ReviewTradeNeed(medicine, f.Resources, p.ResourceTargets, RoutineTradeFloors(p, nil), f.Wealth, p.Trade, RoutineTradeFood(f, p)))
	if !positive(tradeRecovered) {
		addGoal(TradeWithCaravan, 3)
		if _, known := tradeRecovered.Value(); known {
			r.Goals[len(r.Goals)-1].Deficit = domain.Known(1.0)
		}
	}
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
			// which only covers EnsureExpansion/MaintainWood/EnsureBasicDefense
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
			if n.Goal != SecureSupplies && n.Goal != MaintainEssentialRepairs && n.Goal != MaintainCleanFacilities && n.Goal != MaintainStorage && n.Goal != ClearHomeObstructions {
				r.Goals[len(r.Goals)-1].MethodUnavailable = true
			}
		}
	}
	// Clearance ranks below repairs and above direct cleaning. Safety goals
	// already suspend all development work through the shared emergency gate.
	for i := range r.Goals {
		if r.Goals[i].ID == ClearHomeObstructions && upkeep.History.Repairs || r.Goals[i].ID == MaintainCleanFacilities && upkeep.History.Clearance {
			r.Goals[i].MethodUnavailable = true
		}
	}
	homeRecovered, stoneRecovered := domain.Unknown[bool](), domain.Unknown[bool]()
	if rows, known := home.Value(); known {
		homeRecovered = domain.Known(len(rows) == 0)
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
	sleepingRecovered := f.SleepingRecovered
	sleepingPriority := 3
	if _, known := sleepingRecovered.Value(); !known && !sleepingActive && !f.UpkeepIssued[MaintainSleeping] {
		sleepingPriority = 4
	}
	if f.UpkeepIssued[MaintainSleeping] {
		sleepingRecovered = domain.Known(false)
	}
	addAssessment(MaintainSleeping, sleepingPriority, sleepingRecovered)
	if !positive(sleepingRecovered) {
		addGoal(MaintainSleeping, sleepingPriority)
		// Binary need: a confirmed deficit ranks at Known(1.0); an unknown
		// census stays DevelopmentUnknown. Method availability follows the
		// composed capability list (AvailableMethods below), since the
		// sleeping family dispatches bed construction and ownership.
		if _, known := sleepingRecovered.Value(); known {
			r.Goals[len(r.Goals)-1].Deficit = domain.Known(1.0)
		}
	}
	medicalReserveActive := medicine.Active || f.UpkeepIssued[MaintainMedicalReserves]
	medicalReserveRecovered := domain.Unknown[bool]()
	medicalReservePriority := 3
	if _, known := medicine.Stock.Value(); known {
		medicalReserveRecovered = domain.Known(!medicalReserveActive)
	} else if !medicalReserveActive {
		medicalReservePriority = 4
	}
	addAssessment(MaintainMedicalReserves, medicalReservePriority, medicalReserveRecovered)
	if !positive(medicalReserveRecovered) {
		addGoal(MaintainMedicalReserves, medicalReservePriority)
	}
	foodStorageActive := foodStorage.Active || f.UpkeepIssued[MaintainFoodStorage]
	foodStorageRecovered := domain.Unknown[bool]()
	foodStoragePriority := foodStorageUpkeepPriority
	larder, _ := SelectCorpseLarder(f.FoodStorageUpkeep)
	// Releasing cooking inputs and preserving fresh corpses must not wait
	// behind development projects, just as refrigeration must not.
	if larder.Kind != "" {
		foodStoragePriority = 2
	}
	if _, known := foodStorage.StoredNutrition.Value(); known {
		foodStorageRecovered = domain.Known(!foodStorageActive)
	} else if !foodStorageActive {
		foodStoragePriority = 4
	}
	addAssessment(MaintainFoodStorage, foodStoragePriority, foodStorageRecovered)
	if !positive(foodStorageRecovered) {
		addGoal(MaintainFoodStorage, foodStoragePriority)
		r.Goals[len(r.Goals)-1].MethodUnavailable = larder.Kind == ""
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
	addAssessment(MaintainRefrigeration, refrigerationPriority, refrigerationRecovered)
	if !positive(refrigerationRecovered) {
		addGoal(MaintainRefrigeration, refrigerationPriority)
		// A cooler cannot run under a solar flare either (the cooler
		// planner reports solar_flare), but the goal keeps a method: the
		// warm stock is cooked ahead on a bench that still works (#408).
		if nutrition, known := refrigeration.WarmNutrition.Value(); known && p.FoodStorage.AtRiskNutritionThreshold > 0 {
			r.Goals[len(r.Goals)-1].Deficit = domain.Known(min(1, nutrition/p.FoodStorage.AtRiskNutritionThreshold))
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
	animalContainment := domain.Unknown[bool]()
	if targets, known := animals.Containment.Value(); known {
		animalContainment = domain.Known(len(targets) == 0)
	}
	animalFeed := domain.Unknown[bool]()
	if targets, known := animals.Feed.Value(); known {
		animalFeed = domain.Known(len(targets) == 0)
	}
	herdRecovered := domain.Unknown[bool]()
	if deficit, known := AnimalHerdDeficit(f.AnimalUpkeep.Animals, f.AnimalUpkeep.WildAnimals, HerdFeedShort(animals), p.Herd()).Value(); known {
		herdRecovered = domain.Known(!deficit)
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
	prisonerDeficit, prisonerDeficitKnown := PrisonerRecruitDeficit(f.Prisoners, f.FoodDays, p.Prisoners()).Value()
	custodyDeficit, custodyDeficitKnown := CustodyDeficit(f.Custody).Value()
	joinerDeficit, joinerDeficitKnown := JoinerDeficit(f.QuestOffers, JoinerCapacity(f.JoinerCapacity())).Value()
	letterDeficit, letterKnown := JoinerLetterDeficit(f.JoinerLetters, JoinerCapacity(f.JoinerCapacity())).Value()
	switch {
	case prisonerDeficitKnown && prisonerDeficit, custodyDeficitKnown && custodyDeficit, joinerDeficitKnown && joinerDeficit, letterKnown && letterDeficit:
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
	addAssessment(MaintainWaste, 3, wasteRecovered)
	if !positive(wasteRecovered) {
		addGoal(MaintainWaste, 3)
		// MaintainWaste now has a composed dispatch method (WasteAction,
		// waste_admissions, WasteBoundary, RoutineWastePlanner); availability
		// is config-only, gated below through AvailableMethods like
		// MaintainResource/EnsureResearch/ProductionPolicy.
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
	if err := f.Mood.Validate(); err != nil {
		return RoutineNeeds{}, err
	}
	for _, state := range f.Mood.States {
		id, priority, need := MoodGoal(state.Pawn.ID), state.Priority(), state.Need()
		r.Assessments = append(r.Assessments, RoutineAssessment{ID: id, Priority: priority, Need: need})
		if state.Active {
			addGoal(id, priority)
			r.Goals[len(r.Goals)-1].MethodUnavailable = true
		}
	}
	// Dominant environment thought pressure raises the owning upkeep goal's
	// deficit to at least the fraction of pawns under it (#255): the goal's
	// own census still decides whether it is active and what it builds, so a
	// recovered owner is not re-raised, and the pawn's EnsureMood-* goal
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
	r.Disaster, err = ReviewDisaster(f.DisasterConditions, f.RecoveryBuildings, r.Gates, f.Disaster, f.DisasterTick, f.ShortCircuitTick)
	if err != nil {
		return RoutineNeeds{}, err
	}
	if r.Disaster != nil {
		need := RecoveryNeed(r.Disaster, f.RecoverySafety)
		priority := r.Disaster.Promote(RecoverDisasterServices, 3)
		if s, k := f.RecoverySafety.Value(); k && positive(s.RoofHazard) && r.Disaster.Phase != DisasterRestored {
			priority = 2
		}
		r.Assessments = append(r.Assessments, RoutineAssessment{ID: RecoverDisasterServices, Priority: priority, Need: need})
		if need != domain.NeedRecovered {
			addGoal(RecoverDisasterServices, priority)
			r.Goals[len(r.Goals)-1].MethodUnavailable = true
		}
		for i := range r.Goals {
			r.Goals[i].Priority = r.Disaster.Promote(r.Goals[i].ID, r.Goals[i].Priority)
		}
		for i := range r.Assessments {
			r.Assessments[i].Priority = r.Disaster.Promote(r.Assessments[i].ID, r.Assessments[i].Priority)
		}
	}
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
		if len(methods) > 48 {
			return RoutineNeeds{}, errors.New("too many routine method capabilities")
		}
		for _, id := range methods {
			if !recognized[id] || available[id] {
				return RoutineNeeds{}, errors.New("invalid routine method capability")
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
	return r, nil
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
