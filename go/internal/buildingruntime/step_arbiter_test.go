package buildingruntime

import (
	"sync"
	"sync/atomic"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestStepArbiterTryClaimSamePawnTwiceFails(t *testing.T) {
	a := newStepArbiter()
	if !a.tryClaim([]domain.PawnID{"p1"}) {
		t.Fatalf("first claim of p1 should succeed")
	}
	if a.tryClaim([]domain.PawnID{"p1"}) {
		t.Fatalf("second claim of already-held pawn p1 should fail")
	}
}

func TestStepArbiterTryClaimDisjointBothSucceed(t *testing.T) {
	a := newStepArbiter()
	if !a.tryClaim([]domain.PawnID{"p1"}, "haul-item:i1") {
		t.Fatalf("claim of p1/i1 should succeed")
	}
	if !a.tryClaim([]domain.PawnID{"p2"}, "haul-item:i2") {
		t.Fatalf("disjoint claim of p2/i2 should succeed")
	}
}

func TestStepArbiterTryClaimAllOrNothing(t *testing.T) {
	a := newStepArbiter()
	if !a.tryClaim([]domain.PawnID{"p1"}) {
		t.Fatalf("initial claim of p1 should succeed")
	}
	// p2 is free but p1 is already held: the whole claim must fail, and p2
	// must remain unclaimed afterward.
	if a.tryClaim([]domain.PawnID{"p1", "p2"}, "haul-item:i1") {
		t.Fatalf("claim naming an already-held pawn should fail entirely")
	}
	if !a.tryClaim([]domain.PawnID{"p2"}) {
		t.Fatalf("p2 should still be free after the failed all-or-nothing claim")
	}
	if !a.tryClaim(nil, "haul-item:i1") {
		t.Fatalf("haul-item:i1 should still be free after the failed all-or-nothing claim")
	}
}

// TestStepArbiterConcurrentClaimsOnSamePawnAreSerialized is the race
// regression this type exists for: ClockScheduler.Step now runs many
// planners as concurrent goroutines (plannerGroup) sharing one
// stepArbiter, so two "planners" -- here, goroutines standing in for two
// real planners such as RoutineTendPlanner and RoutineRescuePlanner -- racing
// to claim the same pawn must never both win, the same way two real planners
// must never both commit a plan against one pawn in a single Step(). Run with
// -race: tryClaim's own mutex is what must make this safe, not luck.
func TestStepArbiterConcurrentClaimsOnSamePawnAreSerialized(t *testing.T) {
	const attempts = 64
	a := newStepArbiter()
	pawn := domain.PawnID("contested-pawn")
	var wg sync.WaitGroup
	var won int64
	start := make(chan struct{})
	for i := 0; i < attempts; i++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			<-start // maximize actual overlap between goroutines
			if a.tryClaim([]domain.PawnID{pawn}) {
				atomic.AddInt64(&won, 1)
			}
		}()
	}
	close(start)
	wg.Wait()
	if won != 1 {
		t.Fatalf("expected exactly one concurrent claimant to win the contested pawn, got %d", won)
	}
}
