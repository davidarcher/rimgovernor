package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

// The clock's shelter fact comes from the routine review's facts, so it
// holds on steps where the recovery planner never runs (#1569).
func TestClockShelterHeldReadsReviewFacts(t *testing.T) {
	if _, known := clockShelterHeld(nil).Value(); known {
		t.Fatal("no review: unknown")
	}
	if _, known := clockShelterHeld(&store.RoutineReviewResult{}).Value(); known {
		t.Fatal("no detection: unknown")
	}
	worker := policy.RecoveryWorker{Pawn: "a", Dead: domain.Known(false), Downed: domain.Known(false), Mental: domain.Known(false), Drafted: domain.Known(false)}
	restrict := func(area string) domain.Fact[policy.RecoverySafety] {
		return domain.Known(policy.RecoverySafety{Restrictions: []policy.RecoveryRestriction{{Pawn: "a", Area: domain.Known(area)}}})
	}
	f := policy.RoutineFacts{
		Hostiles:           domain.Known(int64(2)),
		DisasterConditions: domain.Known([]policy.DisasterCondition{}),
		ShelterArea:        domain.Known("safe"),
		RecoveryWorkers:    domain.Known([]policy.RecoveryWorker{worker}),
		RecoverySafety:     restrict("safe"),
	}
	review := &store.RoutineReviewResult{Detection: &store.RoutineDetection{Facts: f}}
	if held, known := clockShelterHeld(review).Value(); !known || !held {
		t.Fatalf("restricted to Safe under a threat = %v, %v", held, known)
	}
	review.Detection.Facts.RecoverySafety = restrict("")
	if held, known := clockShelterHeld(review).Value(); !known || held {
		t.Fatalf("unrestricted = %v, %v", held, known)
	}
}
