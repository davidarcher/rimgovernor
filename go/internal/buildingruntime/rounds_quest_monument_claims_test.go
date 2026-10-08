package buildingruntime

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"reflect"
	"testing"
)

func TestPendingMonumentCellsProtectsSketchUntilNativeInstall(t *testing.T) {
	move, _ := domain.NewMoveBuilding("marker", "MonumentMarker", domain.Cell{X: 10, Z: 20}, domain.North)
	action, _ := domain.NewMoveBuildingAction("install", move)
	plan, _ := domain.NewPlan("plan", 1, []domain.Action{action})
	progress, _ := domain.NewProgress(plan, "install")
	stored := store.PlanState{Spec: plan, Progress: []domain.Progress{progress}}
	marker := policy.QuestMonument{Marker: "marker", Installed: domain.Known(false), Pieces: []policy.QuestMonumentPiece{{Footprint: []domain.Cell{{X: -1, Z: 0}, {X: 1, Z: 2}}}}}
	offer := policy.JoinerOffer{State: "Ongoing", Objectives: []policy.QuestObjective{{Monument: domain.Known(marker)}}}
	rows := domain.Known([]policy.JoinerOffer{offer})
	if got := pendingMonumentCells([]store.PlanState{stored}, rows); !reflect.DeepEqual(got, []domain.Cell{{X: 9, Z: 20}, {X: 11, Z: 22}}) {
		t.Fatal(got)
	}
	stored.Retired = true
	if got := pendingMonumentCells([]store.PlanState{stored}, rows); len(got) != 0 {
		t.Fatal(got)
	}
	stored.Retired = false
	marker.Installed = domain.Known(true)
	offer.Objectives[0].Monument = domain.Known(marker)
	if got := pendingMonumentCells([]store.PlanState{stored}, domain.Known([]policy.JoinerOffer{offer})); len(got) != 0 {
		t.Fatal(got)
	}
}
