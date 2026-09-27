package store

import (
	"context"
	"errors"
	"fmt"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// reviewCensus files a disabled routine review whose construction census
// holds the given intents built and blueprints standing.
func reviewCensus(t *testing.T, s *Store, current domain.GenerationSnapshot, tick domain.Tick, built []domain.ActionID, blueprints ...domain.ActionID) {
	t.Helper()
	ctx := context.Background()
	previous, err := s.LoadRoutineReview(ctx)
	if err != nil {
		t.Fatal(err)
	}
	census := policy.CurrentConstruction{Colony: true, Buildings: []policy.CurrentBuilding{}}
	for i, a := range built {
		census.Buildings = append(census.Buildings, policy.CurrentBuilding{ID: fmt.Sprintf("Thing%d", i), IntentKey: string(a) + "/1"})
	}
	for _, a := range blueprints {
		census.Intents = append(census.Intents, policy.ConstructionIntent{Key: string(a) + "/1", Stage: "blueprint"})
	}
	r := RoutineReviewRequest{Revision: previous.Revision, Current: current, Tick: tick, Policy: policy.DefaultRoutinePolicy(), Facts: policy.RoutineFacts{CurrentConstruction: domain.Known(census)}}
	if _, err = s.ReviewRoutine(ctx, r); err != nil {
		t.Fatal(err)
	}
}

// An accepted building intent completes at its blueprint; its dependent
// waits until the census reports the building built (#937).
func TestBuildingDependencyWaitsForCensusBuilt(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	base := plan(t, "p", "floor", "bed")
	p, err := domain.NewPlan(base.ID(), base.Revision(), base.Actions(), domain.ActionDependency{Action: "bed", Requires: "floor"})
	if err != nil {
		t.Fatal(err)
	}
	if err = s.CreatePlan(ctx, p); err != nil {
		t.Fatal(err)
	}
	prepare(t, s, "floor")
	if _, err = s.Dispatch(ctx, "p", "floor", scope(), 10); err != nil {
		t.Fatal(err)
	}
	if _, err = s.RecordReceipt(ctx, "p", "floor", 1, domain.ReceiptAccepted); err != nil {
		t.Fatal(err)
	}
	if _, err = s.Prepare(ctx, "p", "bed", scope(), 11); !errors.Is(err, domain.ErrDependency) {
		t.Fatal("dependent admitted before any census", err)
	}
	reviewCensus(t, s, scope(), 11, nil, "floor")
	if _, err = s.Prepare(ctx, "p", "bed", scope(), 11); !errors.Is(err, domain.ErrDependency) {
		t.Fatal("dependent admitted onto a blueprint", err)
	}
	other := scope()
	other.Load = "other"
	reviewCensus(t, s, other, 12, []domain.ActionID{"floor"})
	if _, err = s.Prepare(ctx, "p", "bed", scope(), 12); !errors.Is(err, domain.ErrDependency) {
		t.Fatal("another world's census released the dependent", err)
	}
	reviewCensus(t, s, scope(), 13, []domain.ActionID{"floor"})
	if _, err = s.Prepare(ctx, "p", "bed", scope(), 13); err != nil {
		t.Fatal(err)
	}
	// An incomplete census keeps the last built set.
	previous, err := s.LoadRoutineReview(ctx)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.ReviewRoutine(ctx, RoutineReviewRequest{Revision: previous.Revision, Current: scope(), Tick: 14, Policy: policy.DefaultRoutinePolicy()}); err != nil {
		t.Fatal(err)
	}
	if _, err = s.Dispatch(ctx, "p", "bed", scope(), 14); err != nil {
		t.Fatal(err)
	}
}
