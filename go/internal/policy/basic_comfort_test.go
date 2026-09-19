package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestBasicComfortCountsCapacityWithoutProofOfUse(t *testing.T) {
	v := ComfortObservation{People: []PawnID{"a", "b"}}
	check := func(want ComfortMethod, fraction float64, recovered domain.Fact[bool]) {
		t.Helper()
		r, err := ReviewBasicComfort(domain.Known(v))
		if err != nil {
			t.Fatal(err)
		}
		method, err := SelectBasicComfortMethod(v, r)
		if err != nil || method != want || r.Deficit() != domain.Known(fraction) || r.Recovered() != recovered {
			t.Fatal(r, method, err)
		}
	}
	check(ComfortBuildTable, 1, domain.Known(false))
	v.Surfaces = []DiningSurface{{ID: "table", Adjacent: []domain.Cell{{X: 1, Z: 2}}}}
	check(ComfortBuildChair, 1, domain.Known(false))
	v.Dining = []ComfortFacility{{ID: "chair", AccessibleTo: []PawnID{"a", "b"}}}
	check(ComfortBuildRecreation, .5, domain.Known(false))
	// Unused furniture everyone can reach is provided: proof of use is
	// EnsureComfort's later concern.
	v.Recreation = []ComfortFacility{{ID: "pin", Kind: "Dexterity", AccessibleTo: []PawnID{"a", "b"}}, {ID: "chess", Kind: "Cerebral", AccessibleTo: []PawnID{"a", "b"}}}
	v.Joy = providedComfort("a", "b").Joy
	check(ComfortNoMethod, 0, domain.Known(true))
	v.Dining[0].AccessibleTo = []PawnID{"a"}
	check(ComfortAccessBlocked, .5, domain.Known(false))
}

func TestBasicComfortUnknownCensus(t *testing.T) {
	r, err := ReviewBasicComfort(domain.Unknown[ComfortObservation]())
	if err != nil || r.Recovered() != domain.Unknown[bool]() || r.Deficit() != domain.Unknown[float64]() {
		t.Fatal(r, err)
	}
	if _, err := SelectBasicComfortMethod(ComfortObservation{}, r); err == nil {
		t.Fatal("unknown evidence selected a method")
	}
}

func TestBasicComfortRanksAtFootholdOnceShelterStands(t *testing.T) {
	f := stableRoutine()
	f.BasicComfort = domain.Known(ComfortObservation{People: []PawnID{"a"}})
	find := func(needs RoutineNeeds) (DevelopmentGoal, bool) {
		for _, g := range needs.Goals {
			if g.ID == EnsureBasicComfort {
				return g, true
			}
		}
		return DevelopmentGoal{}, false
	}
	needs, err := DetectRoutine(f, RoutineLatches{}, DefaultRoutinePolicy())
	if err != nil {
		t.Fatal(err)
	}
	g, ok := find(needs)
	if !ok || g.Priority != 2 || g.MethodUnavailable || g.Deficit != domain.Known(1.0) || len(g.Labor) == 0 {
		t.Fatal("basic comfort is not a foothold construction goal", g, ok)
	}
	// While the initial shelter is owed there is nothing to furnish: the
	// goal stays assessed but holds no method.
	f.IndoorCapacity = domain.Known[int64](0)
	needs, err = DetectRoutine(f, RoutineLatches{}, DefaultRoutinePolicy())
	if err != nil {
		t.Fatal(err)
	}
	if g, ok := find(needs); !ok || !g.MethodUnavailable {
		t.Fatal("basic comfort competes before the shelter stands", g, ok)
	}
	f.IndoorCapacity = domain.Known[int64](8)
	f.BasicComfort = domain.Known(providedComfort("a"))
	needs, err = DetectRoutine(f, RoutineLatches{}, DefaultRoutinePolicy())
	if err != nil {
		t.Fatal(err)
	}
	if _, ok := find(needs); ok {
		t.Fatal("provided comfort still a goal")
	}
	for _, a := range needs.Assessments {
		if a.ID == EnsureBasicComfort && a.Need != domain.NeedRecovered {
			t.Fatal("provided comfort not assessed recovered", a)
		}
	}
}

// providedComfort is a census in which every named colonist can reach a seat
// at a table and a recreation source.
func providedComfort(people ...PawnID) ComfortObservation {
	j := &RecreationCensus{Kinds: []string{"Dexterity", "Cerebral"}}
	for _, p := range people {
		j.Pawns = append(j.Pawns, JoyTolerance{Pawn: p, Tolerance: []float64{0, 0}, Bored: []bool{false, false}})
	}
	return ComfortObservation{People: people, Joy: j,
		Surfaces:   []DiningSurface{{ID: "table"}},
		Dining:     []ComfortFacility{{ID: "chair", AccessibleTo: people}},
		Recreation: []ComfortFacility{{ID: "pin", Kind: "Dexterity", AccessibleTo: people}, {ID: "chess", Kind: "Cerebral", AccessibleTo: people}}}
}
