package store

import (
	"context"
	"path/filepath"
	"reflect"
	"testing"
)

// Snapshot copies an open store consistently and refuses to overwrite.
func TestSnapshotCopiesOpenStore(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, _ := fixture(t)
	prepare(t, s, "a")
	if _, err := s.Dispatch(ctx, "p", "a", scope(), 10); err != nil {
		t.Fatal(err)
	}
	dst := filepath.Join(t.TempDir(), "copy.db")
	if err := s.Snapshot(ctx, dst); err != nil {
		t.Fatal(err)
	}
	if err := s.Snapshot(ctx, dst); err == nil {
		t.Fatal("a second snapshot overwrote the first")
	}
	before, err := s.LoadPlan(ctx, "p")
	if err != nil {
		t.Fatal(err)
	}
	copied := open(t, dst)
	after, err := copied.LoadPlan(ctx, "p")
	if err != nil {
		t.Fatal(err)
	}
	if !reflect.DeepEqual(before, after) {
		t.Fatal("the snapshot differs from the live store")
	}
	if _, err := copied.Dispatch(ctx, "p", "a", scope(), 11); err == nil {
		t.Fatal("the copy replayed the dispatch")
	}
}
