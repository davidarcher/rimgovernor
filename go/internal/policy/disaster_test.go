package policy

import (
	"math"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func disasterGates() FootholdGates {
	k := domain.Known(true)
	return FootholdGates{Food: k, Production: k, Sleeping: k, Shelter: k, Temperature: k, Cooking: k, Power: k, Storage: k}
}
func recoveryBuilding(id string) RecoveryBuilding {
	return RecoveryBuilding{ID: id, UsesHitPoints: domain.Known(true), HitPoints: domain.Known(int64(100)), MaxHitPoints: domain.Known(int64(100)), Broken: domain.Known(false), Forbidden: domain.Known(false), Burning: domain.Known(false), Refuelable: domain.Known(false)}
}
func TestDisasterCompoundExpiryAndRenewal(t *testing.T) {
	g := disasterGates()
	empty := domain.Known([]DisasterCondition{})
	conditions := domain.Known([]DisasterCondition{{ID: "2", Definition: "SolarFlare"}, {ID: "1", Definition: "ColdSnap"}})
	buildings := domain.Known([]RecoveryBuilding{})
	h, err := ReviewDisaster(empty, buildings, g, nil, 10)
	if err != nil || h != nil {
		t.Fatal(h, err)
	}
	g.Food, g.Power, g.Temperature = domain.Known(false), domain.Known(false), domain.Known(false)
	h, err = ReviewDisaster(conditions, buildings, g, nil, 11)
	if err != nil || h.Phase != DisasterDisrupted || h.Conditions[0].ID != "1" {
		t.Fatal(h, err)
	}
	if h.Promote(MaintainWood, 3) != 2 || h.Promote(ActiveCombat, 0) != 0 || h.Promote(EnsureComfort, 4) != 4 {
		t.Fatal("priority escaped affected services")
	}
	first := cloneDisaster(h)
	h, err = ReviewDisaster(empty, buildings, g, h, 12)
	if err != nil || h.Phase != DisasterRecovering || !reflect.DeepEqual(first.Conditions, []DisasterCondition{{ID: "1", Definition: "ColdSnap"}, {ID: "2", Definition: "SolarFlare"}}) {
		t.Fatal(h, err)
	}
	h, err = ReviewDisaster(empty, buildings, disasterGates(), h, 13)
	if err != nil || h.Phase != DisasterRestored || len(h.Affected) != 3 || h.Promote(MaintainWood, 3) != 3 {
		t.Fatal(h, err)
	}
	later, err := ReviewDisaster(empty, buildings, g, h, 14)
	if err != nil || !reflect.DeepEqual(later, h) {
		t.Fatal("unrelated later shortage reopened completed event")
	}
	h, err = ReviewDisaster(conditions, buildings, g, h, 15)
	if err != nil || h.Started != 15 {
		t.Fatal(h, err)
	}
}
func TestDisasterUnknownAndExactDamageRecovery(t *testing.T) {
	conditions := domain.Known([]DisasterCondition{{ID: "1", Definition: "ColdSnap"}})
	g := disasterGates()
	b := recoveryBuilding("wall")
	b.HitPoints = domain.Known(int64(50))
	h, err := ReviewDisaster(conditions, domain.Known([]RecoveryBuilding{b}), g, nil, 1)
	if err != nil || h.Phase != DisasterDisrupted || len(h.Damaged) != 1 {
		t.Fatal(h, err)
	}
	h, err = ReviewDisaster(domain.Unknown[[]DisasterCondition](), domain.Unknown[[]RecoveryBuilding](), g, h, 2)
	if err != nil || h.Phase != DisasterUnknown || len(h.Damaged) != 1 {
		t.Fatal(h, err)
	}
	h, err = ReviewDisaster(domain.Known([]DisasterCondition{}), domain.Known([]RecoveryBuilding{}), g, h, 3)
	if err != nil || h.Phase != DisasterRecovering {
		t.Fatal("missing building was recovered", h, err)
	}
	b.HitPoints = domain.Known(int64(100))
	b.Forbidden = domain.Known(true)
	h, err = ReviewDisaster(domain.Known([]DisasterCondition{}), domain.Known([]RecoveryBuilding{b}), g, h, 4)
	if err != nil || h.Phase != DisasterRestored {
		t.Fatal(h, err)
	}
	if _, err = ReviewDisaster(conditions, domain.Known([]RecoveryBuilding{b}), g, h, 3); err == nil {
		t.Fatal("accepted stale review")
	}
}
func TestDisasterTemporarySurvivalRequiresCompleteServices(t *testing.T) {
	conditions := domain.Known([]DisasterCondition{{ID: "1", Definition: "ToxicFallout"}})
	buildings := domain.Known([]RecoveryBuilding{})
	h, err := ReviewDisaster(conditions, buildings, disasterGates(), nil, 1)
	if err != nil || h.Phase != DisasterSurvival {
		t.Fatal(h, err)
	}
	g := disasterGates()
	g.Storage = domain.Unknown[bool]()
	h, err = ReviewDisaster(conditions, buildings, g, h, 2)
	if err != nil || h.Phase != DisasterUnknown {
		t.Fatal(h, err)
	}
	if _, err = ReviewDisaster(domain.Known([]DisasterCondition{{ID: "1", Definition: "ColdSnap"}, {ID: "1", Definition: "SolarFlare"}}), buildings, g, nil, 3); err == nil {
		t.Fatal("accepted duplicate condition")
	}
}
func TestRecoveryPendingNativeThresholdsAndProtection(t *testing.T) {
	a, b := recoveryBuilding("a"), recoveryBuilding("b")
	a.HitPoints = domain.Known(int64(40))
	a.Broken = domain.Known(true)
	b.Refuelable = domain.Known(true)
	b.Fuel = domain.Known(1.0)
	b.FuelTarget = domain.Known(20.0)
	work, err := RecoveryPending(domain.Known([]RecoveryBuilding{a, b}))
	rows, k := work.Value()
	if err != nil || !k || !reflect.DeepEqual(rows, []RecoveryWork{{"b", RecoveryRefuel}, {"a", RecoveryBreakdown}, {"a", RecoveryRepair}}) {
		t.Fatal(rows, k, err)
	}
	b.Fuel = domain.Known(5.0)
	a.Forbidden = domain.Known(true)
	work, err = RecoveryPending(domain.Known([]RecoveryBuilding{a, b}))
	rows, k = work.Value()
	if err != nil || !k || len(rows) != 0 {
		t.Fatal(rows, k, err)
	}
	b.Refuelable = domain.Unknown[bool]()
	work, err = RecoveryPending(domain.Known([]RecoveryBuilding{b}))
	if _, k = work.Value(); err != nil || k {
		t.Fatal("unknown service recovered")
	}
	b.Fuel = domain.Known(math.NaN())
	if _, err = RecoveryPending(domain.Known([]RecoveryBuilding{b})); err == nil {
		t.Fatal("accepted NaN")
	}
	if _, err = RecoveryPending(domain.Known([]RecoveryBuilding{a, a})); err == nil {
		t.Fatal("accepted duplicate building")
	}
}
func TestDisasterHistoryRejectsFalseRecoveryAndAliasing(t *testing.T) {
	h, err := ReviewDisaster(domain.Known([]DisasterCondition{{ID: "1", Definition: "ColdSnap"}}), domain.Known([]RecoveryBuilding{}), disasterGates(), nil, 1)
	if err != nil {
		t.Fatal(err)
	}
	for _, mutate := range []func(*DisasterHistory){func(h *DisasterHistory) { h.Phase = DisasterRestored }, func(h *DisasterHistory) { h.Services[0].Need = domain.NeedDeficit }, func(h *DisasterHistory) { h.Observed = 0 }, func(h *DisasterHistory) { h.Affected = []DisasterService{"invented"} }} {
		copy := cloneDisaster(h)
		mutate(copy)
		if copy.Validate() == nil {
			t.Fatal("accepted inconsistent history", copy)
		}
	}
	if h.Validate() != nil {
		t.Fatal("validation mutation aliased original")
	}
}

func TestRoutineDisasterPromotesOnlyObservedServiceDeficits(t *testing.T) {
	f := RoutineFacts{Colonists: domain.Known(int64(3)), BedCapacity: domain.Known(int64(3)), IndoorCapacity: domain.Known(int64(3)), GrowingCells: domain.Known(int64(30)), FoodDays: domain.Known(10.0), FieldCoverage: domain.Known(1.0), FoodStorage: domain.Known(true), Cooking: domain.Known(true), PowerRequired: domain.Known(false), DisabledConsumers: domain.Known(false), SleepingMin: domain.Known(0.0), SleepingMax: domain.Known(22.0), Wood: domain.Known(int64(0)), DisasterConditions: domain.Known([]DisasterCondition{{ID: "cold", Definition: "ColdSnap"}}), RecoveryBuildings: domain.Known([]RecoveryBuilding{}), DisasterTick: 10}
	r, err := DetectRoutine(f, RoutineLatches{}, DefaultRoutinePolicy())
	if err != nil || r.Disaster.Phase != DisasterDisrupted {
		t.Fatal(r.Disaster, err)
	}
	priority := func(r RoutineNeeds, id GoalID) int {
		for _, n := range r.Assessments {
			if n.ID == id {
				return n.Priority
			}
		}
		t.Fatal("missing assessment", id)
		return -1
	}
	if priority(r, MaintainWood) != 2 || priority(r, EnsureComfort) != 4 {
		t.Fatal("incorrect disaster promotion")
	}
	f.Disaster = r.Disaster
	f.DisasterTick++
	f.DisasterConditions = domain.Known([]DisasterCondition{})
	f.SleepingMin = domain.Known(22.0)
	r, err = DetectRoutine(f, r.Latches, DefaultRoutinePolicy())
	if err != nil || r.Disaster.Phase != DisasterRestored || priority(r, MaintainWood) != 3 {
		t.Fatal(r.Disaster, err)
	}
	for _, n := range r.Assessments {
		if n.ID == RecoverDisasterServices && n.Need != domain.NeedRecovered {
			t.Fatal(n)
		}
	}
}

func TestReviewDisasterRejectsInconsistentDuration(t *testing.T) {
	g := FootholdGates{}
	five := int64(5)
	negative := int64(-1)
	for _, bad := range [][]DisasterCondition{
		{{ID: "1", Definition: "ColdSnap", Permanent: true, TicksLeft: &five}},
		{{ID: "1", Definition: "ColdSnap", TicksLeft: &negative}},
	} {
		if _, err := ReviewDisaster(domain.Known(bad), domain.Known([]RecoveryBuilding{}), g, nil, 3); err == nil {
			t.Fatal("accepted", bad)
		}
	}
	h, err := ReviewDisaster(domain.Known([]DisasterCondition{{ID: "1", Definition: "ColdSnap", TicksLeft: &five}}), domain.Known([]RecoveryBuilding{}), g, nil, 3)
	if err != nil || h == nil || h.Validate() != nil || *h.Conditions[0].TicksLeft != 5 {
		t.Fatal(h, err)
	}
}

func TestConditionRemainingTicksAndSolarFlareHold(t *testing.T) {
	flare := int64(12000)
	untimed := domain.Known([]DisasterCondition{{ID: "f", Definition: ConditionSolarFlare}})
	timed := domain.Known([]DisasterCondition{{ID: "f", Definition: ConditionSolarFlare, TicksLeft: &flare}, {ID: "c", Definition: ConditionColdSnap, TicksLeft: &flare}})
	if _, known := ConditionRemainingTicks(domain.Unknown[[]DisasterCondition](), ConditionSolarFlare).Value(); known {
		t.Fatal("unknown census produced a duration")
	}
	if got, known := ConditionRemainingTicks(untimed, ConditionSolarFlare).Value(); !known || got != 0 {
		t.Fatal(got, known)
	}
	if got, known := ConditionRemainingTicks(timed, ConditionSolarFlare).Value(); !known || got != flare {
		t.Fatal(got, known)
	}
	if got, known := ConditionRemainingTicks(timed, ConditionVolcanicWinter).Value(); !known || got != 0 {
		t.Fatal(got, known)
	}
	if SolarFlareHold(untimed) || SolarFlareHold(domain.Unknown[[]DisasterCondition]()) || !SolarFlareHold(timed) {
		t.Fatal("solar flare hold follows the remaining-duration read")
	}
	if got := GrowthPauseDays(timed); got != 0.2 {
		t.Fatal(got)
	}
}
