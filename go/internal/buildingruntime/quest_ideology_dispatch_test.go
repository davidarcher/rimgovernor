package buildingruntime

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"slices"
	"testing"
)

func TestPopulationPlannerOwnsEveryQuestDriverAction(t *testing.T) {
	for _, kind := range []domain.ActionKind{domain.HackDesignationAction, domain.GiveItemAction, domain.MineAcquisitionAction, domain.PrisonerInteractionAction} {
		if !roundsExecutableKind(kind) {
			t.Fatalf("quest action %s is not executable", kind)
		}
		found := false
		for _, entry := range plannerCatalog {
			if entry.name == "populationJoiner" {
				found = slices.Contains(entry.kinds, kind)
				break
			}
		}
		if !found {
			t.Fatalf("population planner lacks action %s", kind)
		}
	}
}
