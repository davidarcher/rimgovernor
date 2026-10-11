package store

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func stageOf(t *testing.T, s *Store, action int) domain.Stage {
	t.Helper()
	plans, err := s.LoadPlans(context.Background())
	if err != nil || len(plans) != 1 {
		t.Fatal(plans, err)
	}
	return plans[0].Progress[action].View().Stage
}

// A read after a write sees the write; a read after a read is served from the
// cache. (Every hit is also compared with a fresh decode under test.)
func TestPlanCacheServesReadsAndSeesWrites(t *testing.T) {
	t.Parallel()
	s, _ := fixture(t)
	if stageOf(t, s, 1) != domain.Pending {
		t.Fatal("fresh action is not pending")
	}
	before := s.PlanCacheStats()
	if stageOf(t, s, 1) != domain.Pending {
		t.Fatal("cached read changed")
	}
	if got := s.PlanCacheStats(); got.Hits != before.Hits+1 {
		t.Fatalf("second read was not a hit: %+v -> %+v", before, got)
	}
	prepare(t, s, "b")
	if stageOf(t, s, 1) != domain.Prepared {
		t.Fatal("read after a write served the old state")
	}
	if _, err := s.Dispatch(context.Background(), "p", "b", scope(), 10); err != nil {
		t.Fatal(err)
	}
	if stageOf(t, s, 1) != domain.Dispatched {
		t.Fatal("read after dispatch served the old state")
	}
}

// A write made through another handle on the same file is invisible to this
// connection's hooks; data_version flushes the cache.
func TestPlanCacheSeesAnotherConnectionsWrite(t *testing.T) {
	t.Parallel()
	s, path := fixture(t)
	if stageOf(t, s, 0) != domain.Pending {
		t.Fatal("fresh action is not pending")
	}
	other := open(t, path)
	if _, err := other.Prepare(context.Background(), "p", "a", scope(), 10); err != nil {
		t.Fatal(err)
	}
	if stageOf(t, s, 0) != domain.Prepared {
		t.Fatal("another connection's write was not seen")
	}
}

// A plan read back inside a transaction that then rolls back must not stay
// cached.
func TestPlanCacheForgetsARolledBackWrite(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, _ := fixture(t)
	tx, err := s.begin(ctx)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = tx.ExecContext(ctx, "UPDATE plans SET method_id='rolled-back' WHERE id='p'"); err != nil {
		t.Fatal(err)
	}
	inside, err := s.plans.load(ctx, tx, "p")
	if err != nil || inside.Method != "rolled-back" {
		t.Fatal(inside.Method, err)
	}
	if err = tx.Rollback(); err != nil {
		t.Fatal(err)
	}
	after, err := s.LoadPlan(ctx, "p")
	if err != nil || after.Method != "" {
		t.Fatalf("rolled-back method survived: %q %v", after.Method, err)
	}
}

// A caller that replaces an element of a returned plan cannot change what the
// next read serves.
func TestPlanCacheReturnsDetachedSlices(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, _ := fixture(t)
	first, err := s.LoadPlans(ctx)
	if err != nil {
		t.Fatal(err)
	}
	first[0].Progress[0] = first[0].Progress[1]
	first[0].Progress = first[0].Progress[:1]
	if stageOf(t, s, 0) != domain.Pending || len(mustPlans(t, s)[0].Progress) != 3 {
		t.Fatal("a caller's edit reached the cache")
	}
}

func mustPlans(t *testing.T, s *Store) []PlanState {
	t.Helper()
	plans, err := s.LoadPlans(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	return plans
}
