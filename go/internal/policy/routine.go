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
	EnsureComfort           GoalID = "EnsureComfort"
	EnsureExpansion         GoalID = "EnsureExpansion"
	MaintainEquipment       GoalID = "MaintainEquipment"
)

type RoutinePolicy struct {
	AnimalUpkeep                                  AnimalUpkeepPolicy
	MedicalReserve                                MedicalReservePolicy
	MaxDevelopmentProjects                        int
	FoodMinDays, FoodTargetDays, FootholdFoodDays float64
	ColdEnter, ColdExit, HotExit, HotEnter        float64
	WoodMin, WoodTarget, WoodMax                  int64
}

func DefaultRoutinePolicy() RoutinePolicy {
	return RoutinePolicy{AnimalUpkeep: DefaultAnimalUpkeepPolicy(), MedicalReserve: DefaultMedicalReservePolicy(), MaxDevelopmentProjects: 2, FoodMinDays: 3, FoodTargetDays: 7, FootholdFoodDays: 3,
		ColdEnter: 12, ColdExit: 16, HotExit: 28, HotEnter: 32, WoodMin: 120, WoodTarget: 350, WoodMax: 500}
}

func (p RoutinePolicy) Validate() error {
	if !foodNumber(p.AnimalUpkeep.FeedMinimumDays) || !foodNumber(p.AnimalUpkeep.FeedTargetDays) || p.AnimalUpkeep.FeedTargetDays <= p.AnimalUpkeep.FeedMinimumDays {
		return errors.New("invalid animal feed thresholds")
	}
	if p.MedicalReserve.MinimumPerColonist < 0 || p.MedicalReserve.TargetPerColonist <= p.MedicalReserve.MinimumPerColonist {
		return errors.New("invalid medicine reserve thresholds")
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
	return nil
}

// RoutineFacts holds derived native facts, not forecasts masquerading as output.
// FoodDays is the accessible diet/rot-aware stock runway. FieldCoverage is the
// separate native crop-capacity forecast; it never increases FoodDays.
type RoutineFacts struct {
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
	MedicalReserve      MedicalReserveObservation
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
	for _, n := range upkeep.Needs {
		recovered := domain.Unknown[bool]()
		if _, known := n.Targets.Value(); known {
			recovered = domain.Known(!n.Active)
		}
		addAssessment(n.Goal, n.Priority, recovered)
		if !positive(recovered) {
			addGoal(n.Goal, n.Priority)
			// Direct upkeep orders join the shared execution family in G01.07c.
			// Keep observed risk visible without taking an optional project slot.
			r.Goals[len(r.Goals)-1].MethodUnavailable = true
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
	animalContainment := domain.Unknown[bool]()
	if targets, known := animals.Containment.Value(); known {
		animalContainment = domain.Known(len(targets) == 0)
	}
	animalFeed := domain.Unknown[bool]()
	if targets, known := animals.Feed.Value(); known {
		animalFeed = domain.Known(len(targets) == 0)
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
		need := r.Disaster.Services[len(r.Disaster.Services)-1].Need
		if r.Disaster.Phase == DisasterUnknown {
			need = domain.NeedUnknown
		}
		priority := r.Disaster.Promote(RecoverDisasterServices, 3)
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
