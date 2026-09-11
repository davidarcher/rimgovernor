package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"reflect"
	"testing"
)

func workTeam(manual bool) []WorkPawn {
	work := []WorkPriority{{Work: "Construction"}, {Work: "Growing"}, {Work: "Cooking"}, {Work: "Doctor"}, {Work: "PlantCutting"}, {Work: "Hunting"}, {Work: "Hauling"}, {Work: "Cleaning"}, {Work: "Firefighter"}}
	return []WorkPawn{
		{ID: "builder", Available: domain.Known(true), Applies: domain.Known(true), Manual: domain.Known(manual), Ranged: domain.Known(false), Work: domain.Known(work), Skills: domain.Known([]WorkSkill{{Name: "Construction", Level: 15}, {Name: "Plants", Level: 1}, {Name: "Cooking", Level: 10}, {Name: "Medicine", Level: 8}, {Name: "Shooting", Level: 5}})},
		{ID: "grower", Available: domain.Known(true), Applies: domain.Known(true), Manual: domain.Known(manual), Ranged: domain.Known(false), Work: domain.Known(work), Skills: domain.Known([]WorkSkill{{Name: "Construction", Level: 3}, {Name: "Plants", Level: 15}, {Name: "Cooking", Level: 1}, {Name: "Medicine", Level: 3}, {Name: "Shooting", Level: 5}})},
	}
}
func workValue(t *testing.T, d WorkDecision, pawn PawnID, work WorkType) int {
	t.Helper()
	for _, p := range d.Assignments {
		if p.Pawn == pawn {
			for _, v := range p.Priorities {
				if v.Work == work {
					return v.Priority
				}
			}
		}
	}
	t.Fatal("assignment missing", pawn, work)
	return -1
}
func TestWorkAssignmentSpecialistsModesAndReadback(t *testing.T) {
	for _, manual := range []bool{false, true} {
		team := workTeam(manual)
		decision, err := AssignWork(team, nil, nil)
		if err != nil {
			t.Fatal(err)
		}
		if capable, known := decision.Capacity.Value(); !known || !capable {
			t.Fatal(decision)
		}
		if workValue(t, decision, "builder", "Construction") != 1 || workValue(t, decision, "grower", "Growing") != 1 {
			t.Fatal(decision)
		}
		secondary := 0
		if manual {
			secondary = 3
		}
		if workValue(t, decision, "grower", "Construction") != secondary {
			t.Fatal(decision)
		}
		if matches, known := decision.Matches.Value(); !known || matches {
			t.Fatal("unassigned work matched", decision)
		}
		for i := range team {
			values := append([]WorkPriority(nil), decision.Assignments[i].Priorities...)
			if !manual {
				for j := range values {
					if values[j].Priority > 0 {
						values[j].Priority = 3
					}
				}
			}
			team[i].Work = domain.Known(values)
		}
		next, err := AssignWork(team, nil, nil)
		if err != nil {
			t.Fatal(err)
		}
		if matches, known := next.Matches.Value(); !known || !matches {
			t.Fatal("native mode readback did not match", next)
		}
		team[0], team[1] = team[1], team[0]
		reordered, err := AssignWork(team, nil, nil)
		if err != nil || !reflect.DeepEqual(next, reordered) {
			t.Fatal("pawn order changed assignment", reordered, err)
		}
	}
}
func TestWorkAssignmentRequirementsOverridesAndUnknown(t *testing.T) {
	team := workTeam(true)
	d, err := AssignWork(team, []WorkRequirement{{Work: "Construction", Skill: "Construction", Minimum: 10}}, []WorkOverride{{Pawn: "builder", Work: "Construction", Priority: 0}})
	if err != nil {
		t.Fatal(err)
	}
	if capable, known := d.Capacity.Value(); !known || capable {
		t.Fatal("override/minimum ignored", d)
	}
	if workValue(t, d, "builder", "Construction") != 0 {
		t.Fatal(d)
	}
	team[0].Manual = domain.Unknown[bool]()
	d, err = AssignWork(team, nil, nil)
	if err != nil {
		t.Fatal(err)
	}
	if _, known := d.Matches.Value(); known || len(d.Assignments) != 0 {
		t.Fatal("unknown mode manufactured assignment", d)
	}
	team[0].Available = domain.Known(false)
	d, err = AssignWork(team, nil, nil)
	if err != nil || len(d.Assignments) != 1 || d.Assignments[0].Pawn != "grower" {
		t.Fatal(d, err)
	}
	if _, err = AssignWork(workTeam(true), nil, []WorkOverride{{Pawn: "builder", Work: "Cooking", Priority: 0}, {Pawn: "builder", Work: "Cooking", Priority: 1}}); err == nil {
		t.Fatal("duplicate override")
	}
	if _, err = AssignWork(workTeam(true), nil, []WorkOverride{{Pawn: "builder", Work: "MissingWork", Priority: 1}}); err == nil {
		t.Fatal("unavailable override ignored")
	}
}
