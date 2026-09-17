package store

import (
	"context"
	"errors"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func caravanTrackingFixture(t *testing.T) *Store {
	t.Helper()
	db, err := Open(context.Background(), memoryPath(t))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { db.Close() })
	return db
}

func TestCaravanTrackingStartIsIdempotentButRejectsConflict(t *testing.T) {
	t.Parallel()
	db := caravanTrackingFixture(t)
	ctx := context.Background()
	crew := []domain.PawnID{"alpha", "beta"}
	if err := db.StartCaravanTracking(ctx, "caravan-1", crew); err != nil {
		t.Fatal(err)
	}
	// Replaying the same completion evidence (e.g. after a restart) must not
	// duplicate or error.
	if err := db.StartCaravanTracking(ctx, "caravan-1", crew); err != nil {
		t.Fatal(err)
	}
	active, err := db.ListActiveCaravanTracking(ctx)
	if err != nil || len(active) != 1 {
		t.Fatal(active, err)
	}
	// Conflicting crew evidence for the same caravan id is corruption, not a
	// replay, and must be rejected rather than silently overwritten.
	if err := db.StartCaravanTracking(ctx, "caravan-1", []domain.PawnID{"gamma"}); err == nil {
		t.Fatal("accepted conflicting crew for existing caravan")
	}
}

func TestCaravanTrackingRejectsInvalidCrew(t *testing.T) {
	t.Parallel()
	db := caravanTrackingFixture(t)
	ctx := context.Background()
	for _, crew := range [][]domain.PawnID{nil, {}, {"dup", "dup"}, {""}} {
		if err := db.StartCaravanTracking(ctx, "caravan-x", crew); err == nil {
			t.Fatal("accepted invalid crew", crew)
		}
	}
}

func TestCaravanTrackingResolveRequiresActiveRecord(t *testing.T) {
	t.Parallel()
	db := caravanTrackingFixture(t)
	ctx := context.Background()
	if err := db.ResolveCaravanTracking(ctx, "missing"); !errors.Is(err, ErrNotFound) {
		t.Fatal(err)
	}
	if err := db.StartCaravanTracking(ctx, "caravan-1", []domain.PawnID{"alpha"}); err != nil {
		t.Fatal(err)
	}
	if err := db.ResolveCaravanTracking(ctx, "caravan-1"); err != nil {
		t.Fatal(err)
	}
	active, err := db.ListActiveCaravanTracking(ctx)
	if err != nil || len(active) != 0 {
		t.Fatal(active, err)
	}
	// Resolving an already-resolved record is refused rather than a silent
	// no-op: a caller should only ever resolve a record it just listed as
	// active.
	if err := db.ResolveCaravanTracking(ctx, "caravan-1"); !errors.Is(err, ErrNotFound) {
		t.Fatal(err)
	}
}

func TestCaravanStuckMarkIsIdempotentOnSinceTick(t *testing.T) {
	t.Parallel()
	db := caravanTrackingFixture(t)
	ctx := context.Background()
	if err := db.StartCaravanTracking(ctx, "caravan-1", []domain.PawnID{"alpha"}); err != nil {
		t.Fatal(err)
	}
	if err := db.MarkCaravanStuck(ctx, "caravan-1", 100, StuckCaravanStopped); err != nil {
		t.Fatal(err)
	}
	// A later observation at a different tick and status must not move
	// SinceTick backward or forward: it always reflects the first time the
	// caravan was seen stuck.
	if err := db.MarkCaravanStuck(ctx, "caravan-1", 250, StuckCaravanUnknown); err != nil {
		t.Fatal(err)
	}
	stuck, err := db.ListStuckCaravanTracking(ctx, 250, 0)
	if err != nil || len(stuck) != 1 {
		t.Fatal(stuck, err)
	}
	if stuck[0].CaravanID != "caravan-1" || stuck[0].SinceTick != 100 || stuck[0].Status != StuckCaravanUnknown {
		t.Fatal(stuck[0])
	}
}

func TestCaravanStuckThresholdFiltersRecentlyStuck(t *testing.T) {
	t.Parallel()
	db := caravanTrackingFixture(t)
	ctx := context.Background()
	if err := db.StartCaravanTracking(ctx, "caravan-1", []domain.PawnID{"alpha"}); err != nil {
		t.Fatal(err)
	}
	if err := db.MarkCaravanStuck(ctx, "caravan-1", 1000, StuckCaravanStopped); err != nil {
		t.Fatal(err)
	}
	if stuck, err := db.ListStuckCaravanTracking(ctx, 1050, 100); err != nil || len(stuck) != 0 {
		t.Fatal(stuck, err)
	}
	if stuck, err := db.ListStuckCaravanTracking(ctx, 1100, 100); err != nil || len(stuck) != 1 {
		t.Fatal(stuck, err)
	}
}

func TestCaravanStuckClearRemovesRecord(t *testing.T) {
	t.Parallel()
	db := caravanTrackingFixture(t)
	ctx := context.Background()
	if err := db.StartCaravanTracking(ctx, "caravan-1", []domain.PawnID{"alpha"}); err != nil {
		t.Fatal(err)
	}
	if err := db.MarkCaravanStuck(ctx, "caravan-1", 10, StuckCaravanStopped); err != nil {
		t.Fatal(err)
	}
	if err := db.ClearCaravanStuck(ctx, "caravan-1"); err != nil {
		t.Fatal(err)
	}
	if stuck, err := db.ListStuckCaravanTracking(ctx, 10, 0); err != nil || len(stuck) != 0 {
		t.Fatal(stuck, err)
	}
	// Clearing when nothing is recorded is a no-op, not an error.
	if err := db.ClearCaravanStuck(ctx, "caravan-1"); err != nil {
		t.Fatal(err)
	}
}

func TestCaravanStuckClearedOnResolve(t *testing.T) {
	t.Parallel()
	db := caravanTrackingFixture(t)
	ctx := context.Background()
	if err := db.StartCaravanTracking(ctx, "caravan-1", []domain.PawnID{"alpha"}); err != nil {
		t.Fatal(err)
	}
	if err := db.MarkCaravanStuck(ctx, "caravan-1", 10, StuckCaravanStopped); err != nil {
		t.Fatal(err)
	}
	if err := db.ResolveCaravanTracking(ctx, "caravan-1"); err != nil {
		t.Fatal(err)
	}
	if stuck, err := db.ListStuckCaravanTracking(ctx, 10, 0); err != nil || len(stuck) != 0 {
		t.Fatal(stuck, err)
	}
}

func TestCaravanStuckRejectsInvalidInputs(t *testing.T) {
	t.Parallel()
	db := caravanTrackingFixture(t)
	ctx := context.Background()
	if err := db.StartCaravanTracking(ctx, "caravan-1", []domain.PawnID{"alpha"}); err != nil {
		t.Fatal(err)
	}
	if err := db.MarkCaravanStuck(ctx, "caravan-1", -1, StuckCaravanStopped); err == nil {
		t.Fatal("accepted negative tick")
	}
	if err := db.MarkCaravanStuck(ctx, "caravan-1", 1, StuckCaravanStatus("bogus")); err == nil {
		t.Fatal("accepted invalid status")
	}
	// A caravan_id with no matching caravan_tracking row violates the
	// foreign key and must be rejected.
	if err := db.MarkCaravanStuck(ctx, "missing", 1, StuckCaravanStopped); err == nil {
		t.Fatal("accepted stuck record for untracked caravan")
	}
	if _, err := db.ListStuckCaravanTracking(ctx, -1, 0); err == nil {
		t.Fatal("accepted negative current tick")
	}
	if _, err := db.ListStuckCaravanTracking(ctx, 0, -1); err == nil {
		t.Fatal("accepted negative threshold")
	}
}

func TestCaravanTrackingListOrdersDeterministically(t *testing.T) {
	t.Parallel()
	db := caravanTrackingFixture(t)
	ctx := context.Background()
	for _, id := range []string{"caravan-c", "caravan-a", "caravan-b"} {
		if err := db.StartCaravanTracking(ctx, id, []domain.PawnID{"alpha"}); err != nil {
			t.Fatal(err)
		}
	}
	if err := db.ResolveCaravanTracking(ctx, "caravan-b"); err != nil {
		t.Fatal(err)
	}
	active, err := db.ListActiveCaravanTracking(ctx)
	if err != nil || len(active) != 2 || active[0].CaravanID != "caravan-a" || active[1].CaravanID != "caravan-c" {
		t.Fatal(active, err)
	}
}
