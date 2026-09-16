package policy

import (
	"errors"
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

const (
	ConfirmColonyNames      GoalID = "ConfirmColonyNames"
	ActiveCombat            GoalID = "ActiveCombat"
	CriticalMedicine        GoalID = "CriticalMedical"
	RestoreWorkers          GoalID = "RestoreWorkers"
	AllowStartingSupplies   GoalID = "AllowStartingSupplies"
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
	EnsureComfort           GoalID = "EnsureComfort"
	EnsureExpansion         GoalID = "EnsureExpansion"
	MaintainEquipment       GoalID = "MaintainEquipment"
	EnsureResearch          GoalID = "EnsureResearch"
	MaintainResource        GoalID = "MaintainResource"
	ProductionPolicy        GoalID = "ProductionPolicy"
)

// foodStorageUpkeepPriority is MaintainFoodStorage's entry development
// priority, the same priority medicalReservePriority (a local var, not a
// const, at its own point of use below) starts MaintainMedicalReserves at.
const foodStorageUpkeepPriority = 3

type RoutinePolicy struct {
	AnimalUpkeep                                  AnimalUpkeepPolicy
	MedicalReserve                                MedicalReservePolicy
	FoodStorage                                   FoodStoragePolicy
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
	// ResearchTarget is an operator-declared desired native ResearchProjectDef
	// name; empty disables EnsureResearch's routine dispatch. Unlike
	// research.py's needs(), which derives targets from every other active
	// goal's own observed capability gaps, this only supports one explicit
	// target -- deriving targets from other goals' evidence generically
	// remains an open gap (no Go goal family yet records the
	// UnavailableThings/BlockedRecipes evidence ResearchNeeds expects).
	ResearchTarget string
	// ResourceTargets is an operator-declared map of native resource
	// definition name to the native stock floor MaintainResource should keep
	// it above; an empty map disables the goal entirely, the same config-only
	// posture ResearchTarget uses for EnsureResearch. Unlike
	// production_policy.py's plan-wide resource_policy (many simultaneously
	// tracked floors driving both goal creation and the native
	// SetProductionPolicy push), this only supports
	// policy.SelectResourceTarget's own single-goal dynamic-target selection
	// across these targets and issues no SetProductionPolicy push at all.
	ResourceTargets map[Resource]int64
	// ResourceReserves and StoppedResources are operator-declared inputs to
	// ProductionFloors, mirroring production_policy.py's plan.control
	// resource_policy reserve/spending-stopped configuration. Unlike
	// ResourceTargets (which drives MaintainResource's own goal/method
	// selection), these drive the ProductionPolicy goal's own config-only
	// posture: RoutineProductionPolicyPlanner dispatches ProductionFloors's
	// computed floors/stopped rows through the native SetProductionPolicy
	// write whenever they diverge from a fresh ReadProductionPolicy.
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
	// both must be set. Unlike Python's husbandry.py per-race target (which
	// also carries a minimum, protected-id set and breeding-reserve count),
	// this only supports the maximum half of that target, narrowed the same
	// way ResearchTarget's doc comment discloses its own gap.
	HerdPopulationMax map[Resource]int64
}

func DefaultRoutinePolicy() RoutinePolicy {
	return RoutinePolicy{AnimalUpkeep: DefaultAnimalUpkeepPolicy(), MedicalReserve: DefaultMedicalReservePolicy(), FoodStorage: DefaultFoodStoragePolicy(), MaxDevelopmentProjects: 2, FoodMinDays: 3, FoodTargetDays: 7, FootholdFoodDays: 3,
		ColdEnter: 12, ColdExit: 16, HotExit: 28, HotEnter: 32, WoodMin: 120, WoodTarget: 350, WoodMax: 500, HuntStallTicks: 6000}
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
	if p.MaxDevelopmentProjects < 1 || p.MaxDevelopmentProjects > 8 {
		return errors.New("invalid development project limit")
	}
	for _, n := range []float64{p.FoodMinDays, p.FoodTargetDays, p.FootholdFoodDays, p.ColdEnter, p.ColdExit, p.HotExit, p.HotEnter} {
		if math.IsNaN(n) || math.IsInf(n, 0) {
			return errors.New("nonfinite routine threshold")
		}
	}
	if p.FoodMinDays <= 0 || p.FoodTargetDays <= p.FoodMinDays || p.FootholdFoodDays <= 0 ||
		p.ColdEnter >= p.ColdExit || p.ColdExit >= p.HotExit || p.HotExit >= p.HotEnter ||
		p.WoodMin < 0 || p.WoodTarget <= p.WoodMin || p.WoodMax < p.WoodTarget {
		return errors.New("unordered routine thresholds")
	}
	if p.HuntStallTicks <= 0 {
		return errors.New("invalid hunt stall grace")
	}
	if p.ResearchTarget != "" && !validResource(Resource(p.ResearchTarget)) {
		return errors.New("invalid research target")
	}
	if err := ValidateResourceTargets(p.ResourceTargets); err != nil {
		return err
	}
	if _, _, err := ProductionFloors(p.ResourceReserves, p.StoppedResources); err != nil {
		return err
	}
	if err := ValidateHerdPopulationMax(p.HerdPopulationMax); err != nil {
		return err
	}
	return nil
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
	RecoverySafety      domain.Fact[RecoverySafety]
	RecoveryWorkers     domain.Fact[[]RecoveryWorker]
	DisasterConditions  domain.Fact[[]DisasterCondition]
	RecoveryBuildings   domain.Fact[[]RecoveryBuilding]
	Disaster            *DisasterHistory
	DisasterTick        domain.Tick
	MoodPawns           domain.Fact[[]MoodPawn]
	Mood                MoodHistory
	HomeCoverage        domain.Fact[HomeCoverageObservation]
	StoneStructures     domain.Fact[[]StoneStructure]
	OwnedStockpiles     domain.Fact[[]OwnedStockpile]
	ConstructionClaims  domain.Fact[[]ConstructionClaim]
	CurrentConstruction domain.Fact[CurrentConstruction]
	Sleeping            domain.Fact[SleepingObservation]
	SleepingRecovered   domain.Fact[bool]
	AnimalUpkeep        AnimalUpkeepObservation
	FoodStorageUpkeep   FoodStorageObservation
	MedicalReserve      MedicalReserveObservation
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
	// Waste carries MaintainWaste's exposed/eligible native item census (the
	// same WasteReply the generic per-tick colony read already carries), for
	// pendingWaste/WasteDeficit to detect and, eventually, SelectWasteMethod
	// to dispatch containment/burial candidates from.
	Waste domain.Fact[[]WasteItem]
	// AvailableMethods is supplied by the configured runtime, never native facts.
	AvailableMethods                                                           domain.Fact[[]GoalID]
	Upkeep                                                                     UpkeepObservation
	UpkeepIssued                                                               map[GoalID]bool
	Gear                                                                       domain.Fact[GearObservation]
	Comfort                                                                    domain.Fact[ComfortObservation]
	ComfortRecovered                                                           domain.Fact[bool]
	ComfortDeficit                                                             domain.Fact[float64]
	StartingSupplyCells                                                        domain.Fact[[]domain.Cell]
	MedicalPawns                                                               domain.Fact[[]CarePawn]
	MedicalCareRecovered                                                       domain.Fact[bool]
	Workers                                                                    domain.Fact[int]
	Colonists, HousingTarget, BedCapacity, IndoorCapacity, GrowingCells, Armed domain.Fact[int64]
	FoodDays, PopulationFoodDays, FieldCoverage                                domain.Fact[float64]
	SleepingMin, SleepingMax, OutdoorTemperature, PowerHeadroom                domain.Fact[float64]
	Wood                                                                       domain.Fact[int64]
	Hostiles, CriticalPatients                                                 domain.Fact[int64]
	AllPatientsResting, ColonyNaming, CleanupPawns, ForbiddenSupplies          domain.Fact[bool]
	FoodStorage, Cooking, WorkCoverage, PowerRequired, DisabledConsumers       domain.Fact[bool]
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
	Food, Cold, Hot, Wood    bool
	Upkeep                   UpkeepHistory
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
	upkeep, err := ReviewUpkeep(f.Upkeep, previous.Upkeep, f.UpkeepIssued)
	if err != nil {
		return RoutineNeeds{}, err
	}
	gear, err := ReviewGear(f.Gear)
	if err != nil {
		return RoutineNeeds{}, err
	}
	if v, known := f.ComfortDeficit.Value(); known && (math.IsNaN(v) || math.IsInf(v, 0) || v < 0 || v > 1) {
		return RoutineNeeds{}, errors.New("invalid comfort deficit")
	}
	if err := p.Validate(); err != nil {
		return RoutineNeeds{}, err
	}
	for _, fact := range []domain.Fact[int64]{f.Colonists, f.HousingTarget, f.BedCapacity, f.IndoorCapacity, f.GrowingCells, f.Armed, f.Wood, f.Hostiles, f.CriticalPatients} {
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
		Upkeep:         upkeep.History,
		Food:           latchValue(previous.Food, f.FoodDays, p.FoodMinDays, p.FoodTargetDays, false),
		Cold:           latchValue(previous.Cold, fallback(f.SleepingMin, f.OutdoorTemperature), p.ColdEnter, p.ColdExit, false),
		Hot:            latchValue(previous.Hot, fallback(f.SleepingMax, f.OutdoorTemperature), p.HotEnter, p.HotExit, true),
		Wood:           latchValue(previous.Wood, wood, float64(p.WoodMin), float64(p.WoodTarget), false),
	}
	r := RoutineNeeds{Gates: g, Latches: l}
	addGoal := func(id GoalID, priority int) {
		r.Goals = append(r.Goals, DevelopmentGoal{ID: id, Source: AutopilotGoal, Priority: priority, Deficit: RoutineDevelopmentDeficit(id, f, p)})
	}
	if positive(f.ColonyNaming) {
		addGoal(ConfirmColonyNames, 0)
	}
	if !positive(measured(f.Hostiles, func(n int64) bool { return n == 0 })) {
		addGoal(ActiveCombat, 0)
	}
	if !positive(g.Medical) {
		priority := 1
		if positive(f.AllPatientsResting) {
			if n, k := f.CriticalPatients.Value(); k && n > 0 {
				priority = 2
			}
		}
		addGoal(CriticalMedicine, priority)
	}
	if positive(measured(f.Hostiles, func(n int64) bool { return n == 0 })) && positive(f.CleanupPawns) {
		addGoal(RestoreWorkers, 1)
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
	if !positive(g.Power) {
		addGoal(EnsureBasicPower, 2)
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
	}
	if !positive(gear.Recovered) {
		addGoal(MaintainEquipment, 3)
		r.Goals[len(r.Goals)-1].Deficit = gear.Deficit
		// Gear execution remains gated in G01.07d. Keep the need visible without
		// reserving optional capacity for an action family that cannot run yet.
		r.Goals[len(r.Goals)-1].MethodUnavailable = true
	}
	addAssessment := func(id GoalID, priority int, recovered domain.Fact[bool]) {
		need := domain.NeedUnknown
		if value, known := recovered.Value(); known {
			need = domain.NeedDeficit
			if value {
				need = domain.NeedRecovered
			}
		}
		r.Assessments = append(r.Assessments, RoutineAssessment{id, priority, need})
	}
	not := func(f domain.Fact[bool]) domain.Fact[bool] { return measured(f, func(v bool) bool { return !v }) }
	// A retained latch with missing input preserves history, not fresh evidence.
	latchRecovered := func(active bool, observed domain.Fact[float64]) domain.Fact[bool] {
		return measured(observed, func(float64) bool { return !active })
	}
	addAssessment(ConfirmColonyNames, 0, not(f.ColonyNaming))
	addAssessment(ActiveCombat, 0, measured(f.Hostiles, func(n int64) bool { return n == 0 }))
	medicalPriority := 1
	if positive(f.AllPatientsResting) {
		if n, k := f.CriticalPatients.Value(); k && n > 0 {
			medicalPriority = 2
		}
	}
	addAssessment(CriticalMedicine, medicalPriority, g.Medical)
	addAssessment(RestoreWorkers, 1, not(f.CleanupPawns))
	addAssessment(AllowStartingSupplies, 2, not(f.ForbiddenSupplies))
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
	addAssessment(EnsureComfort, 4, f.ComfortRecovered)
	addAssessment(EnsureExpansion, 4, expansion)
	addAssessment(MaintainEquipment, 3, gear.Recovered)
	// EnsureResearch stays config-only: unlike every other goal above, its
	// recovered/deficit state is not derived from a review-time native
	// census (no RoutineFacts field records the current research project),
	// only from whether an operator declared a ResearchTarget at all. The
	// routine planner performs its own fresh native read to decide whether
	// a project is already selected before ever proposing a method; see
	// RoutinePolicy.ResearchTarget's doc comment for the disclosed gap this
	// narrows around (no cross-goal needs-driven target derivation).
	researchRecovered := domain.Known(p.ResearchTarget == "")
	if !positive(researchRecovered) {
		addGoal(EnsureResearch, 4)
	}
	addAssessment(EnsureResearch, 4, researchRecovered)
	// MaintainResource stays config-only, the same posture as EnsureResearch
	// just above: recovered/deficit state is not derived from a review-time
	// native resource census (RoutineFacts carries none), only from whether
	// an operator declared any ResourceTargets at all. RoutineResourcePlanner
	// performs its own fresh native read and policy.SelectResourceTarget's
	// dynamic-target selection immediately before proposing a method.
	resourceRecovered := domain.Known(len(p.ResourceTargets) == 0)
	if !positive(resourceRecovered) {
		addGoal(MaintainResource, 4)
	}
	addAssessment(MaintainResource, 4, resourceRecovered)
	// ProductionPolicy stays config-only, the same posture as EnsureResearch
	// and MaintainResource above: recovered/deficit state is not derived
	// from a review-time native census (RoutineFacts carries none), only
	// from whether an operator declared any ResourceReserves/StoppedResources
	// at all. RoutineProductionPolicyPlanner performs its own fresh
	// ReadProductionPolicy and policy.ProductionFloors comparison immediately
	// before proposing a method.
	productionPolicyRecovered := domain.Known(len(p.ResourceReserves) == 0 && len(p.StoppedResources) == 0)
	if !positive(productionPolicyRecovered) {
		addGoal(ProductionPolicy, 4)
	}
	addAssessment(ProductionPolicy, 4, productionPolicyRecovered)
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
			if n.Goal != SecureSupplies && n.Goal != MaintainEssentialRepairs && n.Goal != MaintainCleanFacilities && n.Goal != MaintainStorage {
				r.Goals[len(r.Goals)-1].MethodUnavailable = true
			}
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
			r.Goals[len(r.Goals)-1].MethodUnavailable = true
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
		r.Goals[len(r.Goals)-1].MethodUnavailable = true
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
		r.Goals[len(r.Goals)-1].MethodUnavailable = true
	}
	foodStorageActive := foodStorage.Active || f.UpkeepIssued[MaintainFoodStorage]
	foodStorageRecovered := domain.Unknown[bool]()
	foodStoragePriority := foodStorageUpkeepPriority
	if _, known := foodStorage.StoredNutrition.Value(); known {
		foodStorageRecovered = domain.Known(!foodStorageActive)
	} else if !foodStorageActive {
		foodStoragePriority = 4
	}
	addAssessment(MaintainFoodStorage, foodStoragePriority, foodStorageRecovered)
	if !positive(foodStorageRecovered) {
		addGoal(MaintainFoodStorage, foodStoragePriority)
		r.Goals[len(r.Goals)-1].MethodUnavailable = true
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
	if deficit, known := AnimalHerdDeficit(f.AnimalUpkeep.Animals, p.AllowSlaughter, p.HerdPopulationMax).Value(); known {
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
	populationRecovered := domain.Unknown[bool]()
	prisonerDeficit, prisonerDeficitKnown := PrisonerRecruitDeficit(f.Prisoners).Value()
	custodyDeficit, custodyDeficitKnown := CustodyDeficit(f.Custody).Value()
	switch {
	case prisonerDeficitKnown && prisonerDeficit, custodyDeficitKnown && custodyDeficit:
		populationRecovered = domain.Known(false)
	case prisonerDeficitKnown:
		populationRecovered = domain.Known(!prisonerDeficit)
	case custodyDeficitKnown:
		populationRecovered = domain.Known(!custodyDeficit)
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
			addGoal(animalNeed.id, priority)
			r.Goals[len(r.Goals)-1].MethodUnavailable = true
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
	if err := f.Mood.Validate(); err != nil {
		return RoutineNeeds{}, err
	}
	for _, state := range f.Mood.States {
		id, priority, need := MoodGoal(state.Pawn.ID), state.Priority(), state.Need()
		r.Assessments = append(r.Assessments, RoutineAssessment{id, priority, need})
		if state.Active {
			addGoal(id, priority)
			r.Goals[len(r.Goals)-1].MethodUnavailable = true
		}
	}
	r.Disaster, err = ReviewDisaster(f.DisasterConditions, f.RecoveryBuildings, r.Gates, f.Disaster, f.DisasterTick)
	if err != nil {
		return RoutineNeeds{}, err
	}
	if r.Disaster != nil {
		need := RecoveryNeed(r.Disaster, f.RecoverySafety)
		priority := r.Disaster.Promote(RecoverDisasterServices, 3)
		if s, k := f.RecoverySafety.Value(); k && positive(s.RoofHazard) && r.Disaster.Phase != DisasterRestored {
			priority = 2
		}
		r.Assessments = append(r.Assessments, RoutineAssessment{RecoverDisasterServices, priority, need})
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
		if len(methods) > 32 {
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
	}
	return r, nil
}
