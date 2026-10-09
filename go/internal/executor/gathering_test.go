package executor

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestGatheringUsesPlainIntentDispatch(t *testing.T) {
	if !plainIntents[domain.GatheringAction] || !domain.GatheringAction.IntentMode() {
		t.Fatal("gathering must use intent dispatch")
	}
}
