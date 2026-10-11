package store

import (
	"context"
	"os"
	"path/filepath"
	"testing"
)

// SnapshotReplace leaves one openable copy at the fixed path, replaces it on
// the next call, and leaves no temporary file behind.
func TestSnapshotReplaceRewritesTheFixedPath(t *testing.T) {
	t.Parallel()
	s, _ := fixture(t)
	ctx := context.Background()
	dest := filepath.Join(t.TempDir(), "observe", "current.sqlite")
	if err := os.MkdirAll(filepath.Dir(dest), 0o755); err != nil {
		t.Fatal(err)
	}
	for range 2 {
		if err := s.SnapshotReplace(ctx, dest); err != nil {
			t.Fatal(err)
		}
	}
	copied, err := Open(ctx, dest)
	if err != nil {
		t.Fatalf("snapshot does not open: %v", err)
	}
	if err = copied.Close(); err != nil {
		t.Fatal(err)
	}
	entries, _ := os.ReadDir(filepath.Dir(dest))
	for _, e := range entries {
		if e.Name() != "current.sqlite" && e.Name() != "current.sqlite-wal" && e.Name() != "current.sqlite-shm" {
			t.Fatalf("stray file %s", e.Name())
		}
	}
}
