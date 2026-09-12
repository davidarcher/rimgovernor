package buildingruntime

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"testing"
)

func TestComfortNativeUseBudgetRequiresOutcomeAndCurrentDirection(t *testing.T) {
	for _, definition := range []string{"Table1x2c", "DiningChair", "HorseshoesPin", "Campfire"} {
		t.Run(definition, func(t *testing.T) {
			building, err := domain.NewBuilding(definition, domain.Cell{X: 2, Z: 2}, domain.North, "")
			if err != nil {
				t.Fatal(err)
			}
			action, err := domain.NewBuildingAction("a", building)
			if err != nil {
				t.Fatal(err)
			}
			spec, err := domain.NewPlan("plan", 1, []domain.Action{action})
			if err != nil {
				t.Fatal(err)
			}
			p, err := domain.NewProgress(spec, action.ID())
			if err != nil {
				t.Fatal(err)
			}
			// Reuse the validated native/session scope from the runtime fixture.
			_, _, session, _, _ := sleepingFixture(t)
			current := session.State().Snapshot
			snapshot := current
			snapshot.Plan, snapshot.Revision = spec.ID(), spec.Revision()
			state := store.PlanState{Spec: spec, Progress: []domain.Progress{p}}
			if comfortNativeWorkTicks(state, current, 7) != 0 {
				t.Fatal("pending furniture granted time")
			}
			p, err = p.Prepare(snapshot, 7)
			if err != nil {
				t.Fatal(err)
			}
			p, err = p.MarkDispatched(snapshot, 7)
			if err != nil {
				t.Fatal(err)
			}
			p, err = p.RecordReceipt(1, domain.ReceiptAccepted)
			if err != nil {
				t.Fatal(err)
			}
			state.Progress[0] = p
			if comfortNativeWorkTicks(state, current, 7) != 0 {
				t.Fatal("receipt granted time")
			}
			p, err = p.Observe(domain.Observation{Action: action.ID(), Attempt: 1, Snapshot: snapshot, Tick: 100, Effect: domain.EffectCompleted, Causality: domain.AfterDispatch}, snapshot)
			if err != nil {
				t.Fatal(err)
			}
			state.Progress[0] = p
			for _, row := range []struct {
				tick domain.Tick
				want uint32
			}{{99, 0}, {100, 10000}, {101, 9999}, {10099, 1}, {10100, 0}} {
				want := row.want
				if definition == "Campfire" {
					want = 0
				}
				if got := comfortNativeWorkTicks(state, current, row.tick); got != want {
					t.Fatal(row, got, want)
				}
			}
			current.Direction++
			if comfortNativeWorkTicks(state, current, 100) != 0 {
				t.Fatal("new direction inherited time")
			}
		})
	}
}

func TestComfortCompilerResolvesNativeMaterialAndDiningAdjacency(t *testing.T) {
	planner := &RoutineBuildingPlanner{goal: policy.EnsureComfort}
	people := []policy.PawnID{"pawn"}
	census := policy.ComfortObservation{People: people}
	facts := observation.ColonyProjection{Facts: policy.RoutineFacts{Comfort: domain.Known(census)}, Definitions: []observation.PlanningDefinition{{Name: "Table1x2c", Stuff: domain.Known("WoodLog")}, {Name: "DiningChair", Stuff: domain.Known("WoodLog")}}}
	selected, reason, err := planner.selectComfort(facts, policy.ComfortHistory{})
	if err != nil || reason != "" || selected.definition != "Table1x2c" || selected.stuff != "WoodLog" || selected.environment != policy.PlacementIndoors {
		t.Fatal(selected, reason, err)
	}
	census.Surfaces = []policy.DiningSurface{{ID: "table", Adjacent: []domain.Cell{{X: 2, Z: 3}}}}
	facts.Facts.Comfort = domain.Known(census)
	selected, reason, err = planner.selectComfort(facts, policy.ComfortHistory{})
	if err != nil || reason != "" || selected.definition != "DiningChair" || len(selected.adjacent) != 1 || selected.adjacent[0] != (domain.Cell{X: 2, Z: 3}) {
		t.Fatal(selected, reason, err)
	}
	census.Dining = []policy.ComfortFacility{{ID: "chair", AccessibleTo: people}}
	facts.Facts.Comfort = domain.Known(census)
	selected, reason, err = planner.selectComfort(facts, policy.ComfortHistory{})
	if err != nil || reason != "" || selected.definition != "HorseshoesPin" || selected.environment != policy.PlacementAnywhere {
		t.Fatal(selected, reason, err)
	}
	census.Recreation = []policy.ComfortFacility{{ID: "hoop", AccessibleTo: people}}
	facts.Facts.Comfort = domain.Known(census)
	selected, reason, err = planner.selectComfort(facts, policy.ComfortHistory{})
	if err != nil || reason != BuildingComfortWait || selected != nil {
		t.Fatal(selected, reason, err)
	}
	if planner.definition != "" || planner.stuff != "" || len(planner.adjacent) != 0 {
		t.Fatal("selection mutated reusable compiler", planner)
	}
}
