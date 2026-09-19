package policy

import (
	"math"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func emptyUpkeep() UpkeepObservation {
	return UpkeepObservation{Clearance: domain.Known([]ClearanceTarget{}), Items: domain.Known([]UpkeepItem{}), Structures: domain.Known([]UpkeepStructure{}), Fires: domain.Known([]UpkeepFire{}), Filth: domain.Known([]UpkeepFilth{}), Lighting: domain.Known(LightingObservation{}), Flooring: domain.Known(FlooringObservation{}), Routes: domain.Known(RoutesObservation{})}
}
func TestUpkeepNativeTargetOrderAndMetrics(t *testing.T) {
	v := emptyUpkeep()
	v.Items = domain.Known([]UpkeepItem{
		{ID: "wood", Definition: "WoodLog", Deterioration: 1, Count: 30},
		{ID: "meal", Definition: "MealSimple", Deterioration: 1, RotTicks: domain.Known(int64(10)), Count: 2},
		{ID: "medicine", Definition: "MedicineHerbal", Deterioration: 1, Medicine: true, Count: 5},
		{ID: "forbidden", Definition: "Steel", Deterioration: 1, Forbidden: true, Count: 50},
		{ID: "safe", Definition: "Steel", Deterioration: 1, Roofed: true, InStorage: true, Count: 50},
		{ID: "steel", Definition: "Steel", Count: 50},
		{ID: "stored-outdoors", Definition: "Steel", InStorage: true, Count: 50},
	})
	v.Structures = domain.Known([]UpkeepStructure{
		{ID: "wall", Home: true, HitPoints: 2, MaxHitPoints: 100, Priority: 1},
		{ID: "heater", Home: true, HitPoints: 99, MaxHitPoints: 100},
		{ID: "door", Home: true, HitPoints: 50, MaxHitPoints: 100, Priority: 1},
		{ID: "outside", HitPoints: 1, MaxHitPoints: 100},
	})
	v.Fires = domain.Known([]UpkeepFire{{ID: "b", Home: true, Size: domain.Known(.5)}, {ID: "a", Home: true, Size: domain.Known(1.0)}, {ID: "outside", Size: domain.Known(4.0)}})
	// Cleaning targets are filth in a measured dirty workspace whose
	// coverage failed (no cleaners): the kitchen's; the hospital filth is
	// outside Home and the loose filth "a" has no room.
	v.Filth = domain.Known([]UpkeepFilth{{ID: "a", Home: true, Thickness: 1}, {ID: "b", Home: true, Room: "Kitchen", RoomID: domain.Known("k"), Thickness: 2}, {ID: "c", Room: "Hospital", RoomID: domain.Known("h"), Thickness: 3}})
	v.Rooms = domain.Known(RoomObservation{Rooms: []Room{
		{ID: "k", Role: domain.Known(RoomRoleKitchen), Enclosed: domain.Known(true), Cleanliness: domain.Known(-3.0)},
		{ID: "h", Role: domain.Known(RoomRoleHospital), Enclosed: domain.Known(true), Cleanliness: domain.Known(-3.0)},
	}})
	v.CleaningWorkers = domain.Known(0)
	r, err := ReviewUpkeep(v, UpkeepHistory{}, nil)
	if err != nil {
		t.Fatal(err)
	}
	for i, want := range [][]string{{"a", "b"}, {"medicine", "meal", "wood"}, {"heater", "wall", "door"}, {"b"}, {"steel"}} {
		got, known := r.Needs[i].Targets.Value()
		metric, mk := r.Needs[i].Metric.Value()
		if !known || !reflect.DeepEqual(got, want) || !mk || metric != []float64{1.5, 37, 149, 2, 50}[i] || !r.Needs[i].Active || r.Needs[i].Unsafe {
			t.Fatal(r.Needs[i])
		}
	}
}
func TestUpkeepUnknownRetainsRiskAndIssuedWork(t *testing.T) {
	r, err := ReviewUpkeep(UpkeepObservation{}, UpkeepHistory{}, nil)
	if err != nil {
		t.Fatal(err)
	}
	for _, n := range r.Needs {
		if n.Active || n.Priority != 4 {
			t.Fatal("unknown created emergency", n)
		}
	}
	history := UpkeepHistory{Fire: true, Supplies: true, Repairs: true, Cleaning: true, Storage: true}
	r, err = ReviewUpkeep(UpkeepObservation{}, history, nil)
	if err != nil {
		t.Fatal(err)
	}
	if !reflect.DeepEqual(r.History, history) || r.Needs[0].Priority != 1 || r.Needs[1].Priority != 3 {
		t.Fatal(r)
	}
	r, err = ReviewUpkeep(emptyUpkeep(), history, map[GoalID]bool{SecureSupplies: true})
	if err != nil {
		t.Fatal(err)
	}
	if !reflect.DeepEqual(r.History, UpkeepHistory{Supplies: true}) {
		t.Fatal("empty census cleared unfinished shared work", r)
	}
	r, err = ReviewUpkeep(emptyUpkeep(), r.History, nil)
	if err != nil || !reflect.DeepEqual(r.History, UpkeepHistory{}) {
		t.Fatal(r, err)
	}
}
func TestUpkeepFireInterventionBound(t *testing.T) {
	for _, rows := range [][]UpkeepFire{
		{{ID: "fire", Home: true}},
		{{ID: "fire", Home: true, Size: domain.Known(1.01)}},
		{{ID: "a", Home: true}, {ID: "b", Home: true}, {ID: "c", Home: true}, {ID: "d", Home: true}},
	} {
		v := emptyUpkeep()
		v.Fires = domain.Known(rows)
		r, err := ReviewUpkeep(v, UpkeepHistory{}, nil)
		if err != nil || !r.Needs[0].Unsafe || !r.Needs[0].Active {
			t.Fatal(r, err)
		}
	}
}
func TestUpkeepRejectsContradictoryNativeFacts(t *testing.T) {
	for _, mutate := range []func(*UpkeepObservation){
		func(v *UpkeepObservation) { v.Fires = domain.Known([]UpkeepFire{{ID: "a"}, {ID: "a"}}) },
		func(v *UpkeepObservation) {
			v.Fires = domain.Known([]UpkeepFire{{ID: "a", Size: domain.Known(math.NaN())}})
		},
		func(v *UpkeepObservation) { v.Items = domain.Known([]UpkeepItem{{ID: "a", Count: -1}}) },
		func(v *UpkeepObservation) {
			v.Items = domain.Known([]UpkeepItem{{ID: "a", Deterioration: math.Inf(1)}})
		},
		func(v *UpkeepObservation) {
			v.Structures = domain.Known([]UpkeepStructure{{ID: "a", HitPoints: 2, MaxHitPoints: 1}})
		},
		func(v *UpkeepObservation) { v.Filth = domain.Known([]UpkeepFilth{{}}) },
		func(v *UpkeepObservation) { v.Filth = domain.Known(make([]UpkeepFilth, 257)) },
	} {
		v := emptyUpkeep()
		mutate(&v)
		if _, err := ReviewUpkeep(v, UpkeepHistory{}, nil); err == nil {
			t.Fatal("invalid facts accepted", v)
		}
	}
}
