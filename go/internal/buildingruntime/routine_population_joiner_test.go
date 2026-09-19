package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestQuestAcceptIsARoutineExecutableKind(t *testing.T) {
	t.Parallel()
	// The joiner answer (#250) is dispatched under the routine worker like
	// every other routine method; a kind missing from the allowlist commits
	// a plan whose action then sits at pending until the offer expires.
	if !routineExecutableKind(domain.QuestAcceptAction) {
		t.Fatal("quest_accept must be routine executable")
	}
}
