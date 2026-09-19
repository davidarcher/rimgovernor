package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"math"
	"testing"
)

func joyComfort(people ...PawnID) ComfortObservation {
	v := providedComfort(people...)
	v.Recreation = v.Recreation[:1]
	v.Recreation[0].Kind = "Dexterity"
	v.Joy = &RecreationCensus{Kinds: []string{"Dexterity"}, Methods: []JoyBuildingMethod{
		{Definition: "TubeTelevision", Kind: "Television", PowerW: 100},
		{Definition: "BilliardsTable", Kind: "Dexterity"},
		{Definition: "ChessTable", Kind: "Cerebral"},
	}}
	for _, p := range people {
		v.Joy.Pawns = append(v.Joy.Pawns, JoyTolerance{Pawn: p, Tolerance: []float64{0}, Bored: []bool{false}})
	}
	return v
}

func TestRecreationVarietyPopulationToleranceAndDistinctKinds(t *testing.T) {
	v := joyComfort("a")
	check := func(missing, priority int) {
		t.Helper()
		r, err := ReviewBasicComfort(domain.Known(v))
		if err != nil || r.MissingVariety != missing || r.Priority() != priority {
			t.Fatal(r, err)
		}
		if r.Recovered() != domain.Known(missing == 0) {
			t.Fatal(r)
		}
	}
	check(0, 3)
	v.Joy.Pawns[0].Tolerance[0], v.Joy.Pawns[0].Bored[0] = .4, true
	check(1, 3) // Native boredom hysteresis survives tolerance dropping below .5.
	v = joyComfort("a", "b")
	check(2, 3)
	v.Recreation = append(v.Recreation, ComfortFacility{ID: "another-pin", Kind: "Dexterity", AccessibleTo: v.People})
	check(2, 3) // Two buildings of one kind do not satisfy variety.
	v.Joy.Kinds = append(v.Joy.Kinds, "Cerebral")
	v.Recreation = append(v.Recreation, ComfortFacility{ID: "chess", Kind: "Cerebral", AccessibleTo: []PawnID{"a"}})
	for i := range v.Joy.Pawns {
		v.Joy.Pawns[i].Tolerance = []float64{.9, .9}
		v.Joy.Pawns[i].Bored = []bool{true, true}
	}
	check(1, 3)
	if m := SelectRecreationVariety(v, func(JoyBuildingMethod) bool { return true }); m != ComfortAccessBlocked {
		t.Fatal(m)
	}
	v.Recreation[2].AccessibleTo = v.People
	check(0, 3) // Bored with two kinds does not generate an endless building loop.
	// Foothold capacity still outranks variety.
	v.Dining = nil
	r, err := ReviewBasicComfort(domain.Known(v))
	if err != nil || r.Priority() != 2 {
		t.Fatal(r, err)
	}
}

func TestRecreationSelectionUsesNativeKindAndAvailability(t *testing.T) {
	v := joyComfort("a", "b")
	all := func(JoyBuildingMethod) bool { return true }
	if got := SelectRecreationVariety(v, all); got != "TubeTelevision" {
		t.Fatal(got)
	}
	if got := SelectRecreationVariety(v, func(m JoyBuildingMethod) bool { return m.PowerW == 0 }); got != "ChessTable" {
		t.Fatal(got)
	}
	// A mod may give billiards a different kind; use the native fact.
	v.Joy.Methods[1].Kind = "Billiards"
	if got := SelectRecreationVariety(v, func(m JoyBuildingMethod) bool { return m.PowerW == 0 }); got != "BilliardsTable" {
		t.Fatal(got)
	}
	if got := SelectRecreationVariety(v, func(JoyBuildingMethod) bool { return false }); got != ComfortAccessBlocked {
		t.Fatal(got)
	}
}

func TestRecreationVarietyRanksAsMaintenance(t *testing.T) {
	f := stableRoutine()
	f.BasicComfort = domain.Known(joyComfort("a", "b"))
	r, err := DetectRoutine(f, RoutineLatches{}, DefaultRoutinePolicy())
	if err != nil {
		t.Fatal(err)
	}
	found := false
	for _, g := range r.Goals {
		if g.ID == EnsureBasicComfort {
			found = true
			if g.Priority != 3 || g.Deficit != domain.Known(.5) || g.MethodUnavailable {
				t.Fatal(g)
			}
		}
	}
	if !found {
		t.Fatal("missing maintenance variety goal")
	}
	for _, a := range r.Assessments {
		if a.ID == EnsureBasicComfort && (a.Priority != 3 || a.Need != domain.NeedDeficit) {
			t.Fatal(a)
		}
	}
}

func TestRecreationCensusRejectsIncompleteAndInvalidMatrices(t *testing.T) {
	v := joyComfort("a")
	v.Joy = nil
	r, err := ReviewBasicComfort(domain.Known(v))
	if err != nil || r.Recovered() != domain.Unknown[bool]() || r.Deficit() != domain.Unknown[float64]() {
		t.Fatal("unavailable matrix certified recovery", r, err)
	}
	for _, mutate := range []func(*ComfortObservation){
		func(v *ComfortObservation) { v.Joy.Kinds = append(v.Joy.Kinds, "Dexterity") },
		func(v *ComfortObservation) { v.Joy.Pawns[0].Tolerance = nil },
		func(v *ComfortObservation) { v.Joy.Pawns[0].Bored = nil },
		func(v *ComfortObservation) { v.Joy.Pawns[0].Tolerance[0] = math.NaN() },
		func(v *ComfortObservation) { v.Joy.Pawns[0].Tolerance[0] = 1.01 },
		func(v *ComfortObservation) { v.Joy.Pawns[0].Pawn = "outsider" },
		func(v *ComfortObservation) { v.Joy.Pawns = append(v.Joy.Pawns, v.Joy.Pawns[0]) },
		func(v *ComfortObservation) { v.Joy.Methods[0].PowerW = -1 },
		func(v *ComfortObservation) { v.Recreation[0].Kind = "unknown" },
	} {
		v := joyComfort("a")
		mutate(&v)
		if v.Validate() == nil {
			t.Fatal("accepted invalid census", v)
		}
	}
}
