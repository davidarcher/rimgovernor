package domain

import (
	"errors"
	"math"
)

const minPopulationMaximum int32 = 1
const maxPopulationMaximum int32 = 100
const minPopulationFoodDays = 1
const maxPopulationFoodDays = 120

// PopulationPolicy is an immutable, comparable colony configuration value:
// the explicitly requested maximum colonist count and the minimum stored
// food reserve, in days.
//
// It deliberately has no accompanying ActionKind, no NewPopulationPolicy-
// Action constructor and no executor/bridge boundary, which makes it the
// first value in this package that is player intent without being a plan
// action. Every other player command value here (BuildingTemperature,
// ZoneEdit, QuestAccept, SettlementGift, ...) names a native entity and
// carries an already-observed CAS before-token, because applying it issues
// a native RimWorld call whose receipt and effect evidence the executor
// must verify. Setting a population policy issues no native call at all:
// it only overwrites a stored current value that population and food-
// reserve policy read later. Routing it through
// Action/Progress would require fabricating an inspection, a dispatch
// attempt, a receipt and a completing Observation for a native call that
// never happens. See store.SubmitPopulationPolicy for the persistence side
// and interpreter.Guidance.PopulationPolicy for the chat nudge that feeds it.
type PopulationPolicy struct {
	maximum       int32
	foodDays      float64
	raidThreshold float64
}

// NewPopulationPolicy bounds maximum to [1,100] colonists, foodDays to
// [1,120] days and raidThreshold to finite nonnegative points. Pass zero
// for raidThreshold to preserve the default population-only admission.
func NewPopulationPolicy(maximum int32, foodDays, raidThreshold float64) (PopulationPolicy, error) {
	if maximum < minPopulationMaximum || maximum > maxPopulationMaximum {
		return PopulationPolicy{}, errors.New("population maximum out of range")
	}
	if math.IsNaN(foodDays) || math.IsInf(foodDays, 0) || foodDays < minPopulationFoodDays || foodDays > maxPopulationFoodDays {
		return PopulationPolicy{}, errors.New("population food days out of range")
	}
	if math.IsNaN(raidThreshold) || math.IsInf(raidThreshold, 0) || raidThreshold < 0 {
		return PopulationPolicy{}, errors.New("population raid threshold out of range")
	}
	return PopulationPolicy{maximum, foodDays, raidThreshold}, nil
}

// RaidThreshold caps projected raid points for joiners without a built firing
// or turret tier. Zero disables this optional gate.
func (p PopulationPolicy) RaidThreshold() float64 { return p.raidThreshold }

func (p PopulationPolicy) Maximum() int32    { return p.maximum }
func (p PopulationPolicy) FoodDays() float64 { return p.foodDays }

// Set reports whether a policy has been established. The zero value is not
// constructible through NewPopulationPolicy, so it unambiguously means "no
// policy".
func (p PopulationPolicy) Set() bool { return p != PopulationPolicy{} }
