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
)

type RoutinePolicy struct {
	FoodMinDays, FoodTargetDays, FootholdFoodDays float64
	ColdEnter, ColdExit, HotExit, HotEnter        float64
	WoodMin, WoodTarget, WoodMax                  int64
}

func DefaultRoutinePolicy() RoutinePolicy {
	return RoutinePolicy{FoodMinDays: 3, FoodTargetDays: 7, FootholdFoodDays: 3,
		ColdEnter: 12, ColdExit: 16, HotExit: 28, HotEnter: 32, WoodMin: 120, WoodTarget: 350, WoodMax: 500}
}

func (p RoutinePolicy) Validate() error {
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

type RoutineLatches struct{ Food, Cold, Hot, Wood bool }
type RoutineNeeds struct {
	Gates   FootholdGates
	Latches RoutineLatches
	Goals   []DevelopmentGoal
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
	l := RoutineLatches{
		Food: latchValue(previous.Food, f.FoodDays, p.FoodMinDays, p.FoodTargetDays, false),
		Cold: latchValue(previous.Cold, fallback(f.SleepingMin, f.OutdoorTemperature), p.ColdEnter, p.ColdExit, false),
		Hot:  latchValue(previous.Hot, fallback(f.SleepingMax, f.OutdoorTemperature), p.HotEnter, p.HotExit, true),
		Wood: latchValue(previous.Wood, wood, float64(p.WoodMin), float64(p.WoodTarget), false),
	}
	r := RoutineNeeds{Gates: g, Latches: l}
	addGoal := func(id GoalID, priority int) {
		r.Goals = append(r.Goals, DevelopmentGoal{ID: id, Source: AutopilotGoal, Priority: priority})
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
	return r, nil
}
