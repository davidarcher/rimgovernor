package buildingruntime

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func TestRoutineProjectWorkTracksSharedLifecycleAndWorld(t *testing.T) {
	t.Parallel()
	current := domain.GenerationSnapshot{Colony: "colony", Load: "load", Map: 0, Plan: "selected", Revision: 1, Native: 1}
	makePlan := func(id domain.PlanID, name string, admitted bool) store.PlanState {
		b, _ := domain.NewBuilding(name, domain.Cell{X: 1, Z: 1}, domain.North, "")
		a, _ := domain.NewBuildingAction(domain.ActionID(id), b)
		spec, err := domain.NewPlan(id, 1, []domain.Action{a})
		if err != nil {
			t.Fatal(err)
		}
		p, _ := domain.NewProgress(spec, a.ID())
		plan := store.PlanState{Spec: spec, Progress: []domain.Progress{p}}
		if admitted {
			s := current
			s.Plan = id
			plan.Admissions = []store.ActionAdmission{{Action: a.ID(), Admission: store.Admission{Snapshot: s}}}
		}
		return plan
	}
	selected := makePlan("selected", "Wall", false)
	shared := makePlan("shared", "HospitalBed", true)
	unselected := makePlan("unselected", "RoyalBed", false)
	otherWorld := makePlan("other", "Door", true)
	otherWorld.Admissions[0].Admission.Snapshot.Load = "previous"
	plans := []store.PlanState{shared, selected, unselected, otherWorld}
	player := map[domain.PlanID]uint64{}
	check := func(want ...string) {
		t.Helper()
		if got := routineProjectDefinitions(plans, current, player); !reflect.DeepEqual(got, want) {
			t.Fatal(got, want)
		}
	}
	check("HospitalBed", "Wall")
	// Player guidance under the root counts as selected intent at its
	// submitted revision only.
	player["unselected"] = 2
	check("HospitalBed", "Wall")
	player["unselected"] = 1
	check("HospitalBed", "RoyalBed", "Wall")
	delete(player, "unselected")
	p := shared.Progress[0]
	scope := shared.Admissions[0].Admission.Snapshot
	p, err := p.Prepare(scope, 1)
	if err != nil {
		t.Fatal(err)
	}
	p, err = p.MarkDispatched(scope, 1)
	if err != nil {
		t.Fatal(err)
	}
	p, err = p.Cancel()
	if err != nil {
		t.Fatal(err)
	}
	plans[0].Progress[0] = p
	check("HospitalBed", "Wall")
	for _, effect := range []domain.Effect{domain.EffectCompleted, domain.EffectAbsent} {
		settled, err := p.Observe(domain.Observation{Action: p.Action().ID(), Attempt: 1, Snapshot: scope, Tick: 2, Effect: effect}, scope)
		if err != nil {
			t.Fatal(err)
		}
		plans[0].Progress[0] = settled
		check("Wall")
	}
	plans[0].Progress[0], _ = shared.Progress[0].Cancel()
	check("Wall")
}

func TestRoutineProjectSkillRequirementsUseMaximumAndPreserveUnknown(t *testing.T) {
	t.Parallel()
	defs := []observation.PlanningDefinition{{Name: "Wall", ConstructionSkill: domain.Known(int32(0))}, {Name: "HospitalBed", ConstructionSkill: domain.Known(int32(8))}}
	got, known := routineProjectWork([]string{"Wall", "HospitalBed"}, defs).Value()
	if !known || !reflect.DeepEqual(got, []policy.WorkRequirement{{Work: "Construction", Skill: "Construction", Minimum: 8}}) {
		t.Fatal(got, known)
	}
	if _, known := routineProjectWork([]string{"Missing"}, defs).Value(); known {
		t.Fatal("missing definition became known")
	}
	defs[1].ConstructionSkill = domain.Unknown[int32]()
	if _, known := routineProjectWork([]string{"HospitalBed"}, defs).Value(); known {
		t.Fatal("missing skill became zero")
	}
	if got, known := routineProjectWork(nil, nil).Value(); !known || len(got) != 0 {
		t.Fatal(got, known)
	}
}
