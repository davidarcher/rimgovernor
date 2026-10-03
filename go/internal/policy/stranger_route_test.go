package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Stranger corpses: fresh x rotten, butcher eligible x none, room x full.
func TestStrangerCorpseRouting(t *testing.T) {
	room := LayoutRoom{Role: ModuleWorkshop, Interior: Rectangle{X: 10, Z: 20, Width: 7, Height: 5}, Door: domain.Cell{X: 13, Z: 19}, DoorRot: domain.North}
	plan := LayoutPlan{Rooms: []LayoutRoom{room}}
	rooms := tombStanding(room)
	for _, worker := range []bool{true, false} {
		for _, space := range []string{"ready", "cells", "full"} {
			b := ProductionBench{ID: "bench", Butcher: true, Usable: domain.Known(true), Token: domain.Known("t"), HumanCorpseNutrition: domain.Known(10.), HumanStorageReady: domain.Known(space == "ready"), Recipes: []ProductionRecipe{{Name: "ButcherCorpseFlesh", Available: domain.Known(true)}}}
			if space == "cells" {
				b.HumanStorageCells = []domain.Cell{{X: 1, Z: 1}}
			}
			if worker {
				b.HumanButchers = []HumanButcherCandidate{{ID: "a", PreceptAcceptable: domain.Known(true), CanWork: domain.Known(true)}}
			}
			open := HumanButcheryOpen(domain.Known([]ProductionBench{b}), domain.Unknown[Ideoligion]())
			if open != (worker && space != "full") {
				t.Fatalf("worker=%v space=%s: open=%v", worker, space, open)
			}
			for _, rot := range []domain.RotStage{domain.RotFresh, domain.RotRotting, domain.RotDessicated, ""} {
				want := StrangerCremate
				if open && !rot.Spoiled() {
					want = StrangerButcher
				}
				if got := RouteStranger(rot, open); got != want {
					t.Errorf("worker=%v space=%s rot=%q: %s", worker, space, rot, got)
				}
				item := WasteItem{ID: "Corpse_1", Kind: "corpse", State: WasteExposed, CorpseOf: domain.CorpseStranger, RotStage: rot}
				step := NextCremationStep(plan, rooms, []WasteItem{item}, nil, open)
				if (step.Strangers == 1) != (want == StrangerCremate) || (step.Kind == CremationPlace) != (want == StrangerCremate) || step.Kind != CremationNone && step.StrangerSpoiledOnly != open {
					t.Errorf("worker=%v space=%s rot=%q: step %+v", worker, space, rot, step)
				}
			}
		}
	}
}
