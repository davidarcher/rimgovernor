package policy

import (
	"math"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func diseaseCondition(severity, immunity, severityRate, immunityRate float64) CareCondition {
	return CareCondition{DefName: domain.Known("Plague"), Severity: domain.Known(severity), Immunity: domain.Known(immunity), SeverityPerDay: domain.Known(severityRate), ImmunityPerDay: domain.Known(immunityRate)}
}

func TestDiseaseProjection(t *testing.T) {
	for _, tt := range []struct {
		name           string
		c              CareCondition
		lethal, immune float64
		rest, known    bool
	}{
		{"losing", diseaseCondition(.5, .25, .5, .25), 1, 3, true, true},
		{"tie", diseaseCondition(.5, .5, .25, .25), 2, 2, true, true},
		{"closing", diseaseCondition(.5, .5, .25, 1.0/3), 2, 1.5, true, true},
		{"one day margin", diseaseCondition(.5, .5, .25, .5), 2, 1, false, true},
		{"safe", diseaseCondition(.25, .5, .25, .5), 3, 1, false, true},
		{"immune", diseaseCondition(.9, 1, .5, 0), .2, 0, false, true},
		{"severity stalled", diseaseCondition(.5, .5, 0, .25), math.Inf(1), 2, false, true},
		{"recovering", diseaseCondition(.5, .5, -.25, .25), math.Inf(1), 2, false, true},
		{"immunity stalled", diseaseCondition(.5, .5, .25, 0), 2, math.Inf(1), true, true},
		{"immunity declining", diseaseCondition(.5, .5, .25, -.25), 2, math.Inf(1), true, true},
		{"both stalled", diseaseCondition(.5, .5, 0, 0), math.Inf(1), math.Inf(1), false, true},
		{"lethal threshold", diseaseCondition(1, .5, -.25, .25), 0, 2, true, true},
		{"missing", CareCondition{}, 0, 0, false, false},
		{"nan", diseaseCondition(.5, math.NaN(), .25, .25), 0, 0, false, false},
		{"infinity", diseaseCondition(.5, .5, math.Inf(1), .25), 0, 0, false, false},
		{"invalid immunity", diseaseCondition(.5, 2, .25, .25), 0, 0, false, false},
	} {
		t.Run(tt.name, func(t *testing.T) {
			p, known := ProjectDisease(tt.c).Value()
			near := func(a, b float64) bool { return a == b || math.Abs(a-b) < 1e-12 }
			if known != tt.known || known && (!near(p.DaysToLethal, tt.lethal) || !near(p.DaysToImmune, tt.immune) || p.NeedsRest() != tt.rest) {
				t.Fatal(p, known)
			}
		})
	}
}

func TestDiseaseRestRetainedUntilAllConditionsImmune(t *testing.T) {
	c := diseaseCondition(.5, .5, .25, .25)
	other := c
	other.DefName = domain.Known("Flu")
	pawn := CarePawn{ID: "patient", Dead: domain.Known(false), Conditions: domain.Known([]CareCondition{c, other})}
	h, err := ReviewDiseaseRest(domain.Known([]CarePawn{pawn}), nil)
	if err != nil || len(h) != 1 || !reflect.DeepEqual(h[0].Conditions, []string{"Flu", "Plague"}) {
		t.Fatal(h, err)
	}
	for _, input := range []domain.Fact[[]CarePawn]{domain.Unknown[[]CarePawn](), domain.Known([]CarePawn{}), domain.Known([]CarePawn{{ID: "patient", Dead: domain.Known(false)}})} {
		got, err := ReviewDiseaseRest(input, h)
		if err != nil || !reflect.DeepEqual(got, h) {
			t.Fatal(got, err)
		}
	}
	// Rest improves the forecast; do not oscillate back to work.
	c.ImmunityPerDay = domain.Known(1.0)
	other.Immunity = domain.Known(1.0)
	pawn.Conditions = domain.Known([]CareCondition{c, other})
	h, err = ReviewDiseaseRest(domain.Known([]CarePawn{pawn}), h)
	if err != nil || len(h) != 1 || !reflect.DeepEqual(h[0].Conditions, []string{"Plague"}) {
		t.Fatal(h, err)
	}
	c.Immunity = domain.Known(1.0)
	pawn.Conditions = domain.Known([]CareCondition{c, other})
	h, err = ReviewDiseaseRest(domain.Known([]CarePawn{pawn}), h)
	if err != nil || len(h) != 0 {
		t.Fatal(h, err)
	}
	// An absent disease in a complete census also proves resolution.
	h, err = ReviewDiseaseRest(domain.Known([]CarePawn{{ID: "patient", Dead: domain.Known(false), Conditions: domain.Known([]CareCondition{})}}), []DiseaseRest{{Pawn: "patient", Conditions: []string{"Plague"}}})
	if err != nil || len(h) != 0 {
		t.Fatal(h, err)
	}
}

func TestDiseaseWorkRestAndRestore(t *testing.T) {
	for _, manual := range []bool{true, false} {
		team := workTeam(manual)
		work, _ := team[0].Work.Value()
		team[0].Work = domain.Known(append(work, WorkPriority{Work: WorkPatient}, WorkPriority{Work: WorkBedRest}))
		// Durable player intent is temporarily masked, not rewritten.
		overrides := []WorkOverride{{Pawn: "builder", Work: WorkConstruction, Priority: 2}}
		demand := WorkDemand{Resting: []DiseaseRest{{Pawn: "builder", Conditions: []string{"Plague"}}}}
		d, err := PlanWork(team, nil, overrides, demand)
		if err != nil {
			t.Fatal(err)
		}
		for _, a := range d.Assignments {
			if a.Pawn != "builder" {
				continue
			}
			for _, w := range a.Priorities {
				want := 0
				if w.Work == WorkPatient || w.Work == WorkBedRest {
					want = 1
				}
				if w.Priority != want {
					t.Fatal(a)
				}
			}
		}
		if coverageOf(t, d, WorkConstruction).Capable != 0 {
			t.Fatal("resting specialist counted as labor", d)
		}
		changes, ok := WorkChanges(team[0], d.Assignments[0])
		if !ok {
			t.Fatal("missing rest patch")
		}
		for _, w := range changes {
			if w.Definition == string(WorkBedRest) && (manual && w.Priority != 1 || !manual && w.Priority != 3) {
				t.Fatal(w)
			}
		}
		restored, err := PlanWork(team, nil, overrides, WorkDemand{})
		if err != nil || workValue(t, restored, "builder", WorkConstruction) != 2 {
			t.Fatal(restored, err)
		}
	}
}
