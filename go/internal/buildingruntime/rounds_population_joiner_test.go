package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The ritual start (#1639) is dispatched under the routine worker too.
func TestRitualIsARoundsExecutableKind(t *testing.T) {
	t.Parallel()
	if !roundsExecutableKind(domain.RitualAction) {
		t.Fatal("ritual must be routine executable")
	}
}

func TestQuestAcceptIsARoundsExecutableKind(t *testing.T) {
	t.Parallel()
	// The joiner answer (#250) is dispatched under the routine worker like
	// every other routine method; a kind missing from the allowlist commits
	// a plan whose action then sits at pending until the offer expires.
	if !roundsExecutableKind(domain.QuestAcceptAction) {
		t.Fatal("quest_accept must be routine executable")
	}
}
