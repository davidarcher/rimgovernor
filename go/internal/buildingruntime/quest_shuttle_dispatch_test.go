package buildingruntime

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"testing"
)

func TestQuestShuttleIsARoundsExecutableKind(t *testing.T) {
	if !roundsExecutableKind(domain.QuestShuttleAction) {
		t.Fatal("quest shuttle must be executable by Rounds")
	}
}
