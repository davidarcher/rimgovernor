package buildingruntime

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// Pending installs protect the native sketch through the same derived layout
// path as pending housing. Installed markers supply their own native facts.
func pendingMonumentCells(plans []store.PlanState, offers domain.Fact[[]policy.JoinerOffer]) []domain.Cell {
	rows, known := offers.Value()
	if !known {
		return nil
	}
	markers := map[string]policy.QuestMonument{}
	for _, offer := range rows {
		if offer.State != "Ongoing" {
			continue
		}
		for _, objective := range offer.Objectives {
			if m, k := objective.Monument.Value(); k {
				if installed, k := m.Installed.Value(); k && installed {
					continue
				}
				markers[m.Marker] = m
			}
		}
	}
	var cells []domain.Cell
	for _, plan := range plans {
		if plan.Retired {
			continue
		}
		for _, progress := range plan.Progress {
			if !store.ProgressOpen(plan, progress) {
				continue
			}
			move, ok := progress.Action().MoveBuilding()
			if !ok {
				continue
			}
			marker, ok := markers[move.Thing()]
			if !ok {
				continue
			}
			for _, piece := range marker.Pieces {
				for _, offset := range piece.Footprint {
					cells = append(cells, domain.Cell{X: move.Cell().X + offset.X, Z: move.Cell().Z + offset.Z})
				}
			}
		}
	}
	return cells
}
