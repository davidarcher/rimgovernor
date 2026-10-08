package executor

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"testing"
)

func TestQuestShuttleUsesPlainIntentDispatch(t *testing.T) {
	if !plainIntents[domain.QuestShuttleAction] {
		t.Fatal("shuttle must use intent dispatch")
	}
}
