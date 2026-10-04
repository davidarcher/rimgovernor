package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// Raiders still in their pods (#908) are a fight to decide with no
// hostile in the census yet, so the pods tactic can draft the nearest
// armed before the open (#891); without them a hostile-free census is no
// fight.
func TestCombatFrameInputsPendingPodsIsAFight(t *testing.T) {
	t.Parallel()
	combat := bridge.Combat{
		Emergency: bridge.EmergencyObservation{Facts: policy.EmergencyFacts{ColonistsComplete: domain.Known(true), Colonists: []policy.EmergencyPawn{{ID: "rifle"}}}},
		Detail:    bridge.PawnsFromMap(map[string]*n.PawnState{"rifle": {}}),
	}
	if _, reason, err := combatFrameInputs(combat, nil); err != nil || !reason.Is(WaitMethodUsed) {
		t.Fatalf("no pods: reason %q err %v", reason, err)
	}
	combat.Emergency.Facts.PodsOpen = 530
	in, reason, err := combatFrameInputs(combat, nil)
	if err != nil || !reason.IsZero() || in.rows["rifle"] == nil {
		t.Fatalf("pending pods: reason %q err %v rows %v", reason, err, in.rows)
	}
}
