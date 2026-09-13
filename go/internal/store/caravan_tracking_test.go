package store

import (
	"context"
	"errors"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func caravanTrackingFixture(t *testing.T) *Store {
	t.Helper()
	db, err := Open(context.Background(), filepath.Join(t.TempDir(), "caravan-tracking.sqlite"))
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
